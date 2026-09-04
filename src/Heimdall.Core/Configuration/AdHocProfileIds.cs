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

namespace Heimdall.Core.Configuration;

/// <summary>
/// The identifier namespace the command palette mints for a destination typed by hand, which no
/// inventory profile may occupy.
/// </summary>
/// <remarks>
/// <para><b>Reserved because the application already behaves as though it were.</b> The palette
/// decides whether an entry is a saved profile or a typed destination by testing this prefix, in
/// six places, and it keys a certificate approval on the same identifier. Nothing enforced the
/// reservation: an import preserves whatever identifier its file carried, so a profile could
/// arrive holding <c>adhoc-rdp-prod.example</c> and share both the palette's classification and
/// the trust store's key with a quick connect to <c>prod.example</c>. Approving a certificate for
/// the imported profile then let the typed destination connect on it without a question.</para>
/// <para><b>Why reserving a prefix is sound here, when recovering a role from one was not.</b>
/// Three earlier attempts tried to work out, from an identifier's text, which profile a session
/// belonged to; all failed, because the same string can legitimately be two things and no
/// examination of it separates them. This is the opposite operation: the palette OWNS this
/// namespace and creates every identifier in it, so the question is not "what is this string" but
/// "may a foreign identifier enter this namespace" - and that is enforced at the one door foreign
/// identifiers come through, <c>ProfileImportService.BuildUniqueId</c>, which already remints an
/// identifier that collides with an existing profile.</para>
/// <para>Shared rather than spelled out again at each site, because a reservation that one reader
/// spells differently from another is not a reservation.</para>
/// </remarks>
public static class AdHocProfileIds
{
    /// <summary>The prefix every palette-minted destination identifier carries.</summary>
    public const string Prefix = "adhoc-";

    /// <summary>
    /// Whether <paramref name="profileId"/> belongs to the palette's destination namespace.
    /// </summary>
    /// <remarks>
    /// Ordinal, because that is what the trust store and the palette both compare with. A
    /// case-insensitive reservation would be STRICTER, not looser - it would remint
    /// <c>ADHOC-rdp-x</c> as well - and it would be reserving a string the palette never mints
    /// and the store keys distinctly, so nothing could collide with it. Matching the consumers
    /// is the property that matters; reserving more than they can produce is only noise.
    /// </remarks>
    public static bool IsAdHoc(string? profileId) =>
        profileId is not null && profileId.StartsWith(Prefix, StringComparison.Ordinal);
}
