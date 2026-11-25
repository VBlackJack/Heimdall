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

namespace Heimdall.Core.Recipes;

/// <summary>
/// The one strict form of the numbers of the format, for typed values, defaults, fixed values and
/// bounds: an <c>int</c> is <c>-?(0|[1-9][0-9]*)</c> within 32 bits, a <c>number</c> is
/// <c>-?(0|[1-9][0-9]*)(\.[0-9]+)?</c> and finite. No leading plus, no leading zero, no exponent, no
/// bare dot, no white space, no digit other than <c>0</c> to <c>9</c>.
/// </summary>
internal static class RecipeNumbers
{
    private const char Minus = '-';

    private const char Dot = '.';

    /// <summary>True when the text has the strict integer form and fits in 32 bits.</summary>
    public static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        return text is not null
            && text.Length <= RecipeLimits.MaxNumericValueLength
            && ScanInteger(text, 0, out int end)
            && end == text.Length
            && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>True when the text has the strict decimal form and is a finite double.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (text is null || text.Length > RecipeLimits.MaxNumericValueLength || !ScanInteger(text, 0, out int end))
            return false;

        if (end < text.Length)
        {
            if (text[end] != Dot || !ScanDigits(text, end + 1, out end) || end != text.Length)
                return false;
        }

        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>Reads <c>-?(0|[1-9][0-9]*)</c> from <paramref name="start"/>; <paramref name="end"/> is the index after it.</summary>
    private static bool ScanInteger(string text, int start, out int end)
    {
        int index = start;
        if (index < text.Length && text[index] == Minus)
            index++;

        end = index;
        if (index >= text.Length || !IsDigit(text[index]))
            return false;

        if (text[index] == '0')
        {
            end = index + 1;
            return true;
        }

        while (index < text.Length && IsDigit(text[index]))
            index++;

        end = index;
        return true;
    }

    /// <summary>Reads one or more digits from <paramref name="start"/>.</summary>
    private static bool ScanDigits(string text, int start, out int end)
    {
        int index = start;
        while (index < text.Length && IsDigit(text[index]))
            index++;

        end = index;
        return index > start;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
