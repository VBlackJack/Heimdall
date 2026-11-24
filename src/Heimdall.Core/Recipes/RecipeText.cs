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

    private const int NtMarkerLength = 2;

    private const int NonAsciiStart = 0x80;

    private const int PlainSpace = 0x20;

    private const int YenSign = 0xA5;

    private const int WonSign = 0x20A9;

    private const int MinusSign = 0x2212;

    private const int DivisionSlash = 0x2215;

    private const int FractionSlash = 0x2044;

    private const int FullWidthStart = 0xFF00;

    private const int FullWidthEnd = 0xFFEF;

    private const int DashStart = 0x2010;

    private const int DashEnd = 0x2015;

    private const int SmallFormStart = 0xFE50;

    private const int SmallFormEnd = 0xFE6F;

    private const int QuoteStart = 0x2018;

    private const int QuoteEnd = 0x201F;

    private const int PrimeStart = 0x2032;

    private const int PrimeEnd = 0x2036;

    private const int ModifierStart = 0x02B9;

    private const int ModifierEnd = 0x02DC;

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

    /// <summary>True for the two path separators, which Windows treats as one and the same.</summary>
    internal static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// True when two characters in a row are both separators, in any mix (a doubled backslash, a
    /// doubled slash, or one of each in either order): the start of a network path or a device path.
    /// </summary>
    internal static bool HasAdjacentSeparators(string text) => FindAdjacentSeparators(text, mixedOnly: false);

    /// <summary>True when a separator pair mixes the two forms (backslash then slash, or slash then backslash).</summary>
    internal static bool HasMixedSeparators(string text) => FindAdjacentSeparators(text, mixedOnly: true);

    /// <summary>
    /// True when the text holds a separator, <c>??</c> and a separator: the NT object prefix, in any
    /// mix of separators.
    /// </summary>
    internal static bool HasNtPrefix(string text)
    {
        for (int index = 0; index + NtMarkerLength + 1 < text.Length; index++)
        {
            if (IsSeparator(text[index])
                && text[index + 1] == '?'
                && text[index + 2] == '?'
                && IsSeparator(text[index + NtMarkerLength + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a colon is followed by two separators, in any mix: the scheme marker of a URL as
    /// Windows would read it.
    /// </summary>
    internal static bool HasUrlMarker(string text)
    {
        for (int index = 0; index + 2 < text.Length; index++)
        {
            if (text[index] == ':' && IsSeparator(text[index + 1]) && IsSeparator(text[index + 2]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a final argument could be read as a network path, an NT or device path or a URL:
    /// two separators in a row, a separator then <c>??</c> then a separator, or a colon then two separators.
    /// </summary>
    internal static bool ContainsNetworkPathShape(string text)
        => HasAdjacentSeparators(text) || HasNtPrefix(text) || HasUrlMarker(text);

    private static bool FindAdjacentSeparators(string text, bool mixedOnly)
    {
        for (int index = 0; index + 1 < text.Length; index++)
        {
            char first = text[index];
            char second = text[index + 1];
            if (IsSeparator(first) && IsSeparator(second) && (!mixedOnly || first != second))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the text holds a character that a program reading its command line in the ANSI
    /// code page would be handed as a different, ASCII one (a full-width hyphen that becomes a
    /// hyphen, a full-width quote that becomes a double quote, and so on). See <see cref="FindLookAlike"/>.
    /// </summary>
    /// <param name="text">The text to examine.</param>
    /// <param name="converter">What the machine does to a character; the real machine when null.</param>
    public static bool ContainsLookAlikeCharacter(string text, IRecipeCharacterConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FindLookAlike(text, converter) >= 0;
    }

    /// <summary>
    /// The index of the first look-alike character, or -1. A character is a look-alike when it lies in
    /// a block that Windows folds onto ASCII (the full-width and half-width forms, the small form
    /// variants, the dashes, the minus sign, the division and fraction slashes, the typographic quotes
    /// and primes, the modifier letters, the yen and won signs, every space separator but the plain
    /// space), or when <paramref name="converter"/> says that the code page of the machine turns it
    /// into one ASCII character. A character the code page cannot represent at all becomes a question
    /// mark, which is harmless, and is accepted; so is an ordinary accented letter that the code page
    /// keeps as itself.
    /// </summary>
    /// <param name="text">The text to examine.</param>
    /// <param name="converter">What the machine does to a character; the real machine when null.</param>
    internal static int FindLookAlike(string text, IRecipeCharacterConverter? converter)
    {
        IRecipeCharacterConverter machine = converter ?? RecipeCharacterConverter.Instance;
        int index = 0;
        while (index < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed);
            if (rune.Value >= NonAsciiStart && (IsFoldedBlock(rune) || machine.BecomesAscii(rune.ToString())))
                return index;

            index += Math.Max(consumed, 1);
        }

        return -1;
    }

    private static bool IsFoldedBlock(Rune rune)
    {
        int value = rune.Value;
        return value is YenSign or WonSign or MinusSign or DivisionSlash or FractionSlash
            || (value >= FullWidthStart && value <= FullWidthEnd)
            || (value >= DashStart && value <= DashEnd)
            || (value >= SmallFormStart && value <= SmallFormEnd)
            || (value >= QuoteStart && value <= QuoteEnd)
            || (value >= PrimeStart && value <= PrimeEnd)
            || (value >= ModifierStart && value <= ModifierEnd)
            || (Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator && value != PlainSpace);
    }

    /// <summary>
    /// Finds which import refusal a literal text triggers, if any. A URL is reported as a URL only,
    /// not also as a double slash, and a mixed pair only when nothing more precise was reported.
    /// </summary>
    internal static IEnumerable<RecipeErrorCode> FindForbiddenFragments(string text)
    {
        bool backslashPair = text.Contains(@"\\", StringComparison.Ordinal);
        if (backslashPair)
            yield return RecipeErrorCode.ForbiddenBackslashPair;

        bool plainUrl = text.Contains("://", StringComparison.Ordinal);
        bool mixedUrl = !plainUrl && !backslashPair && HasUrlMarker(text);
        bool doubleSlash = !plainUrl && text.Contains("//", StringComparison.Ordinal);
        if (plainUrl || mixedUrl)
            yield return RecipeErrorCode.ForbiddenUrl;
        else if (doubleSlash)
            yield return RecipeErrorCode.ForbiddenDoubleSlash;

        if (!backslashPair && !plainUrl && !mixedUrl && !doubleSlash && HasMixedSeparators(text))
            yield return RecipeErrorCode.ForbiddenMixedSeparators;

        if (HasNtPrefix(text))
            yield return RecipeErrorCode.ForbiddenNtPathPrefix;

        if (text.Contains('%', StringComparison.Ordinal))
            yield return RecipeErrorCode.ForbiddenPercent;

        if (text.StartsWith('@'))
            yield return RecipeErrorCode.ForbiddenLeadingAt;
    }
}
