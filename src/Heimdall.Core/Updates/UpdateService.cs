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

using System.Globalization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.Core.Updates;

/// <summary>
/// Composes <see cref="IGitHubReleaseClient"/> and <see cref="IVariantDetector"/>
/// to check for updates and download them with mandatory SHA-256 verification.
/// All HTTP access is delegated to the client; this service owns no HttpClient.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const string InstallerNameFormat = "Heimdall_{0}_{1}_Setup.exe";
    private const string ChecksumFileName = "SHA256SUMS.txt";
    private const string ChecksumSeparator = "  ";
    private const string DefaultInstallerExtension = ".exe";
    private const int DownloadBufferSize = 81920;

    /// <summary>
    /// How long a download may go without delivering a single byte before it is
    /// treated as stalled.
    /// </summary>
    /// <remarks>
    /// <c>HttpClient.Timeout</c> does not reach this code. Measured under .NET 10:
    /// with <c>ResponseHeadersRead</c> that timeout governs the headers only, and a
    /// body that stalls stays blocked in <c>ReadAsync</c> for ever, with the progress
    /// bar frozen and nothing logged. An inactivity budget is the only bound a
    /// streamed download has, and it is per read rather than per download so a slow
    /// but live connection is never cut off.
    /// </remarks>
    public static readonly TimeSpan DefaultDownloadIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly IGitHubReleaseClient _client;
    private readonly IVariantDetector _variantDetector;
    private readonly string _updatesRoot;
    private readonly TimeSpan _downloadIdleTimeout;

    /// <param name="client">The GitHub releases client.</param>
    /// <param name="variantDetector">Which build is running, and whether the installer owns it.</param>
    /// <param name="dataRoot">
    /// The application data root under which installers are staged. Injectable so a
    /// test can stage under a temporary directory rather than in the operator's own
    /// profile, which is the defect BL-0063 records.
    /// </param>
    /// <param name="downloadIdleTimeout">The inactivity budget; <see cref="DefaultDownloadIdleTimeout"/> by default.</param>
    public UpdateService(
        IGitHubReleaseClient client,
        IVariantDetector variantDetector,
        string? dataRoot = null,
        TimeSpan? downloadIdleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(variantDetector);
        _client = client;
        _variantDetector = variantDetector;
        _updatesRoot = ApplicationDataPathResolver.GetUpdatesDirectory(
            string.IsNullOrWhiteSpace(dataRoot) ? ApplicationDataPathResolver.Resolve() : dataRoot);
        _downloadIdleTimeout = downloadIdleTimeout ?? DefaultDownloadIdleTimeout;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        HeimdallVersion current,
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var release = await _client.GetLatestReleaseAsync(owner, repo, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null);
        }

        if (!HeimdallVersion.TryParse(release.TagName, out var releaseVersion))
        {
            FileLogger.Warn($"Update check: release tag '{release.TagName}' is not a valid Heimdall version.");
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null);
        }

        if (releaseVersion <= current)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);
        }

        var releaseRef = new ReleaseRef(releaseVersion, release.TagName, release.HtmlUrl);

        // A copy the installer did not register cannot be replaced in place: a portable
        // archive, an MSI deployment, a build run from its output directory. Offering the
        // installer anyway installed a second copy elsewhere, relaunched the old one and
        // reported "did not apply" on every launch from then on. The honest answer is the
        // release page.
        if (!_variantDetector.IsInstalledInPlace())
        {
            FileLogger.Info($"Update check: release {release.TagName} is available but this copy was not installed by the installer.");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateNotInstallable, null, releaseRef);
        }

        var variant = _variantDetector.Detect();
        var installerName = BuildInstallerName(releaseVersion, variant);
        var selectedAsset = release.Assets
            .FirstOrDefault(a => string.Equals(a.Name, installerName, StringComparison.OrdinalIgnoreCase));
        if (selectedAsset is null)
        {
            FileLogger.Warn($"Update check: release {release.TagName} has no installer asset '{installerName}'.");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateNotInstallable, null, releaseRef);
        }

        var sha256 = await ResolveSha256Async(release, installerName, cancellationToken).ConfigureAwait(false);
        if (!IsSha256Hex(sha256))
        {
            FileLogger.Warn($"Update check: release {release.TagName} has no valid SHA-256 for '{installerName}'.");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateNotInstallable, null, releaseRef);
        }

        var info = new UpdateInfo(releaseVersion, release.TagName, release.HtmlUrl, release.Body, selectedAsset, sha256);
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info);
    }

    public async Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Preserve the installer's real extension so the relauncher can execute it; Windows
        // cannot run a ".tmp" file and would otherwise prompt the user to pick an app.
        var extension = Path.GetExtension(update.Asset.Name);
        if (string.IsNullOrEmpty(extension))
        {
            extension = DefaultInstallerExtension;
        }

        Directory.CreateDirectory(_updatesRoot);

        string stagingDirectory = Path.Combine(
            _updatesRoot,
            $"Heimdall_{update.Version}_{Guid.NewGuid():N}");
        CreateStagingDirectory(stagingDirectory);
        string installerPath = Path.Combine(
            stagingDirectory,
            $"Heimdall_{update.Version}_Setup{extension}");

        try
        {
            await using (var source = await _client.OpenAssetStreamAsync(update.Asset.DownloadUrl, cancellationToken).ConfigureAwait(false))
            await using (var destination = CreateInstallerWriteStream(installerPath))
            {
                await CopyWithProgressAsync(source, destination, update.Asset.SizeBytes, progress, _downloadIdleTimeout, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            if (update.Sha256 is null)
            {
                throw new InvalidOperationException("Refusing to use an update with no published SHA-256 checksum.");
            }

            var integrityLease = new FileStream(
                installerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DownloadBufferSize,
                FileOptions.SequentialScan);
            string actualSha256;
            try
            {
                actualSha256 = Sha256Verifier.ComputeHex(integrityLease);
                integrityLease.Position = 0;
            }
            catch
            {
                integrityLease.Dispose();
                throw;
            }

            if (!string.Equals(
                    actualSha256,
                    update.Sha256.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                integrityLease.Dispose();
                throw new InvalidOperationException("The downloaded update failed SHA-256 verification.");
            }

            return new VerifiedUpdatePackage(
                installerPath,
                update.Sha256.Trim().ToLowerInvariant(),
                stagingDirectory,
                integrityLease);
        }
        catch
        {
            TryDeleteStagingDirectory(stagingDirectory);
            throw;
        }
    }

    /// <summary>
    /// Builds the canonical installer file name for a version and variant. This is
    /// the single source of truth for the name pattern produced by Build.ps1.
    /// </summary>
    internal static string BuildInstallerName(HeimdallVersion version, BuildVariant variant) =>
        string.Format(CultureInfo.InvariantCulture, InstallerNameFormat, version, variant);

    /// <summary>
    /// Extracts the lowercase hash for <paramref name="fileName"/> from a
    /// SHA256SUMS document ("&lt;hash&gt;  &lt;filename&gt;", two spaces), or null.
    /// </summary>
    internal static string? ParseChecksumLine(string checksumText, string fileName)
    {
        foreach (var rawLine in checksumText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(ChecksumSeparator, StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = line[(separatorIndex + ChecksumSeparator.Length)..].Trim();
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return line[..separatorIndex].Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    private async Task<string?> ResolveSha256Async(GitHubRelease release, string installerName, CancellationToken cancellationToken)
    {
        var checksumAsset = release.Assets
            .FirstOrDefault(a => string.Equals(a.Name, ChecksumFileName, StringComparison.OrdinalIgnoreCase));
        if (checksumAsset is null)
        {
            return null;
        }

        var text = await _client.GetAssetTextAsync(checksumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return ParseChecksumLine(text, installerName);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        return value.All(Uri.IsHexDigit);
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        IProgress<double>? progress,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        long copied = 0;
        int read;
        while ((read = await ReadWithIdleTimeoutAsync(source, buffer, idleTimeout, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (progress is not null && totalBytes > 0)
            {
                progress.Report(Math.Min(1.0, (double)copied / totalBytes));
            }
        }

        progress?.Report(1.0);
    }

    /// <summary>
    /// One read, bounded by the inactivity budget. A read that produces nothing within
    /// it is reported as an <see cref="IOException"/>, not as a cancellation: the user
    /// did not cancel, the connection stalled, and the two must not share wording.
    /// </summary>
    private static async Task<int> ReadWithIdleTimeoutAsync(
        Stream source,
        byte[] buffer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);
        try
        {
            return await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"The update download stalled: no data for {idleTimeout.TotalSeconds:0} s.");
        }
    }

    private static void CreateStagingDirectory(string stagingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            SecureFileWriter.CreateRestrictedDirectory(stagingDirectory);
            return;
        }

        Directory.CreateDirectory(
            stagingDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream CreateInstallerWriteStream(string installerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return SecureFileWriter.CreateWriteAndProtect(installerPath);
        }

        return new FileStream(
            installerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void TryDeleteStagingDirectory(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the restrictive staging directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the restrictive staging directory.
        }
    }
}
