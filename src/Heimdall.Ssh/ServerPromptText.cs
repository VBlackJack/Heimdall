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

namespace Heimdall.Ssh;

/// <summary>
/// Makes server-controlled text safe to show inside a Heimdall dialog.
/// </summary>
/// <remarks>
/// <para>A keyboard-interactive prompt is written by the server, and the dialog that shows it
/// carries Heimdall's title and chrome. Left verbatim, a server could send line breaks to open a
/// paragraph of its own, or bidirectional overrides to reorder what is read, and pose as Heimdall
/// asking for the vault master password.</para>
/// <para>Control characters (category Cc) and line or paragraph separators (Zl, Zp) become one
/// space, so words stay apart but no new line can start. Format characters (Cf: bidi overrides,
/// zero-width characters, the byte order mark) and lone surrogates are dropped. Runs of
/// whitespace collapse to one space. The result is capped at <see cref="MaxLength"/>.</para>
/// </remarks>
public static class ServerPromptText
{
    /// <summary>
    /// Longest server text kept, in UTF-16 units. A real prompt ("Verification code:",
    /// "One-time password (OATH) for `user':") is a few dozen; the bound leaves room for a
    /// verbose PAM module while keeping a server from filling the dialog.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>Appended when the text was cut at <see cref="MaxLength"/>.</summary>
    public const string TruncationMarker = "...";

    /// <summary>
    /// Returns <paramref name="text"/> with every character that could change its layout
    /// removed or neutralised, trimmed and capped.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(Math.Min(text.Length, MaxLength + TruncationMarker.Length));
        bool pendingSpace = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                UnicodeCategory pairCategory = CharUnicodeInfo.GetUnicodeCategory(text, index);
                index++;
                if (pairCategory == UnicodeCategory.Format)
                {
                    continue;
                }

                AppendPendingSpace(builder, ref pendingSpace);
                builder.Append(current).Append(text[index]);
                continue;
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(current);
            switch (category)
            {
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                    continue;
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    pendingSpace = builder.Length > 0;
                    continue;
                default:
                    AppendPendingSpace(builder, ref pendingSpace);
                    builder.Append(current);
                    break;
            }
        }

        return Cap(builder.ToString());
    }

    private static void AppendPendingSpace(StringBuilder builder, ref bool pendingSpace)
    {
        if (pendingSpace)
        {
            builder.Append(' ');
            pendingSpace = false;
        }
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        int cut = MaxLength;
        if (char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), TruncationMarker);
    }
}
