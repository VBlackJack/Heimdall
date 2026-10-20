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
/// Masks what a user typed at a password prompt and the terminal echoed. Three cases: the rest of the
/// prompt line when it is not empty; the next line when the prompt line is empty and that line is a
/// single token that does not look like a shell prompt or a message; and the token of a
/// "command not found" line shortly after a prompt (the password typed into a shell).
/// </summary>
internal static class PasswordPromptMasker
{
    // How many lines after a prompt (or after "Login incorrect") a "command not found" is still attributed to it.
    private const int CommandNotFoundWindow = 3;

    private const string LoginIncorrect = "Login incorrect";

    private const string PromptCharacters = ":$#>";

    private const string PromptSuffixCharacters = "$#>%";

    private const char AtSign = '@';

    // An explicitly empty value (YAML or env style) holds no secret.
    private const string EmptyDoubleQuotes = "\"\"";

    private const string EmptySingleQuotes = "''";

    // Cheap pre-filter: a buffer with none of these cannot contain a prompt or a login failure.
    private static readonly string[] Triggers = ["pass", "contrase", LoginIncorrect];

    internal static string Apply(string text, Regex prompt, Regex commandNotFound, string ruleId, RedactionRun run)
    {
        if (!MayContainPrompt(text))
        {
            return text;
        }

        string token = RedactionMask.Token(ruleId);
        List<TextLine> lines = RedactionApply.SplitLines(text);
        var edits = new List<TextEdit>();
        int lastTrigger = int.MinValue;
        int pendingNext = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            TextLine line = lines[i];
            Match promptMatch = prompt.Match(text, line.Start, line.Length);
            if (promptMatch.Success)
            {
                lastTrigger = i;
                int restStart = SkipBlanks(text, promptMatch.Index + promptMatch.Length, line.End);
                if (restStart >= line.End)
                {
                    pendingNext = i + 1;
                    continue;
                }

                ReadOnlySpan<char> rest = text.AsSpan(restStart, line.End - restStart);
                if (rest.SequenceEqual(EmptyDoubleQuotes) || rest.SequenceEqual(EmptySingleQuotes))
                {
                    continue;
                }

                if (!RedactionMask.IsMaskToken(rest))
                {
                    edits.Add(new TextEdit(restStart, line.End, token));

                    // A rest that already holds a token was counted by the rule that masked that part.
                    if (!rest.Contains(RedactionMask.Prefix, StringComparison.Ordinal))
                    {
                        run.Count(ruleId);
                    }
                }

                continue;
            }

            if (text.AsSpan(line.Start, line.Length).Contains(LoginIncorrect, StringComparison.OrdinalIgnoreCase))
            {
                lastTrigger = i;
            }

            if (i == pendingNext && TryFindBareToken(text, line, out int tokenStart, out int tokenEnd))
            {
                edits.Add(new TextEdit(tokenStart, tokenEnd, token));
                run.Count(ruleId);
                continue;
            }

            if (i > lastTrigger && i - lastTrigger <= CommandNotFoundWindow)
            {
                Match notFound = commandNotFound.Match(text, line.Start, line.Length);
                if (notFound.Success)
                {
                    Group value = notFound.Groups[RedactionApply.ValueGroupName];
                    if (!RedactionMask.IsMaskToken(value.ValueSpan))
                    {
                        edits.Add(new TextEdit(value.Index, value.Index + value.Length, token));
                        run.Count(ruleId);
                    }
                }
            }
        }

        return RedactionApply.ApplyEdits(text, edits);
    }

    private static bool MayContainPrompt(string text)
    {
        foreach (string trigger in Triggers)
        {
            if (text.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int SkipBlanks(string text, int start, int end)
    {
        int position = start;
        while (position < end && (text[position] == ' ' || text[position] == '\t'))
        {
            position++;
        }

        return position;
    }

    // The known messages ("Last login", "Sorry, try again.", "Permission denied", "Login incorrect",
    // "Access denied") all contain a space, so the single-token test already excludes them; what
    // remains to exclude is a bare shell prompt such as user@host:~$ or root#.
    private static bool TryFindBareToken(string text, TextLine line, out int start, out int end)
    {
        start = SkipBlanks(text, line.Start, line.End);
        end = line.End;
        while (end > start && (text[end - 1] == ' ' || text[end - 1] == '\t'))
        {
            end--;
        }

        if (end <= start)
        {
            return false;
        }

        ReadOnlySpan<char> token = text.AsSpan(start, end - start);
        foreach (char c in token)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return !RedactionMask.StartsWithMask(token) && !LooksLikeShellPrompt(token);
    }

    private static bool LooksLikeShellPrompt(ReadOnlySpan<char> token)
    {
        if (PromptSuffixCharacters.Contains(token[^1]))
        {
            return true;
        }

        int at = token.IndexOf(AtSign);
        return at >= 0 && token[(at + 1)..].IndexOfAny(PromptCharacters) >= 0;
    }
}
