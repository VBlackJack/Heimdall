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
/// The release was republished between the check and the install, so the checksum this
/// install was authorised against is no longer the one the maintainer publishes.
/// </summary>
/// <remarks>
/// <para>
/// This is a refusal, not a recovery. The obvious repair - adopt the newly published
/// checksum and carry on - would mean the application accepts whatever the source serves
/// at install time, which is precisely the property the frozen checksum exists to deny.
/// Anyone able to replace the asset could also replace the checksum document beside it, so
/// re-resolving as a remedy would verify nothing. Re-resolving as a DIAGNOSIS is worth
/// doing: it separates "the maintainer replaced this release" from "these bytes are not
/// the bytes that were published", which read identically once the download has failed.
/// </para>
/// <para>
/// The user is told to check again. The next check resolves the new release honestly, from
/// its own tag and its own checksum, which is the only path that keeps the guarantee.
/// </para>
/// </remarks>
public sealed class UpdateSupersededException : InvalidOperationException
{
    /// <param name="tagName">The tag the superseded check was made against.</param>
    /// <param name="authorisedSha256">The checksum the check froze.</param>
    /// <param name="publishedSha256">The checksum published now, for the same asset name.</param>
    public UpdateSupersededException(
        string tagName,
        string? authorisedSha256,
        string publishedSha256)
        : base(
            $"Release '{tagName}' was republished since it was checked: the published SHA-256 "
            + $"is now '{publishedSha256}', not '{authorisedSha256}'. Refusing to install.")
    {
        TagName = tagName;
        AuthorisedSha256 = authorisedSha256;
        PublishedSha256 = publishedSha256;
    }

    /// <summary>The tag the superseded check was made against.</summary>
    public string TagName { get; }

    /// <summary>The checksum the check froze and this install was authorised against.</summary>
    public string? AuthorisedSha256 { get; }

    /// <summary>The checksum the source publishes now for the same asset name.</summary>
    public string PublishedSha256 { get; }
}
