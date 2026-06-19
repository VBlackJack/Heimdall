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
using Heimdall.Core.Logging;

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

    private readonly IGitHubReleaseClient _client;
    private readonly IVariantDetector _variantDetector;

    public UpdateService(IGitHubReleaseClient client, IVariantDetector variantDetector)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(variantDetector);
        _client = client;
        _variantDetector = variantDetector;
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

    public async Task<string> DownloadVerifiedAsync(
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

        var tempPath = Path.Combine(Path.GetTempPath(), $"Heimdall_{update.Version}_{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var source = await _client.OpenAssetStreamAsync(update.Asset.DownloadUrl, cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyWithProgressAsync(source, destination, update.Asset.SizeBytes, progress, cancellationToken).ConfigureAwait(false);
            }

            if (update.Sha256 is null)
            {
                throw new InvalidOperationException("Refusing to use an update with no published SHA-256 checksum.");
            }

            if (!Sha256Verifier.Verify(tempPath, update.Sha256))
            {
                throw new InvalidOperationException("The downloaded update failed SHA-256 verification.");
            }

            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
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
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temporary download.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temporary download.
        }
    }
}
