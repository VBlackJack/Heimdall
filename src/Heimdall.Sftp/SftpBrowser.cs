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
    private SftpClient? _client;
    private bool _disposed;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // Connection context retained so a short-lived SSH exec channel can be opened later for a
    // server-side copy, pinned to the SAME host key resolved at connect time (fail-closed: no
    // re-prompt, no auto-accept). Both are cleared on Disconnect alongside the SFTP client.
    private SshConnectionParams? _connectionParams;
    private PinnedFingerprintVerifier? _pinnedHostKeyVerifier;

    // Generous bound on a server-side cp; a server-local copy is fast, but a large tree may take a
    // while. On timeout the copy falls back to the roundtrip path rather than failing.
    private static readonly TimeSpan ServerSideCopyCommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Raised when the current working directory changes.</summary>
    public event Action<string>? DirectoryChanged;

    /// <summary>Raised during file transfers to report progress.</summary>
    public event Action<SftpTransferProgress>? TransferProgress;

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
    public bool IsConnected => _client?.IsConnected ?? false;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connectionParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        if (_client?.IsConnected == true)
        {
            throw new InvalidOperationException("SFTP browser is already connected.");
        }

        var pinnedVerifier = await SshConnectionFactory.ResolveHostKeyAsync(
                connectionParams,
                hostKeyStore,
                hostKeyVerifier,
                ct)
            .ConfigureAwait(false);

        var connectionInfo = SshConnectionFactory.Create(connectionParams);
        var client = new SftpClient(connectionInfo);
        _client = client;

        SshConnectionFactory.AttachPinnedHostKeyVerification(
            client,
            connectionParams,
            pinnedVerifier);

        // Retain the resolved params + pinned verifier for the later server-side-copy exec channel.
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
            client.ErrorOccurred -= OnErrorOccurred;
            client.Dispose();
            _client = null;
            _connectionParams = null;
            _pinnedHostKeyVerifier = null;
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
        var client = GetConnectedClient();
        string targetPath = path ?? CurrentDirectory;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        var client = GetConnectedClient();

        string dir = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return client.WorkingDirectory ?? "/";
        }, ct).ConfigureAwait(false);

        return dir;
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
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.ChangeDirectory(path);
            }, ct).ConfigureAwait(false);

            CurrentDirectory = client.WorkingDirectory ?? "/";
            DirectoryChanged?.Invoke(CurrentDirectory);
        }
        finally
        {
            _clientLock.Release();
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
        var client = GetConnectedClient();

        string fileName = Path.GetFileName(remotePath);
        long totalBytes = 0;
        string tempPath = AtomicLocalFile.CreateTempPath(localPath);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
    public async Task UploadFileAsync(
        string localPath,
        string remotePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var client = GetConnectedClient();

        string fileName = Path.GetFileName(localPath);
        var fileInfo = new FileInfo(localPath);
        long totalBytes = fileInfo.Length;
        string tempRemotePath = SftpAtomicUpload.CreateRemoteTempPath(remotePath);

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await using var fileStream = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    useAsync: true);
                using CancellationAwareStream inputStream = new(fileStream, ct);

                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    client.UploadFile(inputStream, tempRemotePath, bytesTransferred =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        TransferProgress?.Invoke(new SftpTransferProgress(
                            fileName,
                            (long)bytesTransferred,
                            totalBytes,
                            IsUpload: true));
                    });
                    ct.ThrowIfCancellationRequested();
                    SftpAtomicUpload.CommitRename(
                        tempRemotePath,
                        remotePath,
                        atomicRename: (temp, final) => client.RenameFile(temp, final, isPosix: true),
                        plainRename: (temp, final) => client.RenameFile(temp, final),
                        remoteExists: client.Exists,
                        deleteRemote: path =>
                        {
                            if (client.Exists(path))
                            {
                                client.DeleteFile(path);
                            }
                        });
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

    /// <summary>Creates a directory on the remote host.</summary>
    /// <param name="path">Full remote path for the new directory.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var (entry, deletePath) = GetEntryWithoutFollowingTarget(client, path);

                if (entry.IsSymbolicLink)
                {
                    client.DeleteFile(deletePath);
                }
                else if (entry.IsDirectory)
                {
                    DeleteDirectoryRecursive(client, deletePath, ct);
                }
                else
                {
                    client.DeleteFile(deletePath);
                }
            }, ct).ConfigureAwait(false);
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
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        _ = GetConnectedClient();

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
    /// when no pinned context was retained, the command exits non-zero, or the channel/transport fails.
    /// Cancellation propagates; programming errors are never swallowed.
    /// </summary>
    private async Task<bool> TryServerSideCopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        CancellationToken ct)
    {
        SshConnectionParams? connectionParams = _connectionParams;
        PinnedFingerprintVerifier? pinnedVerifier = _pinnedHostKeyVerifier;
        if (connectionParams is null || pinnedVerifier is null)
        {
            return false;
        }

        string command = ServerSideCopyCommand.Build(sourcePath, destinationPath, recursive);
        SshClient? ssh = null;
        try
        {
            var connectionInfo = SshConnectionFactory.Create(connectionParams);
            ssh = new SshClient(connectionInfo);
            SshConnectionFactory.AttachPinnedHostKeyVerification(ssh, connectionParams, pinnedVerifier);

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                ssh.Connect();

                using SshCommand cmd = ssh.CreateCommand(command);
                cmd.CommandTimeout = ServerSideCopyCommandTimeout;
                cmd.Execute();

                if (cmd.ExitStatus == 0)
                {
                    return true;
                }

                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[SftpBrowser] SFTP server-side copy on {connectionParams.Host} exited {cmd.ExitStatus}; "
                    + $"falling back to roundtrip. stderr: {cmd.Error}");
                return false;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is Renci.SshNet.Common.SshException
                or System.Net.Sockets.SocketException
                or IOException
                or TimeoutException
                or HostKeyRejectedException)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SftpBrowser] SFTP server-side copy unavailable on {connectionParams.Host} "
                + $"({ex.GetType().Name}); falling back to roundtrip. {ex.Message}");
            return false;
        }
        finally
        {
            if (ssh is not null)
            {
                try
                {
                    ssh.Disconnect();
                }
                catch (Exception ex)
                {
                    Heimdall.Core.Logging.FileLogger.Debug(
                        $"[SftpBrowser] server-side copy exec disconnect suppressed: {ex.Message}");
                }

                ssh.Dispose();
            }
        }
    }

    private async Task<bool> RemoteExistsAsync(string path, CancellationToken ct)
    {
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        var client = GetConnectedClient();

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
            await UploadFileAsync(localTemp, destinationPath, ct).ConfigureAwait(false);
        }
        finally
        {
            RemoteCopyLocalTemp.TryDelete(localTemp);
        }
    }

    /// <summary>Disconnects from the remote host and releases the SFTP client.</summary>
    public void Disconnect()
    {
        if (_client is null)
        {
            return;
        }

        _client.ErrorOccurred -= OnErrorOccurred;

        if (_client.IsConnected)
        {
            try
            {
                _client.Disconnect();
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[SftpBrowser] disconnect: {ex.Message}");
            }
        }

        _client.Dispose();
        _client = null;

        // Drop the retained server-side-copy context so a torn-down session cannot open an exec channel.
        _connectionParams = null;
        _pinnedHostKeyVerifier = null;

        Disconnected?.Invoke(null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
        _clientLock.Dispose();
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private SftpClient GetConnectedClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is null || !_client.IsConnected)
        {
            throw new InvalidOperationException("SFTP browser is not connected.");
        }

        return _client;
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

    /// <summary>
    /// Hard cap on the depth of <see cref="DeleteDirectoryRecursive"/>.
    /// A malicious or corrupted remote filesystem with deep nesting cannot
    /// blow the managed stack; the iterative traversal also avoids the
    /// implicit per-level frame cost of the recursive form.
    /// </summary>
    internal const int MaxDeleteDepth = 256;

    private static void DeleteDirectoryRecursive(
        SftpClient client,
        string path,
        CancellationToken ct)
    {
        // Iterative post-order traversal with an explicit stack:
        // 1. Push (dir, expanded=false) for each directory we discover.
        // 2. On first pop, list its contents: delete files inline, push
        //    nested dirs (expanded=false), and push the dir itself with
        //    expanded=true so we revisit it after children are gone.
        // 3. On second pop (expanded=true), the directory is empty and we
        //    delete it.
        var stack = new Stack<(string Path, int Depth, bool Expanded)>();
        stack.Push((path, 0, false));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (currentPath, depth, expanded) = stack.Pop();

            if (depth > MaxDeleteDepth)
            {
                throw new InvalidOperationException(
                    $"Refused to delete '{currentPath}': remote directory depth exceeds {MaxDeleteDepth}.");
            }

            if (expanded)
            {
                client.DeleteDirectory(currentPath);
                continue;
            }

            // Re-queue this directory for its post-order delete pass.
            stack.Push((currentPath, depth, true));

            foreach (ISftpFile entry in client.ListDirectory(currentPath))
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Name is "." or "..")
                {
                    continue;
                }

                string fullPath = $"{currentPath.TrimEnd('/')}/{entry.Name}";

                if (entry.IsDirectory)
                {
                    stack.Push((fullPath, depth + 1, false));
                }
                else
                {
                    client.DeleteFile(fullPath);
                }
            }
        }
    }

    private static SftpFileInfo ToSftpFileInfo(ISftpFile entry)
    {
        // Build a rwxrwxrwx permission string from the file attributes
        string permissions = FormatPermissions(entry);

        return new SftpFileInfo(
            Name: entry.Name,
            FullPath: entry.FullName,
            IsDirectory: entry.IsDirectory,
            Size: entry.Attributes.Size,
            LastModified: entry.LastWriteTimeUtc,
            Permissions: permissions,
            Owner: entry.Attributes.GetOwnerIdOrDefault().ToString(),
            Group: entry.Attributes.GetGroupIdOrDefault().ToString());
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
/// <param name="IsDirectory">Whether this entry is a directory.</param>
/// <param name="Size">File size in bytes (0 for directories).</param>
/// <param name="LastModified">Last modification time (UTC).</param>
/// <param name="Permissions">POSIX permission string, e.g., "rwxr-xr-x".</param>
/// <param name="Owner">Numeric owner ID as a string.</param>
/// <param name="Group">Numeric group ID as a string.</param>
public sealed record SftpFileInfo(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastModified,
    string Permissions,
    string Owner,
    string Group);

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
