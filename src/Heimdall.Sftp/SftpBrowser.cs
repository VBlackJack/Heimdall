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

using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Heimdall.Sftp;

/// <summary>
/// SFTP browser backed by SSH.NET's native <see cref="SftpClient"/>.
/// Provides async file operations with progress reporting and cancellation support.
/// </summary>
/// <remarks>
/// Operations wrap blocking SSH.NET calls in <see cref="Task.Run"/>. The
/// <see cref="CancellationToken"/> is honoured at operation boundaries and by
/// cancellation-aware file streams during transfers.
/// </remarks>
public sealed class SftpBrowser : IRemoteBrowser
{
    private enum UploadCommitMode
    {
        ReplaceExisting,
        PublishIfAbsent,
    }

    private static readonly TimeSpan DefaultDisconnectLockTimeout = TimeSpan.FromMilliseconds(250);
    private const int SuccessfulExitStatus = 0;
    private const int CommandNotFoundExitStatus = 127;
    private const string PermissionDeniedDiagnostic = "permission denied";

    private SftpClient? _client;
    private int _disposeState;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly TimeSpan _disconnectLockTimeout;
    private readonly ISftpExecCommandRunner? _injectedExecCommandRunner;

    // Connection context retained so short-lived SSH exec channels can be opened later for
    // server-side copy and recursive deletion, pinned to the SAME host key resolved at connect
    // time (fail-closed: no re-prompt, no auto-accept). Both are cleared on Disconnect alongside
    // the SFTP client.
    private SshConnectionParams? _connectionParams;
    private PinnedFingerprintVerifier? _pinnedHostKeyVerifier;

    // Generous bound on a server-side cp; a server-local copy is fast, but a large tree may take a
    // while. On timeout the copy falls back to the roundtrip path rather than failing.
    private static readonly TimeSpan ServerSideCopyCommandTimeout = TimeSpan.FromMinutes(10);

    public SftpBrowser()
        : this(DefaultDisconnectLockTimeout, null)
    {
    }

    internal SftpBrowser(TimeSpan disconnectLockTimeout)
        : this(disconnectLockTimeout, null)
    {
    }

    internal SftpBrowser(ISftpExecCommandRunner execCommandRunner)
        : this(
            DefaultDisconnectLockTimeout,
            execCommandRunner ?? throw new ArgumentNullException(nameof(execCommandRunner)))
    {
    }

    private SftpBrowser(
        TimeSpan disconnectLockTimeout,
        ISftpExecCommandRunner? injectedExecCommandRunner)
    {
        if (disconnectLockTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disconnectLockTimeout));
        }

        _disconnectLockTimeout = disconnectLockTimeout;
        _injectedExecCommandRunner = injectedExecCommandRunner;
    }

    /// <summary>Raised when the current working directory changes.</summary>
    public event Action<string>? DirectoryChanged;

    /// <summary>Raised during file transfers to report progress.</summary>
    public event Action<SftpTransferProgress>? TransferProgress;

    // SFTP uploads either commit atomically or are refused, so this browser raises no operation warning;
    // the event stays because IRemoteBrowser declares it and FtpBrowser does raise it.
#pragma warning disable CS0067
    /// <inheritdoc/>
    public event Action<RemoteOperationWarning>? OperationWarningRaised;
#pragma warning restore CS0067

    /// <summary>
    /// Raised when the connection is lost. The parameter contains an error
    /// message if the disconnection was unexpected, or null for a clean disconnect.
    /// </summary>
    public event Action<string?>? Disconnected;

    /// <summary>
    /// Raised when a security-relevant failure occurs. Fired in addition to <see cref="Disconnected"/>.
    /// </summary>
    public event Action<SshSessionSecurityEvent>? SecurityEventOccurred;

    /// <summary>Current remote working directory.</summary>
    public string CurrentDirectory { get; private set; } = "/";

    /// <summary>Whether the SFTP client is connected to the remote host.</summary>
    public bool IsConnected => Volatile.Read(ref _client)?.IsConnected ?? false;

    /// <summary>
    /// Connects to the remote host using the supplied SSH connection parameters.
    /// </summary>
    /// <param name="connectionParams">SSH connection parameters (host, credentials, etc.).</param>
    /// <param name="hostKeyStore">TOFU host key store for server verification.</param>
    /// <param name="hostKeyVerifier">Verifier used when a host key is unknown or changed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Already connected.</exception>
    public async Task ConnectAsync(
        SshConnectionParams connectionParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ArgumentNullException.ThrowIfNull(connectionParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        if (Volatile.Read(ref _client)?.IsConnected == true)
        {
            throw new InvalidOperationException("SFTP browser is already connected.");
        }

        var pinnedVerifier = await SshConnectionFactory.ResolveHostKeyAsync(
                connectionParams,
                hostKeyStore,
                hostKeyVerifier,
                ct)
            .ConfigureAwait(false);

        var client = SshConnectionFactory.CreateSftpClient(connectionParams);
        if (connectionParams.KeepAliveIntervalSeconds is > 0)
        {
            client.KeepAliveInterval = TimeSpan.FromSeconds(connectionParams.KeepAliveIntervalSeconds.Value);
        }

        Volatile.Write(ref _client, client);

        SshConnectionFactory.AttachPinnedHostKeyVerification(
            client,
            connectionParams,
            pinnedVerifier);

        // Retain the resolved params + pinned verifier for later trusted SSH exec channels.
        _connectionParams = connectionParams;
        _pinnedHostKeyVerifier = pinnedVerifier;

        client.ErrorOccurred += OnErrorOccurred;

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.Connect();
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _client, null, client), client))
            {
                client.ErrorOccurred -= OnErrorOccurred;
                client.Dispose();
                DropConnectionContext();
            }

            throw;
        }

        CurrentDirectory = client.WorkingDirectory ?? "/";
        DirectoryChanged?.Invoke(CurrentDirectory);
    }

    /// <summary>
    /// Lists all entries in the specified directory, or the current directory if
    /// <paramref name="path"/> is null.
    /// </summary>
    /// <param name="path">Remote directory path, or null for the current directory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of file/directory entries (excluding "." and "..").</returns>
    public async Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
        string? path = null,
        CancellationToken ct = default)
    {
        string targetPath = path ?? CurrentDirectory;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            var entries = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return client.ListDirectory(targetPath);
            }, ct).ConfigureAwait(false);

            var result = new List<SftpFileInfo>();
            foreach (ISftpFile entry in entries)
            {
                if (entry.Name is "." or "..")
                {
                    continue;
                }

                result.Add(ToSftpFileInfo(entry));
            }

            return result;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>Returns the current remote working directory path.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
    {
        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            string dir = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return client.WorkingDirectory ?? "/";
            }, ct).ConfigureAwait(false);

            return dir;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Changes the current directory to <paramref name="path"/> and raises
    /// <see cref="DirectoryChanged"/>.
    /// </summary>
    /// <param name="path">Absolute or relative remote path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? changedDirectory = null;
        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.ChangeDirectory(path);
            }, ct).ConfigureAwait(false);

            CurrentDirectory = client.WorkingDirectory ?? "/";
            changedDirectory = CurrentDirectory;
        }
        finally
        {
            _clientLock.Release();
        }

        if (changedDirectory is not null)
        {
            DirectoryChanged?.Invoke(changedDirectory);
        }
    }

    /// <summary>
    /// Downloads a remote file to a local path, reporting progress via
    /// <see cref="TransferProgress"/>.
    /// </summary>
    /// <param name="remotePath">Full remote file path.</param>
    /// <param name="localPath">Local destination path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DownloadFileAsync(
        string remotePath,
        string localPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        string fileName = Path.GetFileName(remotePath);
        long totalBytes = 0;
        string tempPath = AtomicLocalFile.CreateTempPath(localPath);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            // Retrieve file size for progress reporting
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var attrs = client.GetAttributes(remotePath);
                totalBytes = attrs.Size;
            }, ct).ConfigureAwait(false);

            try
            {
                await using (var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    using CancellationAwareStream outputStream = new(fileStream, ct);

                    await Task.Run(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        client.DownloadFile(remotePath, outputStream, bytesTransferred =>
                        {
                            if (ct.IsCancellationRequested)
                            {
                                return;
                            }

                            TransferProgress?.Invoke(new SftpTransferProgress(
                                fileName,
                                (long)bytesTransferred,
                                totalBytes,
                                IsUpload: false));
                        });
                        ct.ThrowIfCancellationRequested();
                    }, ct).ConfigureAwait(false);
                }

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
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Uploads a local file to a remote path, reporting progress via
    /// <see cref="TransferProgress"/>.
    /// </summary>
    /// <param name="localPath">Local source file path.</param>
    /// <param name="remotePath">Full remote destination path.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UploadFileAsync(
        string localPath,
        string remotePath,
        CancellationToken ct = default)
    {
        return UploadFileAsync(localPath, remotePath, UploadCommitMode.ReplaceExisting, ct);
    }

    private async Task UploadFileAsync(
        string localPath,
        string remotePath,
        UploadCommitMode commitMode,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        string fileName = Path.GetFileName(localPath);
        FileInfo fileInfo = LocalUploadSource.GetRequiredRegularFile(localPath);
        long totalBytes = fileInfo.Length;

        // Before the temporary path is even chosen, let alone created. A replacement that cannot
        // put back what it removes must not begin: refusing after the upload would make the
        // operator pay for a transfer that was never allowed to land.
        if (commitMode == UploadCommitMode.ReplaceExisting)
        {
            await EnsureReplacementPreservesMetadataAsync(remotePath, ct).ConfigureAwait(false);
        }

        string tempRemotePath = SftpAtomicUpload.CreateRemoteTempPath(remotePath);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SftpClient client = GetConnectedClient();
            try
            {
                await using FileStream fileStream = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    useAsync: true);
                using CancellationAwareStream inputStream = new(fileStream, ct);

                await Task.Run(() =>
                {
                    // The staged upload replaces SftpClient.UploadFile, which created the temporary
                    // AND filled it in one call: its content therefore sat under the server's
                    // default mode - typically world-readable - for the whole copy. Creating the
                    // file empty first lets it be tightened to owner-only before any byte lands.
                    SftpModePreservation.RunStagedUpload(
                        new SftpModePreservation.StagedUploadOperations(
                            CreateEmptyTemp: () => client.Create(tempRemotePath).Dispose(),
                            ReadTempMode: () => GetPermissionMode(client.GetAttributes(tempRemotePath)),
                            ApplyTempMode: mode =>
                            {
                                SftpFileAttributes attributes = client.GetAttributes(tempRemotePath);
                                ApplyPermissionMode(attributes, mode);
                                client.SetAttributes(tempRemotePath, attributes);
                            },
                            OpenTempForWrite: () => client.OpenWrite(tempRemotePath),
                            ReadTargetModeAfterUpload: () =>
                            {
                                ISftpFile? targetEntry = TryGetEntryWithoutFollowingTarget(client, remotePath);
                                RemoteEntryKind? targetKind = targetEntry is null
                                    ? null
                                    : GetRemoteEntryKind(targetEntry);
                                SftpAtomicUpload.EnsureUploadTargetSupported(remotePath, targetKind);

                                return targetEntry is { IsRegularFile: true }
                                    ? GetPermissionMode(targetEntry.Attributes)
                                    : null;
                            },
                            // Routed through the existing apply-and-verify helper, so a mode that
                            // cannot be set still refuses the commit rather than publishing a file
                            // with the wrong permissions.
                            ApplyPublicationMode: mode =>
                            {
                                SftpFileAttributes attributes = client.GetAttributes(tempRemotePath);
                                ApplyUploadModeBeforeCommit(
                                    remotePath,
                                    mode,
                                    GetPermissionMode(attributes),
                                    modeToApply =>
                                    {
                                        ApplyPermissionMode(attributes, modeToApply);
                                        client.SetAttributes(tempRemotePath, attributes);
                                    });
                            },
                            Commit: () =>
                            {
                                // Second characterisation, immediately before publication. The
                                // first one is stale by now: a long upload leaves a window in
                                // which the destination can acquire an ACL, a security attribute
                                // or a capability, and publishing on the strength of the earlier
                                // verdict would authorise destroying metadata that did not exist
                                // when it was taken.
                                if (commitMode == UploadCommitMode.ReplaceExisting)
                                {
                                    EnsureReplacementPreservesMetadataAsync(remotePath, ct)
                                        .GetAwaiter()
                                        .GetResult();
                                }

                                CommitUploadedTemp(
                                    client,
                                    tempRemotePath,
                                    remotePath,
                                    commitMode);
                            }),
                        inputStream,
                        written => TransferProgress?.Invoke(new SftpTransferProgress(
                            fileName,
                            written,
                            totalBytes,
                            IsUpload: true)),
                        ct);
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                await Task.Run(
                        () => SftpAtomicUpload.Rollback(
                            tempRemotePath,
                            temp =>
                            {
                                if (client.Exists(temp))
                                {
                                    client.DeleteFile(temp);
                                }
                            }))
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Refuses the replacement unless the destination's security metadata can be reproduced.
    /// </summary>
    /// <remarks>
    /// Fail-closed at every step. No trusted exec channel means the question cannot be asked, and
    /// an unasked question is a refusal, not a pass; it carries its own reason rather than the
    /// tooling one, which specifically claims getcap, getfattr or getfacl is missing from the
    /// server and would misdiagnose an unavailable route.
    /// <para>
    /// The remote command's standard error is deliberately not propagated to the caller: it is
    /// unlocalized and may quote server-side paths the operator cannot act on. The verdict alone
    /// crosses the boundary, as a localization key.
    /// </para>
    /// </remarks>
    internal async Task EnsureReplacementPreservesMetadataAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ISftpExecCommandRunner? runner = _injectedExecCommandRunner;
        if (runner is null)
        {
            SshConnectionParams? connectionParams = _connectionParams;
            PinnedFingerprintVerifier? pinnedVerifier = _pinnedHostKeyVerifier;
            if (connectionParams is null || pinnedVerifier is null)
            {
                throw new SftpMetadataPreservationException(
                    SftpMetadataPreflightVerdict.ExecUnavailable,
                    remotePath);
            }

            runner = new SshNetSftpExecCommandRunner(connectionParams, pinnedVerifier);
        }

        SftpExecResult result = await runner
            .ExecuteAsync(SftpMetadataPreflight.Build(remotePath), ct)
            .ConfigureAwait(false);

        SftpMetadataPreflightVerdict verdict = SftpMetadataPreflight.Classify(result.ExitStatus);
        if (!SftpMetadataPreflight.AllowsReplacement(verdict))
        {
            throw new SftpMetadataPreservationException(verdict, remotePath);
        }
    }

    private static void CommitUploadedTemp(
        SftpClient client,
        string tempRemotePath,
        string remotePath,
        UploadCommitMode commitMode)
    {
        if (commitMode == UploadCommitMode.PublishIfAbsent)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] publish-if-absent for '{remotePath}' relies on plain SFTP rename semantics; "
                + "a residual collision window may remain on some servers or filesystems.");
            SftpAtomicUpload.CommitPublishIfAbsent(
                tempRemotePath,
                remotePath,
                plainRename: (temp, final) => client.RenameFile(temp, final),
                remoteExists: client.Exists);
            return;
        }

        if (commitMode != UploadCommitMode.ReplaceExisting)
        {
            throw new ArgumentOutOfRangeException(nameof(commitMode), commitMode, null);
        }

        SftpAtomicUpload.CommitRename(
            tempRemotePath,
            remotePath,
            atomicRename: (temp, final) => client.RenameFile(temp, final, isPosix: true),
            plainRename: (temp, final) => client.RenameFile(temp, final),
            remoteExists: client.Exists,
            canDemoteAtomicRenameFailure: IsAtomicRenameCapabilityFailure);
    }

    /// <summary>Creates a directory on the remote host.</summary>
    /// <param name="path">Full remote path for the new directory.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.CreateDirectory(path);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Deletes a file or directory. Directories are deleted recursively.
    /// </summary>
    /// <param name="path">Full remote path to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SftpPathGuard.ThrowIfProtectedRoot(path, "delete");

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SftpClient client = GetConnectedClient();
            string? directoryPath = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                (ISftpFile Entry, string DeletePath) target =
                    GetEntryWithoutFollowingTarget(client, path);

                if (target.Entry.IsSymbolicLink)
                {
                    // SftpClient.DeleteFile canonicalizes through SSH_FXP_REALPATH and can delete the link target.
                    // ISftpFile.Delete acts on the listed entry's stored path instead.
                    target.Entry.Delete();
                    return null;
                }

                if (target.Entry.IsDirectory)
                {
                    return target.DeletePath;
                }

                target.Entry.Delete();
                return null;
            }, ct).ConfigureAwait(false);

            if (directoryPath is not null)
            {
                await DeleteDirectoryViaExecAsync(directoryPath, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Changes the POSIX permissions of a remote file or directory.
    /// </summary>
    /// <param name="path">Full remote path.</param>
    /// <param name="mode">Permission mode as a short (e.g., 0x1ED for 755 octal).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Only the nine standard rwx permission bits are applied; setuid, setgid
    /// and sticky bits are not modified by this method. The mode value is a
    /// permissions bitmask (for example 0x1FF for 777), not decimal digits.
    /// </remarks>
    public async Task ChmodAsync(string path, short mode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var attrs = client.GetAttributes(path);

                attrs.OwnerCanRead = (mode & 0x100) != 0;
                attrs.OwnerCanWrite = (mode & 0x080) != 0;
                attrs.OwnerCanExecute = (mode & 0x040) != 0;
                attrs.GroupCanRead = (mode & 0x020) != 0;
                attrs.GroupCanWrite = (mode & 0x010) != 0;
                attrs.GroupCanExecute = (mode & 0x008) != 0;
                attrs.OthersCanRead = (mode & 0x004) != 0;
                attrs.OthersCanWrite = (mode & 0x002) != 0;
                attrs.OthersCanExecute = (mode & 0x001) != 0;

                client.SetAttributes(path, attrs);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>Renames (moves) a remote file or directory.</summary>
    /// <param name="oldPath">Current remote path.</param>
    /// <param name="newPath">New remote path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RenameAsync(
        string oldPath,
        string newPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.RenameFile(oldPath, newPath);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>Copies a remote file or directory to another path on the same server.</summary>
    /// <param name="sourcePath">Existing remote source path.</param>
    /// <param name="destinationPath">New remote destination path; must not already exist.</param>
    /// <param name="recursive">When the source is a directory, copies it and its contents recursively.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Prefers a server-side <c>cp</c> over a pinned SSH exec channel: instant, no client bandwidth, and
    /// it preserves POSIX mode/timestamps (<c>cp -p</c>/<c>cp -a</c>). Falls back to the download-to-temp
    /// + re-upload roundtrip when the exec channel is unavailable, the command fails, or no pinned
    /// connection context was retained. The roundtrip remains the correctness backstop. Neither path is
    /// atomic.
    /// </remarks>
    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // No-overwrite + source-type/recursive validation runs up front so the fast (server-side) and
        // slow (roundtrip) paths share identical semantics. The roundtrip planner re-checks these
        // defensively; the destination still does not exist at that point, so the re-probe is harmless.
        if (await RemoteExistsAsync(destinationPath, ct).ConfigureAwait(false))
        {
            throw new IOException($"Refused to copy: destination already exists: {destinationPath}");
        }

        bool sourceIsDirectory = await RemoteIsDirectoryAsync(sourcePath, ct).ConfigureAwait(false);
        if (sourceIsDirectory && !recursive)
        {
            throw new IOException("Source is a directory; set recursive=true.");
        }

        if (await TryServerSideCopyAsync(sourcePath, destinationPath, recursive, ct).ConfigureAwait(false))
        {
            return;
        }

        await RunRoundtripCopyAsync(sourcePath, destinationPath, recursive, ct).ConfigureAwait(false);
    }

    private Task RunRoundtripCopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        CancellationToken ct)
    {
        var ops = new RemoteCopyOps(
            DestinationExistsAsync: RemoteExistsAsync,
            SourceIsDirectoryAsync: RemoteIsDirectoryAsync,
            ListChildNamesAsync: ListChildNamesAsync,
            CopyFileAsync: CopyFileViaRoundtripAsync,
            CreateDirectoryAsync: CreateDirectoryAsync);

        return RemoteCopyPlanner.CopyAsync(sourcePath, destinationPath, recursive, ops, ct);
    }

    /// <summary>
    /// Attempts a server-side <c>cp</c> over a short-lived SSH exec channel pinned to the host key
    /// resolved at connect time. Returns true on exit status 0; returns false (fall back to roundtrip)
    /// when no pinned context was retained, the command exits non-zero, the command outruns
    /// <see cref="ServerSideCopyCommandTimeout"/>, or the channel/transport fails.
    /// Cancellation propagates; programming errors are never swallowed.
    /// </summary>
    /// <remarks>
    /// The exec channel is driven by <see cref="ISftpExecCommandRunner"/>, which tears the SSH client
    /// down on cancellation so the request reaches the running <c>cp</c>. The previous inline
    /// implementation tested the token once before connecting and then blocked in a synchronous
    /// <c>Execute()</c>: cancelling during the copy of a large tree had no effect at all, because a
    /// token cannot interrupt a delegate already running inside <see cref="Task.Run(Action)"/>.
    /// A cancelled copy must NOT fall back to the roundtrip - that would restart the very transfer
    /// the user just cancelled - so <see cref="OperationCanceledException"/> is rethrown, and only
    /// the timeout of an otherwise-live request degrades to the roundtrip.
    /// </remarks>
    internal async Task<bool> TryServerSideCopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        CancellationToken ct)
    {
        SshConnectionParams? connectionParams = _connectionParams;
        PinnedFingerprintVerifier? pinnedVerifier = _pinnedHostKeyVerifier;
        ISftpExecCommandRunner? runner = _injectedExecCommandRunner;
        if (runner is null)
        {
            if (connectionParams is null || pinnedVerifier is null)
            {
                return false;
            }

            runner = new SshNetSftpExecCommandRunner(connectionParams, pinnedVerifier);
        }

        string host = connectionParams?.Host ?? "the remote host";
        string command = ServerSideCopyCommand.Build(sourcePath, destinationPath, recursive);

        // A generous cap on the command itself, distinct from the caller's token: reaching it means the
        // exec channel is unproductive, not that the user asked to stop, so it degrades to the roundtrip.
        using CancellationTokenSource commandCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        commandCts.CancelAfter(ServerSideCopyCommandTimeout);

        try
        {
            SftpExecResult result = await runner.ExecuteAsync(command, commandCts.Token)
                .ConfigureAwait(false);

            if (result.ExitStatus == SuccessfulExitStatus)
            {
                return true;
            }

            // Collision, EXDEV, and missing-tool failures deliberately share this correctness fallback.
            // Parsing stderr would be server-specific and cannot make the roundtrip commit safer.
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] SFTP server-side copy on {host} exited {result.ExitStatus}; "
                + $"falling back to roundtrip. stderr: {result.StandardError}");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] SFTP server-side copy on {host} exceeded "
                + $"{ServerSideCopyCommandTimeout.TotalMinutes:0} minutes; falling back to roundtrip.");
            return false;
        }
        catch (HostKeyRejectedException)
        {
            // A host-key mismatch on the freshly-opened exec connection is a potential MITM signal, so it
            // gets its own explicit log line rather than being lumped in with routine failures. Fail
            // closed: never proceed on the unverified channel; fall back to the roundtrip over the
            // already-pinned, already-trusted SftpClient. Host + protocol context only, no credentials.
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] host-key mismatch on server-side copy exec channel for {host} "
                + "(possible MITM); falling back to roundtrip over the trusted SFTP channel.");
            return false;
        }
        catch (Exception ex) when (
            ex is Renci.SshNet.Common.SshException
                or System.Net.Sockets.SocketException
                or IOException
                or TimeoutException)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] SFTP server-side copy unavailable on {host} "
                + $"({ex.GetType().Name}); falling back to roundtrip. {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RemoteExistsAsync(string path, CancellationToken ct)
    {
        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return client.Exists(path);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<bool> RemoteIsDirectoryAsync(string path, CancellationToken ct)
    {
        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return client.Get(path).IsDirectory;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<IReadOnlyList<string>> ListChildNamesAsync(string path, CancellationToken ct)
    {
        IReadOnlyList<SftpFileInfo> entries = await ListDirectoryAsync(path, ct).ConfigureAwait(false);
        var names = new List<string>(entries.Count);
        foreach (SftpFileInfo entry in entries)
        {
            names.Add(entry.Name);
        }

        return names;
    }

    private async Task CopyFileViaRoundtripAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        string localTemp = RemoteCopyLocalTemp.Create();
        try
        {
            await DownloadFileAsync(sourcePath, localTemp, ct).ConfigureAwait(false);
            await UploadFileAsync(
                    localTemp,
                    destinationPath,
                    UploadCommitMode.PublishIfAbsent,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            RemoteCopyLocalTemp.TryDelete(localTemp);
        }
    }

    /// <summary>Disconnects from the remote host and releases the SFTP client.</summary>
    public void Disconnect()
    {
        bool lockTaken = _clientLock.Wait(_disconnectLockTimeout);
        bool disconnected = false;
        try
        {
            SftpClient? client = DetachClient();
            if (client is null)
            {
                return;
            }

            client.ErrorOccurred -= OnErrorOccurred;

            if (lockTaken && client.IsConnected)
            {
                try
                {
                    client.Disconnect();
                }
                catch (Exception ex)
                {
                    Heimdall.Core.Logging.FileLogger.Warn($"[SftpBrowser] disconnect: {ex.Message}");
                }
            }

            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                string teardownKind = lockTaken ? "dispose" : "forced dispose";
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[SftpBrowser] {teardownKind} during disconnect suppressed: {ex.Message}");
            }

            disconnected = true;
        }
        finally
        {
            if (lockTaken)
            {
                _clientLock.Release();
            }
        }

        if (disconnected)
        {
            Disconnected?.Invoke(null);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Disconnect();
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private SftpClient GetConnectedClient()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

        SftpClient? client = Volatile.Read(ref _client);
        if (client is null || !client.IsConnected)
        {
            throw new InvalidOperationException("SFTP browser is not connected.");
        }

        return client;
    }

    private SftpClient? DetachClient()
    {
        SftpClient? client = Interlocked.Exchange(ref _client, null);
        DropConnectionContext();
        return client;
    }

    private void DropConnectionContext()
    {
        Interlocked.Exchange(ref _connectionParams, null);
        Interlocked.Exchange(ref _pinnedHostKeyVerifier, null);
    }

    private static bool IsAtomicRenameCapabilityFailure(Exception exception)
    {
        return exception is NotSupportedException
            or Renci.SshNet.Common.SftpException { StatusCode: StatusCode.OperationUnsupported };
    }

    internal static void ApplyUploadModeBeforeCommit(
        string finalRemotePath,
        uint targetPermissions,
        uint tempPermissions,
        Action<uint> applyMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);
        ArgumentNullException.ThrowIfNull(applyMode);

        uint? modeToApply = SftpModePreservation.ResolveModeToApply(
            targetPermissions,
            tempPermissions);
        if (modeToApply is null)
        {
            return;
        }

        try
        {
            applyMode(modeToApply.Value);
        }
        catch (Exception ex)
        {
            string targetMode = FormatPermissionMode(targetPermissions);
            string tempMode = FormatPermissionMode(tempPermissions);
            if (SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
                    targetPermissions,
                    tempPermissions))
            {
                throw new InvalidOperationException(
                    $"SFTP mode preservation failed for '{finalRemotePath}': target mode {targetMode}, "
                    + $"temporary mode {tempMode}; commit refused because exact mode preservation is required.",
                    ex);
            }

            Heimdall.Core.Logging.FileLogger.Warn(
                $"SFTP mode preservation failed for '{finalRemotePath}' (target mode {targetMode}, "
                + $"temporary mode {tempMode}); commit will continue because permissions are not widened: "
                + ex.Message);
        }
    }

    private static void ApplyPermissionMode(SftpFileAttributes attributes, uint mode)
    {
        attributes.IsUIDBitSet = (mode & SftpModePreservation.SetUserIdBit) != 0;
        attributes.IsGroupIDBitSet = (mode & SftpModePreservation.SetGroupIdBit) != 0;
        attributes.IsStickyBitSet = (mode & SftpModePreservation.StickyBit) != 0;
        attributes.OwnerCanRead = (mode & SftpModePreservation.OwnerReadBit) != 0;
        attributes.OwnerCanWrite = (mode & SftpModePreservation.OwnerWriteBit) != 0;
        attributes.OwnerCanExecute = (mode & SftpModePreservation.OwnerExecuteBit) != 0;
        attributes.GroupCanRead = (mode & SftpModePreservation.GroupReadBit) != 0;
        attributes.GroupCanWrite = (mode & SftpModePreservation.GroupWriteBit) != 0;
        attributes.GroupCanExecute = (mode & SftpModePreservation.GroupExecuteBit) != 0;
        attributes.OthersCanRead = (mode & SftpModePreservation.OthersReadBit) != 0;
        attributes.OthersCanWrite = (mode & SftpModePreservation.OthersWriteBit) != 0;
        attributes.OthersCanExecute = (mode & SftpModePreservation.OthersExecuteBit) != 0;
    }

    private static uint GetPermissionMode(SftpFileAttributes attributes)
    {
        uint mode = 0;
        if (attributes.IsUIDBitSet) mode |= SftpModePreservation.SetUserIdBit;
        if (attributes.IsGroupIDBitSet) mode |= SftpModePreservation.SetGroupIdBit;
        if (attributes.IsStickyBitSet) mode |= SftpModePreservation.StickyBit;
        if (attributes.OwnerCanRead) mode |= SftpModePreservation.OwnerReadBit;
        if (attributes.OwnerCanWrite) mode |= SftpModePreservation.OwnerWriteBit;
        if (attributes.OwnerCanExecute) mode |= SftpModePreservation.OwnerExecuteBit;
        if (attributes.GroupCanRead) mode |= SftpModePreservation.GroupReadBit;
        if (attributes.GroupCanWrite) mode |= SftpModePreservation.GroupWriteBit;
        if (attributes.GroupCanExecute) mode |= SftpModePreservation.GroupExecuteBit;
        if (attributes.OthersCanRead) mode |= SftpModePreservation.OthersReadBit;
        if (attributes.OthersCanWrite) mode |= SftpModePreservation.OthersWriteBit;
        if (attributes.OthersCanExecute) mode |= SftpModePreservation.OthersExecuteBit;
        return mode;
    }

    private static string FormatPermissionMode(uint permissions)
    {
        return Convert.ToString(SftpModePreservation.GetMode(permissions), 8).PadLeft(4, '0');
    }

    private static ISftpFile? TryGetEntryWithoutFollowingTarget(SftpClient client, string path)
    {
        try
        {
            return GetEntryWithoutFollowingTarget(client, path).Entry;
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return null;
        }
    }

    private void OnErrorOccurred(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
    {
        SshSessionFailureDispatcher.Dispatch(
            e.Exception,
            SecurityEventOccurred,
            info => Disconnected?.Invoke(info.Message));
    }

    private static (ISftpFile Entry, string DeletePath) GetEntryWithoutFollowingTarget(SftpClient client, string path)
    {
        var trimmedPath = path.TrimEnd('/');
        var normalizedPath = trimmedPath.Length == 0 ? "/" : trimmedPath;

        if (normalizedPath == "/")
        {
            return (client.Get(normalizedPath), normalizedPath);
        }

        var lastSlash = normalizedPath.LastIndexOf('/');
        var parentPath = lastSlash switch
        {
            < 0 => ".",
            0 => "/",
            _ => normalizedPath[..lastSlash]
        };
        var entryName = lastSlash < 0
            ? normalizedPath
            : normalizedPath[(lastSlash + 1)..];

        // SSH.NET 2025.1.0's Get/GetAttributes canonicalize via REALPATH first.
        // A parent listing gives lstat-style entries, so symlinks, including
        // broken ones, are unlinked as entries instead of following their target.
        foreach (ISftpFile entry in client.ListDirectory(parentPath))
        {
            if (string.Equals(entry.Name, entryName, StringComparison.Ordinal))
            {
                return (entry, normalizedPath);
            }
        }

        throw new Renci.SshNet.Common.SftpPathNotFoundException(
            $"Remote path not found: {path}");
    }

    internal async Task DeleteDirectoryViaExecAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        ISftpExecCommandRunner? runner = _injectedExecCommandRunner;
        if (runner is null)
        {
            SshConnectionParams? connectionParams = _connectionParams;
            PinnedFingerprintVerifier? pinnedVerifier = _pinnedHostKeyVerifier;
            if (connectionParams is null || pinnedVerifier is null)
            {
                throw new RemoteRecursiveDeleteException(
                    RemoteRecursiveDeleteFailureReason.ExecUnavailable);
            }

            runner = new SshNetSftpExecCommandRunner(connectionParams, pinnedVerifier);
        }

        string command = RemoteDeleteCommand.Build(path);
        SftpExecResult result;
        try
        {
            result = await runner.ExecuteAsync(command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HostKeyRejectedException ex)
        {
            string rejectionKind = ex.IsMismatch ? "mismatch" : "rejection";
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] host-key {rejectionKind} on recursive-delete exec channel; "
                + "recursive deletion refused.");
            throw new RemoteRecursiveDeleteException(
                RemoteRecursiveDeleteFailureReason.ExecUnavailable,
                ex);
        }
        catch (Exception ex) when (
            ex is Renci.SshNet.Common.SshException
                or System.Net.Sockets.SocketException
                or IOException
                or TimeoutException)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] recursive-delete exec channel unavailable ({ex.GetType().Name}); "
                + "recursive deletion refused.");
            throw new RemoteRecursiveDeleteException(
                RemoteRecursiveDeleteFailureReason.ExecUnavailable,
                ex);
        }

        if (result.ExitStatus == SuccessfulExitStatus)
        {
            return;
        }

        string standardError = result.StandardError ?? string.Empty;
        Heimdall.Core.Logging.FileLogger.Warn(
            $"[SftpBrowser] recursive-delete command exited {result.ExitStatus}. "
            + $"stderr: {standardError}");

        RemoteRecursiveDeleteFailureReason reason = result.ExitStatus switch
        {
            CommandNotFoundExitStatus => RemoteRecursiveDeleteFailureReason.ShellOrRmUnavailable,
            _ when standardError.Contains(
                PermissionDeniedDiagnostic,
                StringComparison.OrdinalIgnoreCase) => RemoteRecursiveDeleteFailureReason.PermissionDenied,
            _ => RemoteRecursiveDeleteFailureReason.CommandFailed,
        };

        throw new RemoteRecursiveDeleteException(reason);
    }

    private static SftpFileInfo ToSftpFileInfo(ISftpFile entry)
    {
        // Build a rwxrwxrwx permission string from the file attributes
        string permissions = FormatPermissions(entry);
        RemoteEntryKind kind = GetRemoteEntryKind(entry);

        return new SftpFileInfo(
            Name: entry.Name,
            FullPath: entry.FullName,
            Kind: kind,
            Size: entry.Attributes.Size,
            LastModified: entry.LastWriteTimeUtc,
            Permissions: permissions,
            Owner: entry.Attributes.GetOwnerIdOrDefault().ToString(),
            Group: entry.Attributes.GetGroupIdOrDefault().ToString());
    }

    private static RemoteEntryKind GetRemoteEntryKind(ISftpFile entry)
    {
        if (entry.IsSymbolicLink)
        {
            return RemoteEntryKind.SymbolicLink;
        }

        if (entry.IsDirectory)
        {
            return RemoteEntryKind.Directory;
        }

        if (entry.IsNamedPipe)
        {
            return RemoteEntryKind.Fifo;
        }

        if (entry.IsSocket)
        {
            return RemoteEntryKind.Socket;
        }

        if (entry.IsBlockDevice || entry.IsCharacterDevice)
        {
            return RemoteEntryKind.Device;
        }

        if (entry.IsRegularFile)
        {
            return RemoteEntryKind.File;
        }

        Heimdall.Core.Logging.FileLogger.Debug(
            $"SftpBrowser: treating unrecognized remote entry type as a file: {entry.FullName}");
        return RemoteEntryKind.File;
    }

    private static string FormatPermissions(ISftpFile entry)
    {
        var attrs = entry.Attributes;

        // SSH.NET exposes permission bits via Attributes
        // Build standard rwxrwxrwx string from the octal permissions
        int mode = attrs.GetPermissionsOrDefault();

        return string.Create(9, mode, static (span, m) =>
        {
            span[0] = (m & 0x100) != 0 ? 'r' : '-';
            span[1] = (m & 0x080) != 0 ? 'w' : '-';
            span[2] = (m & 0x040) != 0 ? 'x' : '-';
            span[3] = (m & 0x020) != 0 ? 'r' : '-';
            span[4] = (m & 0x010) != 0 ? 'w' : '-';
            span[5] = (m & 0x008) != 0 ? 'x' : '-';
            span[6] = (m & 0x004) != 0 ? 'r' : '-';
            span[7] = (m & 0x002) != 0 ? 'w' : '-';
            span[8] = (m & 0x001) != 0 ? 'x' : '-';
        });
    }
}

/// <summary>
/// Extension helpers to safely read SSH.NET <see cref="SftpFileAttributes"/> fields
/// that may not be present in all SFTP server implementations.
/// These fields are listing metadata only; missing values must not fail the browser.
/// </summary>
internal static class SftpFileAttributesExtensions
{
    public static int GetOwnerIdOrDefault(this SftpFileAttributes attrs)
    {
        try { return attrs.UserId; }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[SftpBrowser] read UserId: {ex.Message}"); return -1; }
    }

    public static int GetGroupIdOrDefault(this SftpFileAttributes attrs)
    {
        try { return attrs.GroupId; }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[SftpBrowser] read GroupId: {ex.Message}"); return -1; }
    }

    public static int GetPermissionsOrDefault(this SftpFileAttributes attrs)
    {
        try
        {
            // SftpFileAttributes exposes permissions as bools. Compose the raw
            // Unix bitmask here; the display layer converts it back to octal digits.
            int mode = 0;
            if (attrs.OwnerCanRead) mode |= 0x100;
            if (attrs.OwnerCanWrite) mode |= 0x080;
            if (attrs.OwnerCanExecute) mode |= 0x040;
            if (attrs.GroupCanRead) mode |= 0x020;
            if (attrs.GroupCanWrite) mode |= 0x010;
            if (attrs.GroupCanExecute) mode |= 0x008;
            if (attrs.OthersCanRead) mode |= 0x004;
            if (attrs.OthersCanWrite) mode |= 0x002;
            if (attrs.OthersCanExecute) mode |= 0x001;
            return mode;
        }
        catch (Exception ex)
        {
            // These attributes are display-only; some servers omit or reject them.
            Heimdall.Core.Logging.FileLogger.Warn($"[SftpBrowser] read permissions: {ex.Message}");
            return 0;
        }
    }
}

/// <summary>Represents a file or directory entry from a remote SFTP listing.</summary>
/// <param name="Name">File or directory name (without path).</param>
/// <param name="FullPath">Full remote path.</param>
/// <param name="Kind">Remote filesystem entry type.</param>
/// <param name="Size">File size in bytes (0 for directories).</param>
/// <param name="LastModified">Last modification time (UTC).</param>
/// <param name="Permissions">POSIX permission string, e.g., "rwxr-xr-x".</param>
/// <param name="Owner">Numeric owner ID as a string.</param>
/// <param name="Group">Numeric group ID as a string.</param>
public sealed record SftpFileInfo(
    string Name,
    string FullPath,
    RemoteEntryKind Kind,
    long Size,
    DateTime LastModified,
    string Permissions,
    string Owner,
    string Group)
{
    /// <summary>Gets whether this entry is a directory.</summary>
    public bool IsDirectory => Kind == RemoteEntryKind.Directory;

    /// <summary>Gets whether this entry is a regular file (not a directory, link, fifo, socket or device).</summary>
    public bool IsRegularFile => Kind == RemoteEntryKind.File;
}

/// <summary>Progress information for an SFTP file transfer.</summary>
/// <param name="FileName">Name of the file being transferred.</param>
/// <param name="BytesTransferred">Number of bytes transferred so far.</param>
/// <param name="TotalBytes">Total file size in bytes.</param>
/// <param name="IsUpload">True for uploads, false for downloads.</param>
public sealed record SftpTransferProgress(
    string FileName,
    long BytesTransferred,
    long TotalBytes,
    bool IsUpload);

internal sealed class CancellationAwareStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationToken _ct;

    public CancellationAwareStream(Stream inner, CancellationToken ct)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ct = ct;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
    {
        _ct.ThrowIfCancellationRequested();
        _inner.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        int bytesRead = _inner.Read(buffer, offset, count);
        _ct.ThrowIfCancellationRequested();
        return bytesRead;
    }

    public override int Read(Span<byte> buffer)
    {
        _ct.ThrowIfCancellationRequested();
        int bytesRead = _inner.Read(buffer);
        _ct.ThrowIfCancellationRequested();
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _ct.ThrowIfCancellationRequested();
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _ct.ThrowIfCancellationRequested();
        _inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        _inner.Write(buffer, offset, count);
        _ct.ThrowIfCancellationRequested();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _ct.ThrowIfCancellationRequested();
        _inner.Write(buffer);
        _ct.ThrowIfCancellationRequested();
    }
}
