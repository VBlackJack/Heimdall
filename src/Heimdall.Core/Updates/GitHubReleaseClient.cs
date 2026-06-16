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
    private const string GitHubAcceptHeader = "application/vnd.github+json";
    private const string UserAgentProduct = "Heimdall";

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

        try
        {
            using var request = CreateRequest(url, acceptGitHubJson: false);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                FileLogger.Warn($"Fetching update asset text failed: HTTP {(int)response.StatusCode} for {url}.");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        using var request = CreateRequest(url, acceptGitHubJson: false);
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The caller owns the returned stream; disposing it releases the
        // underlying connection, and the response wrapper is collected with it.
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
