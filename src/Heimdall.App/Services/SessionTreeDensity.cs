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

using System.Collections.Frozen;
using System.Windows;

namespace Heimdall.App.Services;

/// <summary>
/// Which spacing resources the session tree reads, decided apart from the window so the decision
/// can be tested without one.
/// </summary>
/// <remarks>
/// The row templates and the container style read each key in <see cref="RowSpacingKeys"/> with
/// <c>DynamicResource</c>; the theme defines it with today's value and a second key, the same name
/// plus <see cref="CompactSuffix"/>, with the compact one. Turning the preference on puts the
/// compact value over the default key in the tree's own resources; turning it off removes it, so
/// the theme's value shows through again.
/// </remarks>
internal static class SessionTreeDensity
{
    /// <summary>Appended to a default key to name its compact counterpart.</summary>
    public const string CompactSuffix = "Compact";

    /// <summary>Every spacing key the tree's templates read, in the theme's order.</summary>
    public static readonly IReadOnlyList<string> RowSpacingKeys =
    [
        "SessionTreeFolderRowMargin",
        "SessionTreeFolderRowPadding",
        "SessionTreeFolderRowMinHeight",
        "SessionTreeServerRowMargin",
        "SessionTreeServerRowPadding",
        "SessionTreeServerRowMinHeight",
        "SessionTreeItemPadding",
        "SessionTreeChildrenHostMargin",
        "SessionTreeItemsHostMargin",
    ];

    /// <summary>The key that holds the compact value of <paramref name="defaultKey"/>.</summary>
    public static string CompactKeyFor(string defaultKey) => defaultKey + CompactSuffix;

    /// <summary>
    /// The keys the tree overrides, each mapped to the key its value comes from. Empty when the
    /// tree keeps the theme's spacing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Overrides(bool compact)
    {
        if (!compact)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        return RowSpacingKeys.ToFrozenDictionary(key => key, CompactKeyFor, StringComparer.Ordinal);
    }

    /// <summary>
    /// Puts the spacing the preference asks for into <paramref name="treeResources"/>.
    /// <paramref name="lookup"/> finds a value by key, as the theme does.
    /// </summary>
    public static void Apply(ResourceDictionary treeResources, Func<string, object?> lookup, bool compact)
    {
        ArgumentNullException.ThrowIfNull(treeResources);
        ArgumentNullException.ThrowIfNull(lookup);

        IReadOnlyDictionary<string, string> wanted = Overrides(compact);
        foreach (string key in RowSpacingKeys)
        {
            if (wanted.ContainsKey(key))
            {
                continue;
            }

            if (treeResources.Contains(key))
            {
                treeResources.Remove(key);
            }
        }

        foreach ((string key, string compactKey) in wanted)
        {
            object value = lookup(compactKey)
                ?? throw new InvalidOperationException($"The theme does not define '{compactKey}'.");
            if (treeResources.Contains(key) && Equals(treeResources[key], value))
            {
                continue;
            }

            treeResources[key] = value;
        }
    }
}
