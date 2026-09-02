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

using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Reads the RDP view's own source and markup.
/// </summary>
/// <remarks>
/// <para>A handler body is read here only to assert that a decision is actually consulted at the
/// site that owns it. The decision itself is always asserted behaviourally, against the extracted
/// function or against a real WPF element, never against the text.</para>
/// <para><see cref="WithoutCommentsAndLiterals"/> and <see cref="IsStatementOfTheMethodBody"/>
/// exist because a plain substring search over source text is satisfied by text that never runs. A
/// call left behind in a comment, and a call moved inside a branch, both keep a substring search
/// green while the behaviour is gone. Callers that want more than "the text is present" read a
/// body through the first and test the site with the second - which is still short of
/// reachability, and says so in its own remark.</para>
/// </remarks>
internal static class ViewSource
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static string Code() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml.cs"));

    internal static string MarkupPath() => Path.Combine(
        RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml");

    internal static XDocument Markup() => XDocument.Load(MarkupPath());

    /// <summary>Finds the markup element carrying <paramref name="name"/> as its x:Name.</summary>
    internal static XElement NamedElement(string name)
    {
        XElement? element = Markup()
            .Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(s_xaml + "Name") == name);

        Assert.True(element is not null, $"No element named '{name}' in EmbeddedRdpView.xaml.");
        return element!;
    }

    /// <summary>The value of an attached automation property, or null when it is not declared.</summary>
    internal static string? AutomationAttribute(XElement element, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.Attribute("AutomationProperties." + propertyName);
    }

    /// <summary>The local (prefix-free) markup tag name of an element.</summary>
    internal static string TagName(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Name.LocalName;
    }

    /// <summary>
    /// The text of one method, from its signature to the next member declaration at class scope.
    /// </summary>
    internal static string HandlerBody(string signature) => HandlerBody(Code(), signature);

    /// <summary>
    /// The same method text with every comment and literal blanked, so what is read is code
    /// that runs rather than prose that resembles it.
    /// </summary>
    internal static string HandlerLogic(string signature) =>
        HandlerBody(WithoutCommentsAndLiterals(Code()), signature);

    /// <summary>
    /// The text of one method of <paramref name="source"/>, from its signature to the next
    /// member declaration at class scope.
    /// </summary>
    internal static string HandlerBody(string source, string signature)
    {
        ArgumentNullException.ThrowIfNull(source);
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"handler not found in the view: {signature}");

        Match next = Regex.Match(
            source[(start + signature.Length)..],
            @"(?m)^    (private|public|internal|protected)\s");

        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

    /// <summary>
    /// The same text with every comment, string literal and character literal replaced by
    /// spaces, keeping every offset and every line break.
    /// </summary>
    /// <remarks>
    /// Interpolation holes are lexed rather than skipped, because the view really does write
    /// <c>$"...{(flag ? "a" : "b")}"</c>: a naive scan ends the string at the inner quote and
    /// leaves the hole's closing brace behind as code, which would corrupt every brace depth
    /// computed afterwards.
    /// </remarks>
    internal static string WithoutCommentsAndLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        char[] result = source.ToCharArray();
        List<LiteralContext> open = new();
        int index = 0;

        while (index < source.Length)
        {
            if (open.Count > 0 && open[^1].HoleDepth == 0)
            {
                index = ScanLiteralBody(source, result, open, index);
                continue;
            }

            // Class scope, or a hole inside an interpolated string, which is code either way.
            if (Matches(source, index, "//"))
            {
                while (index < source.Length && source[index] != '\n')
                {
                    Blank(source, result, index++);
                }

                continue;
            }

            if (Matches(source, index, "/*"))
            {
                while (index < source.Length && !Matches(source, index, "*/"))
                {
                    Blank(source, result, index++);
                }

                for (int closer = 0; closer < 2 && index < source.Length; closer++)
                {
                    Blank(source, result, index++);
                }

                continue;
            }

            if (TryOpenLiteral(source, result, open, ref index))
            {
                continue;
            }

            if (open.Count > 0)
            {
                char inHole = source[index];
                if (inHole == '{')
                {
                    open[^1] = open[^1] with { HoleDepth = open[^1].HoleDepth + 1 };
                }
                else if (inHole == '}')
                {
                    open[^1] = open[^1] with { HoleDepth = open[^1].HoleDepth - 1 };
                }

                Blank(source, result, index++);
                continue;
            }

            index++;
        }

        return new string(result);
    }

    /// <summary>
    /// Whether <paramref name="statement"/> occurs in <paramref name="logic"/> as a statement of
    /// the method body itself: at the body's own brace depth, starting a statement rather than
    /// trailing a condition, and with no unconditional <c>return</c> standing above it.
    /// </summary>
    /// <remarks>
    /// <para><paramref name="logic"/> must already have come through
    /// <see cref="WithoutCommentsAndLiterals"/>.</para>
    /// <para>This separates "the call is written somewhere in this file" from "the call is written
    /// as a step of this method": <c>if (_disposed) { Call(); }</c> and <c>if (_disposed) Call();</c>
    /// both keep the text and lose the behaviour, and a <c>return</c> placed above the call at body
    /// level makes every statement after it dead. All three are rejected here.</para>
    /// <para><b>What this does not establish is reachability.</b> A conditional early return above
    /// the site - <c>if (server is null) { return X; }</c> - is a statement of the body like any
    /// other, and this predicate walks straight past it: knowing whether the site is reached would
    /// mean evaluating that condition, and nothing here evaluates anything. Invert such a guard and
    /// the site is skipped on every run while this predicate still answers true. A caller may say
    /// "the call stands as a step of this method". It may not say "the call runs".</para>
    /// </remarks>
    internal static bool IsStatementOfTheMethodBody(string logic, string statement)
    {
        ArgumentNullException.ThrowIfNull(logic);
        ArgumentNullException.ThrowIfNull(statement);

        int bodyStart = logic.IndexOf('{');
        Assert.True(bodyStart >= 0, "The method text holds no body.");

        int depth = 0;
        for (int index = bodyStart; index < logic.Length; index++)
        {
            if (logic[index] == '{')
            {
                depth++;
            }
            else if (logic[index] == '}')
            {
                depth--;
                Assert.True(
                    depth >= 0,
                    "Brace depth went negative, so the literal and comment blanking is wrong and "
                        + "nothing measured from it means anything.");
            }
            else if (depth == 1)
            {
                if (string.CompareOrdinal(logic, index, statement, 0, statement.Length) == 0)
                {
                    return StartsAStatement(logic, index);
                }

                // An unconditional return at body level ends the method, so a site below it is
                // written and dead. It is the one unreachability a text reading can settle without
                // evaluating a condition, so it is the one it settles.
                if (IsKeyword(logic, index, "return") && StartsAStatement(logic, index))
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="keyword"/> sits at the offset as a whole word.</summary>
    private static bool IsKeyword(string logic, int index, string keyword)
    {
        if (string.CompareOrdinal(logic, index, keyword, 0, keyword.Length) != 0)
        {
            return false;
        }

        int after = index + keyword.Length;
        return after >= logic.Length
            || (!char.IsLetterOrDigit(logic[after]) && logic[after] != '_');
    }

    /// <summary>Whether nothing but whitespace separates the offset from the previous statement.</summary>
    private static bool StartsAStatement(string logic, int index)
    {
        for (int back = index - 1; back >= 0; back--)
        {
            char previous = logic[back];
            if (previous is '{' or '}' or ';')
            {
                return true;
            }

            if (!char.IsWhiteSpace(previous))
            {
                return false;
            }
        }

        return true;
    }

    private static int ScanLiteralBody(
        string source,
        char[] result,
        List<LiteralContext> open,
        int index)
    {
        LiteralContext context = open[^1];

        if (context.Verbatim && Matches(source, index, "\"\""))
        {
            Blank(source, result, index++);
            Blank(source, result, index++);
            return index;
        }

        if (!context.Verbatim && source[index] == '\\' && index + 1 < source.Length)
        {
            Blank(source, result, index++);
            Blank(source, result, index++);
            return index;
        }

        if (context.Interpolated && Matches(source, index, "{{"))
        {
            Blank(source, result, index++);
            Blank(source, result, index++);
            return index;
        }

        if (context.Interpolated && source[index] == '{')
        {
            open[^1] = context with { HoleDepth = 1 };
            Blank(source, result, index++);
            return index;
        }

        if (source[index] == context.Terminator)
        {
            open.RemoveAt(open.Count - 1);
        }

        Blank(source, result, index++);
        return index;
    }

    private static bool TryOpenLiteral(
        string source,
        char[] result,
        List<LiteralContext> open,
        ref int index)
    {
        int probe = index;
        bool verbatim = false;
        bool interpolated = false;
        while (probe < source.Length && source[probe] is '@' or '$')
        {
            verbatim |= source[probe] == '@';
            interpolated |= source[probe] == '$';
            probe++;
        }

        if (probe < source.Length && source[probe] == '"')
        {
            open.Add(new LiteralContext('"', verbatim, interpolated, 0));
            while (index <= probe)
            {
                Blank(source, result, index++);
            }

            return true;
        }

        if (source[index] == '\'')
        {
            open.Add(new LiteralContext('\'', Verbatim: false, Interpolated: false, HoleDepth: 0));
            Blank(source, result, index++);
            return true;
        }

        return false;
    }

    private static bool Matches(string source, int index, string token) =>
        index + token.Length <= source.Length
        && string.CompareOrdinal(source, index, token, 0, token.Length) == 0;

    private static void Blank(string source, char[] result, int index) =>
        result[index] = source[index] is '\n' or '\r' ? source[index] : ' ';

    private readonly record struct LiteralContext(
        char Terminator,
        bool Verbatim,
        bool Interpolated,
        int HoleDepth);

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
