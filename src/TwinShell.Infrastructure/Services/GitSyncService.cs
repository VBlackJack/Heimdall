using System.IO;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Constants;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Utilities;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Service for Git-based synchronization of TwinShell data.
/// Uses LibGit2Sharp for Git operations with retry logic and detailed progress.
/// Thread-safe: uses SemaphoreSlim to prevent concurrent sync operations.
/// </summary>
public sealed class GitSyncService : IGitSyncService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<GitSyncService> _logger;
    private readonly ILocalizationService _localization;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    // Retry configuration
    private const int MaxRetryAttempts = 3;
    private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000 };

    // Sync operation lock to prevent concurrent operations
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private volatile bool _isOperationInProgress;

    // Cancellation support
    private readonly object _ctsLock = new();
    private CancellationTokenSource? _currentCancellationTokenSource;
    private bool _disposed;

    private string _statusMessage = "Not configured";
    private SyncPhase _currentPhase = SyncPhase.Idle;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings?.GitRemoteUrl)
                                && !string.IsNullOrWhiteSpace(Settings?.GitRepositoryPath);

    public bool IsOperationInProgress => _isOperationInProgress;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessage = value;
            RaiseStatusChanged(value);
        }
    }

    public event EventHandler<GitSyncStatusEventArgs>? StatusChanged;

    private UserSettings? Settings => _settingsService.CurrentSettings;

    public GitSyncService(
        ISettingsService settingsService,
        ILogger<GitSyncService> logger,
        ILocalizationService localization,
        IServiceScopeFactory serviceScopeFactory)
    {
        _settingsService = settingsService;
        _logger = logger;
        _localization = localization;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>
    /// Imports the YAML tree into the database through a fresh DI scope.
    /// <see cref="ISyncService"/> is scoped (it depends on the per-request
    /// <c>DbContext</c> and repositories); resolving it here — rather than
    /// holding it in a field on this singleton — avoids a captive dependency
    /// that would pin one <c>DbContext</c> for the whole application lifetime.
    /// </summary>
    private async Task<SyncImportResult> ImportFromYamlAsync(string localPath, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        ISyncService syncService = scope.ServiceProvider.GetRequiredService<ISyncService>();
        return await syncService.ImportDataFromYamlAsync(localPath, cancellationToken);
    }

    /// <summary>
    /// Exports the database to a YAML tree through a fresh DI scope. See
    /// <see cref="ImportFromYamlAsync"/> for why the scoped service is resolved
    /// per operation instead of being captured in a field.
    /// </summary>
    private async Task<SyncExportResult> ExportToYamlAsync(string localPath, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        ISyncService syncService = scope.ServiceProvider.GetRequiredService<ISyncService>();
        return await syncService.ExportDataToYamlAsync(localPath, cancellationToken);
    }

    /// <summary>
    /// Gets a localized string for the given key
    /// </summary>
    private string L(string key) => _localization.GetString(key);

    /// <summary>
    /// Gets a formatted localized string
    /// </summary>
    private string LF(string key, params object[] args) => _localization.GetFormattedString(key, args);

    /// <summary>
    /// Executes a sync operation with locking to prevent concurrent operations.
    /// Returns an error result if another operation is already in progress.
    /// Supports cancellation via CancelOperation().
    /// </summary>
    private async Task<GitOperationResult> ExecuteWithLockAsync(
        Func<CancellationToken, Task<GitOperationResult>> operation,
        string operationName)
    {
        if (!await _syncLock.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogWarning("Sync operation '{Operation}' blocked - another operation is in progress", operationName);
            return GitOperationResult.Fail(
                "A sync operation is already in progress. Please wait for it to complete.",
                GitSyncErrorCode.Unknown,
                "Concurrent sync operations are not allowed.");
        }

        // Create new cancellation token source for this operation
        CancellationTokenSource cancellationTokenSource = new();
        lock (_ctsLock)
        {
            _currentCancellationTokenSource?.Dispose();
            _currentCancellationTokenSource = cancellationTokenSource;
        }

        CancellationToken cancellationToken = cancellationTokenSource.Token;

        try
        {
            _isOperationInProgress = true;
            return await operation(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sync operation '{Operation}' was cancelled", operationName);
            RaiseStatusChanged("Operation cancelled", SyncPhase.Idle);
            return GitOperationResult.Fail(
                "Operation was cancelled.",
                GitSyncErrorCode.Cancelled,
                "The sync operation was cancelled by user request.");
        }
        finally
        {
            _isOperationInProgress = false;
            lock (_ctsLock)
            {
                if (ReferenceEquals(_currentCancellationTokenSource, cancellationTokenSource))
                {
                    _currentCancellationTokenSource = null;
                }

                cancellationTokenSource.Dispose();
            }

            _syncLock.Release();
        }
    }

    /// <summary>
    /// Cancels any currently running sync operation.
    /// </summary>
    public void CancelOperation()
    {
        CancellationTokenSource? cancellationTokenSource;
        lock (_ctsLock)
        {
            cancellationTokenSource = _currentCancellationTokenSource;
        }

        if (cancellationTokenSource == null || cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Cancellation requested for current sync operation");
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_ctsLock)
        {
            _currentCancellationTokenSource?.Dispose();
            _currentCancellationTokenSource = null;
        }

        _syncLock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Logs a sync operation to the history repository
    /// </summary>
    private async Task LogSyncOperationAsync(
        GitOperationResult result,
        string operationType,
        DateTime startedAt)
    {
        try
        {
            // Create a scope to get a scoped repository instance
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            ISyncHistoryRepository? syncHistoryRepository = scope.ServiceProvider.GetService<ISyncHistoryRepository>();

            if (syncHistoryRepository == null)
            {
                return;
            }

            SyncHistoryEntry entry = SyncHistoryEntry.FromResult(
                result,
                operationType,
                startedAt,
                GitUrlSanitizer.SanitizeForLogging(Settings?.GitRemoteUrl),
                Settings?.GitBranch);
            entry.ErrorDetails = GitUrlSanitizer.RedactCredentials(entry.ErrorDetails);

            await syncHistoryRepository.AddAsync(entry);
        }
        catch (Exception ex)
        {
            // Don't let history logging failures break the sync operation
            _logger.LogWarning(ex, "Failed to log sync operation to history");
        }
    }

    /// <summary>
    /// Raises StatusChanged event with detailed progress information
    /// </summary>
    private void RaiseStatusChanged(
        string status,
        SyncPhase? phase = null,
        double? progress = null,
        string? currentFile = null,
        int totalFiles = 0,
        int processedFiles = 0,
        string? entityType = null)
    {
        if (phase.HasValue)
        {
            _currentPhase = phase.Value;
        }

        StatusChanged?.Invoke(this, new GitSyncStatusEventArgs
        {
            Status = status,
            IsOperationInProgress = _currentPhase != SyncPhase.Idle &&
                                    _currentPhase != SyncPhase.Completed &&
                                    _currentPhase != SyncPhase.Failed,
            Phase = _currentPhase,
            Progress = progress,
            CurrentFile = currentFile,
            TotalFiles = totalFiles,
            ProcessedFiles = processedFiles,
            CurrentEntityType = entityType
        });
    }

    /// <summary>
    /// Executes an async operation with retry logic for transient failures
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken,
        Func<Exception, bool>? shouldRetry = null)
    {
        shouldRetry ??= IsTransientError;

        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts - 1 && shouldRetry(ex))
            {
                var delay = RetryDelaysMs[attempt];
                _logger.LogWarning(ex,
                    "Attempt {Attempt}/{MaxAttempts} for {Operation} failed, retrying in {Delay}ms",
                    attempt + 1, MaxRetryAttempts, operationName, delay);

                RaiseStatusChanged(
                    LF(MessageKeys.GitSyncRetrying, operationName, attempt + 2, MaxRetryAttempts),
                    phase: _currentPhase);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        // Final attempt without catch
        cancellationToken.ThrowIfCancellationRequested();
        return await operation().ConfigureAwait(false);
    }

    /// <summary>
    /// Determines if an exception represents a transient error worth retrying
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        // Network-related transient errors
        if (ex is LibGit2SharpException gitEx)
        {
            var message = gitEx.Message.ToLowerInvariant();
            return message.Contains("network") ||
                   message.Contains("connection") ||
                   message.Contains("timeout") ||
                   message.Contains("ssl") ||
                   message.Contains("tls") ||
                   message.Contains("socket") ||
                   message.Contains("temporarily unavailable");
        }

        return ex is TimeoutException ||
               ex is IOException ||
               ex is System.Net.Http.HttpRequestException;
    }

    /// <summary>
    /// Maps LibGit2Sharp exceptions to GitSyncErrorCode
    /// </summary>
    private static GitSyncErrorCode MapExceptionToErrorCode(Exception ex)
    {
        if (ex is NonFastForwardException)
            return GitSyncErrorCode.PushRejected;

        if (ex is LibGit2SharpException gitEx)
        {
            var message = gitEx.Message.ToLowerInvariant();

            if (message.Contains("authentication") || message.Contains("401") || message.Contains("403"))
                return GitSyncErrorCode.AuthenticationFailed;

            if (message.Contains("not found") || message.Contains("404") || message.Contains("does not exist"))
                return GitSyncErrorCode.RepositoryNotFound;

            if (message.Contains("network") || message.Contains("connection") || message.Contains("timeout"))
                return GitSyncErrorCode.NetworkError;

            if (message.Contains("conflict") || message.Contains("merge"))
                return GitSyncErrorCode.MergeConflict;
        }

        if (ex is IOException || ex is UnauthorizedAccessException)
            return GitSyncErrorCode.FileSystemError;

        if (ex is OperationCanceledException)
            return GitSyncErrorCode.Cancelled;

        if (ex is TimeoutException)
            return GitSyncErrorCode.Timeout;

        return GitSyncErrorCode.Unknown;
    }

    public Task<GitOperationResult> InitializeRepositoryAsync()
    {
        return ExecuteWithLockAsync(InitializeRepositoryInternalAsync, "initialize");
    }

    private async Task<GitOperationResult> InitializeRepositoryInternalAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConfigured)
        {
            var failResult = GitOperationResult.Fail(
                L(MessageKeys.GitSyncNotConfigured),
                GitSyncErrorCode.InvalidConfiguration);
            await LogSyncOperationAsync(failResult, SyncOperationType.Initialize, startedAt);
            return failResult;
        }

        UserSettings settings = Settings!;
        string localPath = settings.GitRepositoryPath!;
        string remoteUrl = settings.GitRemoteUrl!;
        bool hasToken = !string.IsNullOrWhiteSpace(settings.GitAccessToken);

        // SECURITY: Validate repository path
        if (!IsValidRepositoryPath(localPath))
        {
            var failResult = GitOperationResult.Fail(
                "Invalid repository path",
                GitSyncErrorCode.InvalidConfiguration,
                "Repository path contains invalid characters or path traversal attempts.");
            await LogSyncOperationAsync(failResult, SyncOperationType.Initialize, startedAt);
            return failResult;
        }

        if (!GitUrlValidator.IsAllowed(remoteUrl, hasToken, out string remoteUrlReason))
        {
            GitOperationResult failResult = GitOperationResult.Fail(
                "Invalid remote URL",
                GitSyncErrorCode.InvalidConfiguration,
                remoteUrlReason);
            await LogSyncOperationAsync(failResult, SyncOperationType.Initialize, startedAt);
            return failResult;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncCheckingRepository), SyncPhase.Initializing, 0);

            // Check if directory exists and is a git repo
            if (Directory.Exists(localPath))
            {
                if (Repository.IsValid(localPath))
                {
                    RaiseStatusChanged(L(MessageKeys.GitSyncRepositoryInitialized), SyncPhase.Completed, 100);
                    return GitOperationResult.Ok("Repository already exists and is valid.");
                }
                else
                {
                    // Directory exists but not a git repo - check if empty
                    if (Directory.GetFileSystemEntries(localPath).Length > 0)
                    {
                        RaiseStatusChanged(L(MessageKeys.GitSyncInvalidDirectory), SyncPhase.Failed);
                        return GitOperationResult.Fail(
                            "Directory exists but is not a Git repository and is not empty.",
                            GitSyncErrorCode.InvalidConfiguration,
                            "Please choose an empty directory or an existing Git repository.");
                    }
                }
            }
            else
            {
                Directory.CreateDirectory(localPath);
            }

            // Clone the repository with retry logic
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncCloning), SyncPhase.Initializing, 25);

            await ExecuteWithRetryAsync(async () =>
            {
                await Task.Run(() =>
                {
                    CloneOptions options = new()
                    {
                        BranchName = settings.GitBranch
                    };
                    options.FetchOptions.CredentialsProvider = GetCredentialsHandler();
                    options.FetchOptions.OnTransferProgress = _ => !cancellationToken.IsCancellationRequested;

                    Repository.Clone(remoteUrl, localPath, options);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }, "clone", cancellationToken);

            RaiseStatusChanged(L(MessageKeys.GitSyncCloneSuccess), SyncPhase.Completed, 100);
            _logger.LogInformation(
                "Repository cloned successfully from {RemoteUrl} to {LocalPath}",
                GitUrlSanitizer.SanitizeForLogging(remoteUrl),
                localPath);
            var successResult = GitOperationResult.Ok(L(MessageKeys.GitSyncCloneSuccess));
            await LogSyncOperationAsync(successResult, SyncOperationType.Initialize, startedAt);
            return successResult;
        }
        catch (UserCancelledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LibGit2SharpException ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncCloneFailed), SyncPhase.Failed);
            _logger.LogError(
                ex,
                "Failed to clone repository from {RemoteUrl}",
                GitUrlSanitizer.SanitizeForLogging(remoteUrl));
            var failResult = GitOperationResult.Fail("Failed to clone repository", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.Initialize, startedAt);
            return failResult;
        }
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncInitFailed), SyncPhase.Failed);
            _logger.LogError(ex, "Failed to initialize repository at {LocalPath}", localPath);
            var failResult = GitOperationResult.Fail("Failed to initialize repository", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.Initialize, startedAt);
            return failResult;
        }
    }

    public Task<GitOperationResult> PullAndImportAsync()
    {
        return ExecuteWithLockAsync(PullAndImportInternalAsync, "pull");
    }

    private async Task<GitOperationResult> PullAndImportInternalAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConfigured)
        {
            var failResult = GitOperationResult.Fail(L(MessageKeys.GitSyncNotConfigured), GitSyncErrorCode.InvalidConfiguration);
            await LogSyncOperationAsync(failResult, SyncOperationType.Pull, startedAt);
            return failResult;
        }

        var localPath = Settings!.GitRepositoryPath!;

        // SECURITY: Validate repository path
        if (!IsValidRepositoryPath(localPath))
        {
            var failResult = GitOperationResult.Fail(
                "Invalid repository path",
                GitSyncErrorCode.InvalidConfiguration,
                "Repository path contains invalid characters or path traversal attempts.");
            await LogSyncOperationAsync(failResult, SyncOperationType.Pull, startedAt);
            return failResult;
        }

        if (!Repository.IsValid(localPath))
        {
            // Use internal method to avoid deadlock (we already hold the lock)
            var initResult = await InitializeRepositoryInternalAsync(cancellationToken);
            if (!initResult.Success)
            {
                return initResult;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncFetching), SyncPhase.Fetching, 10);

            int commitsMerged = 0;

            // Fetch with retry logic
            await ExecuteWithRetryAsync(async () =>
            {
                await Task.Run(() =>
                {
                    using var repo = new Repository(localPath);

                    // Fetch from remote
                    var remote = repo.Network.Remotes["origin"];
                    var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);

                    Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                    {
                        CredentialsProvider = GetCredentialsHandler(),
                        OnTransferProgress = _ => !cancellationToken.IsCancellationRequested
                    }, "Fetching from origin");
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }, "fetch", cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncMerging), SyncPhase.Merging, 30);

            // Merge changes and detect conflicts
            List<string>? conflictedFiles = null;
            bool hasConflicts = false;

            await Task.Run(() =>
            {
                using var repo = new Repository(localPath);

                // Configure signature for merge commits
                var signature = GetSignature();

                // Get tracking branch
                var trackingBranch = repo.Head.TrackedBranch;
                if (trackingBranch != null)
                {
                    // Check if we're behind
                    var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(repo.Head.Tip, trackingBranch.Tip);
                    commitsMerged = divergence.BehindBy ?? 0;

                    if (commitsMerged > 0)
                    {
                        // Pull (fetch + merge) and capture result
                        var mergeResult = Commands.Pull(repo, signature, new PullOptions
                        {
                            FetchOptions = new FetchOptions
                            {
                                CredentialsProvider = GetCredentialsHandler(),
                                OnTransferProgress = _ => !cancellationToken.IsCancellationRequested
                            },
                            MergeOptions = new MergeOptions
                            {
                                FastForwardStrategy = FastForwardStrategy.Default,
                                // Don't fail on conflict, we'll handle it ourselves
                                FailOnConflict = false
                            }
                        });

                        // Check for merge conflicts
                        if (mergeResult.Status == MergeStatus.Conflicts)
                        {
                            hasConflicts = true;
                            conflictedFiles = repo.Index.Conflicts
                                .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path ?? "unknown")
                                .Distinct()
                                .ToList();

                            _logger.LogWarning("Merge conflicts detected in {Count} files: {Files}",
                                conflictedFiles.Count, string.Join(", ", conflictedFiles));
                        }
                    }
                }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // If conflicts were detected, return with conflict information
            if (hasConflicts && conflictedFiles != null && conflictedFiles.Count > 0)
            {
                RaiseStatusChanged(
                    LF(MessageKeys.SyncMergeConflict, conflictedFiles.Count),
                    SyncPhase.DetectingConflicts);

                var conflictResult = GitOperationResult.WithMergeConflicts(
                    $"Merge conflicts detected in {conflictedFiles.Count} file(s). Please resolve conflicts manually.",
                    conflictedFiles);
                await LogSyncOperationAsync(conflictResult, SyncOperationType.Pull, startedAt);
                return conflictResult;
            }

            // Import YAML files into database
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncImporting), SyncPhase.Importing, 50, entityType: "Actions");
            var importResult = await ImportFromYamlAsync(localPath, cancellationToken);

            if (importResult.Success)
            {
                var totalImported = importResult.TotalCreated + importResult.TotalUpdated;
                RaiseStatusChanged(
                    LF(MessageKeys.GitSyncImportComplete, importResult.TotalCreated, importResult.TotalUpdated),
                    SyncPhase.Completed, 100);
                _logger.LogInformation(
                    "Pull and import completed: {CommitsMerged} commits merged, {Created} created, {Updated} updated",
                    commitsMerged, importResult.TotalCreated, importResult.TotalUpdated);

                var successResult = new GitOperationResult
                {
                    Success = true,
                    Message = $"Pull completed. {commitsMerged} commits merged.",
                    ItemsImported = totalImported,
                    ItemsUpdated = importResult.TotalUpdated,
                    CommitsMerged = commitsMerged
                };
                await LogSyncOperationAsync(successResult, SyncOperationType.Pull, startedAt);
                return successResult;
            }
            else
            {
                RaiseStatusChanged(L(MessageKeys.GitSyncImportFailed), SyncPhase.Failed);
                _logger.LogWarning("Pull succeeded but import failed: {Errors}", string.Join(", ", importResult.Errors));
                var failResult = GitOperationResult.Fail(
                    "Pull succeeded but import failed",
                    GitSyncErrorCode.ValidationError,
                    string.Join(", ", importResult.Errors));
                await LogSyncOperationAsync(failResult, SyncOperationType.Pull, startedAt);
                return failResult;
            }
        }
        catch (UserCancelledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LibGit2SharpException ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncPullFailed), SyncPhase.Failed);
            _logger.LogError(ex, "Failed to pull changes from remote");
            var failResult = GitOperationResult.Fail("Failed to pull changes", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.Pull, startedAt);
            return failResult;
        }
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncSyncFailed), SyncPhase.Failed);
            _logger.LogError(ex, "Failed to sync repository");
            var failResult = GitOperationResult.Fail("Failed to sync", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.Pull, startedAt);
            return failResult;
        }
    }

    public Task<GitOperationResult> ExportAndPushAsync(string? commitMessage = null)
    {
        return ExecuteWithLockAsync(cancellationToken => ExportAndPushInternalAsync(commitMessage, cancellationToken), "push");
    }

    private async Task<GitOperationResult> ExportAndPushInternalAsync(string? commitMessage, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConfigured)
        {
            var failResult = GitOperationResult.Fail(L(MessageKeys.GitSyncNotConfigured), GitSyncErrorCode.InvalidConfiguration);
            await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
            return failResult;
        }

        var localPath = Settings!.GitRepositoryPath!;

        // SECURITY: Validate repository path
        if (!IsValidRepositoryPath(localPath))
        {
            var failResult = GitOperationResult.Fail(
                "Invalid repository path",
                GitSyncErrorCode.InvalidConfiguration,
                "Repository path contains invalid characters or path traversal attempts.");
            await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
            return failResult;
        }

        if (!Repository.IsValid(localPath))
        {
            var failResult = GitOperationResult.Fail(
                L(MessageKeys.GitSyncRepositoryNotInitialized),
                GitSyncErrorCode.RepositoryNotInitialized);
            await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
            return failResult;
        }

        try
        {
            // Export database to YAML
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncExporting), SyncPhase.Exporting, 10, entityType: "Actions");
            var exportResult = await ExportToYamlAsync(localPath, cancellationToken);

            if (!exportResult.Success)
            {
                RaiseStatusChanged(L(MessageKeys.GitSyncExportFailed), SyncPhase.Failed);
                _logger.LogWarning("Failed to export data: {Errors}", string.Join(", ", exportResult.Errors));
                var failResult = GitOperationResult.Fail(
                    "Failed to export data",
                    GitSyncErrorCode.FileSystemError,
                    string.Join(", ", exportResult.Errors));
                await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
                return failResult;
            }

            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncStaging), SyncPhase.Staging, 40);

            bool hasChanges = false;

            await Task.Run(() =>
            {
                using var repo = new Repository(localPath);

                // Stage all changes
                Commands.Stage(repo, "*");

                // Check if there are changes to commit
                var status = repo.RetrieveStatus();
                hasChanges = status.IsDirty;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!hasChanges)
            {
                RaiseStatusChanged(L(MessageKeys.GitSyncNoChanges), SyncPhase.Completed, 100);
                _logger.LogInformation("No changes to push - repository is up to date");
                var noChangesResult = new GitOperationResult
                {
                    Success = true,
                    Message = "No changes to push. Everything is up to date.",
                    ItemsExported = exportResult.TotalExported
                };
                await LogSyncOperationAsync(noChangesResult, SyncOperationType.Push, startedAt);
                return noChangesResult;
            }

            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncCommitting), SyncPhase.Committing, 60);

            await Task.Run(() =>
            {
                using var repo = new Repository(localPath);

                var signature = GetSignature();
                var message = commitMessage ?? $"TwinShell sync: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                repo.Commit(message, signature, signature);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Push to remote with retry logic
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncPushing), SyncPhase.Pushing, 80);

            await ExecuteWithRetryAsync(async () =>
            {
                await Task.Run(() =>
                {
                    using var repo = new Repository(localPath);

                    var remote = repo.Network.Remotes["origin"];
                    var pushRefSpec = $"refs/heads/{Settings.GitBranch}";

                    repo.Network.Push(remote, pushRefSpec, new PushOptions
                    {
                        CredentialsProvider = GetCredentialsHandler(),
                        OnPushTransferProgress = (_, _, _) => !cancellationToken.IsCancellationRequested
                    });
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }, "push", cancellationToken);

            RaiseStatusChanged(LF(MessageKeys.GitSyncPushSuccess, exportResult.TotalExported), SyncPhase.Completed, 100);
            _logger.LogInformation("Successfully pushed {ItemCount} items to remote", exportResult.TotalExported);
            var successResult = new GitOperationResult
            {
                Success = true,
                Message = "Changes pushed successfully.",
                ItemsExported = exportResult.TotalExported
            };
            await LogSyncOperationAsync(successResult, SyncOperationType.Push, startedAt);
            return successResult;
        }
        catch (UserCancelledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NonFastForwardException ex)
        {
            RaiseStatusChanged(L(MessageKeys.GitSyncPushRejected), SyncPhase.Failed);
            _logger.LogWarning(ex, "Push rejected - remote has changes that need to be pulled first");
            var failResult = GitOperationResult.Fail(
                "Push rejected. Remote has changes that need to be pulled first.",
                GitSyncErrorCode.PushRejected,
                "Please pull changes before pushing.");
            await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
            return failResult;
        }
        catch (LibGit2SharpException ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncPushFailed), SyncPhase.Failed);
            _logger.LogError(ex, "Export succeeded but push failed");
            var failResult = GitOperationResult.Fail(
                "Export succeeded but push failed. You can push manually with git push.",
                errorCode,
                ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
            return failResult;
        }
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncPushFailed), SyncPhase.Failed);
            _logger.LogError(ex, "Failed to export and push");
            var failResult = GitOperationResult.Fail($"Failed to export and push: {ex.GetType().Name}", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.Push, startedAt);
            return failResult;
        }
    }

    public Task<GitOperationResult> FullSyncAsync()
    {
        return ExecuteWithLockAsync(FullSyncInternalAsync, "full-sync");
    }

    private async Task<GitOperationResult> FullSyncInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Starting full sync operation");

        // First pull and import (use internal method to avoid deadlock)
        var pullResult = await PullAndImportInternalAsync(cancellationToken);
        if (!pullResult.Success)
        {
            _logger.LogWarning("Full sync aborted - pull failed with error code {ErrorCode}: {Message}",
                pullResult.ErrorCode, pullResult.Message);
            return pullResult;
        }

        // Then export and push (if auto-push enabled)
        if (Settings?.GitAutoPush == true)
        {
            // Use internal method to avoid deadlock
            cancellationToken.ThrowIfCancellationRequested();
            var pushResult = await ExportAndPushInternalAsync(null, cancellationToken);

            // Consider sync successful if pull succeeded, even if push had issues
            // (push failures are usually credential issues, not data issues)
            var message = pushResult.Success
                ? $"Sync complete. {pullResult.ItemsImported} imported, {pushResult.ItemsExported} exported."
                : $"Sync complete. {pullResult.ItemsImported} imported, {pushResult.ItemsExported} exported. Push failed: {pushResult.ErrorDetails}";

            _logger.LogInformation(
                "Full sync completed: {Imported} imported, {Exported} exported, {Merged} commits merged, push success: {PushSuccess}",
                pullResult.ItemsImported, pushResult.ItemsExported, pullResult.CommitsMerged, pushResult.Success);

            return new GitOperationResult
            {
                Success = pushResult.Success,
                Message = message,
                ErrorCode = pushResult.Success ? GitSyncErrorCode.None : pushResult.ErrorCode,
                ErrorDetails = pushResult.Success ? null : pushResult.ErrorDetails,
                ItemsImported = pullResult.ItemsImported,
                ItemsUpdated = pullResult.ItemsUpdated,
                ItemsExported = pushResult.ItemsExported,
                CommitsMerged = pullResult.CommitsMerged,
                Warnings = pushResult.Success ? new List<string>() : new List<string> { pushResult.Message }
            };
        }

        _logger.LogInformation("Full sync completed (pull only): {Imported} imported, {Merged} commits merged",
            pullResult.ItemsImported, pullResult.CommitsMerged);
        return pullResult;
    }

    public Task<GitOperationResult> TestConnectionAsync()
    {
        return ExecuteWithLockAsync(TestConnectionInternalAsync, "test-connection");
    }

    private async Task<GitOperationResult> TestConnectionInternalAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(Settings?.GitRemoteUrl))
        {
            var failResult = GitOperationResult.Fail(L(MessageKeys.GitSyncNotConfigured), GitSyncErrorCode.InvalidConfiguration);
            await LogSyncOperationAsync(failResult, SyncOperationType.TestConnection, startedAt);
            return failResult;
        }

        UserSettings settings = Settings!;
        string remoteUrl = settings.GitRemoteUrl!;
        bool hasToken = !string.IsNullOrWhiteSpace(settings.GitAccessToken);
        if (!GitUrlValidator.IsAllowed(remoteUrl, hasToken, out string remoteUrlReason))
        {
            GitOperationResult failResult = GitOperationResult.Fail(
                "Invalid remote URL",
                GitSyncErrorCode.InvalidConfiguration,
                remoteUrlReason);
            await LogSyncOperationAsync(failResult, SyncOperationType.TestConnection, startedAt);
            return failResult;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged(L(MessageKeys.GitSyncTestingConnection), SyncPhase.Validating, 50);
            _logger.LogDebug(
                "Testing connection to {RemoteUrl}",
                GitUrlSanitizer.SanitizeForLogging(remoteUrl));

            // Test connection with retry logic
            await ExecuteWithRetryAsync(async () =>
            {
                await Task.Run(() =>
                {
                    // Try to list remote references to test connection
                    IEnumerable<Reference> refs = Repository.ListRemoteReferences(remoteUrl, GetCredentialsHandler());
                    int count = refs.Count();
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }, "connection test", cancellationToken);

            RaiseStatusChanged(L(MessageKeys.GitSyncConnectionSuccess), SyncPhase.Completed, 100);
            _logger.LogInformation(
                "Connection test successful for {RemoteUrl}",
                GitUrlSanitizer.SanitizeForLogging(remoteUrl));
            var successResult = GitOperationResult.Ok("Connection to remote repository successful.");
            await LogSyncOperationAsync(successResult, SyncOperationType.TestConnection, startedAt);
            return successResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LibGit2SharpException ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncConnectionFailed), SyncPhase.Failed);
            _logger.LogWarning(
                ex,
                "Failed to connect to remote repository {RemoteUrl}",
                GitUrlSanitizer.SanitizeForLogging(remoteUrl));
            var failResult = GitOperationResult.Fail("Failed to connect to remote repository", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.TestConnection, startedAt);
            return failResult;
        }
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            RaiseStatusChanged(L(MessageKeys.GitSyncConnectionFailed), SyncPhase.Failed);
            _logger.LogError(
                ex,
                "Connection test failed for {RemoteUrl}",
                GitUrlSanitizer.SanitizeForLogging(remoteUrl));
            var failResult = GitOperationResult.Fail("Connection test failed", errorCode, ex.Message);
            await LogSyncOperationAsync(failResult, SyncOperationType.TestConnection, startedAt);
            return failResult;
        }
    }

    public async Task<GitRepositoryStatus> GetRepositoryStatusAsync()
    {
        var status = new GitRepositoryStatus
        {
            RemoteUrl = Settings?.GitRemoteUrl
        };

        if (!IsConfigured || string.IsNullOrWhiteSpace(Settings?.GitRepositoryPath))
        {
            _logger.LogDebug("Repository not configured, returning empty status");
            return status;
        }

        var localPath = Settings.GitRepositoryPath;

        if (!Repository.IsValid(localPath))
        {
            _logger.LogDebug("Repository at {LocalPath} is not valid, returning empty status", localPath);
            return status;
        }

        try
        {
            await Task.Run(() =>
            {
                using var repo = new Repository(localPath);

                status.IsInitialized = true;
                status.CurrentBranch = repo.Head.FriendlyName;
                status.HasLocalChanges = repo.RetrieveStatus().IsDirty;

                if (repo.Head.Tip != null)
                {
                    status.LastCommitMessage = repo.Head.Tip.MessageShort;
                    status.LastSyncTime = repo.Head.Tip.Author.When.DateTime;
                }

                var trackingBranch = repo.Head.TrackedBranch;
                if (trackingBranch != null)
                {
                    var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(
                        repo.Head.Tip, trackingBranch.Tip);
                    status.CommitsAhead = divergence.AheadBy ?? 0;
                    status.CommitsBehind = divergence.BehindBy ?? 0;
                }
            });

            _logger.LogDebug(
                "Repository status: Branch={Branch}, Ahead={Ahead}, Behind={Behind}, HasChanges={HasChanges}",
                status.CurrentBranch, status.CommitsAhead, status.CommitsBehind, status.HasLocalChanges);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            // Git operation failed - return partial status without divergence info
            _logger.LogWarning(ex, "Failed to retrieve full repository status, returning partial status");
        }

        return status;
    }

    private CredentialsHandler GetCredentialsHandler()
    {
        return (url, usernameFromUrl, types) =>
        {
            // HTTPS with token - most common case
            // SECURITY NOTE: LibGit2Sharp doesn't support SecureString, so we minimize
            // token exposure by not storing intermediate references. The token is retrieved
            // directly from the encrypted settings and passed to LibGit2Sharp.
            // Token is cleared from memory when UsernamePasswordCredentials is disposed.
            var token = Settings?.GitAccessToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                return new UsernamePasswordCredentials
                {
                    Username = Settings?.GitUserName ?? usernameFromUrl ?? "git",
                    Password = token
                };
            }

            // If no token configured, use default credentials (will work for public repos or system SSH agent)
            return new DefaultCredentials();
        };
    }

    private Signature GetSignature()
    {
        var name = Settings?.GitUserName ?? "TwinShell User";
        var email = Settings?.GitUserEmail ?? "twinshell@local";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    /// <summary>
    /// Validates a repository path for security
    /// </summary>
    private static bool IsValidRepositoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // SECURITY: Check for path traversal attempts
        if (path.Contains("..") || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        // Ensure path doesn't contain dangerous characters
        var invalidChars = Path.GetInvalidPathChars();
        if (path.Any(c => invalidChars.Contains(c)))
        {
            return false;
        }

        return true;
    }
}
