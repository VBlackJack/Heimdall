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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Configuration;

/// <summary>
/// The identifier namespace of shared session sources: every profile and gateway read from a
/// shared MobaXterm file carries an identifier in it, and nothing local may.
/// </summary>
/// <remarks>
/// <para>Owned by the shared session provider, which mints every identifier in it as
/// <c>shared-&lt;source id&gt;-&lt;hash&gt;</c>, so two sources never collide and a lookup can tell
/// a shared profile from a local one by its identifier alone. Reserving the namespace is sound for
/// the reason <see cref="AdHocProfileIds"/> gives: the question is never "what is this string" but
/// "may a foreign identifier enter this namespace", and that is refused where identifiers arrive -
/// the configuration manager's write doors and the import paths that remint.</para>
/// <para>Ordinal, like the other reservation and like every reader: the lookups, the trust store
/// and the approvals all compare identifiers ordinally.</para>
/// </remarks>
public static partial class SharedSessionIds
{
    /// <summary>The prefix every shared profile and gateway identifier carries.</summary>
    public const string Prefix = "shared-";

    private const char SourceSeparator = '-';

    /// <summary>
    /// The prefix of the identifiers minted for the source <paramref name="sourceId"/>.
    /// </summary>
    public static string PrefixFor(string sourceId)
    {
        if (!IsValidSourceId(sourceId))
        {
            throw new ArgumentException($"'{sourceId}' is not a valid shared source identifier.", nameof(sourceId));
        }

        return Prefix + sourceId + SourceSeparator;
    }

    /// <summary>Whether <paramref name="id"/> belongs to the shared namespace.</summary>
    public static bool IsShared(string? id) =>
        id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="profile"/> comes from a shared source. Both identifiers are read: a
    /// connection replaces <see cref="ServerProfileDto.Id"/> with a session key derived from it.
    /// </summary>
    public static bool IsSharedProfile(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return IsShared(profile.Id) || IsShared(profile.InventoryProfileId);
    }

    /// <summary>
    /// Whether <paramref name="sourceId"/> can identify a source: lowercase letters and digits
    /// only, so the separator that follows it can never appear inside it.
    /// </summary>
    public static bool IsValidSourceId(string? sourceId) =>
        sourceId is not null && SourceIdRegex().IsMatch(sourceId);

    /// <summary>A new source identifier.</summary>
    public static string NewSourceId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The warning a door logs when it turns a foreign identifier out of this namespace.
    /// </summary>
    public static string DescribeRemint(string? candidateId) =>
        $"An imported entry carried the identifier '{candidateId}', which belongs to the shared "
        + "session namespace; a fresh identifier was assigned so it cannot stand in for a shared session.";

    [GeneratedRegex("^[a-z0-9]{1,64}$")]
    private static partial Regex SourceIdRegex();
}
