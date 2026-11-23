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

using System.Collections.Concurrent;

namespace Heimdall.Core.Recipes;

/// <summary>
/// What the machine does to a character when a program reads its command line in the ANSI or OEM
/// code page: the seam that lets a test describe a machine without depending on the real one.
/// </summary>
public interface IRecipeCharacterConverter
{
    /// <summary>
    /// True when the character, once converted to the code page of the machine, becomes one
    /// ASCII character other than <c>?</c> (the best-fit mapping of Windows: for example a
    /// full-width hyphen turning into <c>-</c>). A character the code page cannot represent at all
    /// becomes <c>?</c> and gives false, and so does a character that converts to itself.
    /// </summary>
    /// <param name="character">One character, as one or two UTF-16 code units, outside ASCII.</param>
    bool BecomesAscii(string character);
}

/// <summary>The real machine: it asks Windows, with best-fit mapping left on, as a program would be served.</summary>
public sealed class RecipeCharacterConverter : IRecipeCharacterConverter
{
    private readonly ConcurrentDictionary<string, bool> _answers = new(StringComparer.Ordinal);

    /// <summary>The shared instance.</summary>
    public static RecipeCharacterConverter Instance { get; } = new();

    /// <inheritdoc />
    public bool BecomesAscii(string character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return _answers.GetOrAdd(
            character,
            static text => RecipeProgramNative.BecomesAscii(text, RecipeProgramNative.AnsiCodePage)
                || RecipeProgramNative.BecomesAscii(text, RecipeProgramNative.OemCodePageId));
    }
}
