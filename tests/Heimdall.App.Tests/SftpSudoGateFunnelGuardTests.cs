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
/// Every place the SFTP pane retries a refused operation as root asks the gate first, so the
/// setting and the question cannot be bypassed by a new escalation site.
/// </summary>
/// <remarks>
/// <para>
/// The readings are census counts made inside helper members, in the shape of
/// <see cref="SftpRootActivityFunnelGuardTests"/>: a test member asserts a number or an empty
/// list, never a fragment of production text, and each reading has a floor (so it is not green on
/// a file list that reads nothing) and a positive control over text written here (so it is not
/// green on a matcher that matches nothing). All readings run over source with comments and
/// literals blanked.
/// </para>
/// <para>
/// <b>What these guards do NOT catch</b>: that a gate call RUNS, only that it is written inside the
/// catch or the member before the escalation (a gate call folded behind a false condition keeps
/// its text); a sudo call made through a delegate, an event or reflection; a gate call placed in an
/// unrelated catch of the same member (the member reading is looser than the catch reading, which
/// is why both exist); and an escalation site that does not catch a permission refusal at all.
/// The behaviour of each site is covered by <see cref="EmbeddedSftpSudoConsentTests"/>.
/// </para>
/// </remarks>
public sealed class SftpSudoGateFunnelGuardTests
{
    private const string ViewFile = "EmbeddedSftpView.xaml.cs";
    private const string LoadMember = "Task<LoadDirectoryOutcome> LoadDirectoryCoreAsync";

    private const int MinimumPermissionCatches = 11;
    private const int MinimumEscalationCalls = 8;
    private const int ExpectedListingEscalationsInTheLoad = 2;
    private const int ExpectedBackgroundMembers = 3;

    private static readonly string[] s_gateCalls = ["RequireSudoAsync(", "DecideSudoAsync("];

    // What reaches the privileged channel from the pane, in the order a catch would write it.
    private static readonly string[] s_escalations =
    [
        "_privilegedActs.Run",
        "UploadViaSudoAsync(",
        "DownloadViaSudoAsync(",
        "ListDirectoryViaSudoAsync(",
        "RenameEntryViaSudoAsync(",
        "EditFileSudoAsync(",
    ];

    // The members that are the escalation itself: they are what the gate protects, not callers of it.
    private static readonly string[] s_escalationMembers =
    [
        "UploadViaSudoAsync",
        "DownloadViaSudoAsync",
        "ListDirectoryViaSudoAsync",
        "RenameEntryViaSudoAsync",
    ];

    private static readonly string[] s_backgroundMembers =
    [
        "NavigateToPathCore",
        "NavigateInitialAsync",
        "RefreshAfterOperationAsync",
    ];

    private static readonly Regex s_memberStart = new(
        @"(?m)^    (?:private|public|internal|protected)\s", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex s_memberName = new(
        @"(\w+)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5));

    // G1 ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryPermissionRefusalCatch_AsksTheGateBeforeAnyEscalation()
    {
        IReadOnlyList<string> offenders = CatchOffenders(PaneSources());

        Assert.True(
            offenders.Count == 0,
            "a catch of a permission refusal reaches the privileged channel without asking the gate "
                + "first, so the setting and the question would be skipped: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ThePermissionRefusalCensus_ReadsWhatItClaimsTo()
    {
        int catches = CatchCount(PaneSources());

        Assert.True(
            catches >= MinimumPermissionCatches,
            $"only {catches} permission refusal catch(es) were found, fewer than the {MinimumPermissionCatches} known sites");
    }

    [Fact]
    public void TheCatchReading_FlagsAnUngatedCatchAndAGateThatComesTooLate_AndAcceptsAGatedOne()
    {
        (string Name, string Text)[] ungated =
        [
            ("Ungated.cs", Member("Site", "try { A(); } catch (Exception ex) when (IsPermissionDenied(ex)) { await UploadViaSudoAsync(p, r, ct); }")),
        ];
        (string Name, string Text)[] late =
        [
            ("Late.cs", Member("Site", "try { A(); } catch (Exception ex) when (IsPermissionDenied(ex)) { await UploadViaSudoAsync(p, r, ct); await RequireSudoAsync(k, p, s, ex); }")),
        ];
        (string Name, string Text)[] gatedOnlyInAnotherCatch =
        [
            ("Other.cs", Member("Site", "try { A(); } catch (IOException) { await RequireSudoAsync(k, p, s, ex); } catch (Exception ex) when (IsPermissionDenied(ex)) { await UploadViaSudoAsync(p, r, ct); }")),
        ];
        (string Name, string Text)[] gated =
        [
            ("Gated.cs", Member("Site", "try { A(); } catch (Exception ex) when (IsPermissionDenied(ex)) { await RequireSudoAsync(k, p, s, ex); await UploadViaSudoAsync(p, r, ct); }")),
        ];
        (string Name, string Text)[] gatedByDecision =
        [
            ("Decided.cs", Member("Site", "try { A(); } catch (Exception ex) when (_s is not null && IsPermissionDenied(ex)) { var d = await DecideSudoAsync(k, p, s, ct); await _privilegedActs.RunDeleteAsync(p, op); }")),
        ];
        (string Name, string Text)[] commented =
        [
            ("Commented.cs", Member("Site", "try { A(); } catch (Exception ex) when (IsPermissionDenied(ex)) { // RequireSudoAsync(k, p, s, ex);\n await UploadViaSudoAsync(p, r, ct); }")),
        ];

        Assert.Single(CatchOffenders(ungated));
        Assert.Single(CatchOffenders(late));
        Assert.Single(CatchOffenders(gatedOnlyInAnotherCatch));
        Assert.Single(CatchOffenders(commented));
        Assert.Empty(CatchOffenders(gated));
        Assert.Empty(CatchOffenders(gatedByDecision));
        Assert.Equal(1, CatchCount(gated));
    }

    // G2 ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryCallOfTheSudoPrimitives_StandsInAMemberThatAskedTheGateBeforeIt()
    {
        IReadOnlyList<string> offenders = MemberOffenders(PaneSources());

        Assert.True(
            offenders.Count == 0,
            "a member escalates to root without asking the gate earlier in its own text: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void TheEscalationCensus_ReadsWhatItClaimsTo()
    {
        int calls = EscalationCallCount(PaneSources());

        Assert.True(
            calls >= MinimumEscalationCalls,
            $"only {calls} escalation call(s) were found, so the census measured nothing");
    }

    [Fact]
    public void TheMemberReading_FlagsACallWithNoGateAndAcceptsOneAfterTheGate()
    {
        (string Name, string Text)[] bare =
        [
            ("Bare.cs", Member("Sneaky", "await UploadViaSudoAsync(p, r, ct);")),
        ];
        (string Name, string Text)[] viaOtherObject =
        [
            ("Other.cs", Member("Sneaky", "await _viewModel.UploadViaSudoAsync(p, r, ct);")),
        ];
        (string Name, string Text)[] gatedAfter =
        [
            ("After.cs", Member("Sneaky", "await UploadViaSudoAsync(p, r, ct); await RequireSudoAsync(k, p, s, ex);")),
        ];
        (string Name, string Text)[] gated =
        [
            ("Gated.cs", Member("Fine", "await RequireSudoAsync(k, p, s, ex); await UploadViaSudoAsync(p, r, ct);")),
        ];
        (string Name, string Text)[] declaration =
        [
            ("Declaration.cs", "    internal async Task UploadViaSudoAsync(string p, string r)\n    {\n    }\n\n"),
        ];
        (string Name, string Text)[] recorder =
        [
            ("Recorder.cs", Member("Sneaky", "await _privilegedActs.RunMkdirAsync(path, op);")),
        ];

        Assert.Single(MemberOffenders(bare));
        Assert.Single(MemberOffenders(viaOtherObject));
        Assert.Single(MemberOffenders(gatedAfter));
        Assert.Single(MemberOffenders(recorder));
        Assert.Empty(MemberOffenders(gated));
        Assert.Empty(MemberOffenders(declaration));
        Assert.Equal(0, EscalationCallCount(declaration));
        Assert.Equal(1, EscalationCallCount(gated));
    }

    // G3 ---------------------------------------------------------------------------------------

    [Fact]
    public void TheListingLoad_AsksTheGateForEachOfItsTwoWaysToList()
    {
        int escalations = CallsInMember(PaneSources(), LoadMember, "ListDirectoryViaSudoAsync(");
        int gates = s_gateCalls.Sum(call => CallsInMember(PaneSources(), LoadMember, call));

        Assert.Equal(ExpectedListingEscalationsInTheLoad, escalations);
        Assert.Equal(ExpectedListingEscalationsInTheLoad, gates);
    }

    [Fact]
    public void TheListingsTheUserDidNotStart_SayBackgroundAndNothingElseDoes()
    {
        int members = BackgroundMembers(PaneSources()).Count;
        IReadOnlyList<string> unexpected = BackgroundMembers(PaneSources())
            .Where(member => !s_backgroundMembers.Contains(member))
            .ToList();

        Assert.Equal(ExpectedBackgroundMembers, members);
        Assert.Empty(unexpected);
    }

    [Fact]
    public void OnlyTheSudoButtonRefreshesAsTheUser_EveryOtherRefreshOfThePaneIsBackground()
    {
        IReadOnlyList<string> callers = MembersCalling(PaneViewModelSources(), "Refresh()");

        Assert.Equal("ToggleSudoMode", Assert.Single(callers));
    }

    [Fact]
    public void TheBackgroundReading_SeesAMemberThatNamesTheInitiator()
    {
        (string Name, string Text)[] named =
        [
            ("Named.cs", Member("Quiet", "await LoadDirectoryCoreAsync(p, false, initiator: SftpSudoInitiator.Background);")),
        ];
        (string Name, string Text)[] unnamed =
        [
            ("Unnamed.cs", Member("Loud", "await LoadDirectoryCoreAsync(p, false);")),
        ];

        Assert.Equal("Quiet", Assert.Single(BackgroundMembers(named)));
        Assert.Empty(BackgroundMembers(unnamed));
        Assert.Equal("Caller", Assert.Single(MembersCalling(
            [("Calls.cs", Member("Caller", "await Refresh().ConfigureAwait(false);"))],
            "Refresh()")));
    }

    // Readings ---------------------------------------------------------------------------------

    private static string Member(string name, string body)
        => "    private async Task " + name + "()\n    {\n        " + body + "\n    }\n\n";

    // Every catch whose filter names IsPermissionDenied, with its body.
    private static List<(string File, string Member, string Body)> PermissionCatches(
        IReadOnlyList<(string Name, string Text)> sources)
    {
        List<(string File, string Member, string Body)> catches = [];
        foreach ((string name, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            int index = 0;
            while ((index = logic.IndexOf("IsPermissionDenied(", index, StringComparison.Ordinal)) >= 0)
            {
                int position = index;
                index += "IsPermissionDenied(".Length;
                int catchStart = logic.LastIndexOf("catch", position, StringComparison.Ordinal);
                if (catchStart < 0 || !IsInsideCatchHeader(logic, catchStart, position))
                {
                    continue;
                }

                int open = logic.IndexOf('{', position);
                if (open < 0)
                {
                    continue;
                }

                catches.Add((Path.GetFileName(name), MemberNameAt(logic, position), logic[open..(BlockEnd(logic, open) + 1)]));
            }
        }

        return catches;
    }

    // The header runs from "catch" to the block's brace: no statement ends inside it.
    private static bool IsInsideCatchHeader(string logic, int catchStart, int position)
        => logic.AsSpan(catchStart, position - catchStart).IndexOfAny(';', '{', '}') < 0;

    private static int CatchCount(IReadOnlyList<(string Name, string Text)> sources)
        => PermissionCatches(sources).Count;

    private static IReadOnlyList<string> CatchOffenders(IReadOnlyList<(string Name, string Text)> sources)
    {
        List<string> offenders = [];
        foreach ((string file, string member, string body) in PermissionCatches(sources))
        {
            int gate = FirstIndexOf(body, s_gateCalls);
            int escalation = FirstIndexOf(body, s_escalations);
            if (gate < 0)
            {
                offenders.Add($"{file}: {member} never asks the gate");
            }
            else if (escalation >= 0 && escalation < gate)
            {
                offenders.Add($"{file}: {member} escalates before it asks the gate");
            }
        }

        return offenders;
    }

    private static int FirstIndexOf(string text, IReadOnlyList<string> tokens)
    {
        int first = -1;
        foreach (string token in tokens)
        {
            int index = text.IndexOf(token, StringComparison.Ordinal);
            if (index >= 0 && (first < 0 || index < first))
            {
                first = index;
            }
        }

        return first;
    }

    private static IReadOnlyList<string> MemberOffenders(IReadOnlyList<(string Name, string Text)> sources)
    {
        List<string> offenders = [];
        foreach ((string name, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            foreach ((int position, string token) in EscalationCalls(logic))
            {
                string member = MemberNameAt(logic, position);
                if (s_escalationMembers.Contains(member))
                {
                    continue;
                }

                string before = logic[MemberStart(logic, position)..position];
                if (FirstIndexOf(before, s_gateCalls) < 0)
                {
                    offenders.Add($"{Path.GetFileName(name)}: {member} calls {token.TrimEnd('(')}");
                }
            }
        }

        return offenders;
    }

    private static int EscalationCallCount(IReadOnlyList<(string Name, string Text)> sources)
        => sources.Sum(source => EscalationCalls(ViewSource.WithoutCommentsAndLiterals(source.Text)).Count);

    // Every call of an escalation: a declaration has only modifiers and a type before the name.
    private static List<(int Index, string Token)> EscalationCalls(string logic)
    {
        List<(int Index, string Token)> calls = [];
        foreach (string token in s_escalations)
        {
            int index = 0;
            while ((index = logic.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                int position = index;
                index += token.Length;
                if (position > 0 && (char.IsLetterOrDigit(logic[position - 1]) || logic[position - 1] == '_'))
                {
                    continue;
                }

                if (IsCall(logic, position))
                {
                    calls.Add((position, token));
                }
            }
        }

        return calls;
    }

    private static bool IsCall(string logic, int position)
    {
        int lineStart = logic.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        string prefix = logic[lineStart..position];
        return position > 0 && logic[position - 1] == '.'
            || Regex.IsMatch(prefix, @"\b(?:await|return)\b|=>", RegexOptions.None, TimeSpan.FromSeconds(5));
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
            if (logic.IndexOf(" " + member + "(", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string body = ViewSource.HandlerBody(logic, " " + member + "(");
            int index = 0;
            while ((index = body.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
            {
                if (index == 0 || !(char.IsLetterOrDigit(body[index - 1]) || body[index - 1] == '_'))
                {
                    count++;
                }

                index += call.Length;
            }
        }

        return count;
    }

    // The members whose text says the listing was not started by the user.
    private static IReadOnlyList<string> BackgroundMembers(IReadOnlyList<(string Name, string Text)> sources)
    {
        SortedSet<string> members = new(StringComparer.Ordinal);
        foreach ((_, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            const string Token = "SftpSudoInitiator.Background";
            int index = 0;
            while ((index = logic.IndexOf(Token, index, StringComparison.Ordinal)) >= 0)
            {
                _ = members.Add(MemberNameAt(logic, index));
                index += Token.Length;
            }
        }

        return members.ToList();
    }

    // The members that call the pane's Refresh command (the call, not its declaration).
    private static IReadOnlyList<string> MembersCalling(
        IReadOnlyList<(string Name, string Text)> sources,
        string call)
    {
        List<string> members = [];
        foreach ((_, string text) in sources)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(text);
            int index = 0;
            while ((index = logic.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
            {
                int position = index;
                index += call.Length;
                if (position > 0 && (char.IsLetterOrDigit(logic[position - 1]) || logic[position - 1] == '_'))
                {
                    continue;
                }

                if (IsCall(logic, position))
                {
                    members.Add(MemberNameAt(logic, position));
                }
            }
        }

        return members;
    }

    private static int MemberStart(string logic, int index)
    {
        int start = 0;
        foreach (Match match in s_memberStart.Matches(logic[..Math.Min(index, logic.Length)]))
        {
            start = match.Index;
        }

        return start;
    }

    private static string MemberNameAt(string logic, int index)
    {
        int start = MemberStart(logic, index);
        int lineEnd = logic.IndexOf('\n', start);
        string header = logic[start..(lineEnd < 0 ? logic.Length : lineEnd)].Trim();
        Match name = s_memberName.Match(header);
        return name.Success ? name.Groups[1].Value : "<unnamed member>";
    }

    private static int BlockEnd(string logic, int open)
    {
        int depth = 0;
        for (int cursor = open; cursor < logic.Length; cursor++)
        {
            if (logic[cursor] == '{')
            {
                depth++;
            }
            else if (logic[cursor] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return cursor;
                }
            }
        }

        return logic.Length - 1;
    }

    // Sources --------------------------------------------------------------------------------------

    private static IReadOnlyList<(string Name, string Text)> PaneViewModelSources()
    {
        string viewModels = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "ViewModels");
        Assert.True(Directory.Exists(viewModels), $"Source directory not found: {viewModels}");
        return Directory
            .EnumerateFiles(viewModels, "EmbeddedSftpViewModel*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllText(path)))
            .ToList();
    }

    private static IReadOnlyList<(string Name, string Text)> PaneSources()
    {
        List<(string Name, string Text)> sources = [.. PaneViewModelSources()];
        string view = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", ViewFile);
        sources.Add((view, File.ReadAllText(view)));
        return sources;
    }
}
