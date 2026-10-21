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
using Heimdall.Core.Security;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Shared, pure predicates describing where a parameter placeholder sits inside a
/// command pattern. Centralized here so persistence-time seeding and import-time
/// validation apply the exact same quoting rule.
/// </summary>
public static class CommandPatternQuoting
{
    /// <summary>
    /// What the generator recognizes as a placeholder it might substitute: a brace group
    /// holding an identifier, not preceded by a dollar sign.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrow, because braces are ordinary shell punctuation and a rule that
    /// claimed every brace group was a placeholder would be wrong about most real command
    /// patterns. Excluded by the identifier shape alone: <c>awk '{print $1}'</c> (a space),
    /// <c>jq '{a:1}'</c> (a colon), <c>{a,b}</c> (a comma), <c>{1..10}</c> and <c>a{2}</c>
    /// (no leading letter), and <c>find -exec {} \;</c> (empty). Excluded by the lookbehind:
    /// <c>${VAR}</c>, which has exactly the shape of a placeholder and is the one case the
    /// identifier rule alone would get wrong.
    /// </para>
    /// <para>
    /// This still describes a guess about intent, not a fact about the command. It says
    /// what a reader would most likely have meant, which is why what reads it warns rather
    /// than refuses.
    /// </para>
    /// </remarks>
    private static readonly Regex PlaceholderPattern = new(
        @"(?<!\$)\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The text the generator looks for when substituting <paramref name="parameterName"/>.
    /// </summary>
    /// <remarks>
    /// One builder, because three places used to spell this out independently: the
    /// generator's substitution, the quoting predicate below, and the consistency check.
    /// They have to agree by construction, not by everybody remembering the same braces.
    /// </remarks>
    public static string Placeholder(string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        return "{" + parameterName + "}";
    }

    /// <summary>
    /// Placeholder names written in <paramref name="commandPattern"/> that no parameter
    /// declares, in the order they appear and without repeats.
    /// </summary>
    /// <remarks>
    /// The generator only substitutes parameters it was given, so one of these is left in
    /// the command exactly as written: the operator sees the braces in what they are about
    /// to run.
    /// </remarks>
    public static IReadOnlyList<string> FindUndeclaredPlaceholders(
        string? commandPattern, IEnumerable<string> declaredNames)
    {
        ArgumentNullException.ThrowIfNull(declaredNames);

        if (string.IsNullOrEmpty(commandPattern))
        {
            return [];
        }

        var declared = new HashSet<string>(declaredNames, StringComparer.Ordinal);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in PlaceholderPattern.Matches(commandPattern))
        {
            var name = match.Groups["name"].Value;
            if (!declared.Contains(name) && seen.Add(name))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>
    /// Declared parameter names that appear nowhere in <paramref name="commandPattern"/>.
    /// </summary>
    /// <remarks>
    /// Unlike an undeclared placeholder this is not a guess: the generator replaces an
    /// exact string, so a name the pattern does not contain is a box the operator fills to
    /// no effect. A blank or whitespace name is reported too, since it cannot match either.
    /// </remarks>
    public static IReadOnlyList<string> FindUnusedParameters(
        string? commandPattern, IEnumerable<string> declaredNames)
    {
        ArgumentNullException.ThrowIfNull(declaredNames);

        var unused = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in declaredNames)
        {
            if (name is null || !seen.Add(name))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrEmpty(commandPattern)
                || !commandPattern.Contains(Placeholder(name), StringComparison.Ordinal))
            {
                unused.Add(name);
            }
        }

        return unused;
    }

    /// <summary>
    /// True when <paramref name="parameterName"/> appears in <paramref name="commandPattern"/>
    /// and every occurrence sits in a literal single-quoted span: the precondition for
    /// <c>QuotingMode.InlineInQuotes</c>, which only escapes the value and relies on the
    /// pattern's own quotes.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="GetPlaceholderQuoteContexts"/>, the lexer the generator uses,
    /// rather than a count of the single quotes before the first occurrence: the count
    /// called <c>-Filter "Name -like '*{x}*'"</c> single-quoted although the shell expands
    /// <c>$(...)</c> there, and never looked at a second occurrence.
    /// </remarks>
    public static bool IsPlaceholderOnlyInSingleQuotedSpans(string? commandPattern, string parameterName, bool powerShell)
    {
        ArgumentNullException.ThrowIfNull(parameterName);

        var contexts = GetPlaceholderQuoteContexts(commandPattern, parameterName, powerShell);
        return contexts.Count > 0 && contexts.All(context => context == QuoteContext.SingleQuoted);
    }

    /// <summary>
    /// How the shell reads the text at one occurrence of a placeholder.
    /// </summary>
    public enum QuoteContext
    {
        /// <summary>Outside any quoted span.</summary>
        Bare,

        /// <summary>Inside a single-quoted span, where the shell takes every character literally.</summary>
        SingleQuoted,

        /// <summary>
        /// Inside a span the shell still interprets: a double-quoted string, where
        /// <c>$(...)</c> and backticks expand even between inner single quotes, or a bash
        /// <c>$'...'</c> string, which reads backslash escapes.
        /// </summary>
        Expanding,
    }

    /// <summary>
    /// The quoting context of every occurrence of the <paramref name="parameterName"/>
    /// placeholder in <paramref name="commandPattern"/>, in order. Empty when the
    /// placeholder does not appear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A small lexer rather than a quote count, because the count is wrong exactly where it
    /// matters: in <c>-Filter "Name -like '*{x}*'"</c> the placeholder follows one single
    /// quote, yet the shell reads it inside a double-quoted string and expands
    /// <c>$(...)</c> there. Every occurrence is reported because the generator replaces
    /// every occurrence.
    /// </para>
    /// <para>
    /// <paramref name="powerShell"/> selects the rules: PowerShell escapes with a backtick
    /// and also closes strings on typographic quotes; bash escapes with a backslash outside
    /// single quotes and has <c>$'...'</c>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<QuoteContext> GetPlaceholderQuoteContexts(
        string? commandPattern, string parameterName, bool powerShell)
    {
        ArgumentNullException.ThrowIfNull(parameterName);

        if (string.IsNullOrEmpty(commandPattern) || parameterName.Length == 0)
        {
            return [];
        }

        string placeholder = Placeholder(parameterName);
        char escape = powerShell ? '`' : '\\';
        var contexts = new List<QuoteContext>();
        var state = LexerState.Bare;
        int index = 0;

        // Where the last unescaped dollar sign outside quotes stood: a placeholder right
        // after it is ${name}, which both bash and PowerShell read as a variable, so the
        // value is not literal there whatever quoting it gets.
        int bareDollarIndex = -1;

        while (index < commandPattern.Length)
        {
            if (string.CompareOrdinal(commandPattern, index, placeholder, 0, placeholder.Length) == 0)
            {
                contexts.Add(state switch
                {
                    LexerState.Bare when bareDollarIndex == index - 1 => QuoteContext.Expanding,
                    LexerState.Bare => QuoteContext.Bare,
                    LexerState.Single => QuoteContext.SingleQuoted,
                    _ => QuoteContext.Expanding,
                });
                index += placeholder.Length;
                continue;
            }

            char c = commandPattern[index];
            switch (state)
            {
                case LexerState.Bare:
                    if (c == escape)
                    {
                        index += 2;
                        continue;
                    }

                    if (c == '$')
                    {
                        bareDollarIndex = index;
                    }

                    if (!powerShell && c == '$' && index + 1 < commandPattern.Length && commandPattern[index + 1] == '\'')
                    {
                        state = LexerState.AnsiC;
                        index += 2;
                        continue;
                    }

                    if (IsSingleQuote(c, powerShell))
                    {
                        state = LexerState.Single;
                    }
                    else if (IsDoubleQuote(c, powerShell))
                    {
                        state = LexerState.Double;
                    }

                    break;

                case LexerState.Single:
                    if (IsSingleQuote(c, powerShell))
                    {
                        state = LexerState.Bare;
                    }

                    break;

                case LexerState.Double:
                    if (c == escape)
                    {
                        index += 2;
                        continue;
                    }

                    if (IsDoubleQuote(c, powerShell))
                    {
                        state = LexerState.Bare;
                    }

                    break;

                case LexerState.AnsiC:
                    if (c == '\\')
                    {
                        index += 2;
                        continue;
                    }

                    if (c == '\'')
                    {
                        state = LexerState.Bare;
                    }

                    break;
            }

            index++;
        }

        return contexts;
    }

    private enum LexerState
    {
        Bare,
        Single,
        Double,
        AnsiC,
    }

    private static bool IsSingleQuote(char c, bool powerShell) =>
        powerShell ? PowerShellSingleQuotedString.IsSingleQuote(c) : c == '\'';

    private static bool IsDoubleQuote(char c, bool powerShell) =>
        c == '"' || (powerShell && c is LeftDoubleQuotationMark or RightDoubleQuotationMark or DoubleLow9QuotationMark);

    // PowerShell closes a double-quoted string on these as well as on the ASCII quote.
    private const char LeftDoubleQuotationMark = (char)0x201C;
    private const char RightDoubleQuotationMark = (char)0x201D;
    private const char DoubleLow9QuotationMark = (char)0x201E;
}
