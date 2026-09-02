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

namespace Heimdall.App.Tests;

/// <summary>
/// One assertion that reads production source and anchors on a fragment the same test never
/// carried through <c>ViewSource.IsStatementOfTheMethodBody</c>.
/// </summary>
internal sealed record BareSourceAssertion(
    string TestFile,
    string TestMethod,
    string Rule,
    string Needle)
{
    /// <summary>The entry as one line of the baseline file.</summary>
    /// <remarks>
    /// No line number: a line number moves whenever anything above it is edited, and a key that
    /// churns is a key nobody keeps accurate. File, test and anchor text identify the site.
    /// </remarks>
    internal string BaselineLine =>
        string.Join("|", TestFile, TestMethod, Rule, Needle);
}

/// <summary>What one pass of <see cref="SourceReadingScan"/> saw.</summary>
internal sealed class SourceReadingScanResult
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    /// <summary>Every bare anchor found, in scan order.</summary>
    internal List<BareSourceAssertion> Findings { get; } = new();

    /// <summary>Files whose braces did not balance after blanking, so nothing read from them counts.</summary>
    internal List<string> UnbalancedFiles { get; } = new();

    /// <summary>How many sites each discovery rule reached, whether or not they were bare.</summary>
    internal IReadOnlyDictionary<string, int> Counters => _counters;

    internal int Count(string rule) => _counters.TryGetValue(rule, out int value) ? value : 0;

    internal void Record(string rule) => _counters[rule] = Count(rule) + 1;
}

/// <summary>
/// Finds every test assertion that reads production C# source and anchors on a bare fragment.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> A test that asserts a statement is PRESENT by reading production
/// source text - <c>Contains</c>, <c>IndexOf</c>, an ordering of <c>IndexOf</c> results, a regex
/// between anchors - is satisfied by text that never runs. Deleting the statement is caught.
/// Folding it behind a term that is false by construction is not: the text stays at the same
/// offset, the ordering still holds, and the guard still reports the behaviour it names. Five such
/// sites were repaired one at a time, and each repair left a twin in the same file. This finds the
/// class instead of the instance.</para>
/// <para><b>The rule.</b> Inside a test that holds production source, an anchor used by a presence
/// assertion, an <c>IndexOf</c> or a regex must be an anchor the same test also carried through
/// <c>ViewSource.IsStatementOfTheMethodBody</c>. That predicate is what separates "the text is
/// written in this file" from "the text stands as a step of this method body".</para>
/// <para><b>Absence assertions are exempt, by rule rather than by silence.</b>
/// <c>Assert.DoesNotContain</c>, <c>Assert.DoesNotMatch</c>, a negated <c>Contains</c> and an
/// <c>Assert.False</c> over one invert the risk: folding a statement can only make an absence
/// assertion pass, never fail, and the only way to break one is to ADD the text it forbids. They
/// are counted, so the exemption is visible rather than assumed.</para>
/// <para><b>What this cannot establish.</b> Exactly what the predicate it demands cannot: that the
/// statement RUNS. A whole-statement match settles that a statement is written as a step of a body;
/// the predicate walks straight past every conditional early return above it.</para>
/// </remarks>
internal static class SourceReadingScan
{
    internal const string FilesScannedRule = "files-scanned";
    internal const string TestMembersRule = "test-members-scanned";
    internal const string ViewSourceReaderRule = "reader-files-view-source";
    internal const string FileReadReaderRule = "reader-files-file-read";
    internal const string PresenceRule = "assert-presence";
    internal const string MemberContainsRule = "member-contains";
    internal const string IndexOfRule = "index-of";
    internal const string RegexRule = "regex";
    internal const string AbsenceExemptionRule = "exempt-absence-assertion";
    internal const string SanctionedRule = "sanctioned-statement";

    private const string ProductionDirectory = "src";

    private static readonly Regex s_memberStart = new(
        @"(?m)^    (?:private|public|internal|protected)\s", RegexOptions.Compiled);

    private static readonly Regex s_viewSourceReader = new(
        @"\bViewSource\.(?:Code|HandlerBody|HandlerLogic)\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_fileRead = new(
        @"\bFile\.ReadAllText(?:Async)?\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_productionDirectory = new(
        "\"" + ProductionDirectory + "\"|@?\"" + ProductionDirectory + "[/\\\\]",
        RegexOptions.Compiled);

    private static readonly Regex s_csharpTarget = new(
        "\\.cs\"|\\*\\.cs", RegexOptions.Compiled);

    private static readonly Regex s_localDeclaration = new(
        @"\b(?:string|var)\s+(\w+)\s*=\s*([^;]*);",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_assertCall = new(
        @"\bAssert\.(Contains|DoesNotContain|Matches|DoesNotMatch)\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_memberCall = new(
        @"\b(\w+)\.(Contains|IndexOf)\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_regexCall = new(
        @"\bRegex\.(?:IsMatch|Match|Matches)\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_predicateCall = new(
        @"\bIsStatementOfTheMethodBody\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_identifierName = new(
        @"\b(\w+)\s*\(", RegexOptions.Compiled);

    private static readonly Regex s_valueName = new(
        @"\b(\w+)\s*(?:=|=>|\{)", RegexOptions.Compiled);

    /// <summary>Runs the scan over already-read files, so a control can hand it text it wrote.</summary>
    internal static SourceReadingScanResult Scan(IEnumerable<(string Path, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        SourceReadingScanResult result = new();
        foreach ((string path, string text) in files)
        {
            result.Record(FilesScannedRule);
            ScanFile(path, text, result);
        }

        return result;
    }

    /// <summary>
    /// The same text with every comment and literal replaced by spaces, keeping every offset.
    /// </summary>
    /// <remarks>
    /// Structure is read from this, anchors are sliced out of the original at the same offsets.
    /// Blanking is what stops the scan from seeing its own controls: a fixture written as a string
    /// literal is data here, exactly as a call left behind in a comment is.
    /// </remarks>
    internal static string BlankCommentsAndLiterals(string source)
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

    /// <summary>Advances one step through the body of the literal that is currently open.</summary>
    /// <remarks>
    /// An interpolation hole is lexed as code rather than skipped, because the tests really do
    /// write <c>$"{Encode("""{"alg":"none"}""")}"</c>: a scan that ends the outer string at the
    /// first inner quote leaves the hole's closing brace behind as code and shifts every brace
    /// depth computed afterwards. Two files in this repository take exactly that shape, and the
    /// balance control is what found them.
    /// </remarks>
    private static int ScanLiteralBody(
        string source, char[] result, List<LiteralContext> open, int index)
    {
        LiteralContext context = open[^1];

        if (context.QuoteRun >= 3)
        {
            // A raw string is opaque: its content may hold quotes, braces and whole documents,
            // and only a run of at least as many quotes as opened it can end it.
            int run = 0;
            while (index + run < source.Length && source[index + run] == '"')
            {
                run++;
            }

            if (run >= context.QuoteRun)
            {
                for (int closer = 0; closer < run; closer++)
                {
                    Blank(source, result, index++);
                }

                open.RemoveAt(open.Count - 1);
                return index;
            }

            for (int inner = 0; inner < Math.Max(run, 1); inner++)
            {
                Blank(source, result, index++);
            }

            return index;
        }

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
        string source, char[] result, List<LiteralContext> open, ref int index)
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
            int run = 0;
            while (probe + run < source.Length && source[probe + run] == '"')
            {
                run++;
            }

            open.Add(new LiteralContext('"', verbatim, interpolated && run < 3, run, 0));
            int opening = run >= 3 ? probe + run : probe + 1;
            while (index < opening)
            {
                Blank(source, result, index++);
            }

            return true;
        }

        if (source[index] == '\'')
        {
            open.Add(new LiteralContext(
                '\'', Verbatim: false, Interpolated: false, QuoteRun: 1, HoleDepth: 0));
            Blank(source, result, index++);
            return true;
        }

        return false;
    }

    private readonly record struct LiteralContext(
        char Terminator, bool Verbatim, bool Interpolated, int QuoteRun, int HoleDepth);

    private static bool Matches(string source, int index, string token) =>
        index + token.Length <= source.Length
        && string.CompareOrdinal(source, index, token, 0, token.Length) == 0;

    private static void Blank(string source, char[] result, int index) =>
        result[index] = source[index] is '\n' or '\r' ? source[index] : ' ';

    private static int BraceBalance(string blanked)
    {
        int depth = 0;
        foreach (char character in blanked)
        {
            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
            }
        }

        return depth;
    }

    private sealed record Member(
        string? Name, bool IsTest, bool TakesArguments, string Blanked, string Raw);

    private static void ScanFile(string path, string raw, SourceReadingScanResult result)
    {
        string blanked = BlankCommentsAndLiterals(raw);
        if (BraceBalance(blanked) != 0)
        {
            result.UnbalancedFiles.Add(path);
        }

        // The path oracle reads the original text: a path is a literal, and literals are blanked.
        bool readsProductionSource =
            s_productionDirectory.IsMatch(raw) && s_csharpTarget.IsMatch(raw);

        List<Member> members = SplitMembers(blanked, raw);
        if (members.Count == 0)
        {
            return;
        }

        // Counted over the whole file rather than over the helper members, because a test that
        // calls the reader inline is discovered exactly the same way and must raise the same
        // floor. Counting only helpers made this rule report two files where there are ten.
        if (s_viewSourceReader.IsMatch(blanked))
        {
            result.Record(ViewSourceReaderRule);
        }

        if (readsProductionSource && s_fileRead.IsMatch(blanked))
        {
            result.Record(FileReadReaderRule);
        }

        HashSet<string> invocationReaders = new(StringComparer.Ordinal);
        HashSet<string> valueReaders = new(StringComparer.Ordinal);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Member member in members)
            {
                if (member.IsTest || member.Name is null
                    || invocationReaders.Contains(member.Name)
                    || valueReaders.Contains(member.Name))
                {
                    continue;
                }

                bool reader = false;
                if (s_viewSourceReader.IsMatch(member.Blanked))
                {
                    reader = true;
                }
                else if (readsProductionSource && s_fileRead.IsMatch(member.Blanked))
                {
                    reader = true;
                }
                else if (MentionsAny(member.Blanked, invocationReaders)
                    || MentionsAny(member.Blanked, valueReaders))
                {
                    reader = true;
                }

                if (reader)
                {
                    _ = member.TakesArguments
                        ? invocationReaders.Add(member.Name)
                        : valueReaders.Add(member.Name);
                    changed = true;
                }
            }
        }

        foreach (Member member in members)
        {
            if (!member.IsTest || member.Name is null)
            {
                continue;
            }

            result.Record(TestMembersRule);
            ScanTestMember(
                path, member, readsProductionSource, invocationReaders, valueReaders, result);
        }
    }

    private static bool MentionsAny(string text, IEnumerable<string> names) =>
        names.Any(name => MentionsWord(text, name));

    /// <summary>Whether the identifier occurs in the text as a whole word.</summary>
    /// <remarks>
    /// Written out rather than left to a regex built per call: this runs for every reader name
    /// against every member of every test file, and a pattern compiled per pair is the difference
    /// between a guard that runs and one nobody keeps.
    /// </remarks>
    private static bool MentionsWord(string text, string word)
    {
        int index = text.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            bool clearBefore = index == 0 || !IsWordCharacter(text[index - 1]);
            int after = index + word.Length;
            bool clearAfter = after >= text.Length || !IsWordCharacter(text[after]);
            if (clearBefore && clearAfter)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static List<Member> SplitMembers(string blanked, string raw)
    {
        List<Member> members = new();
        MatchCollection starts = s_memberStart.Matches(blanked);
        for (int index = 0; index < starts.Count; index++)
        {
            int start = starts[index].Index;
            int end = index + 1 < starts.Count ? starts[index + 1].Index : blanked.Length;

            string memberBlanked = blanked[start..end];
            Match invocation = s_identifierName.Match(memberBlanked);
            Match value = s_valueName.Match(memberBlanked);
            string? name = invocation.Success ? invocation.Groups[1].Value
                : value.Success ? value.Groups[1].Value : null;

            members.Add(new Member(
                name,
                IsTestMember(blanked, start),
                invocation.Success,
                memberBlanked,
                raw[start..end]));
        }

        return members;
    }

    /// <summary>
    /// Whether the lines above a member declaration carry a test attribute.
    /// </summary>
    /// <remarks>
    /// The attribute is read backwards rather than folded into the member-start pattern, because
    /// a multi-line <c>[InlineData(...)]</c> breaks any pattern that tries to swallow it forwards,
    /// and a test whose attribute is not seen is a test this scan never looks inside.
    /// </remarks>
    private static bool IsTestMember(string blanked, int start)
    {
        int cursor = start;
        while (cursor > 0)
        {
            int lineEnd = cursor - 1;
            int lineStart = blanked.LastIndexOf('\n', Math.Max(lineEnd - 1, 0));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            string line = blanked[lineStart..Math.Max(lineStart, lineEnd)].Trim();

            if (line.Length == 0)
            {
                cursor = lineStart;
                continue;
            }

            if (line.StartsWith('[') || line.EndsWith(']'))
            {
                if (line.Contains("[Fact", StringComparison.Ordinal)
                    || line.Contains("[Theory", StringComparison.Ordinal))
                {
                    return true;
                }

                cursor = lineStart;
                continue;
            }

            return false;
        }

        return false;
    }

    private static void ScanTestMember(
        string path,
        Member member,
        bool readsProductionSource,
        HashSet<string> invocationReaders,
        HashSet<string> valueReaders,
        SourceReadingScanResult result)
    {
        string blanked = member.Blanked;
        string raw = member.Raw;
        HashSet<string> holders = new(StringComparer.Ordinal);

        bool IsSourceExpression(string expression)
        {
            string trimmed = expression.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            if (s_viewSourceReader.IsMatch(trimmed))
            {
                return true;
            }

            if (readsProductionSource && s_fileRead.IsMatch(trimmed))
            {
                return true;
            }

            return MentionsAny(trimmed, invocationReaders)
                || MentionsAny(trimmed, valueReaders)
                || MentionsAny(trimmed, holders);
        }

        foreach (Match declaration in s_localDeclaration.Matches(blanked))
        {
            if (IsSourceExpression(declaration.Groups[2].Value))
            {
                _ = holders.Add(declaration.Groups[1].Value);
            }
        }

        HashSet<string> sanctioned = new(StringComparer.Ordinal);
        foreach (Match predicate in s_predicateCall.Matches(blanked))
        {
            List<(int Start, int End)> arguments = SplitArguments(blanked, predicate.Index + predicate.Length - 1);
            if (arguments.Count >= 2)
            {
                result.Record(SanctionedRule);
                _ = sanctioned.Add(Normalize(raw, arguments[1]));
            }
        }

        void Report(string rule, string needle)
        {
            result.Record(rule);
            if (!sanctioned.Contains(needle))
            {
                result.Findings.Add(new BareSourceAssertion(path, member.Name!, rule, needle));
            }
        }

        foreach (Match assert in s_assertCall.Matches(blanked))
        {
            List<(int Start, int End)> arguments = SplitArguments(blanked, assert.Index + assert.Length - 1);
            if (arguments.Count < 2 || !IsSourceExpression(Slice(blanked, arguments[1])))
            {
                continue;
            }

            string kind = assert.Groups[1].Value;
            if (kind is "DoesNotContain" or "DoesNotMatch")
            {
                result.Record(AbsenceExemptionRule);
                continue;
            }

            Report(PresenceRule, Normalize(raw, arguments[0]));
        }

        foreach (Match call in s_memberCall.Matches(blanked))
        {
            string haystack = call.Groups[1].Value;
            if (!IsSourceExpression(haystack))
            {
                continue;
            }

            List<(int Start, int End)> arguments = SplitArguments(blanked, call.Index + call.Length - 1);
            if (arguments.Count == 0)
            {
                continue;
            }

            if (IsAbsenceContext(blanked, call.Index))
            {
                result.Record(AbsenceExemptionRule);
                continue;
            }

            Report(
                call.Groups[2].Value == "IndexOf" ? IndexOfRule : MemberContainsRule,
                Normalize(raw, arguments[0]));
        }

        foreach (Match call in s_regexCall.Matches(blanked))
        {
            List<(int Start, int End)> arguments = SplitArguments(blanked, call.Index + call.Length - 1);
            if (arguments.Count < 2 || !IsSourceExpression(Slice(blanked, arguments[0])))
            {
                continue;
            }

            Report(RegexRule, Normalize(raw, arguments[1]));
        }
    }

    /// <summary>
    /// Whether the statement holding the offset reads its <c>Contains</c> as an absence.
    /// </summary>
    private static bool IsAbsenceContext(string blanked, int index)
    {
        int start = index;
        while (start > 0 && blanked[start - 1] is not (';' or '{' or '}'))
        {
            start--;
        }

        string statement = blanked[start..index];
        return statement.Contains("Assert.False(", StringComparison.Ordinal)
            || statement.TrimEnd().EndsWith('!');
    }

    private static string Slice(string text, (int Start, int End) span) =>
        text[span.Start..span.End];

    private static string Normalize(string raw, (int Start, int End) span)
    {
        StringBuilder builder = new();
        bool pendingSpace = false;
        foreach (char character in raw[span.Start..span.End])
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                _ = builder.Append(' ');
                pendingSpace = false;
            }

            _ = builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>The argument spans of a call whose opening parenthesis sits at the offset.</summary>
    private static List<(int Start, int End)> SplitArguments(string blanked, int openParenthesis)
    {
        List<(int Start, int End)> arguments = new();
        int depth = 0;
        int start = openParenthesis + 1;

        for (int index = openParenthesis; index < blanked.Length; index++)
        {
            char current = blanked[index];
            if (current is '(' or '[' or '{')
            {
                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    arguments.Add((start, index));
                    return arguments;
                }
            }
            else if (current == ',' && depth == 1)
            {
                arguments.Add((start, index));
                start = index + 1;
            }
        }

        return arguments;
    }
}
