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
/// Orchestrates update checks and verified downloads by composing the GitHub
/// client and the variant detector. It performs asset selection, checksum
/// resolution, and version comparison; it owns no HTTP client of its own.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Checks the latest release of a repository against the current version,
    /// selecting the installer that matches the running build variant.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        HeimdallVersion current,
        string owner,
        string repo,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the update's installer to a restrictive staging directory and
    /// verifies its SHA-256. Refuses (throws) when no published checksum is
    /// available or the digest does not match. Returns a lease that keeps the
    /// verified installer deny-write locked until the relauncher has started.
    /// </summary>
    Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
