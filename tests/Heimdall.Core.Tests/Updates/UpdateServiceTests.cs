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
using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class UpdateServiceTests
{
    private const string CurrentTag = "v2026.061501";
    private const string NewerTag = "v2026.061502";
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
    public async Task DownloadVerifiedAsync_MatchingHash_ReturnsVerifiedTempPath()
    {
        var payload = Encoding.ASCII.GetBytes("verified-installer-payload");
        var hash = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        var update = UpdateWithSha(hash, payload.Length);
        var progress = new RecordingProgress();

        var path = await service.DownloadVerifiedAsync(update, progress, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.NotEmpty(progress.Reports);
            Assert.Equal(1.0, progress.Reports[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ExeInstaller_ReturnsPathWithExeExtension()
    {
        var payload = Encoding.ASCII.GetBytes("verified-installer-payload");
        var hash = Sha256Verifier.ComputeHex(new MemoryStream(payload));
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        var update = UpdateWithSha(hash, payload.Length);

        var path = await service.DownloadVerifiedAsync(update, null, CancellationToken.None);

        try
        {
            Assert.Equal(".exe", Path.GetExtension(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DownloadVerifiedAsync_HashMismatch_ThrowsAndDeletesTemp()
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var wrongHash = new string('0', 64);
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        // Unique version isolates this test's temp-file snapshot from the other (parallel) download tests.
        const string tag = "v2026.061597";
        var update = UpdateWithSha(wrongHash, payload.Length, tag);

        var before = TempSnapshot(tag);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Empty(TempSnapshot(tag).Except(before));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_NullSha256_ThrowsAndDeletesTemp()
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var client = new StubReleaseClient { StreamFactory = () => new MemoryStream(payload) };
        var service = CreateService(client, BuildVariant.Standard);
        // Unique version isolates this test's temp-file snapshot from the other (parallel) download tests.
        const string tag = "v2026.061598";
        var update = UpdateWithSha(null, payload.Length, tag);

        var before = TempSnapshot(tag);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadVerifiedAsync(update, null, CancellationToken.None));

        Assert.Empty(TempSnapshot(tag).Except(before));
    }

    private static UpdateService CreateService(StubReleaseClient client, BuildVariant variant)
        => new(client, new StubVariantDetector(variant));

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

    private static HashSet<string> TempSnapshot(string versionTag)
    {
        var version = HeimdallVersion.Parse(versionTag);
        return new(Directory.EnumerateFiles(Path.GetTempPath(), $"Heimdall_{version}_*"), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubReleaseClient : IGitHubReleaseClient
    {
        public GitHubRelease? Release { get; set; }

        public string? ChecksumText { get; set; }

        public Func<Stream>? StreamFactory { get; set; }

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
            => Task.FromResult(Release);

        public Task<string?> GetAssetTextAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(ChecksumText);

        public Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(StreamFactory?.Invoke() ?? Stream.Null);
    }

    private sealed class StubVariantDetector(BuildVariant variant) : IVariantDetector
    {
        public BuildVariant Detect() => variant;
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = [];

        public void Report(double value) => Reports.Add(value);
    }
}
