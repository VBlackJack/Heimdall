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

using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private const string CurrentTag = "v2026.061501";
    private const string OlderTag = "v2026.061500";
    private const string NewerTag = "v2026.061502";

    /// <summary>
    /// A data root of this test's own. Staging used to land in the operator's real
    /// profile (the BL-0063 shape), and the snapshot-based assertions had to work
    /// around whatever other tests had left there.
    /// </summary>
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "heimdall-update-service",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A lease still open on a verified installer; the root is disposable.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <remarks>
    /// The only test of the "not newer" branch used an EQUAL version, so the
    /// comparison could have been == and stayed green. A server that went backwards
    /// must read as up to date too, never as an update.
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_LatestOlderThanCurrent_UpToDate()
    {
        var client = new StubReleaseClient { Release = ReleaseFor(OlderTag, includeChecksum: true) };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_CancelledToken_Propagates()
    {
        var client = new StubReleaseClient { Release = ReleaseFor(NewerTag, includeChecksum: true) };
        var service = CreateService(client, BuildVariant.Standard);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", cancellation.Token));
    }

    /// <remarks>
    /// A portable archive, an MSI deployment or a build run from its output directory
    /// detects a variant exactly like an installed copy. Offering it the installer
    /// installed a second copy elsewhere, relaunched the old one, and reported "did
    /// not apply" on every launch from then on. The release page is the honest answer.
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_CopyNotInstalledInPlace_UpdateNotInstallable()
    {
        var client = new StubReleaseClient
        {
            Release = ReleaseFor(NewerTag, includeChecksum: true),
            ChecksumText = ChecksumsFor(NewerTag),
        };
        var service = new UpdateService(
            client,
            new StubVariantDetector(BuildVariant.Standard, installedInPlace: false),
            _dataRoot);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateNotInstallable, result.Status);
        Assert.Null(result.Update);
        AssertReleaseRef(result);
    }

    [Theory]
    [InlineData(BuildVariant.Standard, "Heimdall_2026.061502_Standard_Setup.exe")]
    [InlineData(BuildVariant.SelfContained, "Heimdall_2026.061502_SelfContained_Setup.exe")]
    public void BuildInstallerName_MatchesThePublishedPattern(BuildVariant variant, string expected)
    {
        Assert.Equal(expected, UpdateService.BuildInstallerName(HeimdallVersion.Parse(NewerTag), variant));
    }

    [Theory]
    [InlineData("abc  Heimdall_x.exe\n", "Heimdall_x.exe", "abc")]
    [InlineData("ABC  Heimdall_x.exe\r\n", "heimdall_x.exe", "abc")]
    [InlineData("abc  Heimdall_x.exe\ndef  Other.exe\n", "Other.exe", "def")]
    [InlineData("abc Heimdall_x.exe\n", "Heimdall_x.exe", null)]
    [InlineData("  abc  Heimdall_x.exe  \n", "Heimdall_x.exe", "abc")]
    [InlineData("", "Heimdall_x.exe", null)]
    public void ParseChecksumLine_ReadsTheTwoSpaceFormat(string text, string fileName, string? expected)
    {
        Assert.Equal(expected, UpdateService.ParseChecksumLine(text, fileName));
    }

    /// <remarks>
    /// Measured under .NET 10: HttpClient.Timeout governs the headers only when the
    /// body is streamed, so a body that stalls stayed blocked in ReadAsync for ever
    /// with the progress bar frozen. The inactivity budget is the only bound, and a
    /// stall must read as a failed download, not as a cancellation the user did not
    /// make.
    /// </remarks>
    [Fact(Timeout = 10000)]
    public async Task DownloadVerifiedAsync_SourceStreamStalls_ThrowsIOExceptionAndDeletesStaging()
    {
        var client = new StubReleaseClient { StreamFactory = () => new StalledStream() };
        var service = new UpdateService(
            client,
            new StubVariantDetector(BuildVariant.Standard),
            _dataRoot,
            downloadIdleTimeout: TimeSpan.FromMilliseconds(200));
        const string tag = "v2026.061596";
        var update = UpdateWithSha(new string('0', 64), 10, tag);

        IOException thrown = await Assert.ThrowsAsync<IOException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Contains("stalled", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(StagingSnapshot(tag));
    }

    [Fact(Timeout = 10000)]
    public async Task DownloadVerifiedAsync_CallerCancelsDuringAStall_ReportsCancellationNotAStall()
    {
        var client = new StubReleaseClient { StreamFactory = () => new StalledStream() };
        var service = new UpdateService(
            client,
            new StubVariantDetector(BuildVariant.Standard),
            _dataRoot,
            downloadIdleTimeout: TimeSpan.FromSeconds(30));
        var update = UpdateWithSha(new string('0', 64), 10, "v2026.061595");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadVerifiedAsync(update, null, cancellation.Token));
    }
    private const long StandardSize = 100;
    private const long SelfContainedSize = 200;

    [Fact]
    public async Task CheckForUpdatesAsync_LatestEqualsCurrent_UpToDate()
    {
        var client = new StubReleaseClient { Release = ReleaseFor(CurrentTag, includeChecksum: false) };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_LatestEqualsCurrent_DoesNotRequireInstallerAsset()
    {
        var client = new StubReleaseClient
        {
            Release = new GitHubRelease(CurrentTag, "https://example.test", "notes", []),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_LatestNewer_UpdateAvailable()
    {
        var client = new StubReleaseClient
        {
            Release = ReleaseFor(NewerTag, includeChecksum: true),
            ChecksumText = ChecksumsFor(NewerTag),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Update);
        Assert.Equal("2026.061502", result.Update!.Version.ToString());
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NullRelease_CheckFailed()
    {
        var client = new StubReleaseClient { Release = null };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.CheckFailed, result.Status);
        Assert.Null(result.Update);
        Assert.Null(result.Release);
    }

    /// <summary>
    /// A tag this client cannot read is not a failed check, and this test's expectation was
    /// changed on purpose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from the shape that really occurred rather than from "not-a-version":
    /// <c>v2026.031601-next</c> was a PUBLISHED release of this repository, not a prerelease
    /// and not a draft, and it was what <c>/releases/latest</c> answered. Two unrelated
    /// conditions used to share one status - the source could not be reached, which is
    /// transient, and this one, a well-formed answer naming a release this client cannot act
    /// on. The second reported a failure the user could do nothing about, blamed the network,
    /// and suppressed the throttle, so every launch asked GitHub again for an answer that
    /// would not change.
    /// </para>
    /// <para>
    /// The control that must stay green beside this one is
    /// <see cref="CheckForUpdatesAsync_NullRelease_CheckFailed"/>: the transient case keeps
    /// its status, and if both tests ever agree, the distinction has been lost again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_TagThisClientCannotRead_IsNotAFailedCheck()
    {
        var client = new StubReleaseClient
        {
            Release = new GitHubRelease(
                "v2026.031601-next",
                "https://example.test",
                "notes",
                [new UpdateAsset("Heimdall.Next_build.2026.031601.zip", "https://example.test/next.zip", StandardSize)]),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.NotEqual(UpdateCheckStatus.CheckFailed, result.Status);
        Assert.Null(result.Update);

        Assert.Null(result.Release);
    }

    /// <summary>
    /// The same shape, dated ahead of the running version, which is what makes it a test of
    /// the remedy rather than of the status.
    /// </summary>
    /// <remarks>
    /// The historical tag above is OLDER than any current build, so teaching the parser to
    /// strip at the hyphen would leave every assertion there green - it would simply be
    /// "up to date" for a second reason. Dated ahead, stripping produces a version that
    /// compares NEWER, the check proceeds into asset selection, and the release stops being
    /// null. That is the discriminator: this test fails the moment the suffix is stripped,
    /// and a release candidate must stay distinguishable from its final.
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_NewerTagWithASuffix_IsNotOfferedAndTheSuffixIsNotStripped()
    {
        var client = new StubReleaseClient
        {
            Release = new GitHubRelease(
                "v2026.091501-next",
                "https://example.test",
                "notes",
                [new UpdateAsset("Heimdall_2026.091501_Standard_Setup.exe", "https://example.test/setup.exe", StandardSize)]),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Null(result.Update);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NoMatchingInstaller_UpdateNotInstallable()
    {
        var release = new GitHubRelease(
            NewerTag,
            $"https://github.com/VBlackJack/Heimdall/releases/tag/{NewerTag}",
            "notes",
            [
                new UpdateAsset("Heimdall_2026.061502_SelfContained_Setup.exe", "https://example.test/sc.exe", SelfContainedSize),
                new UpdateAsset("SHA256SUMS.txt", "https://example.test/SHA256SUMS.txt", 256)
            ]);
        var client = new StubReleaseClient { Release = release };

        // Running the Standard variant, but only the SelfContained installer exists.
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateNotInstallable, result.Status);
        Assert.Null(result.Update);
        AssertReleaseRef(result);
    }

    [Theory]
    [InlineData(BuildVariant.Standard, "Heimdall_2026.061502_Standard_Setup.exe")]
    [InlineData(BuildVariant.SelfContained, "Heimdall_2026.061502_SelfContained_Setup.exe")]
    public async Task CheckForUpdatesAsync_SelectsInstallerForVariant(BuildVariant variant, string expectedName)
    {
        var client = new StubReleaseClient
        {
            Release = ReleaseFor(NewerTag, includeChecksum: true),
            ChecksumText = ChecksumsFor(NewerTag),
        };
        var service = CreateService(client, variant);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.NotNull(result.Update);
        Assert.Equal(expectedName, result.Update!.Asset.Name);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_PopulatesSha256FromChecksumAsset()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var client = new StubReleaseClient
        {
            Release = ReleaseFor(NewerTag, includeChecksum: true),
            ChecksumText =
                $"{hash}  Heimdall_2026.061502_Standard_Setup.exe\n" +
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff  Heimdall_2026.061502_SelfContained_Setup.exe\n",
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.NotNull(result.Update);
        Assert.Equal(hash, result.Update!.Sha256);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NewerReleaseWithoutChecksum_UpdateNotInstallable()
    {
        var client = new StubReleaseClient
        {
            Release = ReleaseFor(NewerTag, includeChecksum: false),
            ChecksumText = "should-not-be-read",
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateNotInstallable, result.Status);
        Assert.Null(result.Update);
        AssertReleaseRef(result);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NewerReleaseWithMalformedChecksum_UpdateNotInstallable()
    {
        var client = new StubReleaseClient
        {
            Release = ReleaseFor(NewerTag, includeChecksum: true),
            ChecksumText = "not-a-sha256  Heimdall_2026.061502_Standard_Setup.exe\n",
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateNotInstallable, result.Status);
        Assert.Null(result.Update);
        AssertReleaseRef(result);
    }

    [Fact]
    public async Task DownloadVerifiedAsync_MatchingHash_ReturnsHeldVerifiedPackage()
    {
        var payload = Encoding.ASCII.GetBytes("verified-installer-payload");
        var hash = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        var update = UpdateWithSha(hash, payload.Length);
        var progress = new RecordingProgress();

        IVerifiedUpdatePackage package = await service.DownloadVerifiedAsync(
            update,
            progress,
            CancellationToken.None);
        string stagingDirectory = package.StagingDirectory;

        using (package)
        {
            Assert.True(File.Exists(package.InstallerPath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(package.InstallerPath));
            Assert.Equal(hash, package.ExpectedSha256);
            Assert.Equal(
                Path.GetFullPath(stagingDirectory),
                Path.GetFullPath(Path.GetDirectoryName(package.InstallerPath)!));
            Assert.NotEmpty(progress.Reports);
            Assert.Equal(1.0, progress.Reports[^1]);

            Assert.Throws<IOException>(
                () => File.WriteAllText(package.InstallerPath, "attacker"));
            Assert.Throws<IOException>(
                () => File.Move(
                    package.InstallerPath,
                    Path.Combine(stagingDirectory, "swapped.exe")));
            Assert.Throws<IOException>(() =>
            {
                using FileStream _ = new(
                    package.InstallerPath,
                    FileMode.Truncate,
                    FileAccess.Write,
                    FileShare.ReadWrite);
            });
        }

        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ExeInstaller_ReturnsPathWithExeExtension()
    {
        var payload = Encoding.ASCII.GetBytes("verified-installer-payload");
        var hash = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        var update = UpdateWithSha(hash, payload.Length);

        using IVerifiedUpdatePackage package = await service.DownloadVerifiedAsync(
            update,
            null,
            CancellationToken.None);

        Assert.Equal(".exe", Path.GetExtension(package.InstallerPath));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_HashMismatch_ThrowsAndDeletesStagingDirectory()
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var wrongHash = new string('0', 64);
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        // Unique version isolates this test's temp-file snapshot from the other (parallel) download tests.
        const string tag = "v2026.061597";
        var update = UpdateWithSha(wrongHash, payload.Length, tag);

        var before = StagingSnapshot(tag);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Empty(StagingSnapshot(tag).Except(before));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_NullSha256_ThrowsAndDeletesStagingDirectory()
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        // Unique version isolates this test's temp-file snapshot from the other (parallel) download tests.
        const string tag = "v2026.061598";
        var update = UpdateWithSha(null, payload.Length, tag);

        var before = StagingSnapshot(tag);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Empty(StagingSnapshot(tag).Except(before));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_CancelledDownload_DeletesStagingDirectory()
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var hash = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        const string tag = "v2026.061599";
        var update = UpdateWithSha(hash, payload.Length, tag);
        var before = StagingSnapshot(tag);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadVerifiedAsync(update, null, cancellation.Token));

        Assert.Empty(StagingSnapshot(tag).Except(before));
    }

    private UpdateService CreateService(IGitHubReleaseClient client, BuildVariant variant)
        => new(client, new StubVariantDetector(variant), _dataRoot);

    private static void AssertReleaseRef(UpdateCheckResult result, string tag = NewerTag)
    {
        Assert.NotNull(result.Release);
        Assert.Equal(HeimdallVersion.Parse(tag), result.Release!.Version);
        Assert.Equal(tag, result.Release.TagName);
        Assert.Equal($"https://github.com/VBlackJack/Heimdall/releases/tag/{tag}", result.Release.HtmlUrl);
    }

    private static GitHubRelease ReleaseFor(string tag, bool includeChecksum)
    {
        var version = HeimdallVersion.Parse(tag);
        var assets = new List<UpdateAsset>
        {
            new($"Heimdall_{version}_Standard_Setup.exe", "https://example.test/standard.exe", StandardSize),
            new($"Heimdall_{version}_SelfContained_Setup.exe", "https://example.test/selfcontained.exe", SelfContainedSize),
        };

        if (includeChecksum)
        {
            assets.Add(new UpdateAsset("SHA256SUMS.txt", "https://example.test/SHA256SUMS.txt", 256));
        }

        return new GitHubRelease(tag, $"https://github.com/VBlackJack/Heimdall/releases/tag/{tag}", "notes", assets);
    }

    private static string ChecksumsFor(string tag)
    {
        var version = HeimdallVersion.Parse(tag);
        return
            $"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  Heimdall_{version}_Standard_Setup.exe\n" +
            $"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  Heimdall_{version}_SelfContained_Setup.exe\n";
    }

    /// <summary>
    /// The check carries the checksum document's URL forward to the install.
    /// </summary>
    /// <remarks>
    /// The install is handed an <see cref="UpdateInfo"/> and nothing else - no owner, no
    /// repository, no release. Without this the republication check has nothing to ask and
    /// silently degrades to doing nothing, which is a refusal that never fires rather than
    /// a visible failure. Dropping the assignment turns this red.
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_UpdateAvailable_CarriesTheChecksumUrlForTheInstall()
    {
        const string checksumUrl = "https://example.test/SHA256SUMS.txt";
        var version = HeimdallVersion.Parse(NewerTag);
        var installerName = $"Heimdall_{version}_Standard_Setup.exe";
        var client = new StubReleaseClient
        {
            Release = new GitHubRelease(
                NewerTag,
                "https://example.test/release",
                "notes",
                [
                    new UpdateAsset(installerName, "https://example.test/standard.exe", 1024),
                    new UpdateAsset("SHA256SUMS.txt", checksumUrl, 128),
                ]),
            ChecksumText = $"{new string('c', 64)}  {installerName}",
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(
            HeimdallVersion.Parse(CurrentTag), "owner", "repo", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(checksumUrl, result.Update?.ChecksumUrl);
    }

    /// <summary>
    /// A release republished between the check and the install is refused as superseded.
    /// </summary>
    /// <remarks>
    /// A-06. The checksum is frozen at check time. A maintainer who re-uploads an asset
    /// over an existing release publishes new bytes and a new checksum beside them, and the
    /// install used to download the new bytes, compare them against the old checksum, and
    /// report a verification failure - the same words it uses for a tampered download. The
    /// refusal is unchanged; what changes is that the two causes are now told apart.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_ReleaseRepublishedSinceTheCheck_IsRefusedAsSuperseded()
    {
        var payload = Encoding.ASCII.GetBytes("republished-payload");
        var frozen = new string('a', 64);
        var publishedNow = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        const string tag = "v2026.061591";
        var update = UpdateWithSha(frozen, payload.Length, tag) with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };
        var client = new StubReleaseClient
        {
            StreamFactory = () => new MemoryStream(payload),
            ChecksumText = $"{publishedNow}  {update.Asset.Name}",
        };
        var service = CreateService(client, BuildVariant.Standard);

        var before = StagingSnapshot(tag);
        var refusal = await Assert.ThrowsAsync<UpdateSupersededException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Equal(tag, refusal.TagName);
        Assert.Equal(frozen, refusal.AuthorisedSha256);
        Assert.Equal(publishedNow, refusal.PublishedSha256);

        // Refused BEFORE a byte was fetched. Asserted on the download count, not on the
        // staging directory: staging is removed on any throw, so an empty staging directory
        // would hold just as well if the refusal came after a completed download.
        Assert.Equal(0, client.AssetStreamOpenCount);
        Assert.Empty(StagingSnapshot(tag).Except(before));

        // The checksum DOCUMENT is what gets re-read, not the installer. Reading the
        // installer's URL as text would trip the client's size bound and return null,
        // turning this refusal into one that never fires.
        Assert.Equal(update.ChecksumUrl, Assert.Single(client.AssetTextRequests));
    }

    /// <summary>
    /// The newly published checksum is compared and discarded, never adopted.
    /// </summary>
    /// <remarks>
    /// The obvious repair for A-06 - install against whatever the source publishes now -
    /// would let the source decide at install time what this application runs, which is the
    /// exact property freezing the checksum exists to deny. Here the downloaded bytes match
    /// the newly published checksum perfectly, and the install is still refused.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_BytesMatchingTheNewlyPublishedChecksum_AreStillRefused()
    {
        var payload = Encoding.ASCII.GetBytes("bytes-that-match-the-new-checksum");
        var publishedNow = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        const string tag = "v2026.061592";
        var update = UpdateWithSha(new string('b', 64), payload.Length, tag) with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };
        var client = new StubReleaseClient
        {
            StreamFactory = () => new MemoryStream(payload),
            ChecksumText = $"{publishedNow}  {update.Asset.Name}",
        };
        var service = CreateService(client, BuildVariant.Standard);

        await Assert.ThrowsAsync<UpdateSupersededException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));
    }

    /// <summary>
    /// A release republished DURING the download is named, not called an integrity failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window the pre-download check cannot see. A republication is not atomic: the
    /// installer and its checksum document are clobbered in one upload but do not land
    /// together, so the check before the download can read the old document while the new
    /// bytes are already being served. Here that is exactly what happens, and the frozen
    /// hash then fails against bytes that are perfectly good.
    /// </para>
    /// <para>
    /// The evidence is stronger than the pre-download check's, not weaker: the bytes on disk
    /// ARE what the source vouches for right now. The install is still refused; what changes
    /// is that the user is told to check again rather than to distrust a sound download.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_ReleaseRepublishedDuringTheDownload_IsNamedNotCalledCorruption()
    {
        var payload = Encoding.ASCII.GetBytes("bytes-that-landed-mid-republication");
        var arrived = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var frozen = new string('d', 64);
        const string tag = "v2026.061595";
        var update = UpdateWithSha(frozen, payload.Length, tag) with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };

        // The document still says what it said at check time on the first read, and the new
        // value on the second: the installer was clobbered, its checksum document not yet.
        var client = new ChecksumChangingClient(
            () => new MemoryStream(payload),
            [$"{frozen}  {update.Asset.Name}", $"{arrived}  {update.Asset.Name}"]);
        var service = CreateService(client, BuildVariant.Standard);

        var before = StagingSnapshot(tag);
        var refusal = await Assert.ThrowsAsync<UpdateSupersededException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Equal(arrived, refusal.PublishedSha256);
        Assert.Equal(frozen, refusal.AuthorisedSha256);

        // Downloaded, so this really is the post-download path and not the earlier refusal.
        Assert.Equal(1, client.AssetStreamOpenCount);
        Assert.Empty(StagingSnapshot(tag).Except(before));
    }

    /// <summary>
    /// Bytes that match neither checksum stay an integrity failure.
    /// </summary>
    /// <remarks>
    /// The control for the case above, and the one that keeps the new answer honest: a
    /// republication check that named every mismatch would make the verification failure
    /// unreachable, which is worse than the confusion it set out to fix.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_BytesMatchingNeitherChecksum_StayAVerificationFailure()
    {
        var payload = Encoding.ASCII.GetBytes("bytes-that-match-nothing");
        var frozen = new string('e', 64);
        var publishedNow = new string('f', 64);
        const string tag = "v2026.061596";
        var update = UpdateWithSha(frozen, payload.Length, tag) with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };
        var client = new ChecksumChangingClient(
            () => new MemoryStream(payload),
            [$"{frozen}  {update.Asset.Name}", $"{publishedNow}  {update.Asset.Name}"]);
        var service = CreateService(client, BuildVariant.Standard);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.IsNotType<UpdateSupersededException>(failure);
        Assert.Contains("SHA-256", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A published checksum in another case, or a frozen one with stray whitespace, is the
    /// same checksum.
    /// </summary>
    /// <remarks>
    /// Both tolerances are deliberate and neither is exercised by the production path, where
    /// every value is trimmed and lowercased before it gets here. Without this an
    /// UpdateInfo assembled anywhere else would be refused as a republication of itself.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_SameChecksumInAnotherCaseOrPadded_IsNotARepublication()
    {
        var payload = Encoding.ASCII.GetBytes("case-and-padding-payload");
        var frozen = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var update = UpdateWithSha($"  {frozen.ToUpperInvariant()}  ", payload.Length, "v2026.061598") with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };
        var client = new StubReleaseClient
        {
            StreamFactory = () => new MemoryStream(payload),
            ChecksumText = $"{frozen}  {update.Asset.Name}",
        };
        var service = CreateService(client, BuildVariant.Standard);

        using IVerifiedUpdatePackage package = await service.DownloadVerifiedAsync(
            update, null, CancellationToken.None);

        Assert.Equal(frozen, package.ExpectedSha256);
    }

    /// <summary>
    /// A published value of the right length but the wrong alphabet is not a checksum.
    /// </summary>
    /// <remarks>
    /// A document serving 64 characters of prose - a proxy error page, a truncated write -
    /// must read as "cannot be established", not as a different checksum. Weakening the hex
    /// test to a length test turns this into a refusal of a perfectly good install.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_PublishedValueIsNotHex_IsNotARepublication()
    {
        var payload = Encoding.ASCII.GetBytes("not-hex-payload");
        var frozen = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var update = UpdateWithSha(frozen, payload.Length, "v2026.061599") with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };
        var client = new StubReleaseClient
        {
            StreamFactory = () => new MemoryStream(payload),
            ChecksumText = $"{new string('z', 64)}  {update.Asset.Name}",
        };
        var service = CreateService(client, BuildVariant.Standard);

        using IVerifiedUpdatePackage package = await service.DownloadVerifiedAsync(
            update, null, CancellationToken.None);

        Assert.Equal(frozen, package.ExpectedSha256);
    }

    /// <summary>
    /// A checksum that cannot be re-read does not refuse the install.
    /// </summary>
    /// <remarks>
    /// Silence is not evidence of republication. The frozen checksum still decides what may
    /// be installed, so a network blip on this diagnostic read must not turn into a failed
    /// update - which is what treating "unknown" as "changed" would produce.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_PublishedChecksumUnreadable_InstallsAgainstTheFrozenHash()
    {
        var payload = Encoding.ASCII.GetBytes("unreadable-checksum-payload");
        var frozen = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var client = new StubReleaseClient
        {
            StreamFactory = () => new MemoryStream(payload),
            ChecksumText = null,
        };
        var service = CreateService(client, BuildVariant.Standard);
        var update = UpdateWithSha(frozen, payload.Length, "v2026.061593") with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };

        using IVerifiedUpdatePackage package = await service.DownloadVerifiedAsync(
            update, null, CancellationToken.None);

        Assert.Equal(frozen, package.ExpectedSha256);
    }

    /// <summary>
    /// A release still publishing the checksum the check froze installs normally.
    /// </summary>
    /// <remarks>
    /// The control for the refusal above. Without it, a comparison that refused everything
    /// would pass every other case in this file, because they carry no checksum URL at all.
    /// </remarks>
    [Fact]
    public async Task DownloadVerifiedAsync_PublishedChecksumUnchanged_Installs()
    {
        var payload = Encoding.ASCII.GetBytes("unchanged-checksum-payload");
        var frozen = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        const string tag = "v2026.061594";
        var update = UpdateWithSha(frozen, payload.Length, tag) with
        {
            ChecksumUrl = "https://example.test/SHA256SUMS.txt",
        };
        var client = new StubReleaseClient
        {
            StreamFactory = () => new MemoryStream(payload),
            ChecksumText = $"{frozen}  {update.Asset.Name}",
        };
        var service = CreateService(client, BuildVariant.Standard);

        using IVerifiedUpdatePackage package = await service.DownloadVerifiedAsync(
            update, null, CancellationToken.None);

        Assert.Equal(frozen, package.ExpectedSha256);
    }

    private static UpdateInfo UpdateWithSha(string? sha256, long sizeBytes, string versionTag = NewerTag)
    {
        var version = HeimdallVersion.Parse(versionTag);
        var asset = new UpdateAsset($"Heimdall_{version}_Standard_Setup.exe", "https://example.test/standard.exe", sizeBytes);
        return new UpdateInfo(version, versionTag, "https://example.test", "notes", asset, sha256);
    }

    private HashSet<string> StagingSnapshot(string versionTag)
    {
        var version = HeimdallVersion.Parse(versionTag);
        string updatesRoot = ApplicationDataPathResolver.GetUpdatesDirectory(_dataRoot);
        return Directory.Exists(updatesRoot)
            ? new(
                Directory.EnumerateDirectories(
                    updatesRoot,
                    $"Heimdall_{version}_*"),
                StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private sealed class StubReleaseClient : IGitHubReleaseClient
    {
        public GitHubRelease? Release { get; set; }

        public string? ChecksumText { get; set; }

        public Func<Stream>? StreamFactory { get; set; }

        /// <summary>Why the lookup fails, when <see cref="Release"/> is null.</summary>
        /// <remarks>
        /// Defaulted to a real cause rather than to None. The seam refuses to carry "nothing,
        /// and no reason", so a double that has not been told what to fail with would throw
        /// instead of quietly leaving every caller's failure handling unexercised.
        /// </remarks>
        public UpdateCheckFailure Failure { get; set; } = UpdateCheckFailure.SourceUnavailable;

        public TimeSpan? RetryAfter { get; set; }

        public Task<GitHubReleaseResult> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Release is null
                ? GitHubReleaseResult.Failed(Failure, RetryAfter)
                : GitHubReleaseResult.Succeeded(Release));
        }

        /// <summary>Every URL passed to <see cref="GetAssetTextAsync"/>, in order.</summary>
        /// <remarks>
        /// Recorded because the stub answers any URL with the same text, so a caller that
        /// asks for the WRONG document is indistinguishable from one that asks for the right
        /// one unless the request itself is observed. In production that mistake is not
        /// harmless: reading the installer's URL as text trips the client's one megabyte
        /// bound, returns null, and silently disables the republication diagnosis.
        /// </remarks>
        public List<string> AssetTextRequests { get; } = [];

        /// <summary>How many times a binary asset stream was opened.</summary>
        /// <remarks>
        /// The only way to assert that a refusal happened BEFORE the download. Staging is
        /// cleaned up on any throw, so an empty staging directory says the cleanup ran, not
        /// that nothing was downloaded.
        /// </remarks>
        public int AssetStreamOpenCount { get; private set; }

        public Task<string?> GetAssetTextAsync(string url, CancellationToken cancellationToken)
        {
            AssetTextRequests.Add(url);
            return Task.FromResult(ChecksumText);
        }

        public Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken)
        {
            AssetStreamOpenCount++;
            return Task.FromResult(StreamFactory?.Invoke() ?? Stream.Null);
        }
    }

    /// <summary>
    /// A release client whose checksum document changes between reads.
    /// </summary>
    /// <remarks>
    /// The only way to model a republication that lands mid-install: the pre-download read
    /// sees the old document, the post-download read sees the new one. A single fixed answer
    /// cannot express it, and without it the post-download branch has no oracle at all.
    /// </remarks>
    private sealed class ChecksumChangingClient(Func<Stream> streamFactory, IReadOnlyList<string> answers)
        : IGitHubReleaseClient
    {
        private int _reads;

        public int AssetStreamOpenCount { get; private set; }

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
            => Task.FromResult<GitHubRelease?>(null);

        public Task<string?> GetAssetTextAsync(string url, CancellationToken cancellationToken)
        {
            // The last answer stands for every read past the end, so a caller that reads
            // once more than expected sees the settled state rather than an exception.
            var answer = answers[Math.Min(_reads, answers.Count - 1)];
            _reads++;
            return Task.FromResult<string?>(answer);
        }

        public Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken)
        {
            AssetStreamOpenCount++;
            return Task.FromResult(streamFactory());
        }
    }

    private sealed class StubVariantDetector(BuildVariant variant, bool installedInPlace = true) : IVariantDetector
    {
        public BuildVariant Detect() => variant;

        public bool IsInstalledInPlace() => installedInPlace;
    }

    /// <summary>A body that never delivers a byte and never ends, until cancelled.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = [];

        public void Report(double value) => Reports.Add(value);
    }

    /// <summary>
    /// The cause of a failed lookup reaches the caller, and so does any waiting time.
    /// </summary>
    /// <remarks>
    /// A-17. Five conditions used to arrive as one null and be reported with one sentence
    /// telling the user to read a log they cannot find. The status stays CheckFailed - what a
    /// caller DOES about it has not changed - and the cause rides alongside. Every member is
    /// covered rather than a sample, because a switch that forgets one is the defect.
    /// </remarks>
    [Theory]
    [InlineData(UpdateCheckFailure.NetworkUnreachable)]
    [InlineData(UpdateCheckFailure.SecureChannelFailed)]
    [InlineData(UpdateCheckFailure.RateLimited)]
    [InlineData(UpdateCheckFailure.SourceNotFound)]
    [InlineData(UpdateCheckFailure.MalformedResponse)]
    [InlineData(UpdateCheckFailure.AccessDenied)]
    [InlineData(UpdateCheckFailure.SourceUnavailable)]
    [InlineData(UpdateCheckFailure.TimedOut)]
    public async Task CheckForUpdatesAsync_LookupFailed_CarriesTheCauseNotJustTheFailure(
        UpdateCheckFailure failure)
    {
        var client = new StubReleaseClient { Release = null, Failure = failure };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(
            HeimdallVersion.Parse(CurrentTag), "owner", "repo", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.CheckFailed, result.Status);
        Assert.Equal(failure, result.Failure);
    }

    /// <summary>
    /// A waiting time the source volunteered reaches the caller too.
    /// </summary>
    /// <remarks>
    /// Separate from the cause because it is separately droppable: the hint is optional on
    /// every path, and a caller that has the cause but silently loses the hint would show a
    /// vaguer message than it could, with nothing going red.
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_RateLimitedWithAResetTime_CarriesTheWaitingTime()
    {
        var client = new StubReleaseClient
        {
            Release = null,
            Failure = UpdateCheckFailure.RateLimited,
            RetryAfter = TimeSpan.FromMinutes(42),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(
            HeimdallVersion.Parse(CurrentTag), "owner", "repo", CancellationToken.None);

        Assert.Equal(UpdateCheckFailure.RateLimited, result.Failure);
        Assert.Equal(TimeSpan.FromMinutes(42), result.RetryAfter);
    }

    /// <summary>
    /// A check that succeeded reports no cause at all.
    /// </summary>
    /// <remarks>
    /// The control that keeps the field honest. A Failure left at a stale value on the
    /// success path would be invisible here without it, and would surface later as a cause
    /// attached to an answer that worked.
    /// </remarks>
    [Fact]
    public async Task CheckForUpdatesAsync_Succeeded_ReportsNoCause()
    {
        var client = new StubReleaseClient
        {
            Release = new GitHubRelease(CurrentTag, "https://example.test", "notes", []),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(
            HeimdallVersion.Parse(CurrentTag), "owner", "repo", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(UpdateCheckFailure.None, result.Failure);
        Assert.Null(result.RetryAfter);
    }
}
