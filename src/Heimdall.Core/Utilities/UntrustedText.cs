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
using System.Globalization;
using System.Text;

namespace Heimdall.Core.Utilities;

/// <summary>
/// Makes a value written by someone else safe to show inside a confirmation: nothing in it can
/// start a new line, reorder what is read, hide or pass for blank space, and it cannot grow
/// without bound.
/// </summary>
public static class UntrustedText
{
    /// <summary>The length to pass when a value must be written out whole, never cut.</summary>
    public const int NoLengthLimit = int.MaxValue;

    /// <summary>Appended when a value was cut at the maximum length.</summary>
    public const string TruncationMarker = "...";

    /// <summary>The longest run of spaces that is written as it is; a longer run is written out escaped.</summary>
    public const int MaxPlainSpaceRun = 16;

    /// <summary>The longest run of one escaped code point that is written out; a longer one is written once with a count.</summary>
    public const int MaxRepeatedEscapesWrittenOut = 3;

    /// <summary>What an escape starts with, before the hexadecimal value.</summary>
    private const string EscapePrefix = "\\u{";

    /// <summary>What an escape ends with.</summary>
    private const char EscapeSuffix = '}';

    /// <summary>The format of the hexadecimal value of an escape: at least four lowercase digits.</summary>
    private const string EscapeHexFormat = "x4";

    /// <summary>Separates the code point of an escape from the number of times it repeats: <c>\u{200b*4000}</c>.</summary>
    private const char RepeatSeparator = '*';

    private const int NoPendingEscape = -1;

    private const char SpaceCharacter = ' ';

    private const char Backslash = '\\';

    private const char DoubleQuote = '"';

    /// <summary>The ASCII characters a variation selector may follow: the bases of an emoji keycap sequence.</summary>
    private const string KeycapBases = "0123456789#*";

    private const int FirstVariationSelector = 0xFE00;

    private const int LastVariationSelector = 0xFE0F;

    private const int FirstSupplementaryVariationSelector = 0xE0100;

    private const int LastSupplementaryVariationSelector = 0xE01EF;

    /// <summary>
    /// The spaces of French and Japanese typography: no-break space, narrow no-break space and the
    /// ideographic space. One of them standing alone is kept; in a run of spaces it is escaped.
    /// </summary>
    private static readonly FrozenSet<int> TypographicSpaces =
        new[] { 0x00A0, 0x202F, 0x3000 }.ToFrozenSet();

    /// <summary>
    /// Characters that draw nothing yet are letters or marks, so no category test finds them:
    /// combining grapheme joiner, Hangul fillers, Khmer inherent vowels, Mongolian variation
    /// selectors, the braille blank, the Khitan small script filler, the musical null notehead.
    /// All are written as escapes.
    /// </summary>
    private static readonly FrozenSet<int> InvisibleCodePoints = new[]
    {
        0x034F, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180C, 0x180D, 0x180F, 0x2800, 0x3164, 0xFFA0, 0x16FE4, 0x1D159,
    }.ToFrozenSet();

    /// <summary>
    /// Control characters (Cc), format characters (Cf: bidi overrides, zero-width characters, tag
    /// characters), line and paragraph separators (Zl, Zp), unassigned code points (Cn), lone
    /// surrogates and the invisible letters of <see cref="InvisibleCodePoints"/> are replaced by a
    /// visible <c>\u{XXXX}</c> escape. Spaces stay readable: a run of up to
    /// <see cref="MaxPlainSpaceRun"/> ordinary spaces is kept, and so is a typographic space
    /// (no-break, narrow no-break, ideographic) standing alone; every other space separator, a
    /// typographic space inside a run and a longer run are escaped. A variation selector is kept
    /// only behind a base it can modify (a non-ASCII character that is not a combining mark, a
    /// keycap base for a basic selector, an ideograph for a supplementary one); anywhere else it is
    /// escaped. A run of one escaped code point longer than <see cref="MaxRepeatedEscapesWrittenOut"/>
    /// is written once with its count inside the braces (<c>\u{0020*300}</c>). The result is at
    /// most <paramref name="maxLength"/> characters before the truncation marker; an escape, a
    /// surrogate pair or a counted run is never cut.
    /// </summary>
    public static string MakeVisible(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(Math.Max(0, Math.Min(text.Length, maxLength)));
        VisibleWriter writer = new(builder, maxLength);
        int index = 0;
        while (index < text.Length)
        {
            if (!TryWriteNext(text, ref index, ref writer))
            {
                return builder.Append(TruncationMarker).ToString();
            }
        }

        return writer.Finish() ? builder.ToString() : builder.Append(TruncationMarker).ToString();
    }

    /// <summary>
    /// Escapes a value for a place between double quotes, following the Windows command-line
    /// rule: a run of backslashes is doubled when it stands directly before a double quote or at
    /// the end of the value, and every double quote is then written as <c>\"</c>. A backslash
    /// anywhere else is left alone, so <c>C:\Temp</c> stays as it is. The quote is escaped here,
    /// so a caller must not escape it again.
    /// </summary>
    /// <param name="text">The value to escape.</param>
    /// <param name="closedByQuote">
    /// Whether a quote follows the value. False for a value shown bare: nothing can then be read
    /// as escaped by a trailing backslash, so the end-of-value rule is skipped.
    /// </param>
    public static string EscapeForDoubleQuotes(string? text, bool closedByQuote = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        bool endsWithBackslash = closedByQuote && text[^1] == Backslash;
        if (!text.Contains(DoubleQuote, StringComparison.Ordinal) && !endsWithBackslash)
        {
            return text;
        }

        StringBuilder builder = new(text.Length);
        int backslashes = 0;
        foreach (char character in text)
        {
            if (character == Backslash)
            {
                backslashes++;
                continue;
            }

            if (character == DoubleQuote)
            {
                // The run is doubled, then one more backslash escapes the quote itself.
                builder.Append(Backslash, (backslashes * 2) + 1);
            }
            else
            {
                builder.Append(Backslash, backslashes);
            }

            backslashes = 0;
            builder.Append(character);
        }

        // A run at the end would otherwise let the closing quote of the caller be read as escaped.
        builder.Append(Backslash, closedByQuote ? backslashes * 2 : backslashes);
        return builder.ToString();
    }

    private static bool TryWriteNext(string text, ref int index, ref VisibleWriter writer)
    {
        int start = index;
        Rune rune;
        int consumed;
        if (char.IsAscii(text[start]))
        {
            rune = new Rune(text[start]);
            consumed = 1;
        }
        else if (Rune.DecodeFromUtf16(text.AsSpan(start), out rune, out consumed) != System.Buffers.OperationStatus.Done)
        {
            // A lone surrogate is written with its own value; decoding would turn it into U+FFFD.
            index = start + 1;
            return writer.WriteEscape(text[start]);
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category == UnicodeCategory.SpaceSeparator)
        {
            return WriteSpaceRun(text, ref index, ref writer);
        }

        index = start + consumed;
        if (ShouldEscape(rune, category))
        {
            return writer.WriteEscape(rune.Value);
        }

        if (IsVariationSelector(rune.Value))
        {
            // Kept only behind a base it modifies (an emoji presentation, an ideographic variation
            // sequence); anywhere else it is a character that draws nothing.
            return CanFollow(writer.Preceding, rune.Value)
                ? writer.WritePlain(text, start, consumed, BaseKind.None)
                : writer.WriteEscape(rune.Value);
        }

        return writer.WritePlain(text, start, consumed, BaseKindOf(rune, category));
    }

    /// <summary>What a character written as it is offers a variation selector that follows it.</summary>
    private enum BaseKind
    {
        /// <summary>Nothing: a space, an escape, a combining mark, a selector, an ordinary ASCII character.</summary>
        None,

        /// <summary>A character a basic selector may follow (emoji, symbol, keycap base).</summary>
        Basic,

        /// <summary>An other letter (ideograph): a supplementary selector may follow as well.</summary>
        OtherLetter,
    }

    private static BaseKind BaseKindOf(Rune rune, UnicodeCategory category)
    {
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark)
        {
            return BaseKind.None;
        }

        if (category == UnicodeCategory.OtherLetter)
        {
            return BaseKind.OtherLetter;
        }

        bool eligible = !rune.IsAscii || KeycapBases.Contains((char)rune.Value, StringComparison.Ordinal);
        return eligible ? BaseKind.Basic : BaseKind.None;
    }

    private static bool CanFollow(BaseKind preceding, int selector)
        => selector >= FirstSupplementaryVariationSelector
            ? preceding == BaseKind.OtherLetter
            : preceding != BaseKind.None;

    private static bool WriteSpaceRun(string text, ref int index, ref VisibleWriter writer)
    {
        int end = index;
        while (end < text.Length && char.GetUnicodeCategory(text[end]) == UnicodeCategory.SpaceSeparator)
        {
            end++;
        }

        int length = end - index;
        for (; index < end; index++)
        {
            char space = text[index];
            bool escape = length > MaxPlainSpaceRun
                || (space != SpaceCharacter && (length > 1 || !TypographicSpaces.Contains(space)));
            bool written = escape
                ? writer.WriteEscape(space)
                : writer.WritePlain(text, index, 1, BaseKind.None);
            if (!written)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldEscape(Rune rune, UnicodeCategory category)
        => category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.OtherNotAssigned
            || (!rune.IsAscii && InvisibleCodePoints.Contains(rune.Value));

    private static bool IsVariationSelector(int codePoint)
        => codePoint is (>= FirstVariationSelector and <= LastVariationSelector)
            or (>= FirstSupplementaryVariationSelector and <= LastSupplementaryVariationSelector);

    /// <summary>
    /// Appends pieces whole, merges a run of one escaped code point into a pending escape and
    /// refuses a piece that would pass the maximum length.
    /// </summary>
    private ref struct VisibleWriter
    {
        private readonly StringBuilder _builder;
        private readonly int _maxLength;
        private int _pendingCodePoint;
        private int _pendingCount;

        public VisibleWriter(StringBuilder builder, int maxLength)
        {
            _builder = builder;
            _maxLength = maxLength;
            _pendingCodePoint = NoPendingEscape;
            _pendingCount = 0;
            Preceding = BaseKind.None;
        }

        /// <summary>What the last piece offers a variation selector that follows it.</summary>
        public BaseKind Preceding { get; private set; }

        public bool WritePlain(string text, int start, int length, BaseKind kind)
        {
            if (!FlushPending() || !Fits(length))
            {
                return false;
            }

            _builder.Append(text, start, length);
            Preceding = kind;
            return true;
        }

        public bool WriteEscape(int codePoint)
        {
            Preceding = BaseKind.None;
            if (_pendingCount > 0 && _pendingCodePoint == codePoint)
            {
                _pendingCount++;
                return true;
            }

            if (!FlushPending())
            {
                return false;
            }

            _pendingCodePoint = codePoint;
            _pendingCount = 1;
            return true;
        }

        public bool Finish() => FlushPending();

        private bool FlushPending()
        {
            if (_pendingCount == 0)
            {
                return true;
            }

            int count = _pendingCount;
            _pendingCount = 0;
            string hex = _pendingCodePoint.ToString(EscapeHexFormat, CultureInfo.InvariantCulture);
            if (count <= MaxRepeatedEscapesWrittenOut)
            {
                string escape = EscapePrefix + hex + EscapeSuffix;
                for (int written = 0; written < count; written++)
                {
                    if (!Fits(escape.Length))
                    {
                        return false;
                    }

                    _builder.Append(escape);
                }

                return true;
            }

            // The count sits inside the braces, so literal digits after the escape cannot extend it.
            string counted = EscapePrefix + hex + RepeatSeparator + count.ToString(CultureInfo.InvariantCulture) + EscapeSuffix;
            if (!Fits(counted.Length))
            {
                return false;
            }

            _builder.Append(counted);
            return true;
        }

        private readonly bool Fits(int length) => length <= _maxLength - _builder.Length;
    }
}
