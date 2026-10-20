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

using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Redaction;

/// <summary>A half-open character range of the input replaced by <see cref="Replacement"/>.</summary>
internal readonly record struct TextEdit(int Start, int End, string Replacement);

/// <summary>A logical line: <see cref="Start"/> inclusive, <see cref="End"/> exclusive, excluding <c>\r</c> and <c>\n</c>.</summary>
internal readonly record struct TextLine(int Start, int End)
{
    internal int Length => End - Start;
}

internal static class RedactionApply
{
    /// <summary>Name of the capture group holding the span to mask, shared by every pattern.</summary>
    internal const string ValueGroupName = "v";

    /// <summary>Target chunk size for single-line rules; a chunk always ends on a line boundary.</summary>
    internal const int ChunkChars = 64 * 1024;

    /// <summary>
    /// Runs <paramref name="regex"/> over <paramref name="text"/> chunk by chunk (each chunk ends at a
    /// <c>\n</c>, so no match crosses a chunk) and replaces each match by the evaluator's result.
    /// An evaluator returning null keeps the match unchanged.
    /// </summary>
    internal static string Replace(string text, Regex regex, Func<Match, string?> evaluator)
    {
        StringBuilder? builder = null;
        int copied = 0;
        int chunkStart = 0;
        while (chunkStart < text.Length)
        {
            int chunkEnd = NextChunkEnd(text, chunkStart);
            for (Match match = regex.Match(text, chunkStart, chunkEnd - chunkStart); match.Success; match = match.NextMatch())
            {
                string? replacement = evaluator(match);
                if (replacement is null)
                {
                    continue;
                }

                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, match.Index - copied).Append(replacement);
                copied = match.Index + match.Length;
            }

            chunkStart = chunkEnd;
        }

        if (builder is null)
        {
            return text;
        }

        return builder.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Masks the <see cref="ValueGroupName"/> group of a match, keeping the rest of the match. Returns null
    /// (keep, do not count) when the group is empty or already a mask token.
    /// </summary>
    internal static string? MaskGroup(Match match, string ruleId, string token, RedactionRun run)
    {
        Group group = match.Groups[ValueGroupName];
        if (!group.Success || group.Length == 0 || RedactionMask.IsMaskToken(group.ValueSpan))
        {
            return null;
        }

        run.Count(ruleId);
        int relative = group.Index - match.Index;
        ReadOnlySpan<char> whole = match.ValueSpan;
        return string.Concat(whole[..relative], token, whole[(relative + group.Length)..]);
    }

    /// <summary>Builds the rule applier masking the value group of each pattern in turn.</summary>
    internal static Func<string, RedactionRun, string> GroupMasker(string ruleId, params Regex[] patterns)
    {
        string token = RedactionMask.Token(ruleId);
        return (text, run) =>
        {
            string current = text;
            foreach (Regex pattern in patterns)
            {
                current = Replace(current, pattern, match => MaskGroup(match, ruleId, token, run));
            }

            return current;
        };
    }

    internal static List<TextLine> SplitLines(string text)
    {
        var lines = new List<TextLine>();
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            int contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
            if (newline < 0 && start == text.Length)
            {
                break;
            }

            lines.Add(new TextLine(start, contentEnd));
            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return lines;
    }

    /// <summary>Applies non-overlapping edits given in ascending order.</summary>
    internal static string ApplyEdits(string text, List<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        int copied = 0;
        foreach (TextEdit edit in edits)
        {
            builder.Append(text, copied, edit.Start - copied).Append(edit.Replacement);
            copied = edit.End;
        }

        return builder.Append(text, copied, text.Length - copied).ToString();
    }

    private static int NextChunkEnd(string text, int start)
    {
        int target = start + ChunkChars;
        if (target >= text.Length)
        {
            return text.Length;
        }

        int newline = text.IndexOf('\n', target);
        return newline < 0 ? text.Length : newline + 1;
    }
}
