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

using System.Globalization;
using System.Text;

namespace Heimdall.Core.Recipes;

/// <summary>Character-level helpers shared by the validator, the value rule and the error data.</summary>
public static class RecipeText
{
    private const string EllipsisText = "...";

    private const char PrintableAsciiStart = ' ';

    private const char PrintableAsciiEnd = '~';

    /// <summary>
    /// True when the text holds a character no recipe text may carry: a control character (Cc), a
    /// format character (Cf, which includes the right-to-left override), a line or paragraph
    /// separator (Zl, Zp) or an unpaired surrogate.
    /// </summary>
    public static bool ContainsRefusedCharacter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FindRefusedCharacter(text) >= 0;
    }

    /// <summary>
    /// Makes a text safe to carry inside an error: every refused character (see
    /// <see cref="ContainsRefusedCharacter"/>) becomes a <c>\uXXXX</c> escape, and the result is
    /// cut to <see cref="RecipeLimits.MaxFragmentLength"/> characters with an ASCII ellipsis.
    /// </summary>
    public static string MakeSafe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder builder = new(Math.Min(text.Length, RecipeLimits.MaxFragmentLength) + EllipsisText.Length);
        for (int index = 0; index < text.Length; index++)
        {
            if (builder.Length >= RecipeLimits.MaxFragmentLength)
            {
                builder.Append(EllipsisText);
                break;
            }

            char c = text[index];
            if (IsRefused(text, index))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Makes a text safe to show whole, as plain text: every refused character (see
    /// <see cref="ContainsRefusedCharacter"/>) becomes a <c>\uXXXX</c> escape and nothing is cut.
    /// With <paramref name="escapeNonAscii"/> every character outside printable ASCII is escaped
    /// too, so that a look-alike letter in a program or an argument cannot pass for the ASCII one.
    /// </summary>
    /// <param name="text">The text from a recipe.</param>
    /// <param name="escapeNonAscii">Whether characters outside printable ASCII are escaped as well.</param>
    public static string Escape(string? text, bool escapeNonAscii)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder builder = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            bool outsideAscii = c is < PrintableAsciiStart or > PrintableAsciiEnd;
            if (IsRefused(text, index) || (escapeNonAscii && outsideAscii))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The index of the first refused character, or -1.</summary>
    internal static int FindRefusedCharacter(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (IsRefused(text, index))
                return index;
        }

        return -1;
    }

    private static bool IsRefused(string text, int index)
    {
        UnicodeCategory category = char.GetUnicodeCategory(text, index);
        return category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            || (category == UnicodeCategory.Surrogate && !IsPairedSurrogate(text, index));
    }

    private static bool IsPairedSurrogate(string text, int index)
    {
        char c = text[index];
        return char.IsHighSurrogate(c)
            ? index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            : index > 0 && char.IsHighSurrogate(text[index - 1]);
    }

    /// <summary>
    /// Finds which import refusal a literal text triggers, if any. A URL is reported as a URL only,
    /// not also as a double slash.
    /// </summary>
    internal static IEnumerable<RecipeErrorCode> FindForbiddenFragments(string text)
    {
        if (text.Contains(@"\\", StringComparison.Ordinal))
            yield return RecipeErrorCode.ForbiddenBackslashPair;

        if (text.Contains("://", StringComparison.Ordinal))
            yield return RecipeErrorCode.ForbiddenUrl;
        else if (text.Contains("//", StringComparison.Ordinal))
            yield return RecipeErrorCode.ForbiddenDoubleSlash;

        if (text.Contains('%', StringComparison.Ordinal))
            yield return RecipeErrorCode.ForbiddenPercent;

        if (text.StartsWith('@'))
            yield return RecipeErrorCode.ForbiddenLeadingAt;
    }
}
