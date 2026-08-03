/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.Utilities;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel for the embedded SFTP/FTP file browser. Owns directory and
/// navigation state, the listing, filtering and sorting, file operations
/// (create / rename / delete / chmod), transfer orchestration with sudo
/// fallbacks, and status. The partner view <c>EmbeddedSftpView</c> retains only
/// view-coupled wiring (see its remarks).
/// </summary>
public sealed partial class EmbeddedSftpViewModel : ObservableObject
{
    internal enum SftpDownloadOutcome
    {
        Completed,
        CompletedWithSkippedDirectories,
        OnlyDirectoriesSkipped,
        Empty
    }

    private enum TransferStartState
    {
        Started,
        Busy,
        Unavailable
    }

    /// <summary>Outcome of a planned upload run, including sources and destinations refused up front.</summary>
    private readonly record struct UploadPlanOutcome(
        bool Completed,
        IReadOnlyList<string> SkippedUnsupportedTargets,
        IReadOnlyList<string> SkippedLocalReparsePoints);

    private const string SudoStderrTerminalRequired = "a terminal is required";
    private const string SudoStderrNoTtyPresent = "no tty present";
    private const string SudoStderrNoAskpass = "no askpass";
    private const string SudoStderrPasswordRequired = "a password is required";
    private const string SudoStderrIncorrectPasswordAttempt = "incorrect password attempt";
    private const string SudoStderrSorryTryAgain = "sorry, try again";
    private const string SudoStderrNoPasswordProvided = "no password was provided";
    private static readonly TimeSpan ErrorHighlightDuration = TimeSpan.FromSeconds(5);

    private readonly Stack<string> _navigationHistory = new();
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IRemoteClipboardService _remoteClipboard;
    private readonly IFileConflictDialogPresenter _fileConflictDialogPresenter;
    private Func<string, CancellationToken, Task<SudoRenameCommandResult>>? _sudoRenameCommandExecutor;
    private Func<string, CancellationToken, Task>? _sudoDeleteCommandExecutor = null;
    private IRemoteBrowser? _browser;
    // Emits operation records for the SFTP sudo fallbacks (which bypass the decorated browser).
    private SessionOperationEmitter _sudoEmitter = SessionOperationEmitter.Disabled;
    private SshConnectionParams? _sshParams;
    private HostKeyStore _hostKeyStore = null!;
    private IHostKeyVerifier _hostKeyVerifier = null!;
    private LocalizationManager? _localizer;
    private IDialogService? _dialogService;
    private System.Threading.Timer? _errorHighlightTimer;
    private readonly object _lifecycleCtsGate = new();
    private readonly object _transferCtsGate = new();
    private CancellationTokenSource? _lifecycleCts = new();
    private CancellationTokenSource? _transferCts;
    private string _endpointKey = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedSftpViewModel"/> class.
    /// </summary>
    public EmbeddedSftpViewModel(IUiDispatcher uiDispatcher)
        : this(uiDispatcher, new RemoteClipboardService())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedSftpViewModel"/> class.
    /// </summary>
    public EmbeddedSftpViewModel(
        IUiDispatcher uiDispatcher,
        IRemoteClipboardService remoteClipboard)
        : this(uiDispatcher, remoteClipboard, new WpfFileConflictDialogPresenter())
    {
    }

    internal EmbeddedSftpViewModel(
        IUiDispatcher uiDispatcher,
        IRemoteClipboardService remoteClipboard,
        IFileConflictDialogPresenter fileConflictDialogPresenter)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _remoteClipboard = remoteClipboard ?? throw new ArgumentNullException(nameof(remoteClipboard));
        _fileConflictDialogPresenter = fileConflictDialogPresenter
            ?? throw new ArgumentNullException(nameof(fileConflictDialogPresenter));
        _remoteClipboard.Changed += OnRemoteClipboardChanged;
        Files = [];
        Bookmarks = [];
        UnfilteredEntries = [];
        HomeDirectory = "/";
    }

    /// <summary>The current remote directory path.</summary>
    [ObservableProperty]
    private string _currentPath = "/";

    /// <summary>The editable path bar text mirrored from the current path.</summary>
    [ObservableProperty]
    private string _pathBarText = "/";

    /// <summary>Whether backward navigation is available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    private bool _canGoBack;

    /// <summary>Whether a remote directory listing is currently running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolbarEnabled))]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    private bool _isLoading;

    /// <summary>The current status text displayed by the view.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Whether the current status represents an error (for view-side styling).</summary>
    [ObservableProperty]
    private bool _isErrorStatus;

    /// <summary>Whether the current error status should be visually highlighted.</summary>
    [ObservableProperty]
    private bool _isErrorHighlighted;

    /// <summary>Whether the active session has a persistent security notice.</summary>
    [ObservableProperty]
    private bool _isSecurityNoticeVisible;

    /// <summary>Localized text shown in the persistent security notice badge.</summary>
    [ObservableProperty]
    private string _securityNoticeText = string.Empty;

    /// <summary>Whether a file transfer is currently running.</summary>
    [ObservableProperty]
    private bool _isTransferInProgress;

    /// <summary>The current transfer progress label.</summary>
    [ObservableProperty]
    private string _transferStatusText = string.Empty;

    /// <summary>The current transfer progress percentage.</summary>
    [ObservableProperty]
    private double _transferProgressValue;

    /// <summary>Whether hidden entries should be shown.</summary>
    [ObservableProperty]
    private bool _showHidden = true;

    /// <summary>Whether sudo directory listing mode is enabled.</summary>
    [ObservableProperty]
    private bool _sudoMode;

    /// <summary>The active sort column name.</summary>
    [ObservableProperty]
    private string _sortColumn = "Name";

    /// <summary>The active sort direction.</summary>
    [ObservableProperty]
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    /// <summary>The text shown in the item counter area.</summary>
    [ObservableProperty]
    private string _itemCountText = string.Empty;

    /// <summary>The text shown for the current selection summary.</summary>
    [ObservableProperty]
    private string _selectionInfoText = string.Empty;

    /// <summary>Whether the empty-directory overlay should be visible.</summary>
    [ObservableProperty]
    private bool _showEmptyDirectory;

    /// <summary>Whether the remote browser is currently connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolbarEnabled))]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(HasClipboard))]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    private bool _isConnected;

    /// <summary>The current text filter applied to file names.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnCurrentPathChanged(string value) => PathBarText = value;

    public bool IsToolbarEnabled => !IsLoading && IsConnected;

    public bool CanNavigateBack => !IsLoading && IsConnected && CanGoBack;

    public bool IsDisconnected => !IsConnected;

    /// <summary>Gets or sets whether native rename resolves symbolic-link sources to their targets.</summary>
    internal bool RenameFollowsSymlinkTarget { get; set; }

    partial void OnIsErrorStatusChanged(bool value)
    {
        if (value)
        {
            IsErrorHighlighted = true;
            ArmErrorHighlightTimer();
        }
        else
        {
            DisposeErrorHighlightTimer();
            IsErrorHighlighted = false;
        }
    }

    /// <summary>The currently visible remote entries.</summary>
    public ObservableCollection<SftpFileInfo> Files { get; }

    /// <summary>The remote home directory captured during initialization.</summary>
    public string HomeDirectory { get; private set; }

    /// <summary>The owning session tab for status synchronization.</summary>
    public SessionTabViewModel? SessionTab { get; private set; }

    /// <summary>Bookmarks associated with the remote browser.</summary>
    public List<string> Bookmarks { get; }

    /// <summary>The full unfiltered listing for the current directory.</summary>
    public List<SftpFileInfo> UnfilteredEntries { get; internal set; }

    /// <summary>The primary selected remote entry.</summary>
    public SftpFileInfo? SelectedFile { get; private set; }

    /// <summary>The selected remote entries.</summary>
    public IReadOnlyList<SftpFileInfo> SelectedFiles { get; private set; } = [];

    /// <summary>The normalized host:port:user key for the active remote endpoint.</summary>
    public string EndpointKey => _endpointKey;

    /// <summary>
    /// Raised when the user requests a split action from the embedded view.
    /// </summary>
    public event Action? SplitRequested;

    /// <summary>
    /// Raised when the user requests opening a path in the terminal.
    /// </summary>
    public event Action<string>? OpenInTerminalRequested;

    /// <summary>
    /// Stores session-scoped dependencies used by the view model.
    /// </summary>
    public void Initialize(
        IRemoteBrowser browser,
        SessionTabViewModel sessionTab,
        string displayName,
        string endpoint,
        LocalizationManager localizer,
        IDialogService dialogService,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        SshConnectionParams? sshParams = null,
        ISessionOperationLog? operationLog = null,
        Func<bool>? sessionLoggingEnabledProvider = null,
        string? operationProtocol = null,
        string? operationHost = null,
        bool? sessionLoggingOverride = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        bool firstInitialization = _browser is null;
        _browser = browser;
        _sudoEmitter = operationLog is not null
            && !string.IsNullOrWhiteSpace(operationProtocol)
            && !string.IsNullOrWhiteSpace(operationHost)
            ? new SessionOperationEmitter(
                operationLog,
                sessionLoggingEnabledProvider ?? (static () => false),
                operationProtocol,
                operationHost,
                sessionLoggingOverride)
            : SessionOperationEmitter.Disabled;
        SessionTab = sessionTab;
        _localizer = localizer;
        _dialogService = dialogService;
        _sshParams = sshParams;
        _hostKeyStore = hostKeyStore;
        _hostKeyVerifier = hostKeyVerifier;
        SetEndpointKey(RemoteClipboardEndpointKey.FromConnection(browser, endpoint, sshParams));

        if (firstInitialization)
        {
            HomeDirectory = browser.CurrentDirectory;
            CurrentPath = browser.CurrentDirectory;
        }

        IsConnected = true;
        UpdateStatus(_localizer["SftpStatusConnected"]);
    }

    /// <summary>
    /// Updates the dialog service instance used by the view model.
    /// </summary>
    internal void SetDialogService(IDialogService? dialogService)
    {
        _dialogService = dialogService;
    }

    /// <summary>Overrides the privileged rename command channel for focused tests.</summary>
    internal void SetSudoRenameCommandExecutor(
        Func<string, CancellationToken, Task<SudoRenameCommandResult>> commandExecutor)
    {
        _sudoRenameCommandExecutor = commandExecutor
            ?? throw new ArgumentNullException(nameof(commandExecutor));
    }

    /// <summary>
    /// Marks the view model as disposed so future async operations short-circuit.
    /// </summary>
    internal void MarkDisposed()
    {
        _disposed = true;
        DisposeErrorHighlightTimer();
        lock (_lifecycleCtsGate)
        {
            _lifecycleCts?.Cancel();
            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
        }

        lock (_transferCtsGate)
        {
            _transferCts?.Cancel();
            _transferCts?.Dispose();
            _transferCts = null;
            IsTransferInProgress = false;
        }

        IsConnected = false;
        _remoteClipboard.Changed -= OnRemoteClipboardChanged;
    }

    private bool TryCaptureLifecycleToken(out CancellationToken token)
    {
        lock (_lifecycleCtsGate)
        {
            if (_disposed || _lifecycleCts is null)
            {
                token = CancellationToken.None;
                return false;
            }

            try
            {
                token = _lifecycleCts.Token;
                return true;
            }
            catch (ObjectDisposedException)
            {
                token = CancellationToken.None;
                return false;
            }
        }
    }

    private TransferStartState TryBeginTransfer(out CancellationTokenSource? transferCts)
    {
        lock (_transferCtsGate)
        {
            transferCts = null;
            if (_disposed || _browser is null)
            {
                return TransferStartState.Unavailable;
            }

            if (IsTransferInProgress)
            {
                return TransferStartState.Busy;
            }

            _transferCts?.Cancel();
            _transferCts?.Dispose();
            _transferCts = new CancellationTokenSource();
            transferCts = _transferCts;
            IsTransferInProgress = true;
            return TransferStartState.Started;
        }
    }

    private void CompleteTransfer(CancellationTokenSource transferCts)
    {
        lock (_transferCtsGate)
        {
            if (ReferenceEquals(_transferCts, transferCts))
            {
                _transferCts = null;
            }

            transferCts.Dispose();
            IsTransferInProgress = false;
            TransferProgressValue = 0;
        }
    }

    /// <summary>
    /// Loads a remote directory listing and updates the filtered file view.
    /// </summary>
    public Task LoadDirectoryAsync(string path)
    {
        return LoadDirectoryCoreAsync(path, pushToHistory: true);
    }

    /// <summary>
    /// Applies the current hidden-file, text filter, and sort settings.
    /// </summary>
    public void ApplyFilterAndSort()
    {
        IEnumerable<SftpFileInfo> filtered = UnfilteredEntries;

        if (!ShowHidden)
        {
            filtered = filtered.Where(f => !f.Name.StartsWith('.'));
        }

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            filtered = filtered.Where(f =>
                f.Name.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<SftpFileInfo> sorted = SortColumn switch
        {
            "Size" => SortDirection == ListSortDirection.Ascending
                ? filtered.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Size)
                : filtered.OrderByDescending(f => f.IsDirectory).ThenByDescending(f => f.Size),
            "Modified" => SortDirection == ListSortDirection.Ascending
                ? filtered.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.LastModified)
                : filtered.OrderByDescending(f => f.IsDirectory).ThenByDescending(f => f.LastModified),
            "Permissions" => SortDirection == ListSortDirection.Ascending
                ? filtered.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Permissions)
                : filtered.OrderByDescending(f => f.IsDirectory).ThenByDescending(f => f.Permissions),
            "Owner" => SortDirection == ListSortDirection.Ascending
                ? filtered.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Owner)
                : filtered.OrderByDescending(f => f.IsDirectory).ThenByDescending(f => f.Owner),
            _ => SortDirection == ListSortDirection.Ascending
                ? filtered.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(f => f.IsDirectory).ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };

        Files.Clear();
        foreach (var entry in sorted)
        {
            Files.Add(entry);
        }

        ShowEmptyDirectory = Files.Count == 0;

        int totalCount = UnfilteredEntries.Count;
        int visibleCount = Files.Count;
        ItemCountText = visibleCount == totalCount
            ? _localizer?.Format("SftpItemCount", totalCount.ToString()) ?? $"{totalCount} items"
            : _localizer?.Format(
                "SftpItemCountFiltered",
                visibleCount.ToString(),
                totalCount.ToString()) ?? $"{visibleCount}/{totalCount} items";
    }

    /// <summary>
    /// Navigates to the previous directory in history.
    /// </summary>
    [RelayCommand]
    public Task NavigateBack()
    {
        return _navigationHistory.TryPop(out var previousPath)
            ? LoadDirectoryCoreAsync(previousPath, pushToHistory: false)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Navigates to the parent directory of the current path.
    /// </summary>
    [RelayCommand]
    public Task NavigateUp()
    {
        string parent = GetParentPath(CurrentPath);
        return string.Equals(parent, CurrentPath, StringComparison.Ordinal)
            ? Task.CompletedTask
            : LoadDirectoryCoreAsync(parent, pushToHistory: true);
    }

    /// <summary>
    /// Navigates to the captured home directory.
    /// </summary>
    [RelayCommand]
    public Task NavigateHome()
    {
        return LoadDirectoryCoreAsync(HomeDirectory, pushToHistory: true);
    }

    /// <summary>
    /// Reloads the current directory without pushing navigation history.
    /// </summary>
    [RelayCommand]
    public Task Refresh()
    {
        return LoadDirectoryCoreAsync(CurrentPath, pushToHistory: false);
    }

    /// <summary>
    /// Navigates to the path entered in the path bar.
    /// </summary>
    public Task NavigateToPath(string? path)
    {
        return NavigateToPathCore(path, redactPathInLogs: false);
    }

    /// <summary>
    /// Navigates to a remote path that originated from terminal output. The path
    /// is still surfaced through the normal SFTP status path, but logs stay
    /// redacted because terminal cwd values are untrusted remote input.
    /// </summary>
    internal Task NavigateToUntrustedPath(string? path)
    {
        return NavigateToPathCore(path, redactPathInLogs: true);
    }

    private Task NavigateToPathCore(string? path, bool redactPathInLogs)
    {
        return string.IsNullOrWhiteSpace(path)
            ? Task.CompletedTask
            : LoadDirectoryCoreAsync(path.Trim(), pushToHistory: true, redactPathInLogs: redactPathInLogs);
    }

    /// <summary>
    /// Loads the first directory for a newly mounted SFTP view.
    /// </summary>
    public async Task NavigateInitialAsync(string? preferredPath)
    {
        string homePath = HomeDirectory;
        string? targetPath = string.IsNullOrWhiteSpace(preferredPath)
            ? null
            : preferredPath.Trim();

        if (!string.IsNullOrWhiteSpace(targetPath)
            && !string.Equals(targetPath, homePath, StringComparison.Ordinal))
        {
            await LoadDirectoryCoreAsync(
                targetPath,
                pushToHistory: false,
                suppressErrorStatus: true).ConfigureAwait(false);
            if (string.Equals(CurrentPath, targetPath, StringComparison.Ordinal))
            {
                return;
            }

            Core.Logging.FileLogger.Info(
                $"EmbeddedSFTP initial path restore failed for '{targetPath}', falling back to '{homePath}'.");
        }

        await LoadDirectoryCoreAsync(homePath, pushToHistory: false).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task GoToPath() => NavigateToPath(PathBarText);

    /// <summary>
    /// Handles double-click behavior for a listed remote entry.
    /// </summary>
    public bool HandleFileDoubleClick(SftpFileInfo file)
    {
        if (!file.IsDirectory)
        {
            return false;
        }

        _ = LoadDirectoryAsync(file.FullPath);
        return true;
    }

    /// <summary>
    /// Updates the selection summary text for the current selection.
    /// </summary>
    public void UpdateSelectionInfo(IReadOnlyList<SftpFileInfo> selectedFiles)
    {
        if (selectedFiles.Count <= 1)
        {
            SelectionInfoText = string.Empty;
            return;
        }

        long totalSize = selectedFiles
            .Where(f => !f.IsDirectory)
            .Sum(f => f.Size);

        SelectionInfoText = _localizer?.Format("SftpSelectedCount", selectedFiles.Count.ToString())
            ?? $"{selectedFiles.Count} selected";

        if (totalSize > 0)
        {
            SelectionInfoText += $" ({FormatSize(totalSize)})";
        }
    }

    /// <summary>
    /// Stores the current file-list selection and updates its summary text.
    /// </summary>
    public void SetSelection(IReadOnlyList<SftpFileInfo> selected, SftpFileInfo? primary)
    {
        SelectedFiles = selected;
        SelectedFile = primary;
        UpdateSelectionInfo(selected);
    }

    [RelayCommand]
    private Task RenameSelected()
    {
        return SelectedFile is { } file ? RenameEntryAsync(file) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task DeleteSelected()
    {
        return DeleteEntriesAsync(SelectedFiles);
    }

    [RelayCommand]
    private Task ChmodSelected()
    {
        return ChmodEntriesAsync(SelectedFiles);
    }

    [RelayCommand]
    private void ShowSelectedProperties()
    {
        if (SelectedFile is { } file)
        {
            ShowProperties(file);
        }
    }

    [RelayCommand]
    private void OpenSelectedInTerminal()
    {
        string targetDir = CurrentPath;
        if (SelectedFile is { IsDirectory: true } directory)
        {
            targetDir = directory.FullPath;
        }

        RequestOpenInTerminal(targetDir);
    }

    /// <summary>
    /// Updates the active sort column and applies the new sort order.
    /// </summary>
    public void ToggleSortColumn(string columnName)
    {
        if (string.Equals(SortColumn, columnName, StringComparison.Ordinal))
        {
            SortDirection = SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            SortColumn = columnName;
            SortDirection = ListSortDirection.Ascending;
        }

        ApplyFilterAndSort();
    }

    /// <summary>
    /// Toggles sudo listing mode and refreshes the current directory.
    /// </summary>
    [RelayCommand]
    public async Task ToggleSudoMode()
    {
        await RunOnUiAsync(() =>
        {
            SudoMode = !SudoMode;
            UpdateStatus(SudoMode
                ? (_localizer?["SftpSudoModeEnabled"] ?? "Sudo mode enabled — browsing as root")
                : (_localizer?["SftpSudoModeDisabled"] ?? "Sudo mode disabled"));
        });

        await Refresh().ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the shared status text and session connection state.
    /// </summary>
    public void UpdateStatus(string text)
    {
        StatusText = text;
        IsErrorStatus = false;
        IsConnected = _browser?.IsConnected == true;

        if (SessionTab is not null)
        {
            SessionTab.Status = IsConnected ? "Connected" : "Disconnected";
        }
    }

    /// <summary>
    /// Shows an already-localized non-blocking operation warning without marking the operation as failed.
    /// </summary>
    public void ShowOperationWarning(string message)
    {
        UpdateStatus(message);
    }

    /// <summary>
    /// Shows a persistent security notice for the active browser session.
    /// </summary>
    public void ShowSecurityNotice(string message)
    {
        SecurityNoticeText = message;
        IsSecurityNoticeVisible = true;
    }

    /// <summary>
    /// Sets an error status message and returns it for caller-side styling.
    /// </summary>
    public string SetErrorStatus(string message)
    {
        StatusText = message;
        IsErrorStatus = true;
        IsConnected = _browser?.IsConnected == true;

        if (SessionTab is not null)
        {
            SessionTab.Status = IsConnected ? "Connected" : "Disconnected";
        }

        return message;
    }

    /// <summary>
    /// Returns a localized transfer error message while keeping raw exception details in logs.
    /// </summary>
    public string DescribeTransferError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is SudoAuthenticationException sudoException)
        {
            return GetSudoAuthenticationErrorMessage(sudoException.Kind);
        }

        if (ex is SudoEditFileTooLargeException tooLargeException)
        {
            string fileSize = FormatSize(tooLargeException.FileSizeBytes);
            string maxSize = FormatSize(tooLargeException.MaxSizeBytes);
            return _localizer?.Format("SftpErrorSudoEditFileTooLarge", fileSize, maxSize)
                ?? "SftpErrorSudoEditFileTooLarge";
        }

        if (ex is LocalUploadFileValidationException localUploadException)
        {
            return localUploadException.Failure switch
            {
                LocalUploadFileValidationFailure.Missing =>
                    L10n("SftpErrorLocalUploadFileMissing"),
                LocalUploadFileValidationFailure.NotRegularFile =>
                    L10n("SftpErrorLocalUploadNotRegularFile"),
                _ => L10n("SftpErrorUnknown"),
            };
        }

        if (ex is RemoteUploadTargetUnsupportedException)
        {
            return L10n("SftpErrorRemoteUploadTargetNotRegularFile");
        }

        Core.Logging.FileLogger.Warn(
            $"EmbeddedSFTP transfer failed [{ex.GetType().Name}]: {ex.Message}");
        return L10n("SftpStatusTransferFailed");
    }

    /// <summary>
    /// Sets a localized transfer error status, including typed sudo authentication failures.
    /// </summary>
    public string SetTransferError(Exception ex)
    {
        return SetErrorStatus(DescribeTransferError(ex));
    }

    /// <summary>
    /// Raises the split request event.
    /// </summary>
    [RelayCommand]
    public void RequestSplit()
    {
        SplitRequested?.Invoke();
    }

    /// <summary>
    /// Raises the open-in-terminal request event.
    /// </summary>
    public void RequestOpenInTerminal(string path)
    {
        OpenInTerminalRequested?.Invoke(path);
    }

    /// <summary>
    /// Adds the current path to bookmarks if not already present.
    /// </summary>
    [RelayCommand]
    public void AddBookmark()
    {
        if (Bookmarks.Contains(CurrentPath))
        {
            return;
        }

        Bookmarks.Add(CurrentPath);
        UpdateStatus(_localizer?.Format("SftpBookmarkAdded", CurrentPath)
            ?? $"Bookmarked: {CurrentPath}");
    }

    /// <summary>
    /// Uploads local files to the current remote directory. Thin wrapper over
    /// <see cref="UploadEntriesAsync"/> that targets <see cref="CurrentPath"/>, so the toolbar Upload
    /// button and the "upload here" command share the single recursive upload path.
    /// </summary>
    /// <remarks>Must be invoked on the UI thread.</remarks>
    public Task UploadFilesAsync(IReadOnlyList<string> localPaths)
        => UploadEntriesAsync(localPaths, CurrentPath);

    /// <summary>
    /// Uploads dropped local entries (files and/or directories) into <paramref name="targetRemoteDir"/>,
    /// recursing into directories. Directories are created before their contents and an existing remote
    /// directory is tolerated so re-dropping a tree merges rather than aborting.
    /// </summary>
    /// <remarks>Must be invoked on the UI thread.</remarks>
    public async Task UploadEntriesAsync(IReadOnlyList<string> localPaths, string targetRemoteDir)
    {
        ArgumentNullException.ThrowIfNull(localPaths);

        TransferStartState startState = TryBeginTransfer(out CancellationTokenSource? transferCts);
        if (startState == TransferStartState.Busy)
        {
            UpdateStatus(_localizer?["SftpTransferInProgress"] ?? "A file transfer is already in progress.");
            return;
        }

        if (startState == TransferStartState.Unavailable || transferCts is null)
        {
            return;
        }

        CancellationToken ct = transferCts.Token;
        TransferProgressValue = 0;
        bool refreshAfterTransfer = true;
        List<string> pendingOperationWarnings = [];

        try
        {
            UploadPlanOutcome outcome = await UploadPlannedEntriesAsync(localPaths, targetRemoteDir, ct);
            refreshAfterTransfer = outcome.Completed;
            UpdateStatus(outcome.Completed
                ? _localizer?["SftpStatusTransferComplete"] ?? "Transfer complete"
                : _localizer?["SftpStatusTransferCancelled"] ?? "Transfer cancelled");

            if (outcome.Completed && outcome.SkippedUnsupportedTargets.Count > 0)
            {
                foreach (string path in outcome.SkippedUnsupportedTargets)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped upload to unsupported remote destination '{path}'.");
                }

                string warning = _localizer?.Format(
                    "WarnUploadTargetsSkippedUnsupported",
                    outcome.SkippedUnsupportedTargets.Count)
                    ?? $"Skipped {outcome.SkippedUnsupportedTargets.Count} upload(s): the destination already exists and is not a regular file. See the log for details.";
                pendingOperationWarnings.Add(warning);
            }

            if (outcome.Completed && outcome.SkippedLocalReparsePoints.Count > 0)
            {
                string warning = _localizer?.Format(
                    "WarnUploadSourcesSkippedReparsePoints",
                    outcome.SkippedLocalReparsePoints.Count)
                    ?? $"Skipped {outcome.SkippedLocalReparsePoints.Count} local link(s) encountered inside the selected upload tree. See the log for details.";
                pendingOperationWarnings.Add(warning);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(_localizer?["SftpStatusTransferCancelled"] ?? "Transfer cancelled");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP upload failed [{ex.GetType().Name}]: {ex.Message} (sshParams={(_sshParams is not null ? "present" : "null")})");
            SetTransferError(ex);
        }
        finally
        {
            CompleteTransfer(transferCts);
            if (refreshAfterTransfer)
            {
                if (pendingOperationWarnings.Count > 0)
                {
                    // The refresh ends with UpdateStatus("Ready"); await it so the aggregated
                    // warning below is the last message written, as the paste path already does.
                    await Refresh();
                }
                else
                {
                    _ = Refresh();
                }
            }
        }

        if (pendingOperationWarnings.Count > 0)
        {
            ShowOperationWarning(string.Join(Environment.NewLine, pendingOperationWarnings));
        }
    }

    /// <summary>
    /// Plans the dropped tree with <see cref="RemoteUploadTreePlanner"/> and executes the resulting
    /// ordered operations against the (decorated, journaling) browser: <c>CreateDirectoryAsync</c> for
    /// each directory and <c>UploadFileAsync</c> for each file, keeping the sudo permission fallback.
    /// </summary>
    private async Task<UploadPlanOutcome> UploadPlannedEntriesAsync(
        IReadOnlyList<string> localPaths,
        string targetRemoteDir,
        CancellationToken ct)
    {
        List<string> skippedUnsupportedTargets = [];
        List<string> skippedLocalReparsePoints = [];

        if (_browser is null)
        {
            return new UploadPlanOutcome(
                false,
                skippedUnsupportedTargets,
                skippedLocalReparsePoints);
        }

        List<LocalUploadEntry> roots = new(localPaths.Count);
        foreach (string path in localPaths)
        {
            bool isDirectory = Directory.Exists(path);
            bool isFile = !isDirectory && File.Exists(path);
            LocalUploadEntry? root = ClassifyLocalUploadRoot(path, isDirectory, isFile);
            if (root is null)
            {
                // Skip paths that no longer exist; a drop can race a delete on the source side.
                continue;
            }

            roots.Add(root);
        }

        if (roots.Count == 0)
        {
            return new UploadPlanOutcome(
                true,
                skippedUnsupportedTargets,
                skippedLocalReparsePoints);
        }

        IReadOnlyList<RemoteUploadOp> ops =
            RemoteUploadTreePlanner.Plan(
                roots,
                targetRemoteDir,
                localDirectory => EnumerateLocalChildren(
                    localDirectory,
                    skippedLocalReparsePoints));
        RemoteUploadConflictInventory inventory = await BuildRemoteUploadConflictInventoryAsync(
            _browser,
            ops,
            ct);
        List<RemoteUploadOp> plannedOps = new(ops.Count);
        foreach (RemoteUploadOp op in ops)
        {
            if (inventory.IsUnsupportedTarget(op.RemotePath))
            {
                skippedUnsupportedTargets.Add(op.RemotePath);
                continue;
            }

            plannedOps.Add(op);
        }

        if (plannedOps.Count == 0)
        {
            return new UploadPlanOutcome(
                true,
                skippedUnsupportedTargets,
                skippedLocalReparsePoints);
        }

        IReadOnlyList<FileConflictAnalysisItem> conflictAnalysis = FileConflictPlanner.Analyze(
            plannedOps.Select(op => new FileConflictPlanItem(
                op.LocalPath,
                op.RemotePath,
                op.Kind == RemoteUploadOpKind.MakeDirectory
                    ? FileConflictItemKind.Directory
                    : FileConflictItemKind.File))
                .ToList(),
            inventory.GetTargetKind,
            StringComparer.Ordinal);
        IReadOnlyList<FileConflictAnalysisItem> conflicts = conflictAnalysis
            .Where(item => item.HasConflict)
            .ToList();

        IReadOnlyList<FileConflictDecision> decisions = [];
        if (conflicts.Count > 0)
        {
            FileConflictDialogViewModel dialogViewModel = new(conflicts, _localizer);
            FileConflictDialogResult? dialogResult = await _fileConflictDialogPresenter
                .ShowAsync(dialogViewModel);
            if (dialogResult is null)
            {
                return new UploadPlanOutcome(
                    false,
                    skippedUnsupportedTargets,
                    skippedLocalReparsePoints);
            }

            decisions = dialogResult.Decisions;
        }

        IReadOnlyList<FileConflictResolvedItem> resolvedOps = FileConflictPlanner.Resolve(
            conflictAnalysis,
            decisions,
            inventory.TargetExists,
            StringComparer.Ordinal);

        int totalFiles = resolvedOps.Count(item =>
            item.Action != FileConflictEffectiveAction.Skip
            && plannedOps[item.Index].Kind == RemoteUploadOpKind.UploadFile);
        int uploadedFiles = 0;

        if (resolvedOps.Any(item =>
            item.Action != FileConflictEffectiveAction.Skip
            && plannedOps[item.Index].Kind == RemoteUploadOpKind.MakeDirectory))
        {
            TransferStatusText = _localizer?["SftpStatusUploadingFolder"] ?? "Uploading folder...";
        }

        foreach (FileConflictResolvedItem resolved in resolvedOps)
        {
            ct.ThrowIfCancellationRequested();
            if (resolved.Action == FileConflictEffectiveAction.Skip)
            {
                continue;
            }

            RemoteUploadOp op = plannedOps[resolved.Index];

            if (op.Kind == RemoteUploadOpKind.MakeDirectory)
            {
                try
                {
                    await _browser.CreateDirectoryAsync(resolved.EffectiveTargetPath, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // mkdir fails either because the directory already exists (re-dropping into an
                    // existing tree, which must MERGE) or for a real reason (permission, quota). The
                    // transports expose no typed already-exists error - SSH.NET raises SftpException
                    // with the generic message "Failure" - so consult the pre-transfer inventory:
                    // tolerate only a directory that was already known to exist.
                    if (!inventory.DirectoryExists(resolved.EffectiveTargetPath))
                    {
                        throw;
                    }

                    Core.Logging.FileLogger.Info(
                        $"EmbeddedSFTP upload merge: remote directory already exists, continuing: {resolved.EffectiveTargetPath}");
                }

                continue;
            }

            uploadedFiles++;
            string fileName = Path.GetFileName(op.LocalPath);
            TransferStatusText = _localizer?.Format(
                "SftpStatusUploadingProgress", fileName,
                $"{uploadedFiles}", $"{totalFiles}") ?? $"Uploading {fileName}...";

            try
            {
                await _browser.UploadFileAsync(op.LocalPath, resolved.EffectiveTargetPath, ct);
            }
            catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
            {
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSFTP upload permission denied, falling back to sudo for {fileName}");
                await UploadViaSudoAsync(op.LocalPath, resolved.EffectiveTargetPath, ct);
            }
        }

        return new UploadPlanOutcome(
            true,
            skippedUnsupportedTargets,
            skippedLocalReparsePoints);
    }

    /// <summary>
    /// Lists each distinct planned parent once and materializes all remote conflict probes in memory.
    /// </summary>
    internal static async Task<RemoteUploadConflictInventory> BuildRemoteUploadConflictInventoryAsync(
        IRemoteBrowser browser,
        IReadOnlyList<RemoteUploadOp> ops,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(ops);

        Dictionary<string, FileConflictItemKind> targetKinds = new(StringComparer.Ordinal);
        HashSet<string> existingDirectories = new(StringComparer.Ordinal);
        HashSet<string> unsupportedTargets = new(StringComparer.Ordinal);
        IEnumerable<string> parentDirectories = ops
            .Select(op => GetParentPath(op.RemotePath))
            .Distinct(StringComparer.Ordinal);

        foreach (string parentDirectory in parentDirectories)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                IReadOnlyList<SftpFileInfo> entries = await browser
                    .ListDirectoryAsync(parentDirectory, ct)
                    .ConfigureAwait(false);
                existingDirectories.Add(parentDirectory);

                foreach (SftpFileInfo entry in entries)
                {
                    string targetPath = CombineRemotePath(parentDirectory, entry.Name);
                    switch (entry.Kind)
                    {
                        case RemoteEntryKind.Directory:
                            targetKinds[targetPath] = FileConflictItemKind.Directory;
                            break;
                        case RemoteEntryKind.File:
                            targetKinds[targetPath] = FileConflictItemKind.File;
                            break;
                        case RemoteEntryKind.SymbolicLink:
                        case RemoteEntryKind.Fifo:
                        case RemoteEntryKind.Socket:
                        case RemoteEntryKind.Device:
                            unsupportedTargets.Add(targetPath);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SftpPathNotFoundException)
            {
                // A missing planned parent has no existing children and therefore no conflicts.
            }
        }

        return new RemoteUploadConflictInventory(
            targetKinds,
            existingDirectories,
            unsupportedTargets);
    }

    /// <summary>In-memory remote inventory used by upload analysis and execution.</summary>
    internal sealed class RemoteUploadConflictInventory
    {
        private readonly IReadOnlyDictionary<string, FileConflictItemKind> _targetKinds;
        private readonly IReadOnlySet<string> _existingDirectories;
        private readonly IReadOnlySet<string> _unsupportedTargets;

        internal RemoteUploadConflictInventory(
            IReadOnlyDictionary<string, FileConflictItemKind> targetKinds,
            IReadOnlySet<string> existingDirectories,
            IReadOnlySet<string> unsupportedTargets)
        {
            _targetKinds = targetKinds;
            _existingDirectories = existingDirectories;
            _unsupportedTargets = unsupportedTargets;
        }

        /// <summary>
        /// Gets whether the target path is, or lives under, a remote entry that is neither a
        /// regular file nor a directory. Heimdall cannot write such a destination, and it will
        /// not traverse it either.
        /// </summary>
        internal bool IsUnsupportedTarget(string targetPath)
        {
            if (_unsupportedTargets.Contains(targetPath))
            {
                return true;
            }

            foreach (string unsupported in _unsupportedTargets)
            {
                if (targetPath.StartsWith(unsupported + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal FileConflictItemKind? GetTargetKind(string targetPath)
        {
            if (_targetKinds.TryGetValue(targetPath, out FileConflictItemKind targetKind))
            {
                return targetKind;
            }

            return _existingDirectories.Contains(targetPath)
                ? FileConflictItemKind.Directory
                : null;
        }

        internal bool TargetExists(string targetPath) => GetTargetKind(targetPath) is not null;

        internal bool DirectoryExists(string targetPath)
            => GetTargetKind(targetPath) == FileConflictItemKind.Directory;
    }

    // Reads a local directory's immediate children into planner entries. The impure walk lives here;
    // the planner stays pure and is driven through this delegate.
    private static IReadOnlyList<LocalUploadEntry> EnumerateLocalChildren(
        string localDirectory,
        ICollection<string> skippedLocalReparsePoints)
    {
        List<LocalUploadEntry> children = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(localDirectory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            LocalUploadEntry? child = ClassifyLocalUploadChild(
                entry,
                attributes,
                skippedLocalReparsePoints);
            if (child is not null)
            {
                children.Add(child);
            }
        }

        return children;
    }

    /// <summary>
    /// Accepts an explicitly selected upload root based on the existing following existence probes.
    /// </summary>
    internal static LocalUploadEntry? ClassifyLocalUploadRoot(
        string localPath,
        bool directoryExists,
        bool fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!directoryExists && !fileExists)
        {
            return null;
        }

        return ToLocalUploadEntry(localPath, directoryExists);
    }

    /// <summary>
    /// Rejects reparse points discovered below an explicitly selected upload root.
    /// </summary>
    internal static LocalUploadEntry? ClassifyLocalUploadChild(
        string localPath,
        FileAttributes attributes,
        ICollection<string> skippedLocalReparsePoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentNullException.ThrowIfNull(skippedLocalReparsePoints);

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            skippedLocalReparsePoints.Add(localPath);
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP skipped local reparse point '{localPath}' during upload planning.");
            return null;
        }

        return ToLocalUploadEntry(
            localPath,
            (attributes & FileAttributes.Directory) != 0);
    }

    private static LocalUploadEntry ToLocalUploadEntry(string localPath, bool isDirectory)
    {
        string name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new LocalUploadEntry(localPath, name, isDirectory);
    }

    // Probes whether a remote directory exists by listing it through the (decorated) browser. Used to
    // tell an already-exists mkdir (merge) apart from a genuine failure without relying on the
    // transport's error text. Cancellation propagates; any other listing failure means "absent".
    private async Task<bool> RemoteDirectoryExistsAsync(string path, CancellationToken ct)
    {
        if (_browser is null)
        {
            return false;
        }

        return await RemoteDirectoryExistsAsync(_browser, path, ct).ConfigureAwait(false);
    }

    private static async Task<bool> RemoteDirectoryExistsAsync(
        IRemoteBrowser browser,
        string path,
        CancellationToken ct)
    {
        try
        {
            await browser.ListDirectoryAsync(path, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads selected remote files into the target folder.
    /// </summary>
    /// <remarks>Must be invoked on the UI thread.</remarks>
    public async Task DownloadFilesAsync(IReadOnlyList<SftpFileInfo> files, string targetFolder)
    {
        TransferStartState startState = TryBeginTransfer(out CancellationTokenSource? transferCts);
        if (startState is not TransferStartState.Started || transferCts is null)
        {
            return;
        }

        IRemoteBrowser? browser = _browser;
        if (browser is null)
        {
            CompleteTransfer(transferCts);
            return;
        }

        CancellationToken ct = transferCts.Token;
        TransferProgressValue = 0;
        var downloadedFiles = 0;
        var skippedDirectories = 0;
        var skippedUnsupportedPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var plannedDownloads = new List<(SftpFileInfo File, string TargetPath, int OriginalIndex)>();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                SftpFileInfo file = files[i];
                if (file.Kind is not RemoteEntryKind.File)
                {
                    if (file.Kind is RemoteEntryKind.Directory)
                    {
                        skippedDirectories++;
                    }
                    else
                    {
                        skippedUnsupportedPaths.Add(file.FullPath);
                    }

                    continue;
                }

                if (!LocalDownloadPath.TryResolveContained(targetFolder, file.Name, out string localPath))
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped unsafe local download name '{file.Name}' for target folder '{targetFolder}'.");
                    continue;
                }

                plannedDownloads.Add((file, localPath, i));
            }

            IReadOnlyList<FileConflictAnalysisItem> conflictAnalysis = FileConflictPlanner.Analyze(
                plannedDownloads
                    .Select(item => new FileConflictPlanItem(item.File.FullPath, item.TargetPath))
                    .ToList(),
                File.Exists,
                StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<FileConflictAnalysisItem> conflicts = conflictAnalysis
                .Where(item => item.HasConflict)
                .ToList();

            IReadOnlyList<FileConflictDecision> decisions = [];
            if (conflicts.Count > 0)
            {
                var dialogViewModel = new FileConflictDialogViewModel(conflicts, _localizer);
                FileConflictDialogResult? dialogResult = await _fileConflictDialogPresenter
                    .ShowAsync(dialogViewModel);
                if (dialogResult is null)
                {
                    UpdateStatus(_localizer?["SftpStatusTransferCancelled"] ?? "Transfer cancelled");
                    return;
                }

                decisions = dialogResult.Decisions;
            }

            IReadOnlyList<FileConflictResolvedItem> resolvedDownloads = FileConflictPlanner.Resolve(
                conflictAnalysis,
                decisions,
                File.Exists,
                StringComparer.OrdinalIgnoreCase);

            foreach (FileConflictResolvedItem resolved in resolvedDownloads)
            {
                ct.ThrowIfCancellationRequested();
                if (resolved.Action is FileConflictEffectiveAction.Skip)
                {
                    continue;
                }

                (SftpFileInfo file, _, int originalIndex) = plannedDownloads[resolved.Index];
                TransferStatusText = _localizer?.Format(
                    "SftpStatusDownloadingFile", file.Name,
                    $"{originalIndex + 1}/{files.Count}") ?? $"Downloading {file.Name}...";

                try
                {
                    await browser.DownloadFileAsync(file.FullPath, resolved.EffectiveTargetPath, ct);
                }
                catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
                {
                    Core.Logging.FileLogger.Info(
                        $"EmbeddedSFTP download permission denied, falling back to sudo for {file.Name}");
                    await DownloadViaSudoAsync(file.FullPath, resolved.EffectiveTargetPath, ct);
                }

                downloadedFiles++;
            }

            switch (ClassifyDownloadOutcome(downloadedFiles, skippedDirectories))
            {
                case SftpDownloadOutcome.CompletedWithSkippedDirectories:
                    UpdateStatus(_localizer?.Format(
                        "SftpStatusDownloadCompleteWithSkipped",
                        downloadedFiles,
                        skippedDirectories)
                        ?? $"Downloaded {downloadedFiles} file(s); skipped {skippedDirectories} folder(s) (folders aren't supported).");
                    break;
                case SftpDownloadOutcome.OnlyDirectoriesSkipped:
                    UpdateStatus(_localizer?["SftpStatusDownloadNoFilesFoldersSkipped"]
                        ?? "No files downloaded \u2014 folders aren't supported.");
                    break;
                case SftpDownloadOutcome.Completed:
                case SftpDownloadOutcome.Empty:
                default:
                    UpdateStatus(_localizer?["SftpStatusTransferComplete"] ?? "Transfer complete");
                    break;
            }

            if (skippedUnsupportedPaths.Count > 0)
            {
                foreach (string path in skippedUnsupportedPaths)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped unsupported remote entry '{path}' during download.");
                }

                string warning = _localizer?.Format(
                    "WarnRemoteEntriesSkippedUnsupported",
                    skippedUnsupportedPaths.Count)
                    ?? $"Skipped {skippedUnsupportedPaths.Count} entries that are neither files nor directories. See the log for details.";
                ShowOperationWarning(warning);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(_localizer?["SftpStatusTransferCancelled"] ?? "Transfer cancelled");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP download failed [{ex.GetType().Name}]: {ex.Message} (sshParams={(_sshParams is not null ? "present" : "null")})");
            SetTransferError(ex);
        }
        finally
        {
            CompleteTransfer(transferCts);
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        lock (_transferCtsGate)
        {
            _transferCts?.Cancel();
        }
    }

    /// <summary>
    /// Updates transfer progress display state.
    /// </summary>
    public void UpdateTransferProgress(SftpTransferProgress progress)
    {
        double percent = progress.TotalBytes > 0
            ? (double)progress.BytesTransferred / progress.TotalBytes * 100
            : 0;

        TransferProgressValue = percent;

        string transferred = FormatSize(progress.BytesTransferred);
        string total = FormatSize(progress.TotalBytes);
        string direction = progress.IsUpload ? "\u2191" : "\u2193";
        TransferStatusText = $"{direction} {progress.FileName} — {transferred} / {total} ({percent:F0}%)";
    }

    /// <summary>
    /// Creates an authenticated SSH client using the session connection settings.
    /// </summary>
    internal async Task<Renci.SshNet.SshClient> CreateSudoSshClientAsync(CancellationToken ct = default)
    {
        if (_sshParams is null)
        {
            throw new InvalidOperationException("SSH params not available for sudo.");
        }

        var pinnedVerifier = await SshConnectionFactory.ResolveHostKeyAsync(
                _sshParams,
                _hostKeyStore,
                _hostKeyVerifier,
                ct)
            .ConfigureAwait(false);

        var ssh = SshConnectionFactory.CreateSshClient(_sshParams);

        SshConnectionFactory.AttachPinnedHostKeyVerification(
            ssh,
            _sshParams,
            pinnedVerifier);

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                ssh.Connect();
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            ssh.Dispose();
            throw;
        }

        return ssh;
    }

    /// <summary>
    /// Executes a single sudo privileged body over SSH (chmod, mv, rm, mkdir, etc.).
    /// </summary>
    internal async Task RunSudoCommandAsync(string privilegedBody, CancellationToken ct = default)
    {
        using Renci.SshNet.SshClient ssh = await CreateSudoSshClientAsync(ct).ConfigureAwait(false);
        try
        {
            using Renci.SshNet.SshCommand cmd = await ExecuteSudoBodyAsync(
                    ssh,
                    privilegedBody,
                    ct)
                .ConfigureAwait(false);

            EnsureSudoSucceeded(cmd, "command");
        }
        finally
        {
            SafeDisconnect(ssh);
        }
    }

    private async Task<Renci.SshNet.SshCommand> ExecuteSudoBodyAsync(
        Renci.SshNet.SshClient ssh,
        string privilegedBody,
        CancellationToken ct = default)
    {
        string? password = _sshParams?.Password;
        bool authenticateViaStdin = !string.IsNullOrEmpty(password);
        string commandText = BuildSudoInvocation(privilegedBody, authenticateViaStdin);

        if (!authenticateViaStdin)
        {
            return await Task.Run(() => ssh.RunCommand(commandText), ct).ConfigureAwait(false);
        }

        return await ExecuteSudoBodyWithPasswordAsync(
                ssh,
                commandText,
                password!,
                ct)
            .ConfigureAwait(false);
    }

    private static async Task<Renci.SshNet.SshCommand> ExecuteSudoBodyWithPasswordAsync(
        Renci.SshNet.SshClient ssh,
        string commandText,
        string password,
        CancellationToken ct)
    {
        Renci.SshNet.SshCommand? command = null;
        Task? executeTask = null;
        try
        {
            command = ssh.CreateCommand(commandText);
            executeTask = command.ExecuteAsync(ct);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password + "\n");

            using (Stream inputStream = command.CreateInputStream())
            {
                await inputStream.WriteAsync(passwordBytes, 0, passwordBytes.Length, ct)
                    .ConfigureAwait(false);
            }

            await executeTask.ConfigureAwait(false);

            Renci.SshNet.SshCommand completedCommand = command;
            command = null;
            return completedCommand;
        }
        catch
        {
            command?.Dispose();

            if (executeTask is not null)
            {
                try
                {
                    await executeTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original exception from the stdin write path.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Downloads a file via <c>sudo base64</c> over a direct SSH exec channel,
    /// bypassing SFTP permission restrictions.
    /// </summary>
    internal Task DownloadViaSudoAsync(string remotePath, string localPath, CancellationToken ct)
        => _sudoEmitter.RunDownloadAsync(
            remotePath,
            localPath,
            () => DownloadViaSudoCoreAsync(remotePath, localPath, ct),
            () => new FileInfo(localPath).Length,
            privileged: true);

    private async Task DownloadViaSudoCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        string privilegedBody = BuildSudoBase64DownloadBody(remotePath);
        string tempPath = AtomicLocalFile.CreateTempPath(localPath);
        using Renci.SshNet.SshClient ssh = await CreateSudoSshClientAsync(ct).ConfigureAwait(false);

        try
        {
            using Renci.SshNet.SshCommand cmd = await ExecuteSudoBodyAsync(ssh, privilegedBody, ct)
                .ConfigureAwait(false);

            EnsureSudoSucceeded(cmd, "base64");

            byte[] bytes = DecodeSudoBase64(cmd.Result ?? string.Empty);
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
                AtomicLocalFile.Commit(tempPath, localPath);
            }
            catch
            {
                AtomicLocalFile.Rollback(tempPath);
                throw;
            }
        }
        finally
        {
            SafeDisconnect(ssh);
        }
    }

    /// <summary>
    /// Streams a file over SSH to a root-owned same-directory temp file, then
    /// atomically replaces the privileged target without following symlinks.
    /// </summary>
    internal async Task UploadViaSudoAsync(string localPath, string remotePath, CancellationToken ct)
    {
        if (_browser is null)
        {
            throw new InvalidOperationException("Browser not available for sudo upload.");
        }

        using Renci.SshNet.SshClient ssh = await CreateSudoSshClientAsync(ct).ConfigureAwait(false);
        try
        {
            // Log the privileged write against the user's true target path.
            await _sudoEmitter.RunUploadAsync(
                localPath,
                remotePath,
                async () =>
                {
                    await using var content = new FileStream(
                        localPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    string writeBody = SudoUploadCommands.Build(remotePath);
                    PrivilegedCommandResult result = await PrivilegedFileTransfer.ExecuteAtomicWriteAsync(
                            ssh,
                            writeBody,
                            content,
                            _sshParams?.Password,
                            ct)
                        .ConfigureAwait(false);

                    EnsureSudoSucceeded(
                        result.ExitStatus,
                        result.Error,
                        "atomic write");
                },
                () => new FileInfo(localPath).Length,
                privileged: true).ConfigureAwait(false);
        }
        finally
        {
            SafeDisconnect(ssh);
        }
    }

    private static void EnsureSudoSucceeded(Renci.SshNet.SshCommand cmd, string operationLabel)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        EnsureSudoSucceeded(cmd.ExitStatus ?? -1, cmd.Error, operationLabel);
    }

    private static void EnsureSudoSucceeded(
        int exitStatus,
        string? stderr,
        string operationLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationLabel);

        if (exitStatus == 0)
        {
            return;
        }

        string error = stderr ?? string.Empty;
        SudoFailureKind failureKind = ClassifySudoStderr(error);
        if (failureKind is SudoFailureKind.PasswordUnavailable or SudoFailureKind.PasswordRejected)
        {
            throw new SudoAuthenticationException(failureKind, error);
        }

        throw new InvalidOperationException(
            $"sudo {operationLabel} failed (exit {exitStatus}): {error}");
    }

    private static void SafeDisconnect(Renci.SshNet.SshClient ssh)
    {
        try
        {
            ssh.Disconnect();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"EmbeddedSftpViewModel: sudo SSH disconnect failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Prompts for a folder name and creates it in the current remote directory.
    /// </summary>
    [RelayCommand]
    public async Task CreateFolderAsync()
    {
        if (_disposed || _browser is null || _dialogService is null)
        {
            return;
        }

        string? folderName = await _dialogService.ShowInputAsync(
            L10n("SftpNewFolderTitle"),
            L10n("SftpNewFolderName"));

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        folderName = folderName.Trim();
        if (!SftpPathGuard.IsValidChildName(folderName))
        {
            await RunOnUiAsync(() => SetErrorStatus(L10n("ErrorInvalidFileName"))).ConfigureAwait(false);
            return;
        }

        try
        {
            string remotePath = CombineRemotePath(CurrentPath, folderName);
            try
            {
                await _browser.CreateDirectoryAsync(remotePath);
            }
            catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
            {
                Core.Logging.FileLogger.Info("EmbeddedSFTP mkdir permission denied, falling back to sudo");
                await _sudoEmitter.RunMkdirAsync(
                    remotePath,
                    () => RunSudoCommandAsync($"mkdir -p {PathEscaper.EscapeForShell(remotePath)}"),
                    privileged: true);
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpSuccessMkdir")));
            await Refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
                SetTransferError(ex));
        }
    }

    /// <summary>
    /// Prompts for a new name and renames the selected remote entry.
    /// </summary>
    public async Task RenameEntryAsync(SftpFileInfo file)
    {
        if (_disposed || _browser is null || _dialogService is null)
        {
            return;
        }

        if (RenameFollowsSymlinkTarget && file.Kind == RemoteEntryKind.SymbolicLink)
        {
            await RunOnUiAsync(() =>
                UpdateStatus(L10n("SftpStatusRenameUnsupportedEntry"))).ConfigureAwait(false);
            return;
        }

        string? newName = await _dialogService.ShowInputAsync(
            L10n("SftpBtnRename"),
            L10n("SftpNewFolderName"),
            file.Name);

        if (string.IsNullOrWhiteSpace(newName)
            || string.Equals(newName, file.Name, StringComparison.Ordinal))
        {
            return;
        }

        newName = newName.Trim();
        if (string.Equals(newName, file.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (!SftpPathGuard.IsValidChildName(newName))
        {
            await RunOnUiAsync(() => SetErrorStatus(L10n("ErrorInvalidFileName"))).ConfigureAwait(false);
            return;
        }

        try
        {
            string newPath = CombineRemotePath(CurrentPath, newName);
            try
            {
                await _browser.RenameAsync(file.FullPath, newPath);
            }
            catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
            {
                Core.Logging.FileLogger.Info("EmbeddedSFTP rename permission denied, falling back to sudo");
                bool renamed = await RenameEntryViaSudoAsync(file, newPath);
                if (!renamed)
                {
                    return;
                }
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpSuccessRename")));
            await Refresh().ConfigureAwait(false);
        }
        catch (SudoRenameCollisionException)
        {
            await RunOnUiAsync(() =>
                SetErrorStatus(L10n("SftpErrorSudoRenameCollision")));
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
                SetTransferError(ex));
        }
    }

    private async Task<bool> RenameEntryViaSudoAsync(SftpFileInfo file, string requestedTargetPath)
    {
        FileConflictItemKind? existingKind = await ProbeSudoTargetKindAsync(requestedTargetPath);
        FileConflictPlanItem[] plannedItems =
        [
            new FileConflictPlanItem(
                file.FullPath,
                requestedTargetPath,
                file.IsDirectory ? FileConflictItemKind.Directory : FileConflictItemKind.File),
        ];
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            plannedItems,
            _ => existingKind,
            StringComparer.Ordinal,
            FileConflictPolicy.Rename);
        IReadOnlyList<FileConflictAnalysisItem> conflicts = analysis
            .Where(item => item.HasConflict)
            .ToList();
        IReadOnlyList<FileConflictDecision> decisions = [];

        if (conflicts.Count > 0)
        {
            FileConflictDialogViewModel dialogViewModel = new(conflicts, _localizer);
            FileConflictDialogResult? dialogResult = await _fileConflictDialogPresenter
                .ShowAsync(dialogViewModel);
            if (dialogResult is null)
            {
                UpdateStatus(L10n("SftpStatusTransferCancelled"));
                return false;
            }

            decisions = dialogResult.Decisions;
        }

        HashSet<string> occupiedTargets = new(StringComparer.Ordinal);
        if (existingKind is not null)
        {
            occupiedTargets.Add(requestedTargetPath);
        }

        FileConflictDecision? decision = decisions.FirstOrDefault(item => item.ItemIndex == 0);
        if (decision?.Choice == FileConflictResolutionChoice.AutoRename)
        {
            foreach (string candidate in FileConflictPlanner.EnumerateAutoRenameTargets(requestedTargetPath))
            {
                FileConflictItemKind? candidateKind = await ProbeSudoTargetKindAsync(candidate)
                    .ConfigureAwait(false);
                if (candidateKind is null)
                {
                    break;
                }

                occupiedTargets.Add(candidate);
            }
        }

        IReadOnlyList<FileConflictResolvedItem> resolvedItems = FileConflictPlanner.Resolve(
            analysis,
            decisions,
            occupiedTargets.Contains,
            StringComparer.Ordinal);
        FileConflictResolvedItem resolved = resolvedItems.Single();
        if (resolved.Action == FileConflictEffectiveAction.Skip)
        {
            return false;
        }

        bool replace = decision?.Choice == FileConflictResolutionChoice.Replace;
        await _sudoEmitter.RunRenameAsync(
            file.FullPath,
            resolved.EffectiveTargetPath,
            () => ExecuteSudoRenameMoveAsync(
                file.FullPath,
                resolved.EffectiveTargetPath,
                replace),
            privileged: true).ConfigureAwait(false);
        return true;
    }

    private async Task<FileConflictItemKind?> ProbeSudoTargetKindAsync(string targetPath)
    {
        SudoRenameCommandResult existenceResult = await ExecuteSudoRenameCommandAsync(
                SudoRenameCommands.BuildExistenceProbe(targetPath),
                CancellationToken.None)
            .ConfigureAwait(false);
        EnsureSudoProbeCompleted(existenceResult, "rename existence probe");
        if (existenceResult.ExitStatus == 1)
        {
            return null;
        }

        SudoRenameCommandResult directoryResult = await ExecuteSudoRenameCommandAsync(
                SudoRenameCommands.BuildDirectoryProbe(targetPath),
                CancellationToken.None)
            .ConfigureAwait(false);
        EnsureSudoProbeCompleted(directoryResult, "rename kind probe");
        return directoryResult.ExitStatus == 0
            ? FileConflictItemKind.Directory
            : FileConflictItemKind.File;
    }

    private async Task ExecuteSudoRenameMoveAsync(
        string sourcePath,
        string targetPath,
        bool replace)
    {
        SudoRenameCommandResult moveResult = await ExecuteSudoRenameCommandAsync(
                SudoRenameCommands.BuildMove(sourcePath, targetPath, replace),
                CancellationToken.None)
            .ConfigureAwait(false);
        EnsureSudoSucceeded(moveResult.ExitStatus, moveResult.StandardError, "rename");

        if (replace)
        {
            return;
        }

        SudoRenameCommandResult sourceResult = await ExecuteSudoRenameCommandAsync(
                SudoRenameCommands.BuildExistenceProbe(sourcePath),
                CancellationToken.None)
            .ConfigureAwait(false);
        EnsureSudoProbeCompleted(sourceResult, "rename outcome probe");
        if (sourceResult.ExitStatus == 0)
        {
            throw new SudoRenameCollisionException();
        }
    }

    private async Task<SudoRenameCommandResult> ExecuteSudoRenameCommandAsync(
        string privilegedBody,
        CancellationToken ct)
    {
        if (_sudoRenameCommandExecutor is not null)
        {
            return await _sudoRenameCommandExecutor(privilegedBody, ct).ConfigureAwait(false);
        }

        using Renci.SshNet.SshClient ssh = await CreateSudoSshClientAsync(ct).ConfigureAwait(false);
        try
        {
            using Renci.SshNet.SshCommand command = await ExecuteSudoBodyAsync(ssh, privilegedBody, ct)
                .ConfigureAwait(false);
            return new SudoRenameCommandResult(
                command.ExitStatus ?? -1,
                command.Result ?? string.Empty,
                command.Error ?? string.Empty);
        }
        finally
        {
            SafeDisconnect(ssh);
        }
    }

    private static void EnsureSudoProbeCompleted(
        SudoRenameCommandResult result,
        string operationLabel)
    {
        SudoFailureKind failureKind = ClassifySudoStderr(result.StandardError);
        if (failureKind is SudoFailureKind.PasswordUnavailable or SudoFailureKind.PasswordRejected)
        {
            throw new SudoAuthenticationException(failureKind, result.StandardError);
        }

        if (result.ExitStatus is 0 or 1)
        {
            return;
        }

        EnsureSudoSucceeded(result.ExitStatus, result.StandardError, operationLabel);
    }

    /// <summary>
    /// Confirms and deletes the selected remote entries.
    /// </summary>
    public async Task DeleteEntriesAsync(IReadOnlyList<SftpFileInfo> entries)
    {
        if (entries.Count == 0 || _disposed || _browser is null || _dialogService is null)
        {
            return;
        }

        string itemName = entries.Count == 1
            ? entries[0].Name
            : _localizer?.Format("SftpItemCount", entries.Count.ToString()) ?? $"{entries.Count} items";
        string message = _localizer?.Format("SftpConfirmDelete", itemName)
            ?? $"Delete \"{itemName}\"?";

        bool confirmed = await _dialogService.ShowConfirmAsync(
            L10n("SftpConfirmDeleteTitle"),
            message,
            "warning");

        if (!confirmed)
        {
            return;
        }

        IRemoteBrowser browser = _browser;
        List<DeleteEntryFailure> failures = [];
        int deletedCount = 0;

        try
        {
            foreach (SftpFileInfo file in entries)
            {
                DeleteEntryFailure? failure = await TryDeleteEntryAsync(browser, file)
                    .ConfigureAwait(false);
                if (failure is null)
                {
                    deletedCount++;
                }
                else
                {
                    failures.Add(failure);
                }
            }

            if (failures.Count == 0)
            {
                await RunOnUiAsync(() => UpdateStatus(L10n("SftpSuccessDelete")));
                await Refresh().ConfigureAwait(false);
                return;
            }

            string summary = GetDeleteFailureSummary(failures, entries.Count);
            if (deletedCount == 0)
            {
                await RunOnUiAsync(() => SetErrorStatus(summary)).ConfigureAwait(false);
                return;
            }

            await Refresh().ConfigureAwait(false);
            await RunOnUiAsync(() => ShowOperationWarning(summary)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
                SetTransferError(ex));
        }
    }

    private async Task<DeleteEntryFailure?> TryDeleteEntryAsync(
        IRemoteBrowser browser,
        SftpFileInfo file)
    {
        if (SftpPathGuard.IsProtectedRoot(file.FullPath))
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP refused deletion of protected root '{file.FullPath}'.");
            return new DeleteEntryFailure(file.Name, null, IsProtectedRoot: true);
        }

        try
        {
            await browser.DeleteAsync(file.FullPath).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
        {
            try
            {
                SftpPathGuard.ThrowIfProtectedRoot(file.FullPath, "sudo delete");
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSFTP delete permission denied, falling back to sudo for {file.Name}");
                string flag = file.IsDirectory ? "-rf" : "-f";
                await _sudoEmitter.RunDeleteAsync(
                    file.FullPath,
                    () => RunSudoDeleteCommandAsync(
                        $"rm {flag} {PathEscaper.EscapeForShell(file.FullPath)}",
                        CancellationToken.None),
                    privileged: true).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception sudoException)
            {
                Core.Logging.FileLogger.Warn(
                    $"EmbeddedSFTP sudo deletion failed for '{file.FullPath}' "
                    + $"[{sudoException.GetType().Name}]: {sudoException.Message}");
                return new DeleteEntryFailure(file.Name, null);
            }
        }
        catch (RemoteRecursiveDeleteException ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP recursive deletion refused for '{file.FullPath}' ({ex.Reason}).");
            return new DeleteEntryFailure(file.Name, ex.Reason);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP deletion failed for '{file.FullPath}' "
                + $"[{ex.GetType().Name}]: {ex.Message}");
            return new DeleteEntryFailure(file.Name, null);
        }
    }

    private Task RunSudoDeleteCommandAsync(string command, CancellationToken ct)
    {
        return _sudoDeleteCommandExecutor is null
            ? RunSudoCommandAsync(command, ct)
            : _sudoDeleteCommandExecutor(command, ct);
    }

    private string GetDeleteFailureSummary(
        IReadOnlyList<DeleteEntryFailure> failures,
        int totalCount)
    {
        DeleteEntryFailure firstFailure = failures[0];
        if (failures.Count == 1)
        {
            if (totalCount == 1 && firstFailure.IsProtectedRoot)
            {
                return L10n("SftpErrorProtectedRoot");
            }

            return firstFailure.Reason switch
            {
                RemoteRecursiveDeleteFailureReason.ExecUnavailable =>
                    L10n("SftpDeleteRefusedExecUnavailable"),
                RemoteRecursiveDeleteFailureReason.ShellOrRmUnavailable =>
                    L10n("SftpDeleteRefusedShellUnavailable"),
                _ => _localizer?.Format("SftpDeleteFailedEntry", firstFailure.Name)
                    ?? "SftpDeleteFailedEntry",
            };
        }

        return _localizer?.Format("SftpDeletePartialSummary", failures.Count, totalCount)
            ?? "SftpDeletePartialSummary";
    }

    /// <summary>
    /// Prompts once for new octal permissions and applies chmod to every selected entry. The default
    /// shown is the primary entry's current mode; the entered mode is applied to all entries.
    /// </summary>
    public async Task ChmodEntriesAsync(IReadOnlyList<SftpFileInfo> entries)
    {
        if (entries.Count == 0 || _disposed || _browser is null || _dialogService is null)
        {
            return;
        }

        SftpFileInfo primary = SelectedFile ?? entries[0];
        string currentOctal = PermissionsToOctal(primary.Permissions);

        string title = entries.Count == 1
            ? (_localizer?.Format("SftpChmodTitle", primary.Name) ?? $"chmod {primary.Name}")
            : (_localizer?.Format("SftpChmodTitleMultiple", entries.Count.ToString())
                ?? $"chmod {entries.Count} items");

        string? newPerms = await _dialogService.ShowInputAsync(
            title,
            L10n("SftpChmodLabel"),
            currentOctal);

        if (string.IsNullOrWhiteSpace(newPerms))
        {
            return;
        }

        if (!int.TryParse(newPerms, NumberStyles.None, null, out int octal)
            || octal < 0
            || octal > 777
            || newPerms.Any(c => c < '0' || c > '7'))
        {
            SetErrorStatus(L10n("ErrorInvalidOctalPermission"));
            return;
        }

        short mode = Convert.ToInt16(newPerms, 8);

        try
        {
            foreach (SftpFileInfo entry in entries)
            {
                try
                {
                    await _browser.ChmodAsync(entry.FullPath, mode);
                }
                catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
                {
                    Core.Logging.FileLogger.Info("EmbeddedSFTP chmod permission denied, falling back to sudo");
                    await RunSudoCommandAsync(
                        $"chmod {newPerms} {PathEscaper.EscapeForShell(entry.FullPath)}");
                }
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpChmodSuccess")));
            await Refresh().ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            await RunOnUiAsync(() =>
                SetErrorStatus(L10n("SftpChmodNotSupported")));
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
                SetTransferError(ex));
        }
    }

    /// <summary>
    /// Displays a properties dialog for the selected remote entry.
    /// </summary>
    public void ShowProperties(SftpFileInfo file)
    {
        if (_dialogService is null)
        {
            return;
        }

        string type = L10n(GetRemoteEntryKindDisplayKey(file.Kind));

        string sizeText = file.IsDirectory ? "-" : FormatSize(file.Size);
        string octal = PermissionsToOctal(file.Permissions);

        string body = $"{L10n("SftpPropertiesName")} {file.Name}\n" +
                      $"{L10n("SftpPropertiesType")} {type}\n" +
                      $"{L10n("SftpPropertiesSize")} {sizeText}\n" +
                      $"{L10n("SftpPropertiesModified")} {file.LastModified:yyyy-MM-dd HH:mm:ss}\n" +
                      $"{L10n("SftpPropertiesPermissions")} {file.Permissions} ({octal})\n" +
                      $"{L10n("SftpPropertiesOwner")} {file.Owner}  {L10n("SftpPropertiesGroup")} {file.Group}\n" +
                      $"{L10n("SftpPropertiesPath")} {file.FullPath}";

        _dialogService.ShowInfo(
            _localizer?.Format("SftpPropertiesTitle", file.Name) ?? $"Properties — {file.Name}",
            body);
    }

    internal static string GetRemoteEntryKindDisplayKey(RemoteEntryKind kind)
    {
        switch (kind)
        {
            case RemoteEntryKind.File:
                return "SftpPropertiesTypeFile";
            case RemoteEntryKind.Directory:
                return "SftpPropertiesTypeDirectory";
            case RemoteEntryKind.SymbolicLink:
                return "SftpPropertiesTypeSymlink";
            case RemoteEntryKind.Fifo:
                return "SftpPropertiesTypeFifo";
            case RemoteEntryKind.Socket:
                return "SftpPropertiesTypeSocket";
            case RemoteEntryKind.Device:
                return "SftpPropertiesTypeDevice";
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown remote entry kind.");
    }

    /// <summary>
    /// Returns the parent path for a remote directory path.
    /// </summary>
    public static string GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        string trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

    /// <summary>
    /// Combines a remote directory path and child name.
    /// </summary>
    public static string CombineRemotePath(string directory, string name)
    {
        return $"{directory.TrimEnd('/')}/{name}";
    }

    /// <summary>
    /// Converts a rwxrwxrwx permission string to its octal form.
    /// </summary>
    public static string PermissionsToOctal(string perms)
    {
        if (string.IsNullOrEmpty(perms) || perms.Length != 9)
        {
            return "000";
        }

        static int TriadToDigit(char r, char w, char x) =>
            (r != '-' ? 4 : 0) + (w != '-' ? 2 : 0) + (x != '-' ? 1 : 0);

        int owner = TriadToDigit(perms[0], perms[1], perms[2]);
        int group = TriadToDigit(perms[3], perms[4], perms[5]);
        int other = TriadToDigit(perms[6], perms[7], perms[8]);

        return $"{owner}{group}{other}";
    }

    internal static string BuildSudoInvocation(string privilegedBody, bool authenticateViaStdin)
    {
        return authenticateViaStdin
            ? $"sudo -S -p '' {privilegedBody}"
            : $"sudo {privilegedBody}";
    }

    internal static SudoFailureKind ClassifySudoStderr(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return SudoFailureKind.None;
        }

        if (ContainsSudoStderr(stderr, SudoStderrTerminalRequired)
            || ContainsSudoStderr(stderr, SudoStderrNoTtyPresent)
            || ContainsSudoStderr(stderr, SudoStderrNoAskpass)
            || ContainsSudoStderr(stderr, SudoStderrPasswordRequired))
        {
            return SudoFailureKind.PasswordUnavailable;
        }

        if (ContainsSudoStderr(stderr, SudoStderrIncorrectPasswordAttempt)
            || ContainsSudoStderr(stderr, SudoStderrSorryTryAgain)
            || ContainsSudoStderr(stderr, SudoStderrNoPasswordProvided))
        {
            return SudoFailureKind.PasswordRejected;
        }

        return SudoFailureKind.None;
    }

    private static bool ContainsSudoStderr(string stderr, string match)
    {
        return stderr.Contains(match, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildSudoBase64DownloadBody(string remotePath)
    {
        return PrivilegedFileCommands.BuildNoFollowBase64ReadBody(remotePath);
    }

    internal static byte[] DecodeSudoBase64(string commandOutput)
    {
        return Convert.FromBase64String(commandOutput ?? string.Empty);
    }

    /// <summary>
    /// Formats a byte count using the shared file-size formatter.
    /// </summary>
    public static string FormatSize(long bytes) => FileSize.Format(bytes);

    internal static SftpDownloadOutcome ClassifyDownloadOutcome(
        int downloadedFiles,
        int skippedDirectories)
    {
        if (downloadedFiles > 0)
        {
            return skippedDirectories > 0
                ? SftpDownloadOutcome.CompletedWithSkippedDirectories
                : SftpDownloadOutcome.Completed;
        }

        return skippedDirectories > 0
            ? SftpDownloadOutcome.OnlyDirectoriesSkipped
            : SftpDownloadOutcome.Empty;
    }

    /// <summary>
    /// Determines whether the provided exception represents a permission error.
    /// </summary>
    public static bool IsPermissionDenied(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex is SftpPermissionDeniedException
            or UnauthorizedAccessException
            or RemoteRecursiveDeleteException
        {
            Reason: RemoteRecursiveDeleteFailureReason.PermissionDenied,
        };
    }

    /// <summary>
    /// Resolves the remote directory a drop should land in: the hovered entry's path when it is a
    /// directory row, otherwise the current directory. Pure so it can be unit-tested without a view.
    /// </summary>
    public static string ResolveDropTargetDirectory(SftpFileInfo? hoveredEntry, string currentDirectory)
        => hoveredEntry is { IsDirectory: true } ? hoveredEntry.FullPath : currentDirectory;

    private async Task LoadDirectoryCoreAsync(
        string path,
        bool pushToHistory,
        bool suppressErrorStatus = false,
        bool redactPathInLogs = false)
    {
        if (!TryCaptureLifecycleToken(out CancellationToken ct)
            || _browser is null
            || !_browser.IsConnected
            || IsLoading)
        {
            return;
        }

        await RunOnUiAsync(() => IsLoading = true);

        try
        {
            await RunOnUiAsync(() => UpdateStatus(_localizer?["SftpStatusLoading"] ?? "Loading..."));

            IReadOnlyList<SftpFileInfo> entries;

            if (SudoMode && _sshParams is not null)
            {
                entries = await ListDirectoryViaSudoAsync(path, ct).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    entries = await _browser.ListDirectoryAsync(path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
                {
                    Core.Logging.FileLogger.Info(redactPathInLogs
                        ? "EmbeddedSFTP listdir permission denied, falling back to sudo."
                        : $"EmbeddedSFTP listdir permission denied, falling back to sudo for {path}");
                    entries = await ListDirectoryViaSudoAsync(path, ct).ConfigureAwait(false);
                }
            }

            await RunOnUiAsync(() =>
            {
                if (pushToHistory && !string.Equals(path, CurrentPath, StringComparison.Ordinal))
                {
                    _navigationHistory.Push(CurrentPath);
                }

                CurrentPath = path;
                UnfilteredEntries = [.. entries];
                ApplyFilterAndSort();
                CanGoBack = _navigationHistory.Count > 0;
                UpdateStatus(_localizer?["SftpStatusReady"] ?? "Ready");
            });
        }
        catch (OperationCanceledException)
        {
            Core.Logging.FileLogger.Debug("SFTP listing cancelled");
            await RunOnUiAsync(() => UpdateStatus(_localizer?["SftpStatusReady"] ?? "Ready"));
        }
        catch (Exception ex)
        {
            if (suppressErrorStatus)
            {
                Core.Logging.FileLogger.Info($"EmbeddedSFTP LoadDirectory failed silently: {ex.Message}");
            }
            else
            {
                Core.Logging.FileLogger.Warn(redactPathInLogs
                    ? $"EmbeddedSFTP LoadDirectory failed ({ex.GetType().Name})."
                    : $"EmbeddedSFTP LoadDirectory failed: {ex.Message}");
                await RunOnUiAsync(() =>
                    SetTransferError(ex));
            }
        }
        finally
        {
            await RunOnUiAsync(() => IsLoading = false);
        }
    }

    private async Task<IReadOnlyList<SftpFileInfo>> ListDirectoryViaSudoAsync(
        string path,
        CancellationToken ct)
    {
        string escaped = PathEscaper.EscapeForShell(path);
        string privilegedBody = $"ls -la --time-style=long-iso {escaped}";
        using Renci.SshNet.SshClient ssh = await CreateSudoSshClientAsync(ct).ConfigureAwait(false);

        try
        {
            using Renci.SshNet.SshCommand cmd = await ExecuteSudoBodyAsync(ssh, privilegedBody, ct)
                .ConfigureAwait(false);

            EnsureSudoSucceeded(cmd, "ls");

            return ParseLsOutput(cmd.Result ?? string.Empty, path);
        }
        finally
        {
            SafeDisconnect(ssh);
        }
    }

    /// <remarks>
    /// Expects GNU coreutils <c>ls -la --time-style=long-iso</c> output with
    /// eight whitespace-separated fields; BusyBox or non-GNU <c>ls</c> layouts may differ.
    /// </remarks>
    internal static IReadOnlyList<SftpFileInfo> ParseLsOutput(string output, string parentPath)
    {
        var results = new List<SftpFileInfo>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("total ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 8, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8)
            {
                Heimdall.Core.Logging.FileLogger.Debug(
                    $"EmbeddedSftpViewModel: skipped malformed sudo ls line: {line}");
                continue;
            }

            string permissions = parts[0];
            if (permissions.Length < 2 || !"dl-cbps".Contains(permissions[0]))
            {
                Heimdall.Core.Logging.FileLogger.Debug(
                    $"EmbeddedSftpViewModel: skipped sudo ls line with unsupported permissions: {line}");
                continue;
            }

            string owner = parts[2];
            string group = parts[3];
            _ = long.TryParse(
                parts[4],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long size);

            DateTime lastModified = DateTime.MinValue;
            _ = DateTime.TryParse(
                $"{parts[5]} {parts[6]}",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out lastModified);

            string name = parts[7];
            if (name is "." or "..")
            {
                continue;
            }

            int arrowIndex = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIndex >= 0)
            {
                name = name[..arrowIndex];
            }

            RemoteEntryKind kind = permissions[0] switch
            {
                'd' => RemoteEntryKind.Directory,
                'l' => RemoteEntryKind.SymbolicLink,
                'p' => RemoteEntryKind.Fifo,
                's' => RemoteEntryKind.Socket,
                'c' or 'b' => RemoteEntryKind.Device,
                '-' => RemoteEntryKind.File,
                _ => RemoteEntryKind.File,
            };
            string fullPath = parentPath.EndsWith("/", StringComparison.Ordinal)
                ? $"{parentPath}{name}"
                : $"{parentPath}/{name}";

            results.Add(new SftpFileInfo(
                name, fullPath, kind, size, lastModified,
                permissions, owner, group));
        }

        return results;
    }

    partial void OnFilterTextChanged(string value)
    {
        if (!IsLoading)
        {
            ApplyFilterAndSort();
        }
    }

    partial void OnShowHiddenChanged(bool value)
    {
        if (!IsLoading)
        {
            ApplyFilterAndSort();
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _uiDispatcher.InvokeAsync(action);
    }

    private void SetEndpointKey(string endpointKey)
    {
        if (SetProperty(ref _endpointKey, endpointKey, nameof(EndpointKey)))
        {
            RefreshRemoteClipboardState();
        }
    }

    private bool IsClipboardForCurrentEndpoint(SftpClipboardContent clipboard)
    {
        return string.Equals(clipboard.SourceEndpointKey, EndpointKey, StringComparison.Ordinal);
    }

    private void OnRemoteClipboardChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (_uiDispatcher.CheckAccess())
        {
            RefreshRemoteClipboardState();
            return;
        }

        _ = _uiDispatcher.InvokeAsync(RefreshRemoteClipboardState);
    }

    private void RefreshRemoteClipboardState()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(Clipboard));
        OnPropertyChanged(nameof(HasClipboard));
        PasteCommand.NotifyCanExecuteChanged();
    }

    private void ArmErrorHighlightTimer()
    {
        DisposeErrorHighlightTimer();
        _errorHighlightTimer = new System.Threading.Timer(_ =>
        {
            _ = _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    IsErrorHighlighted = false;
                }
            });
        }, null, ErrorHighlightDuration, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void DisposeErrorHighlightTimer()
    {
        _errorHighlightTimer?.Dispose();
        _errorHighlightTimer = null;
    }

    private sealed record DeleteEntryFailure(
        string Name,
        RemoteRecursiveDeleteFailureReason? Reason,
        bool IsProtectedRoot = false);

    private string L10n(string key) => _localizer?.GetString(key) ?? key;

    private string GetSudoAuthenticationErrorMessage(SudoFailureKind kind)
    {
        return kind switch
        {
            SudoFailureKind.PasswordUnavailable => L10n("ErrorSudoPasswordUnavailable"),
            SudoFailureKind.PasswordRejected => L10n("ErrorSudoPasswordRejected"),
            _ => L10n("ErrorSudoAuthenticationFailed"),
        };
    }
}

internal enum SudoFailureKind
{
    None,
    PasswordUnavailable,
    PasswordRejected,
}

/// <summary>Completed result from the privileged channel used by sudo rename.</summary>
/// <param name="ExitStatus">Remote process exit status.</param>
/// <param name="StandardOutput">Remote standard output.</param>
/// <param name="StandardError">Remote standard error.</param>
internal sealed record SudoRenameCommandResult(
    int ExitStatus,
    string StandardOutput,
    string StandardError);

/// <summary>Raised when a no-clobber rename exits successfully without moving its source.</summary>
internal sealed class SudoRenameCollisionException : InvalidOperationException;

internal static class SudoRenameCommands
{
    /// <summary>Builds the privileged exact-target existence probe.</summary>
    internal static string BuildExistenceProbe(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string escapedPath = PathEscaper.EscapeForShell(targetPath);
        return $"test -e {escapedPath} -o -L {escapedPath}";
    }

    /// <summary>Builds the privileged directory-kind probe.</summary>
    internal static string BuildDirectoryProbe(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return $"test -d {PathEscaper.EscapeForShell(targetPath)}";
    }

    /// <summary>Builds an exact-target move, adding no-clobber unless replacement was selected.</summary>
    internal static string BuildMove(string sourcePath, string targetPath, bool replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string flags = replace ? "-T" : "-nT";
        return $"mv {flags} {PathEscaper.EscapeForShell(sourcePath)} {PathEscaper.EscapeForShell(targetPath)}";
    }
}

internal static class SudoUploadCommands
{
    /// <summary>
    /// Builds the privileged streamed atomic-write body.
    /// </summary>
    /// <param name="targetRemotePath">Privileged target path to replace atomically.</param>
    /// <returns>The privileged shell body.</returns>
    internal static string Build(string targetRemotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);
        return PrivilegedFileCommands.BuildAtomicWriteBody(targetRemotePath);
    }
}
