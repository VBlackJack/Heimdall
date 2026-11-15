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
using System.Linq;
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Every act the SFTP pane runs as root goes through one recorder, so the root badge cannot miss
/// one. A new escalation site that bypasses it is a site the operator would not be told about.
/// </summary>
/// <remarks>
/// <para>
/// The readings are census counts made inside helper members, in the shape of
/// <see cref="SftpConnectCancellationWiringTests"/>: a test member asserts a number or an empty
/// list, never a fragment of production text, and each reading has a floor (so it is not green on
/// a file list that reads nothing) and a positive control (so it is not green on a matcher that
/// matches nothing). All readings run over source with comments and literals blanked.
/// </para>
/// <para>
/// What none of this establishes is that a site RUNS through the recorder, only that it is
/// written inside the recorder's call. The behaviour is covered by the recorder and view model
/// tests.
/// </para>
/// <para>
/// <b>What these guards do NOT catch</b>, so nobody over-trusts them:
/// a call made through a delegate, an event, a stored lambda or reflection (the readings follow
/// names in text, and the two seams the pane stores as delegates are read by name only);
/// a new privileged helper added inside Heimdall.Sftp's <c>PrivilegedFileTransfer.cs</c>, which the
/// census skips because it is where the primitives live; a journal record built directly from
/// <c>SessionOperationRecord</c> without the emitter; a privileged act that reaches the server
/// by some means other than the names listed here (a new type, a new assembly, a shell command
/// run by another class); a member that is named like an allowed one in another file; and
/// whether any listed site actually runs. A new escalation path of one of those kinds is found
/// by review, not by this file.
/// </para>
/// </remarks>
public sealed class SftpRootActivityFunnelGuardTests
{
    private const string RecorderFile = "SftpPrivilegedActRecorder.cs";
    private const string EmitterFile = "SessionOperationEmitter.cs";
    private const string ViewFile = "EmbeddedSftpView.xaml.cs";
    private const string EditorFile = "RemoteFileEditor.cs";
    private const string TransferFile = "PrivilegedFileTransfer.cs";
    private const string RecorderMember = "_privilegedActs";

    private const int MinimumAppSourceFiles = 500;
    private const int MinimumRecorderPrivilegedSites = 7;
    private const int MinimumPrimitiveCalls = 8;
    private const int ExpectedSudoClientMembers = 5;

    private static readonly string[] s_primitives =
    [
        "RunSudoCommandAsync",
        "RunSudoDeleteCommandAsync",
        "RunSudoChmodCommandAsync",
        "ExecuteSudoRenameMoveAsync",
        "DownloadViaSudoCoreAsync",
        "ListDirectoryViaSudoCoreAsync",
        "ExecuteAtomicWriteAsync",
        "EditFileSudoAsync",
        "ExecuteSudoRenameCommandAsync",
    ];

    // The members allowed to call a primitive outside a recorder call: the two seams the pane
    // stores for tests, and the rename probes, a documented exception.
    private static readonly Dictionary<string, string[]> s_primitiveOutsideCallers = new(StringComparer.Ordinal)
    {
        ["RunSudoCommandAsync"] = ["RunSudoDeleteCommandAsync", "RunSudoChmodCommandAsync"],
        ["ExecuteSudoRenameCommandAsync"] = ["ProbeSudoTargetKindAsync", "ExecuteSudoRenameMoveAsync"],
    };

    private const string UnnamedMember = "<unnamed member>";

    private static readonly string[] s_expectedBodyExecutors =
    [
        "ExecuteSudoRenameCommandAsync",
        "ListDirectoryViaSudoCoreAsync",
        "RunSudoCommandAsync",
    ];

    private static readonly string[] s_expectedInvocationBuilders =
    [
        "DownloadViaSudoCoreAsync",
        "ExecuteSudoBodyAsync",
    ];

    private static readonly string[] s_expectedSudoClientMembers =
    [
        "DownloadViaSudoCoreAsync",
        "ExecuteSudoRenameCommandAsync",
        "ListDirectoryViaSudoCoreAsync",
        "RunSudoCommandAsync",
        "UploadViaSudoAsync",
    ];

    private static readonly string[] s_expectedPrivilegedExecutionMembers =
    [
        "EditFileSudoCoreAsync",
        "UploadViaSudoAsync",
        "UploadWithSudoAsync",
    ];

    private static readonly Regex s_privilegedTrue = new(
        @"\bprivileged\s*:\s*true\b", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex s_emitterRunCall = new(
        @"(\w+)\.Run(?:Mkdir|Delete|Chmod|Rename|Download|Upload)Async\(", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex s_memberStart = new(
        @"(?m)^    (?:private|public|internal|protected)\s", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex s_memberName = new(
        @"(\w+)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5));

    // G1 ---------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheRecorderMarksAnOperationAsPrivileged()
    {
        IReadOnlyList<string> offenders = PrivilegedMarkerOffenders(AppSources());

        Assert.True(
            offenders.Count == 0,
            "an operation is marked privileged outside the recorder, so the root badge would not "
                + "see it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheRecorderIsWhereThePrivilegedMarksAre()
    {
        // The floor: without it the reading above is green on a recorder that no longer marks
        // anything, or on a file list that no longer includes it.
        int marks = PrivilegedMarkCount(AppSources(), RecorderFile);

        Assert.True(
            marks >= MinimumRecorderPrivilegedSites,
            $"the recorder holds {marks} privileged marks, fewer than the {MinimumRecorderPrivilegedSites} it is known to hold");
        Assert.True(
            AppSources().Count >= MinimumAppSourceFiles,
            "the census read too few application sources to have measured anything");
    }

    [Fact]
    public void ThePrivilegedMarkReading_CatchesABypassAndAcceptsTheRecorder()
    {
        // The positive control, over text written here: the bypass in each shape this repository
        // has used, and the one shape that must stay allowed.
        (string Name, string Text)[] bypass =
        [
            ("Bypass.cs", "await _sudoEmitter.RunMkdirAsync(path, op, privileged: true);"),
        ];
        (string Name, string Text)[] positional =
        [
            ("Positional.cs", "await emitter.RunDeleteAsync(path, op, true);"),
        ];
        (string Name, string Text)[] refusal =
        [
            ("Refusal.cs", "await _sudoEmitter.RunChmodAsync(path, op, privileged: false);"),
        ];
        (string Name, string Text)[] commented =
        [
            ("Commented.cs", "// await _sudoEmitter.RunMkdirAsync(path, op, privileged: true);"),
        ];

        Assert.NotEmpty(PrivilegedMarkerOffenders(bypass));
        Assert.Single(PrivilegedMarkerOffenders(positional));
        Assert.Empty(PrivilegedMarkerOffenders(refusal));
        Assert.Empty(PrivilegedMarkerOffenders(commented));
    }

    // G2 ---------------------------------------------------------------------------------------

    [Fact]
    public void EverySudoPrimitiveIsCalledInsideItsRecorderCall()
    {
        IReadOnlyList<string> offenders = PrimitiveOffenders(AppSources());

        Assert.True(
            offenders.Count == 0,
            "a sudo primitive is called outside any recorder call, so the root badge would not "
                + "see the act: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheSudoPrimitiveCensus_ReadsWhatItClaimsTo()
    {
        int calls = PrimitiveCallCount(AppSources());

        Assert.True(
            calls >= MinimumPrimitiveCalls,
            $"only {calls} sudo primitive call(s) were found, so the census measured nothing");
    }

    [Fact]
    public void ThePrimitiveReading_CatchesACallOutsideTheRecorderAndAcceptsOneInside()
    {
        (string Name, string Text)[] outside =
        [
            ("Outside.cs", Member("Escalate", "await RunSudoCommandAsync(body, ct);")),
        ];
        (string Name, string Text)[] inside =
        [
            ("Inside.cs", Member("Escalate", "await _privilegedActs.RunMkdirAsync(path, () => RunSudoCommandAsync(body, ct));")),
        ];
        (string Name, string Text)[] seam =
        [
            ("Seam.cs", Member("RunSudoDeleteCommandAsync", "return x is null ? RunSudoCommandAsync(command, ct) : x(command, ct);")),
        ];
        (string Name, string Text)[] declaration =
        [
            ("Declaration.cs", "    internal async Task RunSudoCommandAsync(string body, CancellationToken ct)\n    {\n    }\n"),
        ];
        (string Name, string Text)[] commented =
        [
            ("Commented.cs", Member("Escalate", "// await RunSudoCommandAsync(body, ct);")),
        ];

        (string Name, string Text)[] otherFile =
        [
            ("SomewhereElse.cs", Member("Escalate", "await _editor.EditFileSudoAsync(path, parameters);")),
        ];
        (string Name, string Text)[] probe =
        [
            ("Probe.cs", Member("ProbeSudoTargetKindAsync", "var r = await ExecuteSudoRenameCommandAsync(body, ct);")),
        ];
        (string Name, string Text)[] newRenameCaller =
        [
            ("RenameCaller.cs", Member("SneakyRename", "var r = await ExecuteSudoRenameCommandAsync(body, ct);")),
        ];
        (string Name, string Text)[] noModifier =
        [
            ("NoModifier.cs", "    private async Task Above()\n    {\n    }\n\n    async Task Below()\n    {\n        await RunSudoCommandAsync(body, ct);\n    }\n"),
        ];
        (string Name, string Text)[] nested =
        [
            ("Nested.cs", "    private sealed class Inner\n    {\n        public async Task Run()\n        {\n            await RunSudoCommandAsync(body, ct);\n        }\n    }\n"),
        ];
        (string Name, string Text)[] qualifiedDeclaration =
        [
            ("Qualified.cs", "    private async Task<Renci.SshNet.SshCommand> ExecuteSudoRenameCommandAsync(string body)\n    {\n    }\n"),
        ];

        Assert.Single(PrimitiveOffenders(outside));
        Assert.Single(PrimitiveOffenders(otherFile));
        Assert.Empty(PrimitiveOffenders(probe));
        Assert.Single(PrimitiveOffenders(newRenameCaller));
        Assert.Single(PrimitiveOffenders(noModifier));
        Assert.Single(PrimitiveOffenders(nested));
        Assert.Empty(PrimitiveOffenders(qualifiedDeclaration));
        Assert.Empty(PrimitiveOffenders(inside));
        Assert.Empty(PrimitiveOffenders(seam));
        Assert.Empty(PrimitiveOffenders(declaration));
        Assert.Empty(PrimitiveOffenders(commented));
        Assert.Equal(1, PrimitiveCallCount(inside));
        Assert.Equal(0, PrimitiveCallCount(declaration));
    }

    // G3 ---------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheKnownMembersOpenAPrivilegedSshClient()
    {
        IReadOnlyList<string> actual = MembersCalling("CreateSudoSshClientAsync(", AppSources());

        Assert.True(
            actual.SequenceEqual(s_expectedSudoClientMembers),
            "the members that open a privileged SSH client changed. A new one is an escalation site the "
                + "root badge may not see: add it to the recorder, then to this list. Found: "
                + string.Join(", ", actual));
        Assert.Equal(ExpectedSudoClientMembers, actual.Count);
    }

    [Fact]
    public void TheMemberReading_NamesTheMemberThatCallsAndIgnoresComments()
    {
        (string Name, string Text)[] caller =
        [
            ("Caller.cs", Member("Escalate", "using var ssh = await CreateSudoSshClientAsync(ct);")),
        ];
        (string Name, string Text)[] commented =
        [
            ("Commented.cs", Member("Escalate", "// using var ssh = await CreateSudoSshClientAsync(ct);")),
        ];

        Assert.Equal("Escalate", Assert.Single(MembersCalling("CreateSudoSshClientAsync(", caller)));
        Assert.Empty(MembersCalling("CreateSudoSshClientAsync(", commented));
    }

    [Fact]
    public void OnlyTheKnownMembersRunASudoBodyOrBuildItsInvocation()
    {
        IReadOnlyList<string> executors = MembersCalling("ExecuteSudoBodyAsync(", AppSources());
        IReadOnlyList<string> builders = MembersCalling("BuildSudoInvocation(", AppSources());

        Assert.True(
            executors.SequenceEqual(s_expectedBodyExecutors),
            "the members that run a sudo body changed. A new one builds its own client and runs as root "
                + "without the root badge: add it to the recorder, then to this list. Found: "
                + string.Join(", ", executors));
        Assert.True(
            builders.SequenceEqual(s_expectedInvocationBuilders),
            "the members that build a sudo command line changed. Found: " + string.Join(", ", builders));
    }

    [Fact]
    public void TheBodyReading_FlagsANewMemberThatRunsASudoBody()
    {
        (string Name, string Text)[] sneaky =
        [
            ("Sneaky.cs", Member("RunItMyself", "using var c = await ExecuteSudoBodyAsync(ssh, body, ct);")),
        ];

        Assert.Equal("RunItMyself", Assert.Single(MembersCalling("ExecuteSudoBodyAsync(", sneaky)));
    }

    // G4 ---------------------------------------------------------------------------------------

    [Fact]
    public void PrivilegedExecutionLivesOnlyInTheKnownMembers()
    {
        IReadOnlyList<string> actual = PrivilegedExecutionMembers(AllSources());

        Assert.True(
            actual.SequenceEqual(s_expectedPrivilegedExecutionMembers),
            "code outside the known members runs a privileged command or write. If it is an act the "
                + "pane runs as root, it needs the recorder. Found: " + string.Join(", ", actual));
    }

    [Fact]
    public void ThePrivilegedExecutionReading_ReadsTheSftpAssemblyToo()
    {
        IReadOnlyList<(string Name, string Text)> sources = AllSources();

        Assert.True(
            SourceFileExists(sources, EditorFile),
            "the editor source was not read, so the privileged execution census missed Heimdall.Sftp");
        Assert.True(
            SourceFileExists(sources, TransferFile),
            "the privileged transfer source was not read");
        Assert.Single(
            PrivilegedExecutionMembers(
            [
                ("Other.cs", Member("Sneak", "await PrivilegedFileTransfer.ExecuteCommandAsync(ssh, body, null, ct);")),
            ]));
    }

    // G5 ---------------------------------------------------------------------------------------

    [Fact]
    public void TheEditorSudoSaveSignalIsRecordedByTheRecorder()
    {
        Assert.True(
            RecordsAsAStepOfTheTryBlock(ViewSources(), "OnEditorSudoSaveCompleted"),
            "OnEditorSudoSaveCompleted no longer stands on the recorder call as a step of its try block");
    }

    [Fact]
    public void TheSignalReading_IsNotSatisfiedByAFoldedCallACommentOrAnotherMember()
    {
        const string Call = "_privilegedActs.RecordEditorSave(a, b, c, d);";
        (string Name, string Text)[] present = [("Present.cs", SaveHandler(Call))];
        (string Name, string Text)[] folded = [("Folded.cs", SaveHandler("if (false) " + Call))];
        (string Name, string Text)[] foldedInBlock = [("FoldedBlock.cs", SaveHandler("if (false) { " + Call + " }"))];
        (string Name, string Text)[] commented = [("Commented.cs", SaveHandler("// " + Call))];
        (string Name, string Text)[] elsewhere =
        [
            ("Elsewhere.cs", Member("OnOther", "try { " + Call + " } catch (Exception) { }")
                + Member("OnEditorSudoSaveCompleted", "Log();")),
        ];

        Assert.True(RecordsAsAStepOfTheTryBlock(present, "OnEditorSudoSaveCompleted"));
        Assert.False(RecordsAsAStepOfTheTryBlock(folded, "OnEditorSudoSaveCompleted"));
        Assert.False(RecordsAsAStepOfTheTryBlock(foldedInBlock, "OnEditorSudoSaveCompleted"));
        Assert.False(RecordsAsAStepOfTheTryBlock(commented, "OnEditorSudoSaveCompleted"));
        Assert.False(RecordsAsAStepOfTheTryBlock(elsewhere, "OnEditorSudoSaveCompleted"));
    }

    // Readings ---------------------------------------------------------------------------------

    private static string SaveHandler(string inner)
        => Member("OnEditorSudoSaveCompleted", "try { " + inner + " } catch (Exception) { }");

    private static string Member(string name, string body)
        => "    private async Task " + name + "()\n    {\n        " + body + "\n    }\n\n";

    private static IReadOnlyList<string> PrivilegedMarkerOffenders(IReadOnlyList<(string Name, string Text)> sources)
    {
        List<string> offenders = [];
        foreach ((string name, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            string file = Path.GetFileName(name);
            if (file is RecorderFile or EmitterFile)
            {
                continue;
            }

            if (s_privilegedTrue.IsMatch(logic))
            {
                offenders.Add(file + ": privileged: true");
            }

            foreach (Match call in s_emitterRunCall.Matches(logic))
            {
                if (call.Groups[1].Value == RecorderMember)
                {
                    continue;
                }

                string arguments = CallArguments(logic, call.Index + call.Length - 1);
                if (!Regex.IsMatch(arguments, @"\bprivileged\s*:\s*false\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
                {
                    offenders.Add(file + ": a direct journal call that is not marked privileged: false");
                }
            }

            if (logic.Contains("EmitUploadCompleted(", StringComparison.Ordinal))
            {
                offenders.Add(file + ": EmitUploadCompleted");
            }
        }

        return offenders;
    }

    private static int PrivilegedMarkCount(IReadOnlyList<(string Name, string Text)> sources, string file)
        => sources
            .Where(source => Path.GetFileName(source.Name) == file)
            .Sum(source => s_privilegedTrue.Matches(ViewSource.WithoutCommentsAndLiterals(source.Text)).Count);

    private static IReadOnlyList<string> PrimitiveOffenders(IReadOnlyList<(string Name, string Text)> sources)
    {
        List<string> offenders = [];
        foreach ((string name, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            List<(int Start, int End)> spans = RecorderCallSpans(logic);
            foreach ((int index, string primitive) in PrimitiveCalls(logic))
            {
                if (spans.Any(span => index > span.Start && index < span.End))
                {
                    continue;
                }

                string member = MemberNameAt(logic, index);
                if (s_primitiveOutsideCallers.TryGetValue(primitive, out string[]? allowed) && allowed.Contains(member))
                {
                    continue;
                }

                offenders.Add(Path.GetFileName(name) + ": " + member + " calls " + primitive);
            }
        }

        return offenders;
    }

    private static int PrimitiveCallCount(IReadOnlyList<(string Name, string Text)> sources)
        => sources.Sum(source => PrimitiveCalls(ViewSource.WithoutCommentsAndLiterals(source.Text)).Count);

    // Every call of a primitive: the declarations are not calls.
    private static List<(int Index, string Primitive)> PrimitiveCalls(string logic)
    {
        List<(int Index, string Primitive)> calls = [];
        foreach (string primitive in s_primitives)
        {
            string token = primitive + "(";
            int index = 0;
            while ((index = logic.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                int position = index;
                index += token.Length;
                if (position > 0 && (char.IsLetterOrDigit(logic[position - 1]) || logic[position - 1] == '_'))
                {
                    continue;
                }

                if (!IsDeclaration(logic, position))
                {
                    calls.Add((position, primitive));
                }
            }
        }

        return calls;
    }

    // A declaration has only modifiers and a type before the name; a call has an operator, a
    // keyword or an opening parenthesis there.
    private static bool IsDeclaration(string logic, int nameStart)
    {
        int lineStart = logic.LastIndexOf('\n', Math.Max(0, nameStart - 1)) + 1;
        string prefix = logic[lineStart..nameStart].Trim();

        // A generic return type may be qualified (Task<Renci.SshNet.SshCommand>): its arguments are
        // not part of the question, so they are taken out first.
        string stripped = Regex.Replace(prefix, "<[^<>]*>", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
        while (!string.Equals(stripped, prefix, StringComparison.Ordinal))
        {
            prefix = stripped;
            stripped = Regex.Replace(prefix, "<[^<>]*>", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
        }

        if (prefix.Length == 0 || prefix.IndexOfAny(['(', '=', '?', ':', ';', '{', '.']) >= 0)
        {
            return false;
        }

        return !Regex.IsMatch(prefix, @"\b(?:await|return|new|throw)\b", RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    // The argument lists of the recorder's methods, where a primitive is allowed to appear.
    private static List<(int Start, int End)> RecorderCallSpans(string logic)
    {
        List<(int Start, int End)> spans = [];
        const string Token = "_privilegedActs.Run";
        int index = 0;
        while ((index = logic.IndexOf(Token, index, StringComparison.Ordinal)) >= 0)
        {
            int open = logic.IndexOf('(', index);
            if (open < 0)
            {
                break;
            }

            spans.Add((open, CallEnd(logic, open)));
            index = open + 1;
        }

        return spans;
    }

    private static int CallEnd(string logic, int open)
    {
        int depth = 0;
        for (int cursor = open; cursor < logic.Length; cursor++)
        {
            char current = logic[cursor];
            if (current is '(' or '[' or '{')
            {
                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return cursor;
                }
            }
        }

        return logic.Length;
    }

    private static string CallArguments(string logic, int open)
        => logic[open..Math.Min(logic.Length, CallEnd(logic, open) + 1)];

    // The name of the member the offset sits in, read from the nearest line at the members' own
    // indent. It fails closed: a member with no access modifier, or one inside a nested type, is
    // not named after the member above it but reported as unnamed, which no allow-list holds.
    private static string MemberNameAt(string logic, int index)
    {
        string[] lines = logic[..Math.Min(index, logic.Length)].Split('\n');
        for (int cursor = lines.Length - 1; cursor >= 0; cursor--)
        {
            string line = lines[cursor].TrimEnd('\r');
            if (!line.StartsWith("    ", StringComparison.Ordinal)
                || line.StartsWith("     ", StringComparison.Ordinal)
                || line.Trim().Length == 0)
            {
                continue;
            }

            string header = line.Trim();
            if (header.StartsWith('}') || header.StartsWith('{') || header.StartsWith('[') || header.StartsWith(')'))
            {
                continue;
            }

            if (!s_memberStart.IsMatch(line + "\n")
                || Regex.IsMatch(header, @"\b(?:class|struct|record|interface|enum)\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                return UnnamedMember;
            }

            Match name = s_memberName.Match(header);
            return name.Success ? name.Groups[1].Value : UnnamedMember;
        }

        return UnnamedMember;
    }

    private static IReadOnlyList<string> MembersCalling(string call, IReadOnlyList<(string Name, string Text)> sources)
    {
        SortedSet<string> members = new(StringComparer.Ordinal);
        string name = call.TrimEnd('(');
        foreach ((_, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            int index = 0;
            while ((index = logic.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
            {
                int position = index;
                index += call.Length;
                if (IsDeclaration(logic, position))
                {
                    continue;
                }

                string member = MemberNameAt(logic, position);
                if (member != name)
                {
                    _ = members.Add(member);
                }
            }
        }

        return members.ToList();
    }

    private static IReadOnlyList<string> PrivilegedExecutionMembers(IReadOnlyList<(string Name, string Text)> sources)
    {
        SortedSet<string> members = new(StringComparer.Ordinal);
        string[] calls =
        [
            "PrivilegedFileTransfer.ExecuteCommandAsync(",
            "PrivilegedFileTransfer.ExecuteAtomicWriteAsync(",
        ];
        foreach ((string name, string text) in sources)
        {
            if (Path.GetFileName(name) == TransferFile)
            {
                continue;
            }

            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            foreach (string call in calls)
            {
                int index = 0;
                while ((index = logic.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
                {
                    _ = members.Add(MemberNameAt(logic, index));
                    index += call.Length;
                }
            }
        }

        return members.ToList();
    }

    // The recorder call must stand as a statement of the try block that opens the member's body.
    // Written through the repository's statement chain, so a call folded behind a condition keeps
    // its text and loses the shape.
    private static bool RecordsAsAStepOfTheTryBlock(IReadOnlyList<(string Name, string Text)> sources, string member)
    {
        foreach ((_, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            if (logic.IndexOf(" " + member + "(", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            try
            {
                string body = ViewSource.HandlerBody(logic, " " + member + "(");
                _ = SourceStatements.AssertStatementChain(body, "try", "_privilegedActs.RecordEditorSave(");
                return true;
            }
            catch (Xunit.Sdk.TrueException)
            {
                // The shape is not there.
            }
            catch (InvalidOperationException)
            {
                // The anchor of the chain was not found as a statement.
            }
        }

        return false;
    }

    private static int CallsInMember(
        IReadOnlyList<(string Name, string Text)> sources,
        string member,
        string call)
    {
        int count = 0;
        foreach ((_, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            int signature = logic.IndexOf(" " + member + "(", StringComparison.Ordinal);
            if (signature < 0)
            {
                continue;
            }

            string body = ViewSource.HandlerBody(logic, " " + member + "(");
            int index = 0;
            while ((index = body.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += call.Length;
            }
        }

        return count;
    }

    private static bool SourceFileExists(IReadOnlyList<(string Name, string Text)> sources, string file)
        => sources.Any(source => Path.GetFileName(source.Name) == file);

    // Sources --------------------------------------------------------------------------------------

    private static IReadOnlyList<(string Name, string Text)> AppSources()
        => ReadSources(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App"));

    private static IReadOnlyList<(string Name, string Text)> AllSources()
        => ReadSources(Path.Combine(ViewSource.RepoRoot(), "src"));

    private static IReadOnlyList<(string Name, string Text)> PaneSources()
    {
        string root = ViewSource.RepoRoot();
        List<(string Name, string Text)> sources = [];
        string viewModels = Path.Combine(root, "src", "Heimdall.App", "ViewModels");
        sources.AddRange(Directory
            .EnumerateFiles(viewModels, "EmbeddedSftpViewModel*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllText(path))));
        sources.AddRange(ViewSources());
        return sources;
    }

    private static IReadOnlyList<(string Name, string Text)> ViewSources()
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", ViewFile);
        return [(path, File.ReadAllText(path))];
    }

    private static IReadOnlyList<(string Name, string Text)> ReadSources(string root)
    {
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllText(path)))
            .ToList();
    }

    private static bool IsBuildOutput(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal)
            || relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal);
    }
}
