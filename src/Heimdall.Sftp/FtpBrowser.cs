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

using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using Heimdall.Core.Certificates;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.Sftp;

/// <summary>
/// FTP file browser backed by FluentFTP.
/// Provides the same <see cref="IRemoteBrowser"/> surface as <see cref="SftpBrowser"/>
/// so the embedded file browser view can work with both SFTP and FTP.
/// </summary>
public sealed class FtpBrowser : IRemoteBrowser
{
    private const int DefaultTimeoutMilliseconds = 30_000;
    private const X509ChainStatusFlags NonOverridableChainErrors =
        X509ChainStatusFlags.NotTimeValid
        | X509ChainStatusFlags.NotTimeNested
        | X509ChainStatusFlags.Revoked
        | X509ChainStatusFlags.NotSignatureValid
        | X509ChainStatusFlags.NotValidForUsage
        | X509ChainStatusFlags.Cyclic
        | X509ChainStatusFlags.InvalidExtension
        | X509ChainStatusFlags.InvalidPolicyConstraints
        | X509ChainStatusFlags.InvalidBasicConstraints
        | X509ChainStatusFlags.InvalidNameConstraints
        | X509ChainStatusFlags.HasNotSupportedNameConstraint
        | X509ChainStatusFlags.HasNotDefinedNameConstraint
        | X509ChainStatusFlags.HasNotPermittedNameConstraint
        | X509ChainStatusFlags.HasExcludedNameConstraint
        | X509ChainStatusFlags.CtlNotTimeValid
        | X509ChainStatusFlags.CtlNotSignatureValid
        | X509ChainStatusFlags.CtlNotValidForUsage
        | X509ChainStatusFlags.HasWeakSignature
        | X509ChainStatusFlags.NoIssuanceChainPolicy
        | X509ChainStatusFlags.ExplicitDistrust
        | X509ChainStatusFlags.HasNotSupportedCriticalExtension;

    private readonly SemaphoreSlim _opLock = new SemaphoreSlim(1, 1);

    /// <summary>How long a synchronous disconnect waits for the operation lock before forcing the teardown.</summary>
    private static readonly TimeSpan DisconnectLockTimeout = TimeSpan.FromMilliseconds(250);
    private readonly FtpsCertificateStore _certificateStore;
    private readonly IFtpsCertificateVerifier _certificateVerifier;
    private AsyncFtpClient? _client;
    private string? _host;
    private string? _username;
    private int _port;
    private bool _disposed;
    private bool _connected;
    private bool _useSsl;

    /// <inheritdoc/>
    public event Action<string>? DirectoryChanged;

    /// <inheritdoc/>
    public event Action<SftpTransferProgress>? TransferProgress;

    /// <inheritdoc/>
    public event Action<RemoteOperationWarning>? OperationWarningRaised;

    /// <inheritdoc/>
    public event Action<string?>? Disconnected;

    /// <inheritdoc/>
    public string CurrentDirectory { get; private set; } = "/";

    /// <inheritdoc/>
    public bool IsConnected => _connected;

    /// <summary>Whether FTP over TLS is enabled for the current session.</summary>
    public bool IsTlsEnabled => _useSsl;

    /// <summary>The host the browser is currently connected to, or null when disconnected.</summary>
    public string? Host => _host;

    /// <summary>The port the browser is currently connected to, or 0 when disconnected.</summary>
    public int Port => _port;

    /// <summary>The username used for the current FTP session, or null when disconnected.</summary>
    public string? Username => _username;

    public FtpBrowser()
        : this(new FtpsCertificateStore(), RejectingFtpsCertificateVerifier.Instance)
    {
    }

    public FtpBrowser(
        FtpsCertificateStore certificateStore,
        IFtpsCertificateVerifier certificateVerifier)
    {
        _certificateStore = certificateStore;
        _certificateVerifier = certificateVerifier;
    }

    /// <summary>
    /// Connects to the FTP server with the supplied credentials.
    /// </summary>
    public async Task ConnectAsync(
        string host,
        int port,
        string? username,
        string? password,
        bool passiveMode = true,
        bool useSsl = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connected)
        {
            throw new InvalidOperationException("FTP browser is already connected.");
        }

        _host = host;
        _port = port > 0 ? port : DefaultPorts.Ftp;
        _useSsl = useSsl;

        if (!useSsl && !string.IsNullOrEmpty(username))
        {
            FileLogger.Warn(
                "FtpBrowser: FTP session is using a cleartext channel. Prefer SFTP or FTPS when available.");
        }

        string effectiveUsername = string.IsNullOrEmpty(username) ? "anonymous" : username;
        _username = effectiveUsername;
        FtpConfig config = CreateConfig(passiveMode, useSsl);
        AsyncFtpClient client = new AsyncFtpClient(host, effectiveUsername, password ?? string.Empty, _port, config);
        FtpsCertificateRejectedException? certificateRejection = null;
        client.ValidateCertificate += (_, e) =>
        {
            try
            {
                e.Accept = ValidateServerCertificate(
                    host,
                    _port,
                    e.Certificate,
                    e.Chain,
                    e.PolicyErrors,
                    e.PolicyErrorMessage,
                    ct);
            }
            catch (FtpsCertificateRejectedException ex)
            {
                certificateRejection = ex;
                e.Accept = false;
            }
        };

        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            _host = null;
            _username = null;
            _port = 0;
            _connected = false;
            if (certificateRejection is not null)
            {
                throw certificateRejection;
            }

            throw;
        }

        _client = client;
        _connected = true;
        CurrentDirectory = "/";
        DirectoryChanged?.Invoke(CurrentDirectory);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
        string? path = null,
        CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            string targetPath = NormalizePath(path ?? CurrentDirectory);
            FtpListItem[] items = await client.GetListing(targetPath, ct).ConfigureAwait(false);
            List<SftpFileInfo> result = new List<SftpFileInfo>();

            foreach (FtpListItem item in items)
            {
                if (item.Name is "." or "..")
                {
                    continue;
                }

                SftpFileInfo? mapped = MapFtpItemToFileInfo(item, targetPath);
                if (mapped is not null)
                {
                    result.Add(mapped);
                }
            }

            return result;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            return CurrentDirectory;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? changedDirectory = null;

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            string resolved = ResolvePath(path, CurrentDirectory);
            bool exists = await client.DirectoryExists(resolved, ct).ConfigureAwait(false);
            if (!exists)
            {
                throw new DirectoryNotFoundException($"FTP directory not found: {resolved}");
            }

            CurrentDirectory = resolved;
            changedDirectory = CurrentDirectory;
        }
        finally
        {
            _opLock.Release();
        }

        if (changedDirectory is not null)
        {
            DirectoryChanged?.Invoke(changedDirectory);
        }
    }

    /// <inheritdoc/>
    public async Task DownloadFileAsync(
        string remotePath,
        string localPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        string fileName = Path.GetFileName(remotePath);
        string tempPath = AtomicLocalFile.CreateTempPath(localPath);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            try
            {
                long totalBytes = await client.GetFileSize(remotePath, 0, ct).ConfigureAwait(false);
                totalBytes = Math.Max(0, totalBytes);
                IProgress<FtpProgress> progress = CreateProgress(fileName, totalBytes, isUpload: false);
                FtpStatus status = await client.DownloadFile(
                    tempPath,
                    remotePath,
                    FtpLocalExists.Overwrite,
                    FtpVerify.None,
                    progress,
                    ct).ConfigureAwait(false);

                ThrowIfFailed(status, remotePath, "download");
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
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UploadFileAsync(
        string localPath,
        string remotePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        string fileName = Path.GetFileName(localPath);
        long totalBytes = LocalUploadSource.GetRequiredRegularFile(localPath).Length;
        string tempRemotePath = CreateUploadTempRemotePath(remotePath);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            try
            {
                IProgress<FtpProgress> progress = CreateProgress(fileName, totalBytes, isUpload: true);
                FtpStatus status = await client.UploadFile(
                    localPath,
                    tempRemotePath,
                    FtpRemoteExists.Overwrite,
                    false,
                    FtpVerify.None,
                    progress,
                    ct).ConfigureAwait(false);

                ThrowIfFailed(status, tempRemotePath, "upload");

                await FtpAtomicUpload.CommitRenameAsync(
                    tempRemotePath,
                    remotePath,
                    (path, token) => client.FileExists(path, token),
                    (source, destination, token) =>
                        client.MoveFile(source, destination, FtpRemoteExists.Skip, token),
                    (path, token) => client.DeleteFile(path, token),
                    ct,
                    onExistingTargetReplaced: () => OperationWarningRaised?.Invoke(
                        RemoteOperationWarning.CreateFtpExistingTargetReplaced(remotePath)))
                    .ConfigureAwait(false);
            }
            catch
            {
                await RollbackUploadTempFileAsync(client, tempRemotePath).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            await client.CreateDirectory(path, ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // FluentFTP's DeleteDirectory is recursive; the SFTP browser refuses the root here
        // and this one relied on its callers to.
        SftpPathGuard.ThrowIfProtectedRoot(path, "delete");
        string normalizedPath = NormalizePath(path);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            if (await client.DirectoryExists(normalizedPath, ct).ConfigureAwait(false))
            {
                await client.DeleteDirectory(normalizedPath, ct).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFile(normalizedPath, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException("Changing POSIX permissions is not supported for FTP connections.");
    }

    /// <inheritdoc/>
    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();

            // MoveFile with Skip rather than the low-level Rename: RNFR/RNTO is free to
            // replace the destination on many servers, and a plain rename onto a name
            // taken from a stale listing silently lost the file that was there. The
            // existence check is the client's and the window between it and the rename
            // remains; it is the strongest guarantee FTP can give.
            bool moved = await client.MoveFile(oldPath, newPath, FtpRemoteExists.Skip, ct).ConfigureAwait(false);
            if (!moved)
            {
                throw new IOException($"Refused to rename: destination already exists: {newPath}");
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always refuses. The copy contract requires that an existing destination is never
    /// overwritten, and FTP cannot honour it: every publish available in this client reduces to a
    /// client-side existence check followed by a plain rename, and RFC 959 says nothing about what
    /// a rename onto an existing destination does. A server that silently overwrites is
    /// conformant, so a destination created after the check would be destroyed.
    /// <para>
    /// Refusing is deliberate rather than best-effort. The previous implementation copied by
    /// roundtrip through the ordinary upload, whose commit replaces an existing destination and
    /// reports success, so any missed pre-check became silent data loss. SFTP keeps the feature
    /// because <c>SSH_FXP_RENAME</c> is specified to fail when the target exists, which is a
    /// guarantee this protocol does not provide.
    /// </para>
    /// <para>
    /// Uploading is unaffected: it never promised to preserve an existing destination and still
    /// replaces one when the user asks for it.
    /// </para>
    /// </remarks>
    /// <exception cref="RemoteCopyUnsupportedException">Always thrown.</exception>
    public Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        throw new RemoteCopyUnsupportedException(sourcePath, destinationPath, "FTP");
    }

    /// <inheritdoc/>
    public void Disconnect()
    {
        if (_disposed && _client is null)
        {
            return;
        }

        // Bounded, the way the SFTP browser decided it: an unbounded wait pinned a thread
        // for the whole of a stalled transfer. When the lock is not obtained the client is
        // torn down under the operation that holds it, which is what unblocks that
        // operation.
        bool lockTaken = _opLock.Wait(DisconnectLockTimeout);
        bool disconnected;
        try
        {
            disconnected = DisconnectCore();
        }
        finally
        {
            if (lockTaken)
            {
                _opLock.Release();
            }
        }

        if (disconnected)
        {
            Disconnected?.Invoke(null);
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed && _client is null)
        {
            return;
        }

        bool disconnected;
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            disconnected = DisconnectCore();
        }
        finally
        {
            _opLock.Release();
        }

        if (disconnected)
        {
            Disconnected?.Invoke(null);
        }
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

        // The lock is deliberately not disposed: an operation still awaiting it would meet
        // an ObjectDisposedException instead of the client teardown it is about to observe.
        // Same decision as the SFTP browser, which pins it by test.
    }

    private bool DisconnectCore()
    {
        if (_client is null)
        {
            return false;
        }

        _client.Dispose();
        _client = null;
        _connected = false;
        _host = null;
        _username = null;
        _port = 0;
        return true;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        // Ensure path starts with /
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // Collapse . and .. so the current directory cannot accumulate segments that every
        // listed path would then inherit; the collapse also removes a trailing slash.
        return RemotePathNormalizer.Collapse(path);
    }

    internal static string ResolvePath(string path, string currentDirectory)
    {
        if (path.StartsWith('/'))
        {
            return NormalizePath(path);
        }

        // Relative path: resolve against current directory
        string basePath = currentDirectory.TrimEnd('/');
        return NormalizePath($"{basePath}/{path}");
    }

    internal static string CreateUploadTempRemotePath(string finalRemotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);

        return $"{finalRemotePath}.{Guid.NewGuid():N}.part";
    }

    internal bool ValidateServerCertificate(
        string host,
        int port,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors policyErrors,
        string? policyErrorMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (certificate is null)
        {
            throw new FtpsCertificateRejectedException(
                host,
                port,
                "(missing)",
                null,
                isMismatch: false,
                "FTPS server did not present a certificate.");
        }

        ct.ThrowIfCancellationRequested();

        using var certificate2 = CopyCertificate(certificate);
        string fingerprint = CertificateFingerprint.ComputeSha256(certificate2);
        string validationErrors = BuildValidationErrors(policyErrors, chain, policyErrorMessage);

        var sessionEntry = _certificateStore.GetSessionEntry(host, port);
        if (sessionEntry is not null
            && FtpsCertificateStore.ConstantTimeEquals(sessionEntry.Fingerprint, fingerprint))
        {
            EnsurePinnedCertificateRemainsValid(
                host,
                port,
                certificate2,
                chain,
                policyErrors,
                validationErrors,
                fingerprint);
            return true;
        }

        if (sessionEntry is not null)
        {
            throw new FtpsCertificateRejectedException(
                host,
                port,
                fingerprint,
                sessionEntry.Fingerprint,
                isMismatch: true,
                "FTPS certificate fingerprint mismatch.");
        }

        var storedEntry = _certificateStore.GetEntry(host, port);
        if (storedEntry is not null)
        {
            if (!FtpsCertificateStore.ConstantTimeEquals(storedEntry.Fingerprint, fingerprint))
            {
                throw new FtpsCertificateRejectedException(
                    host,
                    port,
                    fingerprint,
                    storedEntry.Fingerprint,
                    isMismatch: true,
                    "FTPS certificate fingerprint mismatch.");
            }

            EnsurePinnedCertificateRemainsValid(
                host,
                port,
                certificate2,
                chain,
                policyErrors,
                validationErrors,
                fingerprint);
            _certificateStore.RefreshLastSeen(host, port);
            return true;
        }

        var entry = CreateCertificateEntry(
            certificate2,
            fingerprint,
            validationErrors,
            policyErrors == SslPolicyErrors.None
                ? FtpsCertificateSource.SystemValidated
                : FtpsCertificateSource.UserConfirmed);

        if (policyErrors == SslPolicyErrors.None)
        {
            _certificateStore.Trust(host, port, entry);
            return true;
        }

        var prompt = new FtpsCertificatePrompt(
            host,
            port,
            fingerprint,
            storedEntry?.Fingerprint,
            entry.Subject,
            entry.Issuer,
            entry.NotBefore,
            entry.NotAfter,
            validationErrors);
        FtpsCertificateDecision decision = _certificateVerifier
            .VerifyAsync(prompt, ct)
            .GetAwaiter()
            .GetResult();

        if (decision == FtpsCertificateDecision.Accept)
        {
            _certificateStore.Trust(host, port, entry);
            return true;
        }

        if (decision == FtpsCertificateDecision.TrustOnce)
        {
            _certificateStore.TrustForSession(host, port, entry);
            return true;
        }

        throw new FtpsCertificateRejectedException(
            host,
            port,
            fingerprint,
            null,
            isMismatch: false,
            "FTPS certificate was rejected.");
    }

    private static void EnsurePinnedCertificateRemainsValid(
        string host,
        int port,
        X509Certificate2 certificate,
        X509Chain? chain,
        SslPolicyErrors policyErrors,
        string validationErrors,
        string fingerprint)
    {
        if (policyErrors == SslPolicyErrors.None)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        bool outsideValidityPeriod =
            now < certificate.NotBefore.ToUniversalTime()
            || now > certificate.NotAfter.ToUniversalTime();
        bool hasNonOverridableChainError = chain?.ChainStatus.Any(
            static status => (status.Status & NonOverridableChainErrors) != 0) == true;

        if (!outsideValidityPeriod && !hasNonOverridableChainError)
        {
            return;
        }

        throw new FtpsCertificateRejectedException(
            host,
            port,
            fingerprint,
            fingerprint,
            isMismatch: false,
            $"FTPS pinned certificate failed non-overridable validity checks: {validationErrors}");
    }

    /// <summary>
    /// Classifies an FTP entry that the library did not report as a link or a directory.
    /// </summary>
    /// <remarks>
    /// The permission string comes in two shapes and they must not be confused. A ten-character value
    /// carries an explicit type character first (<c>-rw-r--r--</c>), so that character is authoritative.
    /// A nine-character value is mode-only (<c>rw-r--r--</c>) and says nothing at all about the type, so
    /// reading its first character as a type would classify a plain file by whichever permission bit
    /// happened to be first.
    /// <para>
    /// Without a type character, the library's own value is all there is. Any value other than
    /// <see cref="FtpObjectType.File"/> is one this build cannot interpret, so the entry is not treated as
    /// a regular file.
    /// <para>
    /// One residue is worth naming rather than hiding: <see cref="FtpObjectType.File"/> is itself that
    /// enum's zero value, so an item whose type was never assigned is indistinguishable here from one
    /// positively reported as a file. That is the same shape this change removed from
    /// <see cref="RemoteEntryKind"/>, and it cannot be fixed from this side without also refusing every
    /// genuine file that a server lists without permissions. Whether the library can ever leave the value
    /// unset is a property of its parsers, not of this mapper.
    /// </para>
    /// </para>
    /// </remarks>
    private static RemoteEntryKind ClassifyFtpEntry(FtpObjectType type, string? rawPermissions)
    {
        const int TypedPermissionsLength = 10;

        if (rawPermissions is { Length: >= TypedPermissionsLength })
        {
            return rawPermissions[0] switch
            {
                'd' => RemoteEntryKind.Directory,
                'l' => RemoteEntryKind.SymbolicLink,
                'p' => RemoteEntryKind.Fifo,
                's' => RemoteEntryKind.Socket,
                'c' or 'b' => RemoteEntryKind.Device,
                '-' => RemoteEntryKind.File,

                // An explicit type character this build does not recognise. The server stated a type and
                // we cannot read it, which is precisely the case that must not become a regular file.
                _ => RemoteEntryKind.Unknown,
            };
        }

        return type == FtpObjectType.File ? RemoteEntryKind.File : RemoteEntryKind.Unknown;
    }

    internal static SftpFileInfo? MapFtpItemToFileInfo(FtpListItem item, string parentPath)
    {
        // The server names the entry, and that name becomes a path every operation trusts.
        // A name that is not a single clean segment is refused here, at the boundary.
        if (!SftpPathGuard.IsValidChildName(item.Name))
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[FtpBrowser] listing of {parentPath} skipped an entry with an unsafe name");
            return null;
        }

        RemoteEntryKind kind = item.Type switch
        {
            FtpObjectType.Link => RemoteEntryKind.SymbolicLink,
            FtpObjectType.Directory => RemoteEntryKind.Directory,
            _ => ClassifyFtpEntry(item.Type, item.RawPermissions),
        };
        bool isDirectory = kind == RemoteEntryKind.Directory;
        long size = isDirectory ? 0 : Math.Max(0, item.Size);
        DateTime lastModified = item.Modified == default
            ? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
            : DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc);
        string permissions = string.IsNullOrEmpty(item.RawPermissions)
            ? isDirectory ? "rwxr-xr-x" : "rw-r--r--"
            : item.RawPermissions;
        string owner = string.IsNullOrEmpty(item.RawOwner) ? "-" : item.RawOwner;
        string group = string.IsNullOrEmpty(item.RawGroup) ? "-" : item.RawGroup;
        string fullPath = parentPath.TrimEnd('/') + "/" + item.Name;

        return new SftpFileInfo(
            Name: item.Name,
            FullPath: fullPath,
            Kind: kind,
            Size: size,
            LastModified: lastModified,
            Permissions: permissions,
            Owner: owner,
            Group: group);
    }

    /// <summary>
    /// Builds the FluentFTP configuration for a connection attempt.
    /// </summary>
    /// <remarks>
    /// Revocation checking is enabled with encryption rather than unconditionally: FluentFTP maps
    /// it onto the TLS chain policy, so turning it on for a plaintext connection would configure a
    /// check that never runs. Without it the chain is built with
    /// <c>X509RevocationMode.NoCheck</c>, so <c>X509ChainStatusFlags.Revoked</c> can never reach
    /// the pinned-certificate guard and its non-overridable check is unreachable in practice.
    /// </remarks>
    internal static FtpConfig CreateConfig(bool passiveMode, bool useSsl)
    {
        return new FtpConfig
        {
            EncryptionMode = useSsl ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            DataConnectionEncryption = useSsl,
            ValidateCertificateRevocation = useSsl,
            DataConnectionType = passiveMode
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive,
            ConnectTimeout = DefaultTimeoutMilliseconds,
            ReadTimeout = DefaultTimeoutMilliseconds,
            DataConnectionConnectTimeout = DefaultTimeoutMilliseconds,
            DataConnectionReadTimeout = DefaultTimeoutMilliseconds,
        };
    }

    private static void ThrowIfFailed(FtpStatus status, string remotePath, string operation)
    {
        if (status == FtpStatus.Failed)
        {
            throw new IOException($"FTP {operation} failed for '{remotePath}'.");
        }
    }

    private static async Task RollbackUploadTempFileAsync(AsyncFtpClient client, string tempRemotePath)
    {
        try
        {
            if (await client.FileExists(tempRemotePath, CancellationToken.None).ConfigureAwait(false))
            {
                await client.DeleteFile(tempRemotePath, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"FTP temp upload rollback failed for '{tempRemotePath}': {ex.Message}");
        }
    }

    private static X509Certificate2 CopyCertificate(X509Certificate certificate)
    {
        var rawCertificate = certificate.Export(X509ContentType.Cert);
        return X509CertificateLoader.LoadCertificate(rawCertificate);
    }

    private static FtpsCertificateEntry CreateCertificateEntry(
        X509Certificate2 certificate,
        string fingerprint,
        string validationErrors,
        FtpsCertificateSource source)
    {
        var now = DateTimeOffset.UtcNow;
        return new FtpsCertificateEntry(
            fingerprint,
            now,
            now,
            string.IsNullOrWhiteSpace(certificate.Subject) ? "(unknown)" : certificate.Subject,
            string.IsNullOrWhiteSpace(certificate.Issuer) ? "(unknown)" : certificate.Issuer,
            new DateTimeOffset(certificate.NotBefore),
            new DateTimeOffset(certificate.NotAfter),
            source)
        {
            ValidationErrors = validationErrors
        };
    }

    private static string BuildValidationErrors(
        SslPolicyErrors policyErrors,
        X509Chain? chain,
        string? policyErrorMessage)
    {
        if (!string.IsNullOrWhiteSpace(policyErrorMessage))
        {
            return policyErrorMessage.Trim();
        }

        if (policyErrors == SslPolicyErrors.None)
        {
            return "None";
        }

        if (chain?.ChainStatus is { Length: > 0 } statuses)
        {
            var details = statuses
                .Select(static status => status.StatusInformation.Trim())
                .Where(static status => !string.IsNullOrWhiteSpace(status))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (details.Length > 0)
            {
                return string.Join("; ", details);
            }
        }

        return policyErrors.ToString();
    }

    private IProgress<FtpProgress> CreateProgress(string fileName, long totalBytes, bool isUpload)
    {
        return new SynchronousFtpProgress(progress =>
        {
            long transferredBytes = Math.Max(0, progress.TransferredBytes);
            TransferProgress?.Invoke(new SftpTransferProgress(
                fileName,
                transferredBytes,
                totalBytes,
                isUpload));
        });
    }

    private sealed class SynchronousFtpProgress : IProgress<FtpProgress>
    {
        private readonly Action<FtpProgress> _handler;

        public SynchronousFtpProgress(Action<FtpProgress> handler)
        {
            _handler = handler;
        }

        public void Report(FtpProgress value)
        {
            _handler(value);
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected || _host is null || _client is null)
        {
            throw new InvalidOperationException("FTP browser is not connected.");
        }
    }

    private AsyncFtpClient GetConnectedClient()
    {
        EnsureConnected();
        return _client!;
    }
}
