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
/// The write, read, permission change and delete scripts reach their parent folder component by
/// component and check a file's link count before using it. These tests read the script text; the
/// behaviour is run by <see cref="PrivilegedPathAndLinkRunTests"/>.
/// </summary>
public sealed class PrivilegedPathAndLinkTextTests
{
    private const string Target = "/srv/shared/report.txt";

    private static string ScriptOf(string command) => PrivilegedWorkDirectoryTextTests.ScriptOf(command);

    private static string WriteScript() => ScriptOf(PrivilegedFileCommands.BuildAtomicWriteBody(Target));

    private static string ReadScript() => ScriptOf(PrivilegedFileCommands.BuildNoFollowBase64ReadBody(Target));

    private static string ChmodScript() => ScriptOf(PrivilegedFileCommands.BuildNoFollowChmodBody(Target, "600"));

    private static string DeleteScript() => ScriptOf(PrivilegedFileCommands.BuildHeldDeleteBody(Target));

    private static int IndexOf(string text, string value, int from = 0)
    {
        int index = text.IndexOf(value, from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' is not in the script.");
        return index;
    }

    public static TheoryData<string> Scripts() => new()
    {
        "write",
        "read",
        "chmod",
        "delete",
    };

    private static string ScriptNamed(string name) => name switch
    {
        "write" => WriteScript(),
        "read" => ReadScript(),
        "chmod" => ChmodScript(),
        "delete" => DeleteScript(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void EveryScript_WalksTheParentBeforeItCreatesOrRemovesAnything(string name)
    {
        string script = ScriptNamed(name);

        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);
        int probe = IndexOf(script, "for t in stat");
        int created = name == "delete"
            ? IndexOf(script, "rm -rf -- ")
            : IndexOf(script, "mktemp -d");

        Assert.True(probe < walk, "a command is looked up before the tooling is probed.");
        Assert.True(walk < created, "something is created or removed before the parent is held.");
        Assert.Single(Regex.Matches(script, Regex.Escape(PrivilegedFileCommands.HeldParentWalk)).Cast<Match>());
    }

    [Fact]
    public void TheStagedInstaller_CarriesTheWalkAndTheDenyFunction()
    {
        Assert.Contains(PrivilegedFileCommands.HeldParentWalk, PrivilegedFileCommands.StagedInstallScript, StringComparison.Ordinal);
        Assert.Contains("deny() {", PrivilegedFileCommands.StagedInstallScript, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWalk_HasItsHelperItsProbeAndItsLimits()
    {
        string walk = PrivilegedFileCommands.HeldParentWalk;

        Assert.Contains("open_folder() { case \"$1\" in 0|\"$caller\"|\"$3\") ;; *) return 0;; esac; ", walk, StringComparison.Ordinal);
        Assert.Contains("return $(( (0$2 & 022) == 0 )); };", walk, StringComparison.Ordinal);
        Assert.Contains("command -v readlink >/dev/null 2>&1 || { printf '%s\\n' ", walk, StringComparison.Ordinal);
        Assert.Contains("exit 77; };", walk, StringComparison.Ordinal);
        Assert.Contains("caller=${SUDO_UID:-$me}; hops=0;", walk, StringComparison.Ordinal);
        Assert.Contains(": $((hops += 1));", walk, StringComparison.Ordinal);
        Assert.Contains("if [ \"$hops\" -gt 40 ]; then deny pathlink", walk, StringComparison.Ordinal);
        Assert.Contains("rest=${dir#/}; cd /; set -f; IFS=/; set -- $rest; unset IFS; set +f;", walk, StringComparison.Ordinal);
        Assert.Contains("case \"$c\" in ''|.) continue;; ..) dir=..;; *) dir=./$c;; esac;", walk, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWalk_SendsTheMessagesOfStatAndReadlinkToTheNullDevice()
    {
        string walk = PrivilegedFileCommands.HeldParentWalk;

        MatchCollection substitutions = Regex.Matches(walk, @"\b[a-z]+=\$\([^)]*\)");
        Assert.NotEmpty(substitutions);
        Assert.All(
            substitutions.Cast<Match>(),
            substitution => Assert.Contains("2>/dev/null)", substitution.Value, StringComparison.Ordinal));
        Assert.Contains("[ \"$(stat -c %d:%i:%u . 2>/dev/null)\" = \"$want\" ]", walk, StringComparison.Ordinal);
        Assert.Contains("[ \"$(stat -c %d:%i .. 2>/dev/null)\" = \"$before\" ]", walk, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWalk_ComparesTheNewFolderWithTheOneItLeftNotWithTheNameItEntered()
    {
        string walk = PrivilegedFileCommands.HeldParentWalk;

        int before = IndexOf(walk, "before=$(stat -c %d:%i . 2>/dev/null)");
        int enter = IndexOf(walk, "cd -P -- \"$dir\" || refuse changed 'the parent directory changed during the check.';");
        int now = IndexOf(walk, "now=$(stat -c %d:%i . 2>/dev/null)");
        int sameFolder = IndexOf(walk, "if [ \"$now\" = \"${want%:*}\" ]; then :;");
        int sameDevice = IndexOf(walk, "elif [ \"${now%%:*}\" = \"${want%%:*}\" ]; then refuse changed");
        int compare = IndexOf(walk, "[ \"$(stat -c %d:%i .. 2>/dev/null)\" = \"$before\" ]");

        // The folder entered is the one read before the entry; only a different device (a mount
        // that appeared on entry) falls back to the parent it left.
        Assert.True(before < enter && enter < now && now < sameFolder && sameFolder < sameDevice && sameDevice < compare);

        // The name is still read once, before the entry, for the link branch and for the ".." step.
        Assert.Contains("want=$(stat -c %d:%i:%u -- \"$dir\" 2>/dev/null)", walk, StringComparison.Ordinal);
        Assert.True(IndexOf(walk, "want=$(") < IndexOf(walk, "if [ -L \"$dir\" ]"));
    }

    [Fact]
    public void TheWalk_FollowsALinkOnlyWhenTheLinkAndTheFolderHoldingItAreTrusted()
    {
        string walk = PrivilegedFileCommands.HeldParentWalk;

        int link = IndexOf(walk, "if [ -L \"$dir\" ]");
        int hold = IndexOf(walk, "hold=$(stat -c %u:%a . 2>/dev/null)");
        int holder = IndexOf(walk, "if open_folder \"${hold%%:*}\" \"${hold#*:}\" \"\"; then deny pathlink");
        int owner = IndexOf(walk, "case \"${want##*:}\" in 0|\"$caller\") ;; *) deny pathlink");
        int read = IndexOf(walk, "t=$(readlink -- \"$dir\" 2>/dev/null)");
        int resume = IndexOf(walk, "set -f; IFS=/; set -- $t \"$@\"; unset IFS; set +f; continue; fi;");

        Assert.True(link < hold && hold < holder && holder < owner && owner < read && read < resume);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void TheWalkMessages_NameNeitherTheCommandsOfTheOtherScriptsNorAnyMode(string name)
    {
        string walk = PrivilegedFileCommands.HeldParentWalk;
        string script = ScriptNamed(name);

        foreach (string forbidden in new[] { "refuse ", "chmod ", "chown", "640" })
        {
            // The walk calls "refuse" itself, so the word is checked in what it prints instead.
            foreach (Match message in Regex.Matches(walk, "'([^']*)'"))
            {
                Assert.DoesNotContain(forbidden, message.Groups[1].Value, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain('!', walk);
        Assert.DoesNotContain('^', script);
        Assert.DoesNotContain('`', script);
        Assert.DoesNotContain('\n', script);
        Assert.DoesNotContain("$dir/", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWalk_ChangesDirectoryOnlyWithTheStatementsTheExistingChecksAllow()
    {
        string[] statements = Regex.Matches(PrivilegedFileCommands.HeldParentWalk, @"\bcd [^;]*;")
            .Select(match => match.Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "cd -P -- \"$dir\" || refuse changed 'the parent directory changed during the check.';",
                "cd /;",
            }.Order(StringComparer.Ordinal).ToArray(),
            statements);
    }

    // --- deny ------------------------------------------------------------------------------------

    [Fact]
    public void Write_DeniesOnItsOwnMarkerLineAndExitsWith73()
    {
        string script = WriteScript();

        Assert.Equal("HEIMDALL-TRANSFER-REFUSED", PrivilegedFileCommands.TransferRefusedMarker);
        Assert.Contains(
            "deny() { printf '%s %s\\n' 'HEIMDALL-TRANSFER-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };",
            script,
            StringComparison.Ordinal);
        Assert.Contains("refuse() { printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };", script, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivilegedFileCommands.EntryRefusedMarker, script, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DeniesOnItsOwnMarkerLineAndExitsWith74()
    {
        string script = ReadScript();

        Assert.Equal(74, PrivilegedFileCommands.NonRegularSourceExitStatus);
        Assert.Contains(
            "deny() { printf '%s %s\\n' 'HEIMDALL-TRANSFER-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 74; };",
            script,
            StringComparison.Ordinal);
        Assert.Contains("refuse() { printf '%s\\n' \"Refusing: $2\" >&2; exit 74; };", script, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivilegedFileCommands.EntryRefusedMarker, script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chmod")]
    [InlineData("delete")]
    public void ChmodAndDelete_DenyWithTheEntryMarkerAndStatus73(string name)
    {
        string script = ScriptNamed(name);

        Assert.Contains(
            "deny() { printf '%s %s\\n' 'HEIMDALL-ENTRY-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "refuse() { printf '%s %s\\n' 'HEIMDALL-ENTRY-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(PrivilegedFileCommands.TransferRefusedMarker, script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReasonsOfTheNewRefusals_AreThePinnedTokens()
    {
        Assert.Equal("pathlink", PrivilegedFileCommands.RefusalReasonPathLink);
        Assert.Equal("hardlink", PrivilegedFileCommands.RefusalReasonHardLink);

        foreach (string script in new[] { WriteScript(), ReadScript(), ChmodScript(), DeleteScript() })
        {
            foreach (Match call in Regex.Matches(script, @"\bdeny ([a-z]+) '"))
            {
                Assert.Contains(call.Groups[1].Value, new[] { "pathlink", "hardlink" });
            }
        }
    }

    // --- hard links ------------------------------------------------------------------------------

    private const string HardLinkInspection =
        "if [ \"${links%%:*}\" -gt 2 ] && open_folder \"${pinfo%%:*}\" \"${pinfo#*:}\" \"${links#*:}\"; then "
        + "deny hardlink 'the file has other hard links in a folder that others can write to.'; fi; ";

    [Fact]
    public void Write_ChecksTheLinkCountAfterThePinAndBeforeTheOwnerIsRead()
    {
        string script = WriteScript();

        int pin = IndexOf(script, "ln -P -- \"$target\" original || refuse");
        int type = IndexOf(script, "if [ -L original ] || [ ! -f original ]");
        int count = IndexOf(script, "links=$(stat -c %h:%u -- original 2>/dev/null) || refuse");
        int parent = IndexOf(script, "pinfo=$(stat -c %u:%a .. 2>/dev/null) || refuse");
        int deny = IndexOf(script, HardLinkInspection);
        int owner = IndexOf(script, "original_owner=$(stat -c %u:%g -- original);");

        Assert.True(pin < type && type < count && count < parent && parent < deny && deny < owner);
    }

    [Fact]
    public void Read_ChecksTheLinkCountOnTheSourceBeforeItIsOpened()
    {
        string script = ReadScript();

        int type = IndexOf(script, "if [ -L source ] || [ ! -f source ]");
        int count = IndexOf(script, "links=$(stat -c %h:%u -- source 2>/dev/null) || refuse");
        int deny = IndexOf(script, HardLinkInspection);
        int open = IndexOf(script, "exec 3< source;");

        Assert.True(type < count && count < deny && deny < open);
    }

    [Fact]
    public void Chmod_ChecksTheLinkCountAfterTheTypeAndBeforeTheChange()
    {
        string script = ChmodScript();

        int type = IndexOf(script, "[ -f source ] || refuse special 'not a regular file.';");
        int count = IndexOf(script, "links=$(stat -c %h:%u -- source 2>/dev/null) || refuse");
        int deny = IndexOf(script, HardLinkInspection);
        int change = IndexOf(script, "chmod -- \"$mode\" source;");

        Assert.True(type < count && count < deny && deny < change);
    }

    [Fact]
    public void TheLinkCountThreshold_CountsThePinAsOneOfTheLinks()
    {
        foreach (string script in new[] { WriteScript(), ReadScript(), ChmodScript() })
        {
            Assert.Contains("\"${links%%:*}\" -gt 2", script, StringComparison.Ordinal);
            Assert.DoesNotContain("-gt 1 ]", script, StringComparison.Ordinal);
        }
    }

    // --- delete ----------------------------------------------------------------------------------

    [Fact]
    public void Delete_RemovesTheNameFromTheHeldParentAndNothingElse()
    {
        string script = DeleteScript();

        int name = IndexOf(script, "name=${target##*/}; case \"$name\" in ''|.|..|*/*) refuse invalid 'no entry name.';; esac;");
        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);
        int removal = IndexOf(script, "rm -rf -- \"./$name\"");

        Assert.True(name < walk && walk < removal);
        Assert.EndsWith("rm -rf -- \"./$name\"", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, @"\brm -").Cast<Match>());
        Assert.DoesNotContain("$target\"", script[removal..], StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_OfAFileRemovesItWithRmFAndRefusesAFolderAtThatName()
    {
        string script = ScriptOf(PrivilegedFileCommands.BuildHeldDeleteBody(Target, recursive: false));

        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);
        int guard = IndexOf(
            script,
            "if [ -L \"./$name\" ]; then :; elif [ -d \"./$name\" ]; then refuse changed 'the entry is a folder now.'; fi;");
        int removal = IndexOf(script, "rm -f -- \"./$name\"");

        Assert.True(walk < guard && guard < removal);
        Assert.EndsWith("rm -f -- \"./$name\"", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, @"\brm -").Cast<Match>());
        Assert.DoesNotContain("rm -rf", script, StringComparison.Ordinal);
        Assert.DoesNotContain('!', script);
    }

    [Fact]
    public void Delete_OfAFolderIsTheDefaultAndKeepsRmRf()
    {
        Assert.Equal(
            PrivilegedFileCommands.BuildHeldDeleteBody(Target),
            PrivilegedFileCommands.BuildHeldDeleteBody(Target, recursive: true));
        Assert.NotEqual(
            PrivilegedFileCommands.BuildHeldDeleteBody(Target),
            PrivilegedFileCommands.BuildHeldDeleteBody(Target, recursive: false));
    }

    [Fact]
    public void Delete_KeepsTheToolingProbeAndTheSearchPathReductionFirst()
    {
        string script = DeleteScript();

        int reduce = IndexOf(script, "set -f; IFS=:; clean_path=;");
        int probe = IndexOf(script, "for t in stat cp sync mv ln mktemp chmod chown rm rmdir base64 cat; do");
        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);

        Assert.True(reduce < probe && probe < walk);
        Assert.Contains("PATH=$clean_path;", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/srv/with space/it's here")]
    [InlineData("/srv/-leading-dash")]
    [InlineData("/srv/a;b&c|d$e")]
    [InlineData("/srv/double\"quoted name")]
    public void Delete_PassesThePathAsADataParameterOutsideTheScript(string path)
    {
        string command = PrivilegedFileCommands.BuildHeldDeleteBody(path);
        string script = ScriptOf(command);

        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.EndsWith("' sh " + PathEscaper.EscapeForShell(path), command, StringComparison.Ordinal);
        Assert.DoesNotContain("srv", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
    }

    [Fact]
    public void Delete_EscapesABangInThePathOutsideTheScript()
    {
        string command = PrivilegedFileCommands.BuildHeldDeleteBody("/srv/pwned!x");

        Assert.EndsWith(@" '/srv/pwned'\!'x'", command, StringComparison.Ordinal);
        Assert.DoesNotContain('!', ScriptOf(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Delete_RefusesABlankPath(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => PrivilegedFileCommands.BuildHeldDeleteBody(path));
    }

    // --- parsing ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink\nRefusing: x\n", 73, EntryRefusalReason.PathLink)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED hardlink\nRefusing: x\n", 73, EntryRefusalReason.HardLink)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink\nRefusing: x\n", 74, EntryRefusalReason.PathLink)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED hardlink\r\nRefusing: x\r\n", 74, EntryRefusalReason.HardLink)]
    [InlineData("sudo: noise\nHEIMDALL-TRANSFER-REFUSED hardlink", 73, EntryRefusalReason.HardLink)]
    public void ParseTransferRefusal_ReadsAReasonOnItsOwnLineWithTheRightStatus(
        string stderr,
        int status,
        EntryRefusalReason expected)
    {
        Assert.Equal(expected, PrivilegedFileCommands.ParseTransferRefusal(stderr, status));
    }

    [Theory]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink\n", 1)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink\n", 0)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink\n", 75)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink\n", 77)]
    [InlineData("cp: cannot stat 'HEIMDALL-TRANSFER-REFUSED pathlink': No such file\n", 73)]
    [InlineData("x HEIMDALL-TRANSFER-REFUSED hardlink\n", 74)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED pathlink and more\n", 73)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED unknown\n", 73)]
    [InlineData("HEIMDALL-TRANSFER-REFUSED\n", 73)]
    [InlineData("HEIMDALL-ENTRY-REFUSED pathlink\n", 73)]
    [InlineData("Refusing: a symbolic link in the path is not trusted.\n", 73)]
    [InlineData("", 73)]
    [InlineData(null, 73)]
    public void ParseTransferRefusal_ReadsNothingElse(string? stderr, int status)
    {
        Assert.Null(PrivilegedFileCommands.ParseTransferRefusal(stderr, status));
    }

    [Theory]
    [InlineData("HEIMDALL-ENTRY-REFUSED pathlink\nRefusing: x\n", EntryRefusalReason.PathLink)]
    [InlineData("HEIMDALL-ENTRY-REFUSED hardlink\n", EntryRefusalReason.HardLink)]
    [InlineData("ls: HEIMDALL-ENTRY-REFUSED link\n", EntryRefusalReason.Unverifiable)]
    [InlineData("x HEIMDALL-ENTRY-REFUSED changed", EntryRefusalReason.Unverifiable)]
    [InlineData("x HEIMDALL-ENTRY-REFUSED link\nHEIMDALL-ENTRY-REFUSED special\n", EntryRefusalReason.Special)]
    public void ParseEntryRefusalReason_ReadsTheMarkerOnlyAtTheStartOfALine(
        string stderr,
        EntryRefusalReason expected)
    {
        Assert.Equal(expected, PrivilegedFileCommands.ParseEntryRefusalReason(stderr));
    }

    [Fact]
    public void ParseEntryRefusalReason_MapsThePathAndFileReasonsOfTheDeleteAndChmodScripts()
    {
        Assert.Equal(
            EntryRefusalReason.PathLink,
            PrivilegedFileCommands.ParseEntryRefusalReason(
                $"{PrivilegedFileCommands.EntryRefusedMarker} {PrivilegedFileCommands.RefusalReasonPathLink}\n"));
        Assert.Equal(
            EntryRefusalReason.HardLink,
            PrivilegedFileCommands.ParseEntryRefusalReason(
                $"{PrivilegedFileCommands.EntryRefusedMarker} {PrivilegedFileCommands.RefusalReasonHardLink}\n"));
    }

    // --- the editor ------------------------------------------------------------------------------

    [Fact]
    public void TheEditor_ThrowsTheRefusalBeforeReadingSudoMessagesAndDoesNotRetryIt()
    {
        var failure = new PrivilegedCommandResult(
            PrivilegedFileCommands.NonRegularTargetExitStatus,
            string.Empty,
            "HEIMDALL-TRANSFER-REFUSED hardlink\nRefusing: x\nSorry, try again.\n");

        var thrown = Assert.Throws<PrivilegedTransferRefusedException>(
            () => RemoteFileEditor.EnsureSudoWriteSucceeded(failure, "/srv/a b"));

        Assert.Equal(EntryRefusalReason.HardLink, thrown.Reason);
        Assert.Equal("/srv/a b", thrown.RemotePath);
        Assert.True(RemoteFileEditor.IsDeterministicRefusal(thrown));
    }

    [Fact]
    public void TheEditor_KeepsAnOrdinaryFailedWriteAsItWas()
    {
        var failure = new PrivilegedCommandResult(
            PrivilegedFileCommands.NonRegularTargetExitStatus,
            string.Empty,
            "Refusing non-regular or symbolic-link target.\n");

        Assert.Throws<InvalidOperationException>(() => RemoteFileEditor.EnsureSudoWriteSucceeded(failure, "/srv/a"));
        Assert.False(RemoteFileEditor.IsDeterministicRefusal(new InvalidOperationException("x")));
    }
}
