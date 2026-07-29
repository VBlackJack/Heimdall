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

                result.Add(MapFtpItemToFileInfo(item, targetPath));
            }

            return result;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _opLock.Wait(ct);
        try
        {
            EnsureConnected();
            return Task.FromResult(CurrentDirectory);
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

                bool moved = await client.MoveFile(
                    tempRemotePath,
                    remotePath,
                    FtpRemoteExists.Overwrite,
                    ct).ConfigureAwait(false);
                if (!moved)
                {
                    FileLogger.Warn($"FTP upload commit move returned false for '{remotePath}'.");
                    throw new IOException();
                }
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
        // FTP does not natively support chmod; this is a no-op.
        // Some servers support SITE CHMOD but it is non-standard.
        return Task.CompletedTask;
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
            await client.Rename(oldPath, newPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Copies files by roundtrip (remote download to a local temp, then remote upload) under the
    /// existing per-operation lock; FTP has no server-side copy command. The copy is not atomic.
    /// </remarks>
    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        RemoteCopyOps ops = new RemoteCopyOps(
            DestinationExistsAsync: RemoteExistsAsync,
            SourceIsDirectoryAsync: RemoteIsDirectoryAsync,
            ListChildNamesAsync: ListChildNamesAsync,
            CopyFileAsync: CopyFileViaRoundtripAsync,
            CreateDirectoryAsync: CreateDirectoryAsync);

        await RemoteCopyPlanner.CopyAsync(sourcePath, destinationPath, recursive, ops, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> RemoteExistsAsync(string path, CancellationToken ct)
    {
        string normalized = NormalizePath(path);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            if (await client.DirectoryExists(normalized, ct).ConfigureAwait(false))
            {
                return true;
            }

            return await client.FileExists(normalized, ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task<bool> RemoteIsDirectoryAsync(string path, CancellationToken ct)
    {
        string normalized = NormalizePath(path);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AsyncFtpClient client = GetConnectedClient();
            return await client.DirectoryExists(normalized, ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task<IReadOnlyList<string>> ListChildNamesAsync(string path, CancellationToken ct)
    {
        IReadOnlyList<SftpFileInfo> entries = await ListDirectoryAsync(path, ct).ConfigureAwait(false);
        List<string> names = new List<string>(entries.Count);
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

    /// <inheritdoc/>
    public void Disconnect()
    {
        if (_disposed && _client is null)
        {
            return;
        }

        var disconnected = false;
        _opLock.Wait();
        try
        {
            if (_client is null)
            {
                return;
            }

            _client.Dispose();
            _client = null;
            _connected = false;
            _host = null;
            _username = null;
            _port = 0;
            disconnected = true;
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
        _opLock.Dispose();
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

        // Remove trailing slash unless it is the root
        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path.TrimEnd('/');
        }

        return path;
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

    internal static SftpFileInfo MapFtpItemToFileInfo(FtpListItem item, string parentPath)
    {
        bool isDirectory = item.Type == FtpObjectType.Directory;
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
            IsDirectory: isDirectory,
            Size: size,
            LastModified: lastModified,
            Permissions: permissions,
            Owner: owner,
            Group: group);
    }

    private static FtpConfig CreateConfig(bool passiveMode, bool useSsl)
    {
        return new FtpConfig
        {
            EncryptionMode = useSsl ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            DataConnectionEncryption = useSsl,
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
        return new Progress<FtpProgress>(progress =>
        {
            long transferredBytes = Math.Max(0, progress.TransferredBytes);
            TransferProgress?.Invoke(new SftpTransferProgress(
                fileName,
                transferredBytes,
                totalBytes,
                isUpload));
        });
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
