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

namespace Heimdall.Core.Updates;

/// <summary>
/// Thin HTTP wrapper around the GitHub releases API. It is the single owner of
/// network access for the updater; it performs no version or asset selection.
/// </summary>
public interface IGitHubReleaseClient
{
    /// <summary>
    /// Fetches the latest release of a repository, or null on any failure.
    /// </summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a small text asset (such as SHA256SUMS.txt), or null on failure.
    /// </summary>
    Task<string?> GetAssetTextAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the response stream of a binary asset for streamed download.
    /// Throws on a non-success status; the caller is responsible for disposal.
    /// </summary>
    Task<Stream> OpenAssetStreamAsync(string url, CancellationToken cancellationToken);
}
