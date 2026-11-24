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
/// The closed set of icon keys a recipe may name. A recipe never carries an icon of its own; the
/// application maps each key to a built-in glyph. A later change extends <see cref="Keys"/>
/// (and bumps the schema version, since it widens the accepted format).
/// </summary>
public static class RecipeIcons
{
    /// <summary>The key shown when a recipe names none.</summary>
    public const string Default = "tool";

    /// <summary>Every accepted key.</summary>
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
}
