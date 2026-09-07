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
/// Describes a single available update: its version, the originating release,
/// the asset to download, and the expected SHA-256 of that asset.
/// </summary>
public sealed record UpdateInfo(
    HeimdallVersion Version,
    string TagName,
    string ReleaseUrl,
    string ReleaseNotes,
    UpdateAsset Asset,
    string? Sha256)
{
    /// <summary>
    /// Where <see cref="Sha256"/> was read from, so it can be read again at install time.
    /// </summary>
    /// <remarks>
    /// Carried rather than re-derived because the install has no repository coordinates:
    /// it is handed an <see cref="UpdateInfo"/> and nothing else. Optional, and an install
    /// proceeds without it - the frozen checksum still governs what may be installed. Its
    /// only job is to tell a republished release apart from a corrupted download.
    /// </remarks>
    public string? ChecksumUrl { get; init; }
}
