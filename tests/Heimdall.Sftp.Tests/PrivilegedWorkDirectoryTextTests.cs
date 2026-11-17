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

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The write and read scripts create their temporary work directory and check it with one shared
/// procedure before they use it. These tests read the script text; the behaviour is run by
/// <see cref="PrivilegedWorkDirectoryRunTests"/>.
/// </summary>
public sealed class PrivilegedWorkDirectoryTextTests
{
    private const string Target = "/srv/shared/report.txt";

    private const string WriteExitStatus = "73";

    private const string ReadExitStatus = "74";

    /// <summary>The script as the remote shell receives it: the outer quoting undone.</summary>
    internal static string ScriptOf(string command)
    {
        const string opening = "sh -c '";
        const string closing = "' sh '";
        int start = command.IndexOf(opening, StringComparison.Ordinal) + opening.Length;
        int end = command.LastIndexOf(closing, StringComparison.Ordinal);
        return command[start..end].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    private static string WriteScript(bool overwrite = true)
        => ScriptOf(PrivilegedFileCommands.BuildAtomicWriteBody(Target, overwrite));

    private static string ReadScript(long? limit = null)
        => ScriptOf(PrivilegedFileCommands.BuildNoFollowBase64ReadBody(Target, limit));

    private static int IndexOf(string text, string value, int from = 0)
    {
        int index = text.IndexOf(value, from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' is not in the script.");
        return index;
    }

    [Fact]
    public void WriteAndRead_ShareTheSameEntryProcedure()
    {
        string write = WriteScript();
        string read = ReadScript();

        Assert.Contains(PrivilegedFileCommands.VerifiedEntryOpening, write, StringComparison.Ordinal);
        Assert.Contains(PrivilegedFileCommands.VerifiedEntryClosing, write, StringComparison.Ordinal);
        Assert.Contains(PrivilegedFileCommands.VerifiedEntryOpening, read, StringComparison.Ordinal);
        Assert.Contains(PrivilegedFileCommands.VerifiedEntryClosing, read, StringComparison.Ordinal);

        // The staged installer carries the write script whole, so it carries the procedure too.
        Assert.Contains(
            PrivilegedFileCommands.VerifiedEntryClosing,
            PrivilegedFileCommands.StagedInstallScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Write_EntersTheWorkDirectoryOnlyAfterCheckingItAndReachesTheDestinationFromIt()
    {
        string script = WriteScript();

        int parent = IndexOf(script, "cd -P -- \"$dir\" || refuse");
        int armed = IndexOf(script, "trap cleanup EXIT; trap 'exit 129' HUP INT TERM;");
        int create = IndexOf(script, "made=$(mktemp -d -- \"./.heimdall-write.XXXXXXXXXX\") || refuse");
        int enter = IndexOf(script, "cd -P -- \"$made\" || refuse");
        int owner = IndexOf(script, "[ \"$owner\" = \"$me\" ] && [ $((0$perm & 022)) -eq 0 ]");
        int held = IndexOf(script, "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] && [ \"$(stat -c %d .)\" = \"$pdev\" ]");
        int empty = IndexOf(script, "[ -z \"$listing\" ] || refuse");
        int trusted = IndexOf(script, "work=${made#./}; entered=1; target=../$name;");
        int firstUse = IndexOf(script, "if [ -e \"$target\" ]");
        int content = IndexOf(script, "cat > payload;");
        int recheck = IndexOf(script, "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] || refuse changed", content);
        int rename = IndexOf(script, "mv -fT -- payload \"$target\"");

        Assert.True(parent < armed && armed < create && create < enter, "the steps before the entry are out of order.");
        Assert.True(enter < owner && owner < held && held < empty && empty < trusted, "the directory is trusted before it is checked.");
        Assert.True(trusted < firstUse && firstUse < content && content < recheck && recheck < rename, "the destination is used before the directory is trusted.");
    }

    [Fact]
    public void Write_NoLongerNamesTheWorkDirectoryThroughThePathOrChangesItsModeByName()
    {
        string script = WriteScript();

        Assert.DoesNotContain("chmod 700", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cd -- \"$work\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("mktemp -d -- \"$dir", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$dir/", script, StringComparison.Ordinal);

        // The only directory changes: the held parent, the verified work directory, and the root
        // directory the cleanup leaves to.
        string[] actual = Regex.Matches(script, @"\bcd [^;]*;")
            .Select(match => match.Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = new[]
        {
            "cd -P -- \"$dir\" || refuse changed 'the parent directory changed during the check.';",
            "cd -P -- \"$made\" || refuse unverifiable 'the private directory could not be entered.';",
            "cd -P .. 2>/dev/null && [ \"$(stat -c %d:%i .)\" = \"$parent\" ] && rmdir -- \"./$w\" 2>/dev/null || :;",
            "cd /;",
        }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Write_UsesTheDestinationOnlyRelativeToTheHeldDirectory()
    {
        string script = WriteScript();

        // Every use of the entry goes through the variable set from the held directory.
        Assert.Contains("target=../$name;", script, StringComparison.Ordinal);
        Assert.Contains("ln -P -- \"$target\" original", script, StringComparison.Ordinal);
        Assert.Contains("mv -fT -- payload \"$target\"", script, StringComparison.Ordinal);
        Assert.Contains("ln -T -- payload \"$target\"", script, StringComparison.Ordinal);
        Assert.Contains("sync -f ..;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sync -f \"$dir\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ChecksTheHeldParentAgainRightBeforeThePublication()
    {
        string script = WriteScript();

        int removal = IndexOf(script, "rm -f -- original;");
        int recheck = IndexOf(script, "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] || refuse changed 'the parent directory changed during the write.';");
        int linkCheck = IndexOf(script, "if [ -L \"$target\" ]");
        int publish = IndexOf(script, "mv -fT -- payload \"$target\"");

        Assert.True(removal < recheck && recheck < linkCheck && linkCheck < publish);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scripts_RemoveOnlyWhatTheyMadeAndOnlyAfterTheDirectoryIsTrusted(bool write)
    {
        string script = write ? WriteScript() : ReadScript();
        string file = write ? "original payload" : "source";

        // Gated on the flag set after the check, cleared when used, so a second run does nothing.
        Assert.Contains(
            $"cleanup() {{ if [ \"$entered\" = 1 ]; then rm -f -- {file} || :; rmdir -- claim 2>/dev/null || :; entered=0; fi; "
            + "if [ -n \"$work\" ]; then w=$work; work=; cd -P .. 2>/dev/null "
            + "&& [ \"$(stat -c %d:%i .)\" = \"$parent\" ] && rmdir -- \"./$w\" 2>/dev/null || :; fi; cd /; };",
            script,
            StringComparison.Ordinal);
        Assert.Contains("work=; entered=0;", script, StringComparison.Ordinal);

        // Armed before the directory exists; a signal ends the script instead of resuming it.
        Assert.Contains("trap cleanup EXIT; trap 'exit 129' HUP INT TERM;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("trap cleanup EXIT HUP INT TERM", script, StringComparison.Ordinal);
        Assert.EndsWith("cleanup; trap - EXIT HUP INT TERM", script, StringComparison.Ordinal);
        Assert.True(
            IndexOf(script, "trap cleanup EXIT;") < IndexOf(script, "mktemp -d"),
            "the cleanup is armed after the directory is created.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scripts_KeepOnlyAbsoluteDirectoriesInTheSearchPathBeforeAnyCommandRuns(bool write)
    {
        string script = write ? WriteScript() : ReadScript();

        int reduce = IndexOf(script, "set -f; IFS=:; clean_path=; for entry in ${PATH-}; do ");
        int refuse = IndexOf(script, "exit 77; }; PATH=$clean_path;");
        int probe = IndexOf(script, "for t in stat");

        Assert.True(reduce < refuse && refuse < probe, "a command is looked up before the search path is reduced.");
        Assert.Contains("case \"$entry\" in /*) clean_path=", script, StringComparison.Ordinal);
        Assert.Contains("unset IFS; set +f;", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scripts_CreateTheClaimOnlyOnceTheDirectoryIsCheckedAndBeforeTrustingIt(bool write)
    {
        string script = write ? WriteScript() : ReadScript();

        int empty = IndexOf(script, "[ -z \"$listing\" ] || refuse");
        int claim = IndexOf(script, "mkdir -- claim || refuse unverifiable 'the private directory is in use.';");
        int trusted = IndexOf(script, "work=${made#./}; entered=1;");

        Assert.True(empty < claim && claim < trusted);
    }

    [Fact]
    public void Write_PinsTheFileWithoutFollowingLinksAndHasNoFallbackWithoutThatOption()
    {
        string script = WriteScript();

        Assert.Contains(
            "ln -P -- \"$target\" original || refuse unverifiable 'the file could not be pinned.';",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ln -- ", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_ClearTheWorkNameBeforeLeavingAndRemoveOnlyBelowTheHeldParent()
    {
        foreach (string script in new[] { WriteScript(), ReadScript() })
        {
            int clear = IndexOf(script, "w=$work; work=; cd -P .. 2>/dev/null ");
            int identity = IndexOf(script, "&& [ \"$(stat -c %d:%i .)\" = \"$parent\" ] && rmdir -- \"./$w\"");
            Assert.True(clear < identity);
        }
    }

    [Fact]
    public void ChmodScriptAndInstaller_KeepOnlyAbsoluteSearchPathEntriesBeforeTheirFirstCommand()
    {
        string chmod = ScriptOf(PrivilegedFileCommands.BuildNoFollowChmodBody(Target, "640"));
        string installer = PrivilegedFileCommands.StagedInstallScript;

        int chmodReduce = IndexOf(chmod, "set -f; IFS=:; clean_path=;");
        Assert.True(chmodReduce < IndexOf(chmod, "for t in stat"));
        Assert.True(IndexOf(chmod, "PATH=$clean_path;") < IndexOf(chmod, "for t in stat"));

        string marker = $"printf '%s\\n' '{PrivilegedFileCommands.InstallStartedMarker}' >&2; ";
        int start = IndexOf(installer, marker);
        int reduce = IndexOf(installer, "set -f; IFS=:; clean_path=;");
        Assert.True(start < reduce && reduce < IndexOf(installer, "for t in stat"));
        Assert.True(reduce < IndexOf(installer, "stat -c"));
    }

    [Fact]
    public void Write_RemovesNothingOutsideTheDirectoryItHolds()
    {
        string script = WriteScript();

        // Every removal is a bare name in the held directory, or the work directory itself.
        string[] removals = Regex.Matches(script, @"\b(rm -f -- [a-z ]+|rmdir -- ""\./\$w""|rmdir -- claim)")
            .Select(match => match.Value.TrimEnd())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["rm -f -- original", "rm -f -- original payload", "rm -f -- payload", "rmdir -- \"./$w\"", "rmdir -- claim"],
            removals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fragment_EveryCommandSubstitutionAndDirectoryChangeEndsInARefusal(bool write)
    {
        string script = write ? WriteScript() : ReadScript();
        string fragment = PrivilegedFileCommands.VerifiedEntryOpening + PrivilegedFileCommands.VerifiedEntryClosing;

        int assignments = Regex.Matches(fragment, @"\b[a-z]+=\$\(").Count;
        int refused = Regex.Matches(fragment, @"\b[a-z]+=\$\([^)]*\) \|\| refuse [a-z]+ '").Count;
        Assert.True(assignments >= 5, "the fragment checks fewer entries than expected.");
        Assert.Equal(assignments, refused);

        MatchCollection changes = Regex.Matches(script, @"cd -P -- ""[^""]+""( \|\| refuse [a-z]+ ')?");
        Assert.Equal(2, changes.Count);
        Assert.All(changes.Cast<Match>(), change => Assert.True(change.Groups[1].Success, $"{change.Value} can exit bare."));

        // The comparisons that decide a refusal end in one too.
        foreach (string statement in fragment.Split("; "))
        {
            if (statement.StartsWith("[ ", StringComparison.Ordinal))
            {
                Assert.Contains("|| refuse", statement, StringComparison.Ordinal);
            }
        }

        // Every call names a reason token from the shared list, then a message.
        string[] known =
        [
            PrivilegedFileCommands.RefusalReasonLink,
            PrivilegedFileCommands.RefusalReasonSpecial,
            PrivilegedFileCommands.RefusalReasonChanged,
            PrivilegedFileCommands.RefusalReasonUnverifiable,
            PrivilegedFileCommands.RefusalReasonInvalid,
        ];
        MatchCollection calls = Regex.Matches(script, @"\brefuse ([a-z]+) '");
        Assert.NotEmpty(calls);
        Assert.All(calls.Cast<Match>(), call => Assert.Contains(call.Groups[1].Value, known));
        Assert.Equal(calls.Count, Regex.Matches(script, @"\brefuse [^(]").Count);
    }

    [Fact]
    public void Refusals_ExitWithTheStatusOfTheirPathAndNeverPrintTheEntryMarker()
    {
        string write = WriteScript();
        string read = ReadScript();

        Assert.Equal(73, PrivilegedFileCommands.NonRegularTargetExitStatus);
        Assert.Contains(
            "refuse() { printf '%s\\n' \"Refusing: $2\" >&2; exit " + WriteExitStatus + "; };",
            write,
            StringComparison.Ordinal);
        Assert.Contains(
            "refuse() { printf '%s\\n' \"Refusing: $2\" >&2; exit " + ReadExitStatus + "; };",
            read,
            StringComparison.Ordinal);
        Assert.Contains("Refusing non-regular or symbolic-link source.' >&2; exit " + ReadExitStatus + ";", read, StringComparison.Ordinal);

        foreach (string text in new[]
                 {
                     write,
                     read,
                     PrivilegedFileCommands.StagedInstallScript,
                     PrivilegedFileCommands.BuildAtomicWriteBody(Target),
                     PrivilegedFileCommands.BuildNoFollowBase64ReadBody(Target),
                 })
        {
            Assert.DoesNotContain(PrivilegedFileCommands.EntryRefusedMarker, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Read_HoldsTheWorkDirectoryBeforeLinkingAndReadsThroughTheDescriptor()
    {
        string script = ReadScript(limit: 4096);

        int create = IndexOf(script, "made=$(mktemp -d -- \"./.heimdall-read.XXXXXXXXXX\") || refuse");
        int enter = IndexOf(script, "cd -P -- \"$made\" || refuse");
        int trusted = IndexOf(script, "work=${made#./}; entered=1; target=../$name;");
        int link = IndexOf(script, "ln -P -- \"$target\" source");
        int fallback = IndexOf(script, "else ln -- \"$target\" source || refuse unverifiable 'the file could not be pinned.'; fi;");
        int check = IndexOf(script, "if [ -L source ] || [ ! -f source ]");
        int open = IndexOf(script, "exec 3< source; rm -f -- source;");
        int size = IndexOf(script, "stat -Lc %s /proc/self/fd/3");
        int read = IndexOf(script, "base64 <&3; exec 3<&-;");

        Assert.True(create < enter && enter < trusted && trusted < link && link < fallback, "the source is linked before the directory is trusted.");
        Assert.True(fallback < check && check < open && open < size && size < read);
        Assert.DoesNotContain("chmod 700", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cd -- \"$work\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Bodies_HoldNoNewlineAndNoBangWhereTheBuilderDoesNotHandleIt()
    {
        string[] bodies =
        [
            PrivilegedFileCommands.BuildAtomicWriteBody(Target),
            PrivilegedFileCommands.BuildAtomicWriteBody(Target, overwrite: false),
            PrivilegedFileCommands.BuildNoFollowBase64ReadBody(Target),
            PrivilegedFileCommands.BuildNoFollowBase64ReadBody(Target, 4096),
        ];

        foreach (string body in bodies)
        {
            Assert.DoesNotContain('\n', body);
            Assert.DoesNotContain('\r', body);
        }

        // The read body has no bang followed by anything but a space.
        Assert.DoesNotMatch(@"!(?! )", bodies[2]);
        Assert.DoesNotMatch(@"!(?! )", bodies[3]);

        // The shared procedure itself holds none at all, and neither does the placeholder it needs.
        string fragment = PrivilegedFileCommands.VerifiedEntryOpening + PrivilegedFileCommands.VerifiedEntryClosing;
        Assert.DoesNotContain('!', fragment);
        Assert.DoesNotContain('^', fragment);
        Assert.DoesNotContain('\n', fragment);
    }

    [Fact]
    public void Write_KeepsTheStagedInstallerFree_OfTheReplacementCharacter()
    {
        Assert.DoesNotContain('^', PrivilegedFileCommands.StagedInstallScript);
        Assert.DoesNotContain('\n', PrivilegedFileCommands.StagedInstallScript);
        Assert.DoesNotContain(
            "!",
            PrivilegedFileCommands.BuildStagedWriteCommand(Target, true, true, 5),
            StringComparison.Ordinal);
    }
}
