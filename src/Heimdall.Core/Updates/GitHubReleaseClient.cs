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
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.Logging;

namespace Heimdall.Core.Updates;

/// <summary>
/// GitHub releases API client. Wraps an injected <see cref="HttpClient"/> and is
/// the single HTTP owner of the updater. Returns null on any network or parse
/// failure (logged at Warning) so callers degrade gracefully.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private const string ApiBaseUrl = "https://api.github.com";

    /// <summary>
    /// The only hosts an asset may be fetched from. The release JSON names every asset
    /// URL, and until now that URL was dispatched as found: only the API origin was
    /// pinned, every later hop was whatever the JSON said. Release assets live on
    /// github.com and redirect to githubusercontent.com; nothing else is expected.
    /// </summary>
    private static readonly string[] AllowedAssetHostSuffixes =
    [
        "github.com",
        "githubusercontent.com",
    ];
    private const string GitHubAcceptHeader = "application/vnd.github+json";

    /// <summary>Header GitHub uses to say how much of the caller's quota is left.</summary>
    private const string RateLimitRemainingHeader = "X-RateLimit-Remaining";

    /// <summary>Header GitHub uses to say when the caller's quota comes back, as a Unix time.</summary>
    private const string RateLimitResetHeader = "X-RateLimit-Reset";
    private const string UserAgentProduct = "Heimdall";

    /// <summary>
    /// The most an asset text response may hold before it is refused. The only asset
    /// read as text is the checksum list of the application's own release, a few
    /// hundred bytes; a megabyte is already three orders of magnitude of headroom.
    /// </summary>
    /// <remarks>
    /// The bound exists because the response used to be buffered whole with no ceiling
    /// at all: an allowed host serving an unbounded body would have been read into
    /// memory until something else stopped it. Both halves of the check are load
    /// bearing. A declared <c>Content-Length</c> past the bound is refused before a
    /// byte of body is read, and the copy itself stops at the bound as well, because a
    /// chunked response declares no length and a declared one can simply lie.
    /// </remarks>
    private const int MaxAssetTextBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly string _appVersion;

    /// <param name="httpClient">The HTTP client (honors the system proxy by default).</param>
    /// <param name="appVersion">The current application version, used in the User-Agent.</param>
    public GitHubReleaseClient(HttpClient httpClient, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        _httpClient = httpClient;
        _appVersion = appVersion;
    }

    public async Task<GitHubReleaseResult> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var url = $"{ApiBaseUrl}/repos/{owner}/{repo}/releases/latest";
        try
        {
            using var request = CreateRequest(url, acceptGitHubJson: true);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var failure = ClassifyStatus(response.StatusCode, response.Headers);
                var retryAfter = ReadRetryAfter(response.Headers);
                FileLogger.Warn(
                    $"GitHub release check failed: HTTP {(int)response.StatusCode} for {owner}/{repo}, "
                    + $"read as {failure}.");
                return GitHubReleaseResult.Failed(failure, retryAfter);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (dto?.TagName is null)
            {
                FileLogger.Warn($"GitHub release response for {owner}/{repo} contained no tag_name.");
                return GitHubReleaseResult.Failed(UpdateCheckFailure.MalformedResponse);
            }

            var assets = (dto.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.DownloadUrl))
                .Select(a => new UpdateAsset(a.Name!, a.DownloadUrl!, a.Size))
                .ToList();

            return GitHubReleaseResult.Succeeded(
                new GitHubRelease(dto.TagName, dto.HtmlUrl ?? string.Empty, dto.Body ?? string.Empty, assets));
        }
        catch (JsonException ex)
        {
            FileLogger.Warn($"GitHub release check could not read the answer for {owner}/{repo}: {ex.Message}");
            return GitHubReleaseResult.Failed(UpdateCheckFailure.MalformedResponse);
        }
        catch (HttpRequestException ex)
        {
            var failure = ClassifyTransportFailure(ex);
            FileLogger.Warn($"GitHub release check error for {owner}/{repo}: {ex.Message}, read as {failure}.");
            return GitHubReleaseResult.Failed(failure);
        }
        catch (HttpIOException ex)
        {
            // The body is read after the headers, so a connection dropped mid-answer throws
            // this - and it derives from IOException, NOT from HttpRequestException, so it
            // used to escape every catch here and reach the caller's blanket handler as an
            // unexplained fault. That is the ordinary flaky-connection case.
            var failure = ex.HttpRequestError == HttpRequestError.InvalidResponse
                ? UpdateCheckFailure.MalformedResponse
                : UpdateCheckFailure.NetworkUnreachable;
            FileLogger.Warn($"GitHub release check was cut off for {owner}/{repo}: {ex.Message}, read as {failure}.");
            return GitHubReleaseResult.Failed(failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout (not caller cancellation) surfaces as a canceled task.
            FileLogger.Warn($"GitHub release check timed out for {owner}/{repo}.");
            return GitHubReleaseResult.Failed(UpdateCheckFailure.TimedOut);
        }
    }

    /// <summary>
    /// Reads a non-success status as the thing the user would have to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate-limit reading is the one that needs care. GitHub answers a spent quota with
    /// <c>403</c> far more often than with <c>429</c>, and a <c>403</c> is otherwise an
    /// ordinary refusal, so the status alone cannot separate "wait an hour" from "this will
    /// never work". Two headers separate them, and BOTH are needed. The primary quota sets
    /// <c>X-RateLimit-Remaining: 0</c>. The secondary limit - the one an office behind a
    /// single address trips first - typically does not: it sets <c>Retry-After</c> and leaves
    /// a remaining count above zero. Reading only the first sends exactly that case to
    /// "the update server refused the request", which is the sentence this whole change
    /// exists to stop showing. A <c>429</c> is rate limiting whatever the headers say.
    /// </para>
    /// <para>
    /// Everything above 500 is the source's own fault and may work later. Anything else
    /// unexpected is reported the same way rather than invented into a category: a status
    /// this client has no reading for is exactly "the source did not answer usefully".
    /// </para>
    /// </remarks>
    internal static UpdateCheckFailure ClassifyStatus(HttpStatusCode status, HttpResponseHeaders headers)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return UpdateCheckFailure.RateLimited;
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return HasSpentRateLimit(headers) || headers.RetryAfter is not null
                ? UpdateCheckFailure.RateLimited
                : UpdateCheckFailure.AccessDenied;
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            return UpdateCheckFailure.AccessDenied;
        }

        if (status is HttpStatusCode.NotFound or HttpStatusCode.UnavailableForLegalReasons)
        {
            // 451 is permanent in practice - a repository taken down does not come back on
            // its own - so it belongs with "there is nothing here", not with "try later".
            return UpdateCheckFailure.SourceNotFound;
        }

        return UpdateCheckFailure.SourceUnavailable;
    }

    /// <summary>Whether the response says this caller's quota is spent.</summary>
    private static bool HasSpentRateLimit(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues(RateLimitRemainingHeader, out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
            {
                return remaining <= 0;
            }
        }

        return false;
    }

    /// <summary>
    /// How long the source asked the caller to wait, when it said so.
    /// </summary>
    /// <remarks>
    /// Two spellings, and both are read. <c>Retry-After</c> carries a delay or a date;
    /// GitHub's own throttling instead sets <c>X-RateLimit-Reset</c> to a Unix time. A hint
    /// is never invented when neither is present, and a negative one - a reset already past,
    /// or a clock out of step - is dropped rather than shown as "wait 0 seconds".
    /// </remarks>
    internal static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        if (retryAfter?.Date is { } date)
        {
            var untilDate = date - DateTimeOffset.UtcNow;
            return untilDate > TimeSpan.Zero ? untilDate : null;
        }

        if (!headers.TryGetValues(RateLimitResetHeader, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
            {
                continue;
            }

            var untilReset = DateTimeOffset.FromUnixTimeSeconds(epochSeconds) - DateTimeOffset.UtcNow;
            return untilReset > TimeSpan.Zero ? untilReset : null;
        }

        return null;
    }

    /// <summary>
    /// Reads a transport failure as the thing the user would have to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five unrelated conditions arrive here as the same <see cref="HttpRequestException"/>:
    /// no route, a proxy that refuses the tunnel, a handshake that fails, a response that is
    /// not HTTP, and one that ends early. Returning "you are offline" for all of them would
    /// reproduce, one layer down, the collapse this whole change exists to undo.
    /// </para>
    /// <para>
    /// <see cref="HttpRequestException.HttpRequestError"/> is the contractual signal and is
    /// read first. The walk for a nested <see cref="AuthenticationException"/> stays behind
    /// it as a fallback, for an exception that carries no code - one built by hand, or by a
    /// stack that does not set it. It is a guess, which is why it no longer decides alone.
    /// </para>
    /// </remarks>
    internal static UpdateCheckFailure ClassifyTransportFailure(HttpRequestException exception)
    {
        switch (exception.HttpRequestError)
        {
            case HttpRequestError.SecureConnectionError:
                return UpdateCheckFailure.SecureChannelFailed;

            case HttpRequestError.ProxyTunnelError:
                return UpdateCheckFailure.ProxyRefused;

            case HttpRequestError.InvalidResponse:
            case HttpRequestError.ResponseEnded:
            case HttpRequestError.ConfigurationLimitExceeded:
                return UpdateCheckFailure.MalformedResponse;
        }

        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is AuthenticationException)
            {
                return UpdateCheckFailure.SecureChannelFailed;
            }
        }

        return UpdateCheckFailure.NetworkUnreachable;
    }

    public async Task<string?> GetAssetTextAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!IsAllowedAssetUrl(url))
        {
            FileLogger.Warn($"Refusing to fetch update asset text from an unexpected origin: {url}");
            return null;
        }

        try
        {
            using var request = CreateRequest(url, acceptGitHubJson: false);

            // Headers first: a body past the bound must be refused before it is read,
            // not after it has already been buffered.
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                FileLogger.Warn($"Fetching update asset text failed: HTTP {(int)response.StatusCode} for {url}.");
                return null;
            }

            return await ReadBoundedTextAsync(response.Content, url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            FileLogger.Warn($"Fetching update asset text error for {url}: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout (not caller cancellation) surfaces as a canceled task.
            FileLogger.Warn($"Fetching update asset text timed out for {url}.");
            return null;
        }
    }

    /// <summary>
    /// Reads a response body as text, refusing anything past <see cref="MaxAssetTextBytes"/>.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing, because every other failure on this path
    /// already degrades to null and the caller treats a missing checksum list as a
    /// reason to stop, not as an error to report.
    /// </remarks>
    private static async Task<string?> ReadBoundedTextAsync(
        HttpContent content,
        string url,
        CancellationToken cancellationToken)
    {
        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength > MaxAssetTextBytes)
        {
            FileLogger.Warn(
                $"Refusing update asset text for {url}: declared {declaredLength} bytes, over the {MaxAssetTextBytes} byte bound.");
            return null;
        }

        using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];

        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            // Past the bound the body is refused, not truncated: half a checksum list
            // is worse than none, since it would fail verification for the wrong reason.
            if (buffer.Length + read > MaxAssetTextBytes)
            {
                FileLogger.Warn(
                    $"Refusing update asset text for {url}: body exceeds the {MaxAssetTextBytes} byte bound.");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public async Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!IsAllowedAssetUrl(url))
        {
            throw new InvalidOperationException($"Refusing to download an update asset from an unexpected origin: {url}");
        }

        using var request = CreateRequest(url, acceptGitHubJson: false);
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // The response is not returned, so nobody else can release its connection.
            response.Dispose();
            throw;
        }

        // The caller owns the returned stream; disposing it releases the
        // underlying connection, and the response wrapper is collected with it.
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether an asset URL names an origin releases are actually served from: an
    /// absolute https URL on github.com, githubusercontent.com or a subdomain of either.
    /// </summary>
    internal static bool IsAllowedAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        foreach (string suffix in AllowedAssetHostSuffixes)
        {
            if (string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private HttpRequestMessage CreateRequest(string url, bool acceptGitHubJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"{UserAgentProduct}/{_appVersion}");
        if (acceptGitHubJson)
        {
            request.Headers.Accept.ParseAdd(GitHubAcceptHeader);
        }

        return request;
    }

    private sealed record ReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] IReadOnlyList<AssetDto>? Assets);

    private sealed record AssetDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}
