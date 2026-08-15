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

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// The outcome of toggling one tool's favourite status.
/// </summary>
/// <param name="Favorites">The complete set the toggle produces. Nothing is mutated in place.</param>
/// <param name="NormalizedId">The identifier as it is stored and as it is announced to subscribers.</param>
/// <param name="Added">Whether the tool became a favourite, as opposed to stopping being one.</param>
public readonly record struct FavoriteToolToggle(
    IReadOnlyList<string> Favorites,
    string NormalizedId,
    bool Added);

/// <summary>
/// Adds or removes a tool from the favourites, without touching the caller's list.
/// </summary>
/// <remarks>
/// Returning the new set rather than editing the old one is what lets the caller persist before
/// publishing. Every other surface reads the favourites out of the in-memory settings, so a list
/// edited up front would describe a change that had not reached disk yet - and would keep
/// describing it if the write failed.
/// </remarks>
public static class FavoriteToolSet
{
    /// <summary>
    /// Returns the favourites with <paramref name="toolId"/> added if absent, or removed if
    /// present.
    /// </summary>
    /// <remarks>
    /// Membership is case-insensitive, matching how <c>ToolRegistry</c> identifies a tool, and the
    /// stored form is upper-cased. Removal takes every spelling, so a set that already held two
    /// casings of one tool comes back holding neither.
    /// </remarks>
    public static FavoriteToolToggle Toggle(IEnumerable<string> favorites, string toolId)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(toolId);

        string normalizedId = toolId.ToUpperInvariant();
        List<string> updated = [];
        bool removed = false;

        foreach (string favorite in favorites)
        {
            if (string.Equals(favorite, normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }

            updated.Add(favorite);
        }

        if (!removed)
        {
            updated.Add(normalizedId);
        }

        return new FavoriteToolToggle(updated, normalizedId, Added: !removed);
    }
}
