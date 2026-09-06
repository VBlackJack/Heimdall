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

    [Fact]
    public async Task CheckForUpdatesAsync_UnparseableTag_CheckFailed()
    {
        var client = new StubReleaseClient
        {
            Release = new GitHubRelease("not-a-version", "https://example.test", "notes", []),
        };
        var service = CreateService(client, BuildVariant.Standard);

        var result = await service.CheckForUpdatesAsync(HeimdallVersion.Parse(CurrentTag), "o", "r", CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.CheckFailed, result.Status);
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

    private UpdateService CreateService(StubReleaseClient client, BuildVariant variant)
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

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Release);
        }

        public Task<string?> GetAssetTextAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(ChecksumText);

        public Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(StreamFactory?.Invoke() ?? Stream.Null);
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
}
