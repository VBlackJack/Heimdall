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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Redaction;

/// <summary>
/// Masks PEM private key blocks with a single linear pass over the lines, so a buffer holding many
/// BEGIN lines and no END cannot cost more than one scan. Three shapes are handled: a full block, an
/// END line whose BEGIN was evicted (the text starts mid-key), and a BEGIN without END (the key is cut).
/// </summary>
internal static class PrivateKeyBlockMasker
{
    private const string MarkerLiteral = "-----";

    private const string EndKind = "END";

    private const string KindGroupName = "k";

    private const string ProcTypeHeader = "Proc-Type:";

    private const string DekInfoHeader = "DEK-Info:";

    // PEM bodies wrap at 64 or 76 columns; the final line is at least one base64 quantum.
    private const int MinBodyLineLength = 4;

    private const int MaxBodyLineLength = 1024;

    private enum LineKind
    {
        Other,
        Base64,
        Header,
        Blank,
    }

    internal static string Apply(string text, Regex marker, string ruleId, RedactionRun run)
    {
        if (!text.Contains(MarkerLiteral, StringComparison.Ordinal))
        {
            return text;
        }

        string token = RedactionMask.Token(ruleId);
        List<TextLine> lines = RedactionApply.SplitLines(text);
        var edits = new List<TextEdit>();
        int floorLine = 0;
        int index = 0;
        int position = 0;

        while (index < lines.Count)
        {
            TextLine line = lines[index];
            position = Math.Max(position, line.Start);
            Match match = marker.Match(text, position, line.End - position);
            if (!match.Success)
            {
                index++;
                continue;
            }

            int matchEnd = match.Index + match.Length;
            if (IsEnd(match))
            {
                int first = index;
                while (first - 1 >= floorLine && IsBodyLine(text, lines[first - 1], allowBlank: false, afterHeader: false))
                {
                    first--;
                }

                int start = first < index ? lines[first].Start : match.Index;
                Emit(edits, start, matchEnd, token, ruleId, run);
                floorLine = index;
                position = matchEnd;
                continue;
            }

            Match next = match.NextMatch();
            if (next.Success && IsEnd(next))
            {
                int sameLineEnd = next.Index + next.Length;
                Emit(edits, match.Index, sameLineEnd, token, ruleId, run);
                position = sameLineEnd;
                continue;
            }

            int blockEnd = line.End;
            bool closed = false;
            bool afterHeader = false;
            int scan = index + 1;
            for (; scan < lines.Count; scan++)
            {
                TextLine candidate = lines[scan];
                LineKind kind = Classify(text, candidate);
                if (IsBodyKind(kind, allowBlank: true, afterHeader))
                {
                    afterHeader = kind == LineKind.Header;
                    blockEnd = candidate.End;
                    continue;
                }

                Match endMatch = marker.Match(text, candidate.Start, candidate.Length);
                if (endMatch.Success && IsEnd(endMatch))
                {
                    blockEnd = endMatch.Index + endMatch.Length;
                    closed = true;
                }

                break;
            }

            Emit(edits, match.Index, blockEnd, token, ruleId, run);
            floorLine = scan;
            index = scan;
            position = closed ? blockEnd : 0;
        }

        return RedactionApply.ApplyEdits(text, edits);
    }

    private static void Emit(List<TextEdit> edits, int start, int end, string token, string ruleId, RedactionRun run)
    {
        edits.Add(new TextEdit(start, end, token));
        run.Count(ruleId);
    }

    private static bool IsEnd(Match match) =>
        string.Equals(match.Groups[KindGroupName].Value, EndKind, StringComparison.Ordinal);

    private static bool IsBodyLine(string text, TextLine line, bool allowBlank, bool afterHeader) =>
        IsBodyKind(Classify(text, line), allowBlank, afterHeader);

    private static bool IsBodyKind(LineKind kind, bool allowBlank, bool afterHeader) => kind switch
    {
        LineKind.Base64 or LineKind.Header => true,
        LineKind.Blank => allowBlank && afterHeader,
        _ => false,
    };

    private static LineKind Classify(string text, TextLine line)
    {
        ReadOnlySpan<char> content = text.AsSpan(line.Start, line.Length).Trim();
        if (content.IsEmpty)
        {
            return LineKind.Blank;
        }

        if (content.StartsWith(ProcTypeHeader, StringComparison.Ordinal) || content.StartsWith(DekInfoHeader, StringComparison.Ordinal))
        {
            return LineKind.Header;
        }

        if (content.Length < MinBodyLineLength || content.Length > MaxBodyLineLength)
        {
            return LineKind.Other;
        }

        foreach (char c in content)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '/' && c != '=')
            {
                return LineKind.Other;
            }
        }

        return LineKind.Base64;
    }
}
