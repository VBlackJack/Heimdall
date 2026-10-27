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

namespace Heimdall.Core.Security;

/// <summary>Why <see cref="ArgumentTemplate.FindUnsafeValue"/> refuses a value.</summary>
public enum ArgumentRejection
{
    /// <summary>
    /// The value holds white space, a double quote or a control character where the template
    /// does not quote the placeholder, so it would split into several arguments.
    /// </summary>
    NeedsQuotes,

    /// <summary>The value starts with a dash where it begins an argument, so it would be an option.</summary>
    LeadingDash,
}

/// <summary>A value <see cref="ArgumentTemplate.FindUnsafeValue"/> refuses, and why.</summary>
/// <param name="Token">The placeholder whose value is refused, as the template spells it in the placeholder list.</param>
/// <param name="Reason">Why it is refused.</param>
public sealed record ArgumentTemplateRejection(string Token, ArgumentRejection Reason);

/// <summary>
/// Expands <c>{Placeholder}</c> tokens of a command-line template: the one expansion the
/// credential command and the external tools share.
/// </summary>
/// <remarks>
/// <para>The template is read once from left to right and a value is never expanded again, so a
/// host or entry name containing <c>{KeyFile}</c> is not replaced by the key file path. Quotes are
/// never added: a template that quotes a placeholder keeps its own quoting, and one that does not
/// gets the value verbatim. Whether that value is safe there is
/// <see cref="FindUnsafeValue"/>'s question, which the external tools ask before every launch.</para>
/// <para>The quoted state of the template follows the CRT rules: a double quote opens or closes a
/// quoted region unless an odd run of backslashes precedes it.</para>
/// </remarks>
public static class ArgumentTemplate
{
    private const char Quote = '"';
    private const char Backslash = '\\';
    private const char OptionPrefix = '-';

    /// <summary>
    /// Replaces every placeholder of the template in one pass over the template only. When an
    /// occurrence is followed by a double quote and <paramref name="protectClosingQuote"/> is
    /// set, trailing backslashes of the value are doubled: an entry named <c>share\</c> would
    /// otherwise turn the closing quote into a literal one and merge the following arguments
    /// into it. Token comparison ignores case.
    /// </summary>
    public static string Expand(
        string template,
        IReadOnlyList<(string Token, string Value)> placeholders,
        bool protectClosingQuote)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(placeholders);

        var builder = new StringBuilder(template.Length);
        int position = 0;
        while (position < template.Length)
        {
            (string Token, string Value)? match = template[position] == '{'
                ? FindPlaceholder(template, position, placeholders)
                : null;

            if (match is not { } found)
            {
                builder.Append(template[position]);
                position++;
                continue;
            }

            int after = position + found.Token.Length;
            bool closedByQuote = after < template.Length && template[after] == Quote;
            builder.Append(protectClosingQuote && closedByQuote ? DoubleTrailingBackslashes(found.Value) : found.Value);
            position = after;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The first placeholder occurrence of <paramref name="template"/> whose value would not stay
    /// one plain argument, or null when every substituted value is safe.
    /// </summary>
    /// <remarks>
    /// <para>Outside the template's quotes a value is refused when it holds white space, a double
    /// quote or a control character (it would split into several arguments: the user is told to
    /// quote the placeholder), or when it starts with a dash and the placeholder begins a
    /// whitespace-delimited token (it would be an option: <c>-oProxyCommand=...</c> as an OpenSSH
    /// user name runs a command). A placeholder glued to other text, such as the shipped
    /// <c>\\{Host}</c> or <c>{User}@{Host}</c>, does not begin a token.</para>
    /// <para>Inside the template's quotes the value is one argument with its quotes stripped, but
    /// a quoted region that begins a token and starts with the value is still an option when the
    /// value starts with a dash, so that case is refused too. A control character is refused
    /// wherever it stands.</para>
    /// <para>Only placeholders the template actually uses are judged.</para>
    /// </remarks>
    public static ArgumentTemplateRejection? FindUnsafeValue(
        string template,
        IReadOnlyList<(string Token, string Value)> placeholders)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(placeholders);

        bool insideQuotes = false;
        int pendingBackslashes = 0;
        int position = 0;
        while (position < template.Length)
        {
            (string Token, string Value)? match = template[position] == '{'
                ? FindPlaceholder(template, position, placeholders)
                : null;

            if (match is not { } found)
            {
                char current = template[position];
                if (current == Quote && pendingBackslashes % 2 == 0)
                {
                    insideQuotes = !insideQuotes;
                }

                pendingBackslashes = current == Backslash ? pendingBackslashes + 1 : 0;
                position++;
                continue;
            }

            string value = found.Value;
            bool beginsToken = insideQuotes
                ? template[position - 1] == Quote && BeginsToken(template, position - 1)
                : BeginsToken(template, position);

            if (value.Any(char.IsControl)
                || (!insideQuotes && value.Any(static character => char.IsWhiteSpace(character) || character == Quote)))
            {
                return new ArgumentTemplateRejection(found.Token, ArgumentRejection.NeedsQuotes);
            }

            if (beginsToken && value.StartsWith(OptionPrefix))
            {
                return new ArgumentTemplateRejection(found.Token, ArgumentRejection.LeadingDash);
            }

            pendingBackslashes = 0;
            position += found.Token.Length;
        }

        return null;
    }

    /// <summary>Whether the character at <paramref name="index"/> starts a whitespace-delimited token.</summary>
    private static bool BeginsToken(string template, int index) =>
        index == 0 || char.IsWhiteSpace(template[index - 1]);

    private static (string Token, string Value)? FindPlaceholder(
        string template,
        int position,
        IReadOnlyList<(string Token, string Value)> placeholders)
    {
        foreach (var placeholder in placeholders)
        {
            if (string.Compare(template, position, placeholder.Token, 0, placeholder.Token.Length,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                return placeholder;
            }
        }

        return null;
    }

    private static string DoubleTrailingBackslashes(string value)
    {
        int trailing = 0;
        while (trailing < value.Length && value[value.Length - 1 - trailing] == Backslash)
        {
            trailing++;
        }

        return trailing == 0 ? value : value + new string(Backslash, trailing);
    }
}
