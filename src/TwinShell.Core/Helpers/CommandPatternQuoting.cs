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
    /// Returns <c>true</c> when the first occurrence of the
    /// <paramref name="parameterName"/> placeholder ("{name}") inside
    /// <paramref name="commandPattern"/> falls within an open single-quoted span,
    /// i.e. an odd number of single quotes precede it. This is the precondition
    /// for <c>QuotingMode.InlineInQuotes</c>, which only escapes the value and
    /// relies on surrounding quotes already present in the pattern.
    /// </summary>
    /// <param name="commandPattern">The command pattern containing the placeholder.</param>
    /// <param name="parameterName">The parameter name without braces.</param>
    /// <returns>
    /// <c>true</c> when the placeholder is inside a single-quoted span;
    /// <c>false</c> when it is outside one or absent from the pattern.
    /// </returns>
    public static bool IsPlaceholderInsideSingleQuotedSpan(string commandPattern, string parameterName)
    {
        if (string.IsNullOrEmpty(commandPattern) || string.IsNullOrEmpty(parameterName))
        {
            return false;
        }

        string placeholder = Placeholder(parameterName);
        int placeholderIndex = commandPattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            return false;
        }

        int quoteCount = 0;
        for (int index = 0; index < placeholderIndex; index++)
        {
            if (commandPattern[index] == '\'')
            {
                quoteCount++;
            }
        }

        return quoteCount % 2 == 1;
    }
}
