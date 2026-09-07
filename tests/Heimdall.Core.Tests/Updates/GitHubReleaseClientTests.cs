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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
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

        var result = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.Equal(UpdateCheckFailure.None, result.Failure);
        var release = result.Release;
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

    /// <summary>
    /// The five conditions that used to arrive as one null now arrive named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A-17, asserted through the real client rather than through
    /// <c>ClassifyStatus</c> alone. A perfect classifier that nothing reached would leave
    /// every one of these green, which is the vacuity this covers: each row here drives an
    /// actual response or an actual throw all the way to a returned cause.
    /// </para>
    /// <para>
    /// The advice differs for every row. Connect to the internet; look at your proxy or your
    /// clock; wait; nothing you can do, it is the maintainer's problem; nothing anybody can
    /// do yet. One sentence for all five sent every user to read a log they cannot find.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetLatestReleaseAsync_EachStoppingCondition_ArrivesWithItsOwnCause()
    {
        await AssertCauseAsync(
            (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound),
            UpdateCheckFailure.SourceNotFound);

        await AssertCauseAsync(
            (_, _) => throw new HttpRequestException("network down"),
            UpdateCheckFailure.NetworkUnreachable);

        await AssertCauseAsync(
            (_, _) => throw new HttpRequestException(
                "handshake", new AuthenticationException("bad certificate")),
            UpdateCheckFailure.SecureChannelFailed);

        // HttpClient timeout surfaces as TaskCanceledException while the caller token is not signaled.
        await AssertCauseAsync(
            (_, _) => throw new TaskCanceledException("timed out"),
            UpdateCheckFailure.TimedOut);

        await AssertCauseAsync(
            (_, _) => JsonResponse(HttpStatusCode.OK, "{ this is not json"),
            UpdateCheckFailure.MalformedResponse);

        // A 200 that parses but names no release is unreadable in the sense that matters.
        await AssertCauseAsync(
            (_, _) => JsonResponse(HttpStatusCode.OK, "{ }"),
            UpdateCheckFailure.MalformedResponse);
    }

    /// <summary>
    /// A throttled answer carries both the cause and the wait the source volunteered.
    /// </summary>
    /// <remarks>
    /// The 403-with-no-quota-left shape is GitHub's usual way of throttling, and the reason
    /// the classifier reads a header rather than the status alone. The reset time is what
    /// turns "try again later" into something the user can act on.
    /// </remarks>
    [Fact]
    public async Task GetLatestReleaseAsync_ThrottledWithAResetTime_ReportsBoth()
    {
        var client = CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Reset",
                DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var result = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.Equal(UpdateCheckFailure.RateLimited, result.Failure);
        Assert.NotNull(result.RetryAfter);
        Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(20));
    }

    private async Task AssertCauseAsync(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler,
        UpdateCheckFailure expected)
    {
        var client = CreateClient(handler);

        var result = await client.GetLatestReleaseAsync("VBlackJack", "Heimdall", CancellationToken.None);

        Assert.Null(result.Release);
        Assert.Equal(expected, result.Failure);
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

    // The asset text path is the checksum list: a few hundred bytes from the
    // application's own release. It used to be buffered whole with no ceiling, so an
    // allowed host serving an unbounded body would have been read into memory until
    // something else stopped it. The bound has two halves and each is tested on its own,
    // because either one alone leaves a way through.

    [Fact]
    public async Task GetAssetTextAsync_DeclaredLengthPastTheBound_IsRefusedWithoutReadingTheBody()
    {
        // A small body behind a header that claims two megabytes. Only the declared
        // length can catch this one: the body itself is well under the bound, so a
        // reader that trusts what it receives would accept it and never notice the lie.
        var content = new StringContent("chunk-of-checksums", Encoding.UTF8);
        content.Headers.ContentLength = 2 * 1024 * 1024;

        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        string? text = await client.GetAssetTextAsync(ChecksumUrl, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task GetAssetTextAsync_UndeclaredBodyPastTheBound_IsRefusedWhileReading()
    {
        // No Content-Length at all, which is what a chunked response looks like, and a
        // body over the bound. Only the copy can catch this one.
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnsizedContent(2 * 1024 * 1024),
        });

        string? text = await client.GetAssetTextAsync(ChecksumUrl, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task GetAssetTextAsync_OrdinaryChecksumList_IsReturnedWhole()
    {
        // The control that keeps the two refusals honest: a bound that rejected
        // everything would satisfy both of them and break the updater completely.
        const string Body = "a1b2c3  Heimdall_2026.061502_Standard_Setup.exe\nd4e5f6  Heimdall_build.zip\n";
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Body, Encoding.UTF8),
        });

        string? text = await client.GetAssetTextAsync(ChecksumUrl, CancellationToken.None);

        Assert.Equal(Body, text);
    }

    /// <summary>A body whose length is not known in advance, as a chunked response is.</summary>
    private sealed class UnsizedContent(int byteCount) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] chunk = new byte[8192];
            Array.Fill(chunk, (byte)'x');

            int written = 0;
            while (written < byteCount)
            {
                int size = Math.Min(chunk.Length, byteCount - written);
                stream.Write(chunk, 0, size);
                written += size;
            }

            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
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

    /// <summary>
    /// Each non-success status is read as the thing the user would have to do about it.
    /// </summary>
    /// <remarks>
    /// A-17. The two rows that matter are the pair of 403s. GitHub answers a spent quota
    /// with 403 far more often than with 429, and a 403 is otherwise an ordinary refusal, so
    /// the status alone cannot tell "wait an hour" from "this will never work" - only
    /// X-RateLimit-Remaining can. Collapse the two and half the users get advice that will
    /// never help them.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, null, UpdateCheckFailure.SourceNotFound)]
    [InlineData(HttpStatusCode.Forbidden, "0", UpdateCheckFailure.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, "57", UpdateCheckFailure.AccessDenied)]
    [InlineData((HttpStatusCode)451, null, UpdateCheckFailure.SourceNotFound)]
    [InlineData(HttpStatusCode.Forbidden, null, UpdateCheckFailure.AccessDenied)]
    [InlineData(HttpStatusCode.Forbidden, "not a number", UpdateCheckFailure.AccessDenied)]
    [InlineData(HttpStatusCode.TooManyRequests, "57", UpdateCheckFailure.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, null, UpdateCheckFailure.AccessDenied)]
    [InlineData(HttpStatusCode.InternalServerError, null, UpdateCheckFailure.SourceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, UpdateCheckFailure.SourceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, null, UpdateCheckFailure.SourceUnavailable)]
    [InlineData((HttpStatusCode)418, null, UpdateCheckFailure.SourceUnavailable)]
    public void ClassifyStatus_ReadsTheStatusAsACause(
        HttpStatusCode status, string? rateLimitRemaining, UpdateCheckFailure expected)
    {
        using HttpResponseMessage response = new(status);
        if (rateLimitRemaining is not null)
        {
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", rateLimitRemaining);
        }

        Assert.Equal(expected, GitHubReleaseClient.ClassifyStatus(status, response.Headers));
    }

    /// <summary>
    /// GitHub's secondary rate limit is read as rate limiting, not as a plain refusal.
    /// </summary>
    /// <remarks>
    /// The row the first version of this change missed, and the one that matters most in
    /// practice. GitHub's documented behaviour for a secondary limit is a 403 or 429 with
    /// <c>Retry-After</c> set, and with <c>X-RateLimit-Remaining</c> typically ABOVE zero -
    /// the primary quota is not what was spent. Reading only the remaining count sends
    /// exactly that case to "the update server refused the request. See the log for
    /// details", which is the sentence A-17 exists to stop showing.
    /// </remarks>
    [Fact]
    public void ClassifyStatus_SecondaryRateLimit_IsReadAsRateLimiting()
    {
        using HttpResponseMessage secondary = new(HttpStatusCode.Forbidden);
        secondary.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "57");
        secondary.Headers.TryAddWithoutValidation("Retry-After", "60");

        Assert.Equal(
            UpdateCheckFailure.RateLimited,
            GitHubReleaseClient.ClassifyStatus(HttpStatusCode.Forbidden, secondary.Headers));

        // And the wait it asked for survives, which is the whole value of naming it.
        Assert.Equal(TimeSpan.FromSeconds(60), GitHubReleaseClient.ReadRetryAfter(secondary.Headers));
    }

    /// <summary>
    /// A transport failure is read from the error code the stack sets, not from a guess.
    /// </summary>
    /// <remarks>
    /// Five unrelated conditions arrive as the same exception type. Returning "you are
    /// offline" for all of them would rebuild, one layer down, the collapse this change
    /// undoes: a proxy demanding credentials and a response that is not HTTP are neither of
    /// them a missing network, and neither is fixed by looking at one.
    /// </remarks>
    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, UpdateCheckFailure.SecureChannelFailed)]
    [InlineData(HttpRequestError.ProxyTunnelError, UpdateCheckFailure.ProxyRefused)]
    [InlineData(HttpRequestError.InvalidResponse, UpdateCheckFailure.MalformedResponse)]
    [InlineData(HttpRequestError.ResponseEnded, UpdateCheckFailure.MalformedResponse)]
    [InlineData(HttpRequestError.ConfigurationLimitExceeded, UpdateCheckFailure.MalformedResponse)]
    [InlineData(HttpRequestError.NameResolutionError, UpdateCheckFailure.NetworkUnreachable)]
    [InlineData(HttpRequestError.ConnectionError, UpdateCheckFailure.NetworkUnreachable)]
    public void ClassifyTransportFailure_ReadsTheErrorCode(
        HttpRequestError error, UpdateCheckFailure expected)
    {
        Assert.Equal(
            expected,
            GitHubReleaseClient.ClassifyTransportFailure(new HttpRequestException(error, "transport")));
    }

    /// <summary>
    /// An answer cut off after its headers is reported, not left to escape.
    /// </summary>
    /// <remarks>
    /// The body is read after the headers, so a dropped connection throws
    /// <see cref="HttpIOException"/> - which derives from <see cref="IOException"/> and NOT
    /// from <see cref="HttpRequestException"/>, so it used to slip past every catch here and
    /// reach the caller's blanket handler as an unexplained fault. That is the ordinary
    /// flaky-connection case, and it was not in the enumeration this finding claimed to make.
    /// </remarks>
    [Fact]
    public async Task GetLatestReleaseAsync_AnswerCutOffAfterTheHeaders_IsReportedNotEscaped()
    {
        await AssertCauseAsync(
            (_, _) => throw new HttpIOException(HttpRequestError.ResponseEnded, "cut off"),
            UpdateCheckFailure.NetworkUnreachable);

        await AssertCauseAsync(
            (_, _) => throw new HttpIOException(HttpRequestError.InvalidResponse, "not http"),
            UpdateCheckFailure.MalformedResponse);
    }

    /// <summary>
    /// A waiting time is read when the source volunteered one, and never invented.
    /// </summary>
    /// <remarks>
    /// Two spellings, because GitHub uses the second: <c>Retry-After</c> carries a delay or a
    /// date, while its own throttling sets <c>X-RateLimit-Reset</c> to a Unix time. A reset
    /// already in the past is dropped rather than shown as "wait 0 minutes", which is what a
    /// clock a few seconds out would otherwise produce on every throttled check.
    /// </remarks>
    [Fact]
    public void ReadRetryAfter_ReadsBothSpellingsAndInventsNothing()
    {
        using HttpResponseMessage none = new(HttpStatusCode.Forbidden);
        Assert.Null(GitHubReleaseClient.ReadRetryAfter(none.Headers));

        using HttpResponseMessage delta = new(HttpStatusCode.TooManyRequests);
        delta.Headers.TryAddWithoutValidation("Retry-After", "120");
        Assert.Equal(TimeSpan.FromSeconds(120), GitHubReleaseClient.ReadRetryAfter(delta.Headers));

        using HttpResponseMessage reset = new(HttpStatusCode.Forbidden);
        reset.Headers.TryAddWithoutValidation(
            "X-RateLimit-Reset",
            DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var fromReset = GitHubReleaseClient.ReadRetryAfter(reset.Headers);
        Assert.NotNull(fromReset);
        Assert.InRange(fromReset!.Value, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(30));

        using HttpResponseMessage past = new(HttpStatusCode.Forbidden);
        past.Headers.TryAddWithoutValidation(
            "X-RateLimit-Reset",
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Assert.Null(GitHubReleaseClient.ReadRetryAfter(past.Headers));

        using HttpResponseMessage garbage = new(HttpStatusCode.Forbidden);
        garbage.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "soon");
        Assert.Null(GitHubReleaseClient.ReadRetryAfter(garbage.Headers));
    }

    /// <summary>
    /// A connection that could not be secured is not reported as an absent network.
    /// </summary>
    /// <remarks>
    /// A TLS failure arrives as an <see cref="HttpRequestException"/> exactly like an
    /// unreachable host, with the real cause nested inside. Telling somebody behind an
    /// intercepting proxy, or with a clock a year out, that they are offline sends them to
    /// look at the one thing that is working.
    /// </remarks>
    [Fact]
    public void ClassifyTransportFailure_SeparatesATlsFailureFromAnAbsentNetwork()
    {
        Assert.Equal(
            UpdateCheckFailure.NetworkUnreachable,
            GitHubReleaseClient.ClassifyTransportFailure(new HttpRequestException("no route to host")));

        Assert.Equal(
            UpdateCheckFailure.SecureChannelFailed,
            GitHubReleaseClient.ClassifyTransportFailure(
                new HttpRequestException("failed", new AuthenticationException("bad certificate"))));

        // Nested one level deeper than HttpClient usually puts it, because it is not
        // contractual: the walk has to reach the cause wherever the stack put it.
        Assert.Equal(
            UpdateCheckFailure.SecureChannelFailed,
            GitHubReleaseClient.ClassifyTransportFailure(
                new HttpRequestException(
                    "failed",
                    new IOException("stream", new AuthenticationException("bad certificate")))));
    }

    /// <summary>
    /// A lookup cannot say "no release" without saying why, and cannot say both.
    /// </summary>
    /// <remarks>
    /// The invariant is the point of the type. A test double left returning a bare null
    /// would leave every caller's failure handling unexercised while every existing test
    /// stayed green - which is exactly the shape of the defect A-17 records.
    /// </remarks>
    [Fact]
    public void GitHubReleaseResult_CannotBeSilentAndCannotBeBoth()
    {
        Assert.Throws<ArgumentException>(
            () => new GitHubReleaseResult(null, UpdateCheckFailure.None));

        Assert.Throws<ArgumentException>(
            () => new GitHubReleaseResult(
                new GitHubRelease("v1", "url", "body", []), UpdateCheckFailure.RateLimited));

        var failed = GitHubReleaseResult.Failed(UpdateCheckFailure.TimedOut);
        Assert.Null(failed.Release);
        Assert.Equal(UpdateCheckFailure.TimedOut, failed.Failure);
    }
}
