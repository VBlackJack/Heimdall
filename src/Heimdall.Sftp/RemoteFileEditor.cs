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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.Sftp;

/// <summary>
/// Manages remote file editing sessions: downloads a file to a local temp directory,
/// opens it in an external editor, and auto-uploads changes via a
/// <see cref="FileSystemWatcher"/> with debounce protection.
/// </summary>
public sealed class RemoteFileEditor : IDisposable
{
    /// <summary>
    /// Process-wide minimum interval between consecutive auto-uploads for the
    /// same file.
    /// </summary>
    /// <remarks>
    /// This is a process-wide setting applied once at application startup from
    /// AppSettings.SftpUploadDebounceMs (see App.xaml.cs). It is not intended
    /// to change at runtime.
    /// </remarks>
    public static TimeSpan UploadDebounceInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal const long MaxSudoEditFileBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan UploadDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IRemoteBrowser _browser;
    private readonly string _editorPath;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly ConcurrentDictionary<string, EditSession> _activeSessions = new();
    private long _sessionTransitions;
    private bool _disposed;

    /// <summary>
    /// Raised after an auto-upload attempt. Parameters: remote path, success flag.
    /// </summary>
    public event Action<string, bool>? FileUploaded;

    /// <summary>
    /// Raised when an auto-upload is rejected because the host key changed
    /// after the sudo edit session was opened.
    /// </summary>
    public event Action<HostKeyRotationEvent>? HostKeyRotatedDuringUpload;

    /// <summary>
    /// Raised after a privileged (sudo) auto-upload completes, on BOTH success and failure, carrying
    /// the true remote target and the local edited file path. The App layer subscribes to record an
    /// operations entry. The non-sudo branch never raises this; it is logged via the browser decorator.
    /// </summary>
    public event Action<RemoteEditorSudoSaveCompleted>? SudoSaveCompleted;

    /// <summary>
    /// Creates a new <see cref="RemoteFileEditor"/> backed by the given SFTP browser.
    /// </summary>
    /// <param name="browser">Connected SFTP browser used for file transfers.</param>
    /// <param name="hostKeyStore">TOFU host key store for server verification on SSH connections.</param>
    /// <param name="hostKeyVerifier">Verifier used when a host key is unknown or changed.</param>
    /// <param name="editorPath">
    /// Path to the external editor executable (defaults to <c>notepad.exe</c>).
    /// </param>
    public RemoteFileEditor(
        IRemoteBrowser browser,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        string editorPath = "notepad.exe")
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        _browser = browser;
        _editorPath = editorPath;
        _hostKeyStore = hostKeyStore;
        _hostKeyVerifier = hostKeyVerifier;
    }

    /// <summary>
    /// Opens a remote file for editing: downloads it locally, launches the
    /// configured editor, and starts watching for changes to auto-upload.
    /// </summary>
    /// <param name="remotePath">Full remote path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// The file is already open for editing in another session.
    /// </exception>
    public async Task EditFileAsync(string remotePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        // Close previous edit session for this file if one exists
        CloseEdit(remotePath);

        string localPath = CreateTempFilePath(remotePath);

        try
        {
            await _browser.DownloadFileAsync(remotePath, localPath, ct).ConfigureAwait(false);
        }
        catch
        {
            CleanupTempFile(localPath);
            throw;
        }

        var session = new EditSession
        {
            RemotePath = remotePath,
            LocalPath = localPath,
            IsSudo = false,
            LastUploadTime = DateTime.UtcNow
        };

        if (!_activeSessions.TryAdd(remotePath, session))
        {
            session.Dispose();
            CleanupTempFile(localPath);
            return;
        }

        Interlocked.Increment(ref _sessionTransitions);
        StartWatcher(session);
        AttachEditor(session);
    }

    /// <summary>
    /// Opens a privileged (sudo) remote file for editing. The file is downloaded
    /// through a symlink-refusing held descriptor and streamed back through a
    /// root-owned same-directory temp file followed by an atomic rename.
    /// </summary>
    /// <param name="remotePath">Full remote path to the privileged file.</param>
    /// <param name="sshParams">SSH connection parameters for the sudo SSH session.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EditFileSudoAsync(
        string remotePath,
        SshConnectionParams sshParams,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentNullException.ThrowIfNull(sshParams);

        // Close previous edit session for this file if one exists
        CloseEdit(remotePath);

        string localPath = CreateTempFilePath(remotePath);

        PinnedFingerprintVerifier pinnedVerifier;

        try
        {
            pinnedVerifier = await SshConnectionFactory.ResolveHostKeyAsync(
                    sshParams,
                    _hostKeyStore,
                    _hostKeyVerifier,
                    ct)
                .ConfigureAwait(false);

            // Download with sudo via SSH command
            using var sshClient = SshConnectionFactory.CreateSshClient(sshParams);

            SshConnectionFactory.AttachPinnedHostKeyVerification(
                sshClient,
                sshParams,
                pinnedVerifier);

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                sshClient.Connect();
            }, ct).ConfigureAwait(false);

            try
            {
                string readBody = PrivilegedFileCommands.BuildNoFollowBase64ReadBody(
                    remotePath,
                    MaxSudoEditFileBytes);
                PrivilegedCommandResult downloadResult = await PrivilegedFileTransfer.ExecuteCommandAsync(
                        sshClient,
                        readBody,
                        sshParams.Password,
                        ct)
                    .ConfigureAwait(false);

                if (downloadResult.ExitStatus == PrivilegedFileCommands.FileTooLargeExitStatus
                    && TryParseSudoFileSize(downloadResult.Result, out long remoteSize))
                {
                    EnsureSudoEditFileSizeWithinLimit(remotePath, remoteSize);
                }

                if (downloadResult.ExitStatus != 0)
                {
                    throw new InvalidOperationException(
                        $"sudo base64 failed (exit {downloadResult.ExitStatus}): {downloadResult.Error}");
                }

                await WriteBase64DecodedFileAsync(
                    localPath,
                    downloadResult.Result,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                sshClient.Disconnect();
            }
        }
        catch
        {
            CleanupTempFile(localPath);
            throw;
        }

        var session = new EditSession
        {
            RemotePath = remotePath,
            LocalPath = localPath,
            IsSudo = true,
            SshParams = sshParams,
            Verifier = pinnedVerifier,
            LastUploadTime = DateTime.UtcNow
        };

        if (!_activeSessions.TryAdd(remotePath, session))
        {
            session.Dispose();
            CleanupTempFile(localPath);
            return;
        }

        Interlocked.Increment(ref _sessionTransitions);
        StartWatcher(session);
        AttachEditor(session);
    }

    /// <summary>
    /// Closes an active edit session, stopping the file watcher and cleaning up
    /// the temporary local file.
    /// </summary>
    /// <param name="remotePath">Remote path of the file being edited.</param>
    public void CloseEdit(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        if (_activeSessions.TryRemove(remotePath, out var session))
        {
            ReleaseSession(session);
        }
    }

    /// <summary>Returns the list of remote paths currently open for editing.</summary>
    public IReadOnlyList<string> GetActiveEdits()
    {
        return _activeSessions.Keys.ToList();
    }

    /// <summary>
    /// A counter that only ever increases, moved every time an edit session opens or closes.
    /// The pane's close guard folds it into its change stamp, so a consent given while no file
    /// was open outside cannot be spent on a close that happens after one was opened.
    /// </summary>
    public long EditSessionTransitions => Interlocked.Read(ref _sessionTransitions);

    /// <summary>The editor executable this instance launches, as configured.</summary>
    internal string EditorPath => _editorPath;

    internal IReadOnlyDictionary<string, EditSession> ActiveSessionsForTesting => _activeSessions;

    internal bool AddSessionForTesting(EditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _activeSessions.TryAdd(session.RemotePath, session);
    }

    internal void TriggerOnFileChangedForTesting(EditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        OnFileChanged(session);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var kvp in _activeSessions.ToArray())
        {
            if (_activeSessions.TryRemove(kvp.Key, out var session))
            {
                ReleaseSession(session);
            }
        }

        _activeSessions.Clear();
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private static string CreateTempFilePath(string remotePath)
    {
        string fileName = Path.GetFileName(remotePath);

        // The one factory both editors use: the root is defined once, and the restrictive ACL is
        // applied there, so a root-owned file read through sudo is never staged in a directory
        // every account can read.
        string tempDir = Heimdall.Core.Utilities.EditorTempPaths.CreateWorkingDirectory();
        return Path.Combine(tempDir, fileName);
    }

    /// <summary>
    /// Unregisters a session: stops its watcher and drains its upload, then removes the staged
    /// copy unless the external editor still has it open.
    /// </summary>
    /// <remarks>
    /// Deleting the file under a running editor turned the user's next Ctrl+S into a file that
    /// nothing watched: the save landed on disk and never reached the server. A copy left behind
    /// is removed by the startup sweeper once it is old enough.
    /// </remarks>
    private void ReleaseSession(EditSession session)
    {
        Interlocked.Increment(ref _sessionTransitions);
        bool editorRunning = session.IsEditorRunning;
        DrainSession(session);

        if (editorRunning)
        {
            Heimdall.Core.Logging.FileLogger.Info(
                $"RemoteFileEditor left the staged copy of {session.RemotePath} in place: the external editor is still running.");
            return;
        }

        CleanupTempFile(session.LocalPath);
    }

    /// <summary>
    /// Starts the external editor on a registered session, and unregisters the session when the
    /// editor cannot be started.
    /// </summary>
    private void AttachEditor(EditSession session)
    {
        try
        {
            session.EditorProcess = LaunchEditor(_editorPath, session.LocalPath);
        }
        catch (ExternalEditorLaunchException)
        {
            // Registered and watching before the editor failed to start: a session with no editor
            // would otherwise watch, for the life of the pane, a file nobody edits.
            if (_activeSessions.TryRemove(session.RemotePath, out EditSession? orphan))
            {
                Interlocked.Increment(ref _sessionTransitions);
                DrainSession(orphan);
                CleanupTempFile(orphan.LocalPath);
            }

            throw;
        }
    }

    private static void CleanupTempFile(string localPath)
    {
        try
        {
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }

            string? dir = Path.GetDirectoryName(localPath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"RemoteFileEditor temp cleanup failed for {localPath}: {ex.Message}");
        }
    }

    private void StartWatcher(EditSession session)
    {
        string? directory = Path.GetDirectoryName(session.LocalPath);
        string fileName = Path.GetFileName(session.LocalPath);

        if (directory is null)
        {
            return;
        }

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            // Atomic-save editors (VS Code, Notepad++, Sublime) write to a temp file
            // then rename, so we need FileName and Size in addition to LastWrite.
            NotifyFilter = NotifyFilters.LastWrite
                          | NotifyFilters.FileName
                          | NotifyFilters.Size
        };

        watcher.Changed += (_, _) => OnFileChanged(session);
        watcher.Created += (_, _) => OnFileChanged(session);
        watcher.Renamed += (_, _) => OnFileChanged(session);

        session.Watcher = watcher;
        session.DebounceTimer = new System.Threading.Timer(
            _ => OnFileChanged(session),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(EditSession session)
    {
        CancellationToken ct;
        try
        {
            ct = session.UploadCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        Task upload = OnFileChangedAsync(session, ct);
        session.TrackUploadIfReplaceable(upload);

        _ = upload.ContinueWith(
            static task =>
            {
                if (task.Exception is { } exception)
                {
                    Heimdall.Core.Logging.FileLogger.Warn(
                        $"RemoteFileEditor auto-upload task faulted unexpectedly: {exception.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task OnFileChangedAsync(EditSession session, CancellationToken ct)
    {
        var enteredSemaphore = false;

        if (!session.ShouldUpload)
        {
            ArmDebounceTimer(session);
            return;
        }

        bool success;
        try
        {
            // Serialize uploads per file - prevents concurrent saves from overlapping
            if (!await session.UploadSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
            {
                ArmDebounceTimer(session);
                return; // Another upload is in progress, skip (debounce will catch the next one)
            }

            enteredSemaphore = true;
            ct.ThrowIfCancellationRequested();

            if (session.IsSudo && session.SshParams is not null)
            {
                try
                {
                    await UploadWithSudoAsync(session, ct).ConfigureAwait(false);
                    SudoSaveCompleted?.Invoke(new RemoteEditorSudoSaveCompleted(
                        session.RemotePath, session.LocalPath, Success: true));
                }
                catch
                {
                    // Signal the failure (the App layer records it), then rethrow so the existing
                    // FileUploaded / HostKeyRotatedDuringUpload handling below runs unchanged.
                    SudoSaveCompleted?.Invoke(new RemoteEditorSudoSaveCompleted(
                        session.RemotePath, session.LocalPath, Success: false));
                    throw;
                }
            }
            else
            {
                await _browser.UploadFileAsync(
                    session.LocalPath,
                    session.RemotePath,
                    ct).ConfigureAwait(false);
            }

            session.LastUploadTime = DateTime.UtcNow;
            success = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Heimdall.Core.Logging.FileLogger.Info(
                $"RemoteFileEditor auto-upload cancelled for {session.RemotePath}.");
            FileUploaded?.Invoke(session.RemotePath, false);
            return;
        }
        catch (Heimdall.Ssh.HostKeyRejectedException ex)
        {
            // A host key change between the open-edit step and the upload step
            // is a security event, not a benign upload failure.
            Heimdall.Core.Logging.FileLogger.Error(
                $"RemoteFileEditor: host key rejected during upload of {session.RemotePath} "
                + $"({ex.Host}:{ex.Port}, presented={ex.PresentedFingerprint}, stored={ex.StoredFingerprint ?? "<none>"}). Upload aborted.");
            HostKeyRotatedDuringUpload?.Invoke(new HostKeyRotationEvent(
                session.RemotePath,
                ex.PresentedFingerprint,
                ex.StoredFingerprint,
                ex.Host,
                ex.Port));
            FileUploaded?.Invoke(session.RemotePath, false);
            return;
        }
        catch (Exception ex)
        {
            success = false;
            Heimdall.Core.Logging.FileLogger.Warn(
                $"RemoteFileEditor auto-upload failed for {session.RemotePath}: {ex.Message}");
            ArmDebounceTimer(session);
        }
        finally
        {
            if (enteredSemaphore)
            {
                try
                {
                    session.UploadSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The session may have been closed while a non-cancellable
                    // upload API was still unwinding.
                }
            }
        }

        FileUploaded?.Invoke(session.RemotePath, success);
    }

    private static void ArmDebounceTimer(EditSession session)
    {
        try
        {
            session.DebounceTimer?.Change(
                UploadDebounceInterval,
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The session was torn down while a drop path was unwinding.
        }
    }

    internal static bool TryParseSudoFileSize(string? output, out long fileSize)
    {
        fileSize = 0;
        string? trimmed = output?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        return long.TryParse(
                trimmed,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out fileSize)
            && fileSize >= 0;
    }

    internal static void EnsureSudoEditFileSizeWithinLimit(string remotePath, long fileSizeBytes)
    {
        if (fileSizeBytes > MaxSudoEditFileBytes)
        {
            throw new SudoEditFileTooLargeException(
                remotePath,
                fileSizeBytes,
                MaxSudoEditFileBytes);
        }
    }

    internal static async Task WriteBase64DecodedFileAsync(
        string localPath,
        string? base64Content,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Content ?? "");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "sudo base64 returned invalid base64 output.",
                ex);
        }

        await File.WriteAllBytesAsync(localPath, bytes, ct).ConfigureAwait(false);
    }

    internal static bool ShouldReplaceTrackedUpload(Task? current) =>
        current is null || current.IsCompleted;

    internal static async Task UploadWithSudoAsync(EditSession session, CancellationToken ct = default)
    {
        if (session.SshParams is null)
        {
            throw new InvalidOperationException("SSH parameters required for sudo upload.");
        }

        if (session.Verifier is null)
        {
            throw new InvalidOperationException(
                "Sudo edit session must have a cached pinned verifier; was the session created via EditFileSudoAsync?");
        }

        using var sshClient = SshConnectionFactory.CreateSshClient(session.SshParams);

        SshConnectionFactory.AttachPinnedHostKeyVerification(
            sshClient,
            session.SshParams,
            session.Verifier);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            sshClient.Connect();
        }, ct).ConfigureAwait(false);

        try
        {
            await using var fileStream = new FileStream(
                session.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string writeBody = PrivilegedFileCommands.BuildAtomicWriteBody(session.RemotePath);
            PrivilegedCommandResult result = await PrivilegedFileTransfer.ExecuteAtomicWriteAsync(
                    sshClient,
                    writeBody,
                    fileStream,
                    session.SshParams.Password,
                    ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException(
                    $"sudo atomic write failed (exit {result.ExitStatus}): {result.Error}");
            }
        }
        finally
        {
            sshClient.Disconnect();
        }
    }

    private static void DrainSession(EditSession session)
    {
        try
        {
            session.UploadCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            session.DebounceTimer?.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }

        var pendingUpload = session.CurrentUpload;
        if (pendingUpload is not null && !pendingUpload.IsCompleted)
        {
            try
            {
                if (!pendingUpload.Wait(UploadDrainTimeout))
                {
                    Heimdall.Core.Logging.FileLogger.Warn(
                        $"RemoteFileEditor upload drain timed out for {session.RemotePath}.");
                }
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static inner =>
                inner is OperationCanceledException or TaskCanceledException))
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"RemoteFileEditor upload drain observed a fault for {session.RemotePath}: {ex.Message}");
            }
        }

        session.Dispose();
    }

    /// <summary>
    /// Resolves the configured editor to a concrete executable path.
    /// </summary>
    /// <param name="editorPath">Configured editor path, or null/empty for the default editor.</param>
    /// <returns>The editor executable path to launch.</returns>
    internal static string ResolveEditorPath(string? editorPath)
    {
        var trimmed = editorPath?.Trim();
        var isDefault = string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, "notepad.exe", StringComparison.OrdinalIgnoreCase);

        if (isDefault && OperatingSystem.IsWindows())
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return Path.Combine(systemDirectory, "notepad.exe");
        }

        return string.IsNullOrEmpty(trimmed) ? "notepad.exe" : trimmed;
    }

    /// <summary>
    /// Starts the editor and returns its process, kept so the session knows whether the editor
    /// still has the staged copy open when the session closes.
    /// </summary>
    /// <exception cref="ExternalEditorLaunchException">The editor could not be started.</exception>
    private static Process? LaunchEditor(string editorPath, string localPath)
    {
        var resolvedEditorPath = ResolveEditorPath(editorPath);
        var psi = new ProcessStartInfo
        {
            FileName = resolvedEditorPath,
            UseShellExecute = false
        };

        // ArgumentList performs proper Win32-aware quoting per arg, so a
        // local path containing quotes, spaces, or shell metacharacters
        // cannot break out of the editor argument.
        psi.ArgumentList.Add(localPath);

        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or FileNotFoundException
            or PlatformNotSupportedException)
        {
            throw new ExternalEditorLaunchException(resolvedEditorPath, ex);
        }
    }
}

/// <summary>
/// Carries details for a host-key rotation detected during a sudo auto-upload.
/// </summary>
public sealed record HostKeyRotationEvent(
    string RemotePath,
    string PresentedFingerprint,
    string? StoredFingerprint,
    string Host,
    int Port);

/// <summary>
/// Carries the outcome of a privileged (sudo) editor auto-upload so the App layer can record an
/// operations entry. The non-sudo branch is logged via the browser decorator and never raises this.
/// </summary>
/// <param name="RemotePath">The true remote target path of the edited file.</param>
/// <param name="LocalPath">The local edited file path (the upload source).</param>
/// <param name="Success">Whether the privileged save completed successfully.</param>
public sealed record RemoteEditorSudoSaveCompleted(
    string RemotePath,
    string LocalPath,
    bool Success);

/// <summary>
/// Indicates that a privileged edit was refused because the remote file is too large to buffer safely.
/// </summary>
public sealed class SudoEditFileTooLargeException : InvalidOperationException
{
    public SudoEditFileTooLargeException(
        string remotePath,
        long fileSizeBytes,
        long maxSizeBytes)
        : base(
            $"Sudo edit refused for '{remotePath}' because the file is {fileSizeBytes} bytes and the limit is {maxSizeBytes} bytes.")
    {
        RemotePath = remotePath;
        FileSizeBytes = fileSizeBytes;
        MaxSizeBytes = maxSizeBytes;
    }

    public string RemotePath { get; }

    public long FileSizeBytes { get; }

    public long MaxSizeBytes { get; }
}

/// <summary>
/// Tracks state for a single remote file editing session.
/// </summary>
internal sealed class EditSession : IDisposable
{
    private readonly object _currentUploadLock = new();
    private Task? _currentUpload;

    /// <summary>Full remote path of the file being edited.</summary>
    public required string RemotePath { get; init; }

    /// <summary>Local temp file path.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Whether this file requires sudo for writes.</summary>
    public bool IsSudo { get; init; }

    /// <summary>SSH connection parameters for sudo operations. Null for non-sudo edits.</summary>
    public SshConnectionParams? SshParams { get; init; }

    /// <summary>
    /// Pinned host-key verifier resolved when the sudo edit session opened.
    /// Non-null for sudo sessions; null for direct-browser sessions.
    /// </summary>
    public PinnedFingerprintVerifier? Verifier { get; init; }

    /// <summary>File system watcher for auto-upload on save.</summary>
    public FileSystemWatcher? Watcher { get; set; }

    /// <summary>The external editor started on the staged copy, when one was.</summary>
    public Process? EditorProcess { get; set; }

    /// <summary>Whether the external editor still runs, and so may still hold the staged copy.</summary>
    public bool IsEditorRunning
    {
        get
        {
            try
            {
                return EditorProcess is { HasExited: false };
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// One-shot timer that re-checks for a pending upload after the debounce
    /// interval elapses (trailing-edge debounce).
    /// </summary>
    public System.Threading.Timer? DebounceTimer { get; set; }

    /// <summary>Serializes upload operations per file to prevent concurrent save races.</summary>
    public SemaphoreSlim UploadSemaphore { get; } = new(1, 1);

    /// <summary>Cancels in-flight auto-upload work when the edit session closes.</summary>
    public CancellationTokenSource UploadCts { get; } = new();

    /// <summary>Most recent auto-upload task, retained so teardown can observe it.</summary>
    public Task? CurrentUpload
    {
        get
        {
            lock (_currentUploadLock)
            {
                return _currentUpload;
            }
        }
    }

    /// <summary>Timestamp of the last successful upload (UTC).</summary>
    public DateTime LastUploadTime { get; set; }

    /// <summary>
    /// Returns true if enough time has elapsed since the last upload to allow
    /// another upload (debounce guard).
    /// </summary>
    public bool ShouldUpload =>
        (DateTime.UtcNow - LastUploadTime) >= RemoteFileEditor.UploadDebounceInterval;

    /// <summary>
    /// Tracks an upload task only when no upload is tracked or the tracked one
    /// is complete. The decision and assignment are atomic per edit session.
    /// </summary>
    public void TrackUploadIfReplaceable(Task upload)
    {
        ArgumentNullException.ThrowIfNull(upload);

        lock (_currentUploadLock)
        {
            if (RemoteFileEditor.ShouldReplaceTrackedUpload(_currentUpload))
            {
                _currentUpload = upload;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Watcher?.Dispose();
        DebounceTimer?.Dispose();
        UploadSemaphore.Dispose();
        UploadCts.Dispose();
        EditorProcess?.Dispose();
        GC.SuppressFinalize(this);
    }
}
