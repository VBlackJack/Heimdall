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

namespace Heimdall.Core.Utilities;

/// <summary>
/// Makes a value written by someone else safe to show inside a confirmation: nothing in it can
/// start a new line, reorder what is read or hide, and it cannot grow without bound.
/// </summary>
public static class UntrustedText
{
    /// <summary>Appended when a value was cut at the maximum length.</summary>
    public const string TruncationMarker = "...";

    /// <summary>
    /// Control characters (Cc), format characters (Cf: bidi overrides, zero-width characters) and
    /// line or paragraph separators (Zl, Zp) are replaced by a visible <c>\u{XXXX}</c> escape. The
    /// result is at most <paramref name="maxLength"/> characters before the truncation marker.
    /// </summary>
    public static string MakeVisible(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            string piece = category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                ? string.Create(CultureInfo.InvariantCulture, $"\\u{{{rune.Value:x4}}}")
                : rune.ToString();

            if (builder.Length + piece.Length > maxLength)
            {
                return builder.Append(TruncationMarker).ToString();
            }

            builder.Append(piece);
        }

        return builder.ToString();
    }
}
