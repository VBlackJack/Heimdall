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

        var text = await client.GetAssetTextAsync("https://example.test/SHA256SUMS.txt", CancellationToken.None);

        Assert.Equal(body, text);
    }

    [Fact]
    public async Task GetAssetTextAsync_Failure_ReturnsNull()
    {
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var text = await client.GetAssetTextAsync("https://example.test/SHA256SUMS.txt", CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task GetAssetTextAsync_Timeout_ReturnsNull()
    {
        var client = CreateClient((_, _) => throw new TaskCanceledException("timed out"));

        var text = await client.GetAssetTextAsync("https://example.test/SHA256SUMS.txt", CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task GetAssetTextAsync_CallerCanceled_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = CreateClient((_, ct) => throw new TaskCanceledException("canceled", innerException: null, ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAssetTextAsync("https://example.test/SHA256SUMS.txt", cts.Token));
    }

    [Fact]
    public async Task OpenAssetStreamAsync_ReturnsAssetBytes()
    {
        var payload = Encoding.ASCII.GetBytes("installer-bytes");
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        await using var stream = await client.OpenAssetStreamAsync("https://example.test/standard.exe", CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task OpenAssetStreamAsync_NonSuccess_Throws()
    {
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.OpenAssetStreamAsync("https://example.test/missing.exe", CancellationToken.None));
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
