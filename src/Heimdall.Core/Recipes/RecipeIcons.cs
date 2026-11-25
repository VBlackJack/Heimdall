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

namespace Heimdall.Core.Recipes;

/// <summary>
/// The icon keys of a recipe. A recipe never carries an icon of its own; the application maps a key
/// to a built-in glyph. Any key of the right shape (see <see cref="IsKeyShape"/>) is accepted by the
/// format, so a later version can add glyphs without changing what a file means: a key this version
/// does not know shows <see cref="Default"/>.
/// </summary>
public static class RecipeIcons
{
    /// <summary>The key shown when a recipe names none, or names one this version does not know.</summary>
    public const string Default = "tool";

    /// <summary>The keys this version has a glyph for.</summary>
    public static readonly FrozenSet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        Default,
        "terminal",
        "network",
        "search",
        "document",
        "shield",
        "clock",
        "server",
    }.ToFrozenSet(StringComparer.Ordinal);

    private const char Hyphen = '-';

    /// <summary>
    /// True when the key has the shape of an icon key: a lowercase letter, then lowercase letters,
    /// digits and hyphens up to <see cref="RecipeLimits.MaxIconKeyLength"/> characters in all, not
    /// ending with a hyphen and without two hyphens in a row.
    /// </summary>
    /// <param name="key">The key.</param>
    public static bool IsKeyShape(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > RecipeLimits.MaxIconKeyLength)
            return false;

        if (key[0] is not (>= 'a' and <= 'z') || key[^1] == Hyphen)
            return false;

        for (int index = 1; index < key.Length; index++)
        {
            char c = key[index];
            bool allowed = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or Hyphen;
            if (!allowed || (c == Hyphen && key[index - 1] == Hyphen))
                return false;
        }

        return true;
    }

    /// <summary>True when this version has a glyph for the key.</summary>
    /// <param name="key">The key.</param>
    public static bool IsKnown(string? key) => key is not null && Keys.Contains(key);
}
