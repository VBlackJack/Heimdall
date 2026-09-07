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

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
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
                FileLogger.Warn($"GitHub release check failed: HTTP {(int)response.StatusCode} for {owner}/{repo}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (dto?.TagName is null)
            {
                FileLogger.Warn($"GitHub release response for {owner}/{repo} contained no tag_name.");
                return null;
            }

            var assets = (dto.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.DownloadUrl))
                .Select(a => new UpdateAsset(a.Name!, a.DownloadUrl!, a.Size))
                .ToList();

            return new GitHubRelease(dto.TagName, dto.HtmlUrl ?? string.Empty, dto.Body ?? string.Empty, assets);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            FileLogger.Warn($"GitHub release check error for {owner}/{repo}: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout (not caller cancellation) surfaces as a canceled task.
            FileLogger.Warn($"GitHub release check timed out for {owner}/{repo}.");
            return null;
        }
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
