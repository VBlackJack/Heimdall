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

using System.Net;
using System.Text;
using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class GitHubReleaseClientTests
{
    private const string AppVersion = "2026.061501";

    private const string ChecksumUrl = "https://github.com/VBlackJack/Heimdall/releases/download/v2026.061502/SHA256SUMS.txt";

    private const string InstallerUrl = "https://github.com/VBlackJack/Heimdall/releases/download/v2026.061502/standard.exe";

    private const string LatestReleaseJson = """
    {
        "tag_name": "v2026.061502",
        "html_url": "https://github.com/VBlackJack/Heimdall/releases/tag/v2026.061502",
        "body": "Release notes body.",
        "assets": [
            { "name": "Heimdall_2026.061502_Standard_Setup.exe", "browser_download_url": "https://example.test/standard.exe", "size": 112233 },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt", "size": 256 }
        ]
    }
    """;

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesReleaseAndAssets()
    {
        var client = CreateClient((_, _) => JsonResponse(HttpStatusCode.OK, LatestReleaseJson));

        var release = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("v2026.061502", release!.TagName);
        Assert.Equal("https://github.com/VBlackJack/Heimdall/releases/tag/v2026.061502", release.HtmlUrl);
        Assert.Equal("Release notes body.", release.Body);
        Assert.Equal(2, release.Assets.Count);

        var installer = release.Assets[0];
        Assert.Equal("Heimdall_2026.061502_Standard_Setup.exe", installer.Name);
        Assert.Equal("https://example.test/standard.exe", installer.DownloadUrl);
        Assert.Equal(112233, installer.SizeBytes);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_SendsUserAgentAndAcceptHeaders()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient((request, _) =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, LatestReleaseJson);
        });

        await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal($"Heimdall/{AppVersion}", captured!.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", captured.Headers.Accept.ToString());
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NotFound_ReturnsNull()
    {
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        var release = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_HttpRequestException_ReturnsNull()
    {
        var client = CreateClient((_, _) => throw new HttpRequestException("network down"));

        var release = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_Timeout_ReturnsNull()
    {
        // HttpClient timeout surfaces as TaskCanceledException while the caller token is not signaled.
        var client = CreateClient((_, _) => throw new TaskCanceledException("timed out"));

        var release = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_CallerCanceled_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = CreateClient((_, ct) => throw new TaskCanceledException("canceled", innerException: null, ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetLatestReleaseAsync("VBlackJack", "Heimdall", cts.Token));
    }

    [Fact]
    public async Task GetAssetTextAsync_ReturnsBody()
    {
        const string body = "abc123  Heimdall_2026.061502_Standard_Setup.exe";
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8),
        });

        var text = await client.GetAssetTextAsync(ChecksumUrl, CancellationToken.None);

        Assert.Equal(body, text);
    }

    [Fact]
    public async Task GetAssetTextAsync_Failure_ReturnsNull()
    {
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var text = await client.GetAssetTextAsync(ChecksumUrl, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task GetAssetTextAsync_Timeout_ReturnsNull()
    {
        var client = CreateClient((_, _) => throw new TaskCanceledException("timed out"));

        var text = await client.GetAssetTextAsync(ChecksumUrl, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task GetAssetTextAsync_CallerCanceled_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = CreateClient((_, ct) => throw new TaskCanceledException("canceled", innerException: null, ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAssetTextAsync(ChecksumUrl, cts.Token));
    }

    [Fact]
    public async Task OpenAssetStreamAsync_ReturnsAssetBytes()
    {
        var payload = Encoding.ASCII.GetBytes("installer-bytes");
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        await using var stream = await client.OpenAssetStreamAsync(InstallerUrl, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task OpenAssetStreamAsync_NonSuccess_Throws()
    {
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.OpenAssetStreamAsync("https://github.com/VBlackJack/Heimdall/releases/download/v1/missing.exe", CancellationToken.None));
    }

    /// <remarks>
    /// Only the API origin was pinned; every asset URL came out of the JSON and was
    /// dispatched as found, checksum file and installer alike. A refused URL must not
    /// reach the network at all.
    /// </remarks>
    [Theory]
    [InlineData("https://evil.example/Heimdall_Setup.exe")]
    [InlineData("http://github.com/VBlackJack/Heimdall/releases/download/v1/setup.exe")]
    [InlineData("https://github.com.evil.example/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("not a url")]
    public async Task OpenAssetStreamAsync_UnexpectedOrigin_RefusesWithoutSending(string url)
    {
        int sent = 0;
        var client = CreateClient((_, _) =>
        {
            sent++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.OpenAssetStreamAsync(url, CancellationToken.None));

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task GetAssetTextAsync_UnexpectedOrigin_ReturnsNullWithoutSending()
    {
        int sent = 0;
        var client = CreateClient((_, _) =>
        {
            sent++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("abc  x.exe") };
        });

        string? text = await client.GetAssetTextAsync("https://evil.example/SHA256SUMS.txt", CancellationToken.None);

        Assert.Null(text);
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("https://github.com/VBlackJack/Heimdall/releases/download/v1/setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/x", true)]
    [InlineData("https://api.github.com/repos/x/y/releases/assets/1", true)]
    [InlineData("https://GITHUB.COM/x", true)]
    [InlineData("https://githubusercontent.com/x", true)]
    [InlineData("https://notgithub.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("http://github.com/x", false)]
    [InlineData("/relative/path", false)]
    public void IsAllowedAssetUrl_AcceptsOnlyHttpsOnGitHubOrigins(string url, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.IsAllowedAssetUrl(url));
    }

    /// <remarks>
    /// The response was neither returned nor disposed when the status check threw,
    /// so its connection stayed with the garbage collector.
    /// </remarks>
    [Fact]
    public async Task OpenAssetStreamAsync_NonSuccess_DisposesTheResponse()
    {
        var content = new DisposeTrackingContent();
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.OpenAssetStreamAsync("https://github.com/x/missing.exe", CancellationToken.None));

        Assert.True(content.Disposed, "the response content must be disposed when it is not returned");
    }

    private sealed class DisposeTrackingContent() : ByteArrayContent([0])
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GitHubReleaseClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responder));
        return new GitHubReleaseClient(httpClient, AppVersion);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request, cancellationToken));
    }
}
