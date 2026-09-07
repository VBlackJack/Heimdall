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
        var lookup = await _client.GetLatestReleaseAsync(owner, repo, cancellationToken).ConfigureAwait(false);
        if (lookup.Release is null)
        {
            // The status stays CheckFailed - what a caller does about it has not changed -
            // and the cause rides alongside so the user can be told which of five very
            // different things happened. Through the factory, which refuses a failed check
            // that declines to say why.
            return UpdateCheckResult.Failed(lookup.Failure, lookup.RetryAfter);
        }

        var release = lookup.Release;

        if (!HeimdallVersion.TryParse(release.TagName, out var releaseVersion))
        {
            // The maintainer's signal stays: a tag this client cannot read is a mistake
            // somebody has to see, and the log is where they see it.
            FileLogger.Warn($"Update check: release tag '{release.TagName}' is not a valid Heimdall version.");

            // But the USER is told the truth, which is that there is nothing to install -
            // not that the check failed. Two unrelated conditions used to share one status:
            // "the source could not be reached", which is transient and must be retried on
            // the next launch, and this one, a well-formed answer naming a release this
            // client cannot act on. Reporting the second as a failure both blamed the
            // network and suppressed the throttle, so every launch asked GitHub again for
            // an answer that would not change.
            //
            // The repository already produced exactly this for an unofferable latest: v1.0.0
            // was the latest release for seventeen hours on 2026-03-17, it parses, it
            // compares below every 2026.x, and every user was told they were up to date.
            // The parser keeps refusing a tag with a suffix, deliberately - inventing a
            // version identity for a tag whose whole purpose is to say "not that version"
            // would make a release candidate indistinguishable from its final.
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);
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

        var checksumAsset = FindChecksumAsset(release);
        var sha256 = await ResolveSha256Async(checksumAsset, installerName, cancellationToken).ConfigureAwait(false);
        if (!IsSha256Hex(sha256))
        {
            FileLogger.Warn($"Update check: release {release.TagName} has no valid SHA-256 for '{installerName}'.");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateNotInstallable, null, releaseRef);
        }

        var info = new UpdateInfo(releaseVersion, release.TagName, release.HtmlUrl, release.Body, selectedAsset, sha256)
        {
            // Kept so the install can ask the source what it publishes NOW. The answer is
            // never adopted, only compared; see UpdateSupersededException.
            ChecksumUrl = checksumAsset?.DownloadUrl,
        };
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info);
    }

    public async Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        await RefuseIfRepublishedAsync(update, cancellationToken).ConfigureAwait(false);

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
                // Abandonable: this reads the whole installer back, and on a cold cache or a
                // slow volume that is not instant. Cancelling here used to do nothing until
                // the read finished.
                actualSha256 = await Sha256Verifier
                    .ComputeHexAsync(integrityLease, cancellationToken)
                    .ConfigureAwait(false);
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

                // Ask once more before calling this an integrity failure. If what the source
                // publishes now is exactly what arrived, the release was republished during
                // the download, which is not the same event and must not carry the same
                // words. Throws when it is; falls through to the honest failure when it
                // cannot tell.
                await RefuseIfTheDownloadedBytesAreTheRepublishedOnesAsync(
                    update, actualSha256, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Refuses an install when the source already publishes a different checksum than the
    /// one the check froze, unless it cannot be asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The checksum is frozen at check time, and a maintainer who re-uploads an asset over
    /// an existing release publishes new bytes and a new checksum beside them. The install
    /// then downloaded the new bytes, compared them against the old checksum, and reported
    /// a verification failure - the same words it uses for a tampered or truncated
    /// download. The two are not the same event and do not deserve the same answer.
    /// </para>
    /// <para>
    /// Asking the source again is a diagnosis, never a repair. The newly published checksum
    /// is compared and then discarded; adopting it would make the application install
    /// whatever the source serves at install time, which is the exact property freezing the
    /// checksum exists to deny.
    /// </para>
    /// <para>
    /// This is the cheap half of the answer and it is not the whole of it. A republication
    /// is not atomic: Build.ps1 clobbers the installer and the checksum document in a single
    /// upload, and the two do not land at the same instant, so this read can still see the
    /// old document while the new bytes are already being served. What that window produces
    /// is caught after the download instead, by
    /// <see cref="RefuseIfTheDownloadedBytesAreTheRepublishedOnesAsync"/>.
    /// </para>
    /// <para>
    /// Silence is not evidence of republication. A checksum that cannot be re-read - the
    /// document gone, the network down, an unparseable line - lets the install continue,
    /// because the frozen checksum still decides what may be installed and refusing here
    /// would turn an unrelated network blip into a failed update.
    /// </para>
    /// </remarks>
    private async Task RefuseIfRepublishedAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (update.Sha256 is null)
        {
            return;
        }

        var publishedNow = await ReadPublishedSha256Async(update, cancellationToken).ConfigureAwait(false);
        if (publishedNow is null
            || string.Equals(publishedNow, update.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FileLogger.Warn(
            $"Update install: release {update.TagName} was republished since it was checked.");
        throw new UpdateSupersededException(update.TagName, update.Sha256.Trim(), publishedNow);
    }

    /// <summary>
    /// Names a failed hash comparison as a republication when the bytes that arrived are
    /// exactly what the source publishes now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half, and the stronger evidence of the two: the pre-download check infers
    /// republication from a changed document, while this one observes that the bytes on disk
    /// ARE the bytes the source currently vouches for. It closes the window that the
    /// pre-download read cannot see, which is the seconds during which the installer has
    /// been clobbered and its checksum document has not yet.
    /// </para>
    /// <para>
    /// It costs nothing on a successful install: the only caller is the mismatch branch,
    /// which was about to fail anyway. The install is still refused. All that changes is
    /// which of two very different events the user is told about, since only one of them
    /// means the download is worth looking at.
    /// </para>
    /// </remarks>
    private async Task RefuseIfTheDownloadedBytesAreTheRepublishedOnesAsync(
        UpdateInfo update,
        string actualSha256,
        CancellationToken cancellationToken)
    {
        var publishedNow = await ReadPublishedSha256Async(update, cancellationToken).ConfigureAwait(false);
        if (publishedNow is null
            || !string.Equals(publishedNow, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FileLogger.Warn(
            $"Update install: release {update.TagName} was republished while it was downloading.");
        throw new UpdateSupersededException(update.TagName, update.Sha256?.Trim(), publishedNow);
    }

    /// <summary>
    /// Reads the checksum the source publishes right now for this update's asset, or null
    /// when it cannot be established.
    /// </summary>
    /// <remarks>
    /// Null means "not known", never "unchanged": every caller treats it as a reason to stop
    /// diagnosing, not as a comparison that succeeded. The URL is the checksum document's,
    /// carried from the check - reading the installer's own URL here would fetch a hundred
    /// megabytes as text, trip the client's size bound, return null, and silently disable
    /// the whole diagnosis.
    /// </remarks>
    private async Task<string?> ReadPublishedSha256Async(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.ChecksumUrl))
        {
            // Not reachable from a check, which only reports UpdateAvailable once a checksum
            // asset has been found and parsed. Kept for an UpdateInfo assembled elsewhere.
            return null;
        }

        string? publishedNow;
        try
        {
            var text = await _client
                .GetAssetTextAsync(update.ChecksumUrl, cancellationToken)
                .ConfigureAwait(false);
            publishedNow = string.IsNullOrEmpty(text)
                ? null
                : ParseChecksumLine(text, update.Asset.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLogger.WarnDetailed("Update install: could not re-read the published checksum", ex);
            return null;
        }

        if (!IsSha256Hex(publishedNow))
        {
            FileLogger.Warn(
                $"Update install: the published checksum for '{update.Asset.Name}' could not be "
                + "re-read; continuing against the checksum the check froze.");
            return null;
        }

        return publishedNow;
    }

    private static UpdateAsset? FindChecksumAsset(GitHubRelease release) => release.Assets
        .FirstOrDefault(a => string.Equals(a.Name, ChecksumFileName, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> ResolveSha256Async(UpdateAsset? checksumAsset, string installerName, CancellationToken cancellationToken)
    {
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
