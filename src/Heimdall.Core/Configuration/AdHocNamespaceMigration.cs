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

using Heimdall.Core.Certificates;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Moves any saved profile out of the quick-connect identifier namespace, taking the certificate
/// approvals filed under its old identifier with it.
/// </summary>
/// <remarks>
/// <para><b>Why reserving the namespace at the import was not enough.</b> The reservation stops a
/// NEW profile entering; it says nothing about the profiles already in <c>servers.json</c>, which
/// are loaded with the identifiers they were saved with. A profile imported before the reservation
/// existed keeps its identifier across the upgrade, so it still shares both the palette's
/// classification and the trust store's key with a destination typed by hand. Announcing the
/// reservation as complete while this was open was the error; this closes it.</para>
/// <para><b>And the approvals move with the profile, because renaming alone would leave them
/// behind.</b> Durable approvals are persisted separately, keyed by the identifier that was in use
/// when they were granted. Renaming the profile and stopping there leaves its approval sitting
/// under an identifier the palette now mints for a typed destination - which is the whole defect,
/// with the profile no longer even visible as its source.</para>
/// <para><b>What it deliberately does NOT touch: a reserved key with no profile behind it.</b>
/// That is a typed destination's own approval, granted by someone who typed that host and said
/// yes, and removing it would re-ask about a machine they accepted. A key that belonged to a
/// profile since deleted is indistinguishable from it, and is left alone for the same reason -
/// bounded, because after this runs no profile can enter the namespace again, so no new key of
/// that shape can be a profile's.</para>
/// <para>Pure, so what it decides is measured by running it rather than by reading the startup
/// path it is called from.</para>
/// </remarks>
public static class AdHocNamespaceMigration
{
    /// <summary>The identifiers to be replaced, old to new, empty when there is nothing to do.</summary>
    /// <param name="servers">The inventory as loaded.</param>
    /// <param name="mintIdentifier">
    /// Supplies a fresh identifier. Injected so a test asserts the mapping rather than asserting
    /// that some GUID or other appeared.
    /// </param>
    public static IReadOnlyDictionary<string, string> Plan(
        IEnumerable<ServerProfileDto> servers,
        Func<string> mintIdentifier)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(mintIdentifier);

        Dictionary<string, string> renames = new(StringComparer.Ordinal);
        foreach (ServerProfileDto server in servers)
        {
            if (!AdHocProfileIds.IsAdHoc(server.Id) || renames.ContainsKey(server.Id))
            {
                continue;
            }

            renames[server.Id] = mintIdentifier();
        }

        return renames;
    }

    /// <summary>Applies <paramref name="renames"/> to the inventory and to the durable approvals.</summary>
    /// <returns>Whether anything was changed.</returns>
    public static bool Apply(
        IReadOnlyDictionary<string, string> renames,
        IEnumerable<ServerProfileDto> servers,
        IDictionary<string, List<RdpCertificateEntry>> trustedCertificates)
    {
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(trustedCertificates);

        if (renames.Count == 0)
        {
            return false;
        }

        bool changed = false;

        foreach (ServerProfileDto server in servers)
        {
            if (server.Id is not null && renames.TryGetValue(server.Id, out string? replacement))
            {
                server.Id = replacement;
                changed = true;
            }
        }

        foreach ((string oldId, string newId) in renames)
        {
            if (!trustedCertificates.TryGetValue(oldId, out List<RdpCertificateEntry>? entries))
            {
                continue;
            }

            // Removed from the old key in every case. Leaving it would hand the profile's
            // approval to whatever destination the palette mints that identifier for, which is
            // the defect this exists to close.
            trustedCertificates.Remove(oldId);

            if (entries is null || entries.Count == 0)
            {
                changed = true;
                continue;
            }

            // Merged rather than overwritten: the new identifier is freshly minted and holds
            // nothing, but a caller that reuses a plan must not silently drop approvals.
            if (trustedCertificates.TryGetValue(newId, out List<RdpCertificateEntry>? existing)
                && existing is not null)
            {
                existing.AddRange(entries);
            }
            else
            {
                trustedCertificates[newId] = entries;
            }

            changed = true;
        }

        return changed;
    }
}
