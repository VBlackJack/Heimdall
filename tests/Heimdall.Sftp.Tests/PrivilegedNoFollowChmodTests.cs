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
/// Root changes the permissions of an entry the account was refused, and must never follow a
/// symbolic link to do it. These tests read the script text: it cannot be run against a server
/// here, so each property that matters is pinned where it can be seen.
/// </summary>
public sealed class PrivilegedNoFollowChmodTests
{
    private const string Target = "/srv/shared/notes";

    private const string Mode = "640";

    private const string ScriptOpening = "sh -c '";

    private const string ScriptClosing = "' sh '";

    // An embedded quote travels as quote, backslash, quote, quote.
    private static readonly string EscapedQuote = "'" + (char)92 + "''";

    private static string Command(string target = Target, string mode = Mode)
        => PrivilegedFileCommands.BuildNoFollowChmodBody(target, mode);

    /// <summary>The script as the remote shell receives it: the outer quoting undone.</summary>
    private static string Script(string command)
    {
        int start = command.IndexOf(ScriptOpening, StringComparison.Ordinal) + ScriptOpening.Length;
        int end = command.LastIndexOf(ScriptClosing, StringComparison.Ordinal);
        return command[start..end].Replace(EscapedQuote, "'", StringComparison.Ordinal);
    }

    private static int IndexOf(string text, string value)
    {
        int index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' is not in the script.");
        return index;
    }

    // 1 -----------------------------------------------------------------------------------------

    [Fact]
    public void Body_PassesTheModeAndThePathAsPositionalParameters()
    {
        string command = Command();
        string script = Script(command);

        Assert.StartsWith(ScriptOpening, command, StringComparison.Ordinal);
        Assert.EndsWith("' sh '640' '/srv/shared/notes'", command, StringComparison.Ordinal);
        Assert.Contains("mode=$1; target=$2;", script, StringComparison.Ordinal);

        // The path is data: it never sits inside the script text.
        Assert.DoesNotContain("srv/shared", script, StringComparison.Ordinal);
        Assert.DoesNotContain("640", script, StringComparison.Ordinal);
    }

    // 2 -----------------------------------------------------------------------------------------

    [Fact]
    public void Script_ChecksTheToolingBeforeAnyWorkDirectoryOrChange()
    {
        string script = Script(Command());

        int probe = IndexOf(script, "stat --version");
        int firstRefusal = IndexOf(script, "exit 77;");
        int firstMktemp = IndexOf(script, "mktemp -d");
        int firstChange = IndexOf(script, "chmod -- ");

        Assert.True(probe < firstMktemp && firstRefusal < firstMktemp, "the probe runs after the work directory.");
        Assert.True(probe < firstChange, "the probe runs after a change.");
        Assert.Equal(PrivilegedFileCommands.ToolingUnavailableExitStatus, 77);
    }

    // 3 -----------------------------------------------------------------------------------------

    [Fact]
    public void Script_RefusesALinkBeforeAnyChmodAndNeverChmodsTheNamedPath()
    {
        string script = Script(Command());

        int linkCheck = IndexOf(script, "if [ -L \"./$name\" ]; then refuse link 'symbolic link.'; fi;");
        int firstChange = IndexOf(script, "chmod -- ");
        Assert.True(linkCheck < firstChange, "a link is looked for after the first chmod.");

        // Every chmod has exactly one of two operands: the held directory, or the private link.
        string[] operands = Regex.Matches(script, "chmod -- \"\\$mode\" ([^;\\s]+);")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal([".", "source"], operands.Order(StringComparer.Ordinal).ToArray());

        // No chmod outside those two: the only other one is the name in the tooling probe.
        Assert.Equal(2, Regex.Matches(script, "chmod -- ").Count);
        Assert.Single(Regex.Matches(script, "\\bchmod (?!--)").Cast<Match>());
        Assert.DoesNotContain("chmod -- \"$mode\" \"$target\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod -- \"$mode\" \"./$name\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod -R", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chown", script.Replace("chmod chown", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // 4 -----------------------------------------------------------------------------------------

    [Fact]
    public void Script_ChangesAFileOnlyThroughAHardLinkInAVerifiedRootDirectory()
    {
        string script = Script(Command());

        int owner = IndexOf(script, "case \"$(stat -c %u:%a .)\" in 0:700|0:2700) ;; ");
        int verify = IndexOf(
            script,
            "[ \"$(stat -c %d:%i ..)\" = \"$parent\" ] && [ \"$(stat -c %d .)\" = \"$pdev\" ] "
            + "|| refuse unverifiable 'the private directory could not be verified.';");
        int empty = IndexOf(script, "[ -z \"$listing\" ] || refuse unverifiable");
        int entered = IndexOf(script, "entered=1;");
        int link = IndexOf(script, "ln -P -T -- \"../$name\" source || refuse unverifiable 'the file could not be pinned.';");
        int recheck = IndexOf(script, "if [ -L source ]; then refuse link 'symbolic link.'; fi;");
        int regular = IndexOf(script, "[ -f source ] || refuse special 'not a regular file.';");
        int change = IndexOf(script, "chmod -- \"$mode\" source;");

        Assert.True(
            owner < verify && verify < empty && empty < entered && entered < link,
            "the directory is trusted before it is verified.");
        Assert.True(link < recheck && recheck < regular && regular < change, "the link is changed before it is checked.");

        // A fallback that links without -P could hard-link what a symbolic link points to.
        Assert.DoesNotContain("|| ln", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ln -- ", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, "ln -P -T -- ").Cast<Match>());
    }

    [Fact]
    public void Script_RemovesTheLinkOnlyOnceInsideTheVerifiedDirectory()
    {
        string script = Script(Command());

        // The one removal of "source" is inside the cleanup, guarded by the flag, and the flag is
        // dropped right after it, so a second run (a signal after the first) does nothing...
        Assert.Single(Regex.Matches(script, "rm -f -- source").Cast<Match>());
        Assert.Contains(
            "cleanup() { if [ \"$entered\" = 1 ]; then rm -f -- source || :; entered=0; fi; cd /; "
            + "if [ -n \"$work\" ]; then rmdir -- \"$work\" 2>/dev/null || :; work=; fi; };",
            script,
            StringComparison.Ordinal);

        // ...the flag and the directory name start empty and are set together, only after the
        // verification and the emptiness check (a foreign directory is never ours to remove)...
        Assert.Contains("work=; entered=0;", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, "entered=1").Cast<Match>());
        Assert.Single(Regex.Matches(script, "work=\\$candidate").Cast<Match>());
        Assert.Contains("work=$candidate; entered=1;", script, StringComparison.Ordinal);
        Assert.True(
            IndexOf(script, "the private directory is not empty.") < IndexOf(script, "entered=1;")
            && IndexOf(script, "if [ -e source ] || [ -L source ]; then refuse") < IndexOf(script, "entered=1;")
            && IndexOf(script, "the private directory could not be verified.") < IndexOf(script, "entered=1;"),
            "the flag is raised before the verification.");

        // ...the trap exists before anything is created, and a signal exits so the script cannot
        // carry on from the root directory the cleanup changed to.
        Assert.True(
            IndexOf(script, "trap cleanup EXIT;") < IndexOf(script, "mktemp -d"),
            "the work directory is created before the cleanup is armed.");
        Assert.Contains("trap 'exit 129' HUP INT TERM;", script, StringComparison.Ordinal);
        Assert.EndsWith("cleanup; trap - EXIT HUP INT TERM", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CreatesTheWorkDirectoryInsideTheDirectoryItHolds()
    {
        string script = Script(Command());

        // Relative to the held parent, so a path swapped after the check cannot move it.
        Assert.Contains("mktemp -d -- \"./.heimdall-chmod.XXXXXXXXXX\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("mktemp -d -- \"$dir", script, StringComparison.Ordinal);
    }

    // 5 -----------------------------------------------------------------------------------------

    [Fact]
    public void Script_ChangesADirectoryOnlyThroughTheDirectoryItHolds()
    {
        string script = Script(Command());

        int identity = IndexOf(script, "held=$(stat -c %d:%i -- \"./$name\") || refuse changed");
        int enter = IndexOf(script, "cd -P -- \"./$name\" || refuse changed");
        int recheck = IndexOf(script, "[ \"$(stat -c %d:%i .)\" = \"$held\" ] || refuse changed 'the directory changed during the check.';");
        int change = IndexOf(script, "chmod -- \"$mode\" .; exit 0; fi;");

        Assert.True(identity < enter && enter < recheck && recheck < change, "the held directory is changed before it is verified.");

        // The directory case ends the script before any work directory exists.
        Assert.True(change < IndexOf(script, "mktemp -d"), "a directory also goes through the work directory.");
    }

    // 6 -----------------------------------------------------------------------------------------

    [Fact]
    public void Command_IsSafeForTcshAndFish()
    {
        string command = Command();

        Assert.DoesNotContain('!', command);
        Assert.DoesNotContain('\n', command);
        Assert.DoesNotContain('\r', command);
        Assert.DoesNotContain("\\\\", command, StringComparison.Ordinal);
        Assert.DoesNotContain('`', command);
    }

    [Fact]
    public void Command_EscapesABangAndAQuoteInThePathOutsideTheScript()
    {
        string command = Command("/srv/it's here; touch /tmp/pwned!x");

        Assert.EndsWith(@" '640' '/srv/it'\''s here; touch /tmp/pwned'\!'x'", command, StringComparison.Ordinal);
        for (int index = command.IndexOf('!', StringComparison.Ordinal);
            index >= 0;
            index = command.IndexOf('!', index + 1))
        {
            Assert.True(index > 0 && command[index - 1] == '\\', "a bang is not escaped.");
        }

        Assert.DoesNotContain("pwned", Script(command), StringComparison.Ordinal);
    }

    // 7 -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("8")]
    [InlineData("12345")]
    [InlineData("7 5")]
    [InlineData("-1")]
    [InlineData("rwx")]
    public void Body_RefusesAnInvalidMode(string mode)
    {
        Assert.ThrowsAny<ArgumentException>(() => Command(mode: mode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Body_RefusesABlankPath(string target)
    {
        Assert.ThrowsAny<ArgumentException>(() => Command(target: target));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("755")]
    [InlineData("4755")]
    public void Body_AcceptsOneToFourOctalDigits(string mode)
    {
        Assert.EndsWith($"' sh '{mode}' '{Target}'", Command(mode: mode), StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ChecksTheModeAgainOnTheServer()
    {
        string script = Script(Command());

        Assert.Contains(
            "case \"$mode\" in [0-7]|[0-7][0-7]|[0-7][0-7][0-7]|[0-7][0-7][0-7][0-7]) ;; *) refuse invalid 'invalid mode.';; esac;",
            script,
            StringComparison.Ordinal);
    }

    // 8 -----------------------------------------------------------------------------------------

    [Fact]
    public void Script_RefusesWithStatus73AndTheMarkerAsItsFirstLine()
    {
        string script = Script(Command());

        Assert.Equal(73, PrivilegedFileCommands.NonRegularTargetExitStatus);
        Assert.Equal("HEIMDALL-ENTRY-REFUSED", PrivilegedFileCommands.EntryRefusedMarker);
        Assert.Contains(
            "refuse() { printf '%s %s\\n' 'HEIMDALL-ENTRY-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };",
            script,
            StringComparison.Ordinal);

        // The marker belongs to the permission change alone: the writer and the reader never print it.
        Assert.DoesNotContain(
            PrivilegedFileCommands.EntryRefusedMarker,
            PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            PrivilegedFileCommands.EntryRefusedMarker,
            PrivilegedFileCommands.BuildNoFollowBase64ReadBody("/srv/app.conf"),
            StringComparison.Ordinal);
    }

    // The setgid parent, the device, the emptiness ----------------------------------------------

    [Fact]
    public void Script_AcceptsThePrivateDirectoryOfASetgidParentAndRequiresTheSameDevice()
    {
        string script = Script(Command());

        // A directory made inside a setgid parent inherits the bit and reads 2700.
        Assert.Contains("case \"$(stat -c %u:%a .)\" in 0:700|0:2700) ;; ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("= 0:700 ]", script, StringComparison.Ordinal);

        // A mount root that claims 0:700 is refused at the check, not later at the link.
        Assert.Contains("pdev=$(stat -c %d .) || refuse changed", script, StringComparison.Ordinal);
        Assert.Contains("[ \"$(stat -c %d .)\" = \"$pdev\" ]", script, StringComparison.Ordinal);
        Assert.Contains("[ \"$(stat -c %d:%i ..)\" = \"$parent\" ]", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_RefusesAPrivateDirectoryThatIsNotEmptyBeforeTrustingIt()
    {
        string script = Script(Command());

        // ls -A: POSIX, no glob option to depend on, and it lists dot files. A failed listing refuses.
        Assert.Contains("listing=$(ls -A) || refuse unverifiable", script, StringComparison.Ordinal);
        Assert.Contains("[ -z \"$listing\" ] || refuse unverifiable 'the private directory is not empty.';", script, StringComparison.Ordinal);
        Assert.Contains(
            "if [ -e source ] || [ -L source ]; then refuse unverifiable 'the private directory is not empty.'; fi;",
            script,
            StringComparison.Ordinal);
    }

    // The reason on the marker line ---------------------------------------------------------------

    [Fact]
    public void Script_NamesTheReasonOfEveryRefusal()
    {
        string script = Script(Command());

        Assert.Equal("link", PrivilegedFileCommands.RefusalReasonLink);
        Assert.Equal("special", PrivilegedFileCommands.RefusalReasonSpecial);
        Assert.Equal("changed", PrivilegedFileCommands.RefusalReasonChanged);
        Assert.Equal("unverifiable", PrivilegedFileCommands.RefusalReasonUnverifiable);
        Assert.Equal("invalid", PrivilegedFileCommands.RefusalReasonInvalid);

        // Every refuse call carries one of the five tokens as its first argument.
        MatchCollection calls = Regex.Matches(script, "\\brefuse ([a-z]+) '");
        Assert.NotEmpty(calls);
        string[] known = ["link", "special", "changed", "unverifiable", "invalid"];
        Assert.All(calls.Cast<Match>(), call => Assert.Contains(call.Groups[1].Value, known));
        Assert.Equal(calls.Count, Regex.Matches(script, "\\brefuse [^(]").Count);
    }

    [Theory]
    [InlineData("HEIMDALL-ENTRY-REFUSED link\nRefusing: symbolic link.\n", EntryRefusalReason.Link)]
    [InlineData("HEIMDALL-ENTRY-REFUSED special\nRefusing: x\n", EntryRefusalReason.Special)]
    [InlineData("HEIMDALL-ENTRY-REFUSED changed\r\nRefusing: x\r\n", EntryRefusalReason.Changed)]
    [InlineData("HEIMDALL-ENTRY-REFUSED unverifiable\nRefusing: x\n", EntryRefusalReason.Unverifiable)]
    [InlineData("HEIMDALL-ENTRY-REFUSED invalid\nRefusing: x\n", EntryRefusalReason.Invalid)]
    [InlineData("ln: oops\nHEIMDALL-ENTRY-REFUSED link", EntryRefusalReason.Link)]
    [InlineData("HEIMDALL-ENTRY-REFUSED\nRefusing: x\n", EntryRefusalReason.Unverifiable)]
    [InlineData("HEIMDALL-ENTRY-REFUSED nonsense\n", EntryRefusalReason.Unverifiable)]
    [InlineData("nothing here", EntryRefusalReason.Unverifiable)]
    [InlineData("", EntryRefusalReason.Unverifiable)]
    [InlineData(null, EntryRefusalReason.Unverifiable)]
    public void ParseEntryRefusalReason_ReadsTheTokenAfterTheMarker(string? stderr, EntryRefusalReason expected)
    {
        Assert.Equal(expected, PrivilegedFileCommands.ParseEntryRefusalReason(stderr));
    }

    [Fact]
    public void Script_RefusesWithATokenWhenAnEntryChangesUnderItInsteadOfExitingBare()
    {
        string script = Script(Command());

        // Every assignment from a command and every directory change ends in a refusal with a
        // token: under set -e a bare failure exits 1 with no marker, a generic error to the user.
        // The chmod commands are left bare on purpose: their failure is a real failure (an
        // immutable file, a read-only file system), not a changed entry.
        foreach (string assignment in new[] { "parent", "pdev", "held", "made", "listing" })
        {
            Match match = Regex.Match(script, assignment + "=\\$\\([^)]*\\)( \\|\\| refuse [a-z]+ )?");
            Assert.True(match.Success, $"{assignment} is not assigned from a command.");
            Assert.True(match.Groups[1].Success, $"{assignment}=$(...) can exit bare.");
        }

        MatchCollection changes = Regex.Matches(script, "cd -P -- \"[^\"]+\"( \\|\\| refuse [a-z]+ )?");
        Assert.Equal(3, changes.Count);
        Assert.All(changes.Cast<Match>(), change => Assert.True(change.Groups[1].Success, $"{change.Value} can exit bare."));

        Assert.Contains("cd -P -- \"$dir\" || refuse changed", script, StringComparison.Ordinal);
        Assert.Contains("parent=$(stat -c %d:%i .) || refuse changed", script, StringComparison.Ordinal);
        Assert.Contains("pdev=$(stat -c %d .) || refuse changed", script, StringComparison.Ordinal);
        Assert.Contains("made=$(mktemp -d -- \"./.heimdall-chmod.XXXXXXXXXX\") || refuse unverifiable", script, StringComparison.Ordinal);
    }

    // The messages the application reads --------------------------------------------------------

    [Theory]
    [InlineData("alice is not in the sudoers file.  This incident will be reported.", true)]
    [InlineData("Sorry, user alice may not run sudo on host.", true)]
    [InlineData("alice is not allowed to execute '/bin/sh' as root on host.", true)]
    [InlineData("alice is not allowed to run sudo on host.", true)]
    [InlineData("sudo: I'm afraid I can't do that", true)]
    [InlineData("sudo: alice is not allowed to authenticate as root", true)]
    [InlineData("sudo: Authentication failed, try again.", false)]
    [InlineData("Sorry, try again.", false)]
    [InlineData("chmod: changing permissions of 'x': Operation not permitted", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSudoNotPermittedMessage_ReadsTheListedMessages(string? stderr, bool expected)
    {
        Assert.Equal(expected, PrivilegedFileTransfer.IsSudoNotPermittedMessage(stderr));
    }

    [Theory]
    [InlineData(127, "sh: 1: sudo: not found", true)]
    [InlineData(127, "bash: sudo: command not found", true)]
    [InlineData(127, "sh: 1: foo: not found", false)]
    [InlineData(127, "", false)]
    [InlineData(127, null, false)]
    [InlineData(1, "sh: 1: sudo: not found", false)]
    public void IsSudoMissing_NeedsTheStatusAndTheNameOfSudo(int exitStatus, string? stderr, bool expected)
    {
        Assert.Equal(expected, PrivilegedFileTransfer.IsSudoMissing(exitStatus, stderr));
    }
}
