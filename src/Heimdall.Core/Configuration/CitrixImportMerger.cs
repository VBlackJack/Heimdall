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

using Heimdall.Core.Logging;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Summary of an idempotent Citrix cache import merge operation.
/// </summary>
/// <param name="Added">Number of new Citrix profiles appended to the inventory.</param>
/// <param name="Updated">Number of existing Citrix profiles whose store/app/server metadata changed.</param>
/// <param name="Unchanged">Number of existing Citrix profiles whose launch token was refreshed without metadata changes.</param>
public readonly record struct CitrixImportMergeResult(int Added, int Updated, int Unchanged);

/// <summary>
/// Merges Citrix profiles discovered from the local SelfService cache into an existing
/// server profile inventory without duplicating previously imported resources.
/// </summary>
public static class CitrixImportMerger
{
    private const string CitrixConnectionType = "Citrix";

    /// <summary>
    /// Merges <paramref name="incoming"/> Citrix profiles into <paramref name="existing"/> keyed on <see cref="ServerProfileDto.CitrixId"/>.
    /// </summary>
    /// <param name="existing">Mutable server inventory to update in place.</param>
    /// <param name="incoming">Newly scanned Citrix profiles (with launch command lines already protected for the active vault state).</param>
    /// <returns>Counts of added, updated, and unchanged profiles.</returns>
    public static CitrixImportMergeResult Merge(
        List<ServerProfileDto> existing,
        IReadOnlyList<ServerProfileDto> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        Dictionary<string, ServerProfileDto> existingByCitrixId = new(StringComparer.Ordinal);
        foreach (ServerProfileDto profile in existing)
        {
            if (IsCitrixProfile(profile) && !string.IsNullOrWhiteSpace(profile.CitrixId))
            {
                existingByCitrixId.TryAdd(profile.CitrixId, profile);
            }
        }

        HashSet<string> seenIncomingIds = new(StringComparer.Ordinal);
        int added = 0;
        int updated = 0;
        int unchanged = 0;
        int duplicateIncomingCount = 0;

        foreach (ServerProfileDto candidate in incoming)
        {
            if (string.IsNullOrWhiteSpace(candidate.CitrixId))
            {
                existing.Add(candidate);
                added++;
                continue;
            }

            if (!seenIncomingIds.Add(candidate.CitrixId))
            {
                duplicateIncomingCount++;
                continue;
            }

            if (existingByCitrixId.TryGetValue(candidate.CitrixId, out ServerProfileDto? target))
            {
                bool metadataChanged =
                    !string.Equals(target.CitrixStoreFrontUrl, candidate.CitrixStoreFrontUrl, StringComparison.Ordinal) ||
                    !string.Equals(target.CitrixAppName, candidate.CitrixAppName, StringComparison.Ordinal) ||
                    !string.Equals(target.RemoteServer, candidate.RemoteServer, StringComparison.Ordinal);

                target.CitrixLaunchCommandLine = candidate.CitrixLaunchCommandLine;
                target.CitrixStoreFrontUrl = candidate.CitrixStoreFrontUrl;
                target.CitrixAppName = candidate.CitrixAppName;
                target.RemoteServer = candidate.RemoteServer;

                if (metadataChanged)
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                existing.Add(candidate);
                existingByCitrixId[candidate.CitrixId] = candidate;
                added++;
            }
        }

        if (duplicateIncomingCount > 0)
        {
            FileLogger.Warn(
                $"[CitrixImportMerger] Ignored {duplicateIncomingCount} duplicate Citrix resource(s) with identical CitrixId during import.");
        }

        return new CitrixImportMergeResult(added, updated, unchanged);
    }

    private static bool IsCitrixProfile(ServerProfileDto profile) =>
        string.Equals(profile.ConnectionType, CitrixConnectionType, StringComparison.OrdinalIgnoreCase);
}
