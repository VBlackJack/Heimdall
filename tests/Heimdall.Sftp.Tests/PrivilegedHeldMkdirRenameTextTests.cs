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

using System.Security.Cryptography;
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The root-side create-folder and rename scripts reach their entry through the same parent walk as
/// the delete. These tests read the script text; the behaviour is run by
/// <see cref="PrivilegedHeldMkdirRenameRunTests"/>.
/// </summary>
public sealed class PrivilegedHeldMkdirRenameTextTests
{
    private const string Target = "/srv/shared/report";

    private const string NewName = "renamed";

    // The SHA-256 of the delete command of the two kinds for Target, taken before the shared start
    // of the entry scripts was moved into its own constant.
    private const string DeleteFolderHash = "DD54C2B55017464BB3B77E7B6E23605B8DADFF8184FF586259A09F9A4DCA5D30";

    private const string DeleteFileHash = "54933BD0A2097057E2DC62931F7E94E914E46419D25243D86AEB53314C220A4A";

    private static string ScriptOf(string command) => PrivilegedWorkDirectoryTextTests.ScriptOf(command);

    private static string MkdirScript(string path = Target)
        => ScriptOf(PrivilegedFileCommands.BuildHeldMkdirBody(path));

    private static string RenameScript(bool replace, string path = Target, string name = NewName)
        => ScriptOf(PrivilegedFileCommands.BuildHeldRenameBody(path, name, replace));

    private static int IndexOf(string text, string value, int from = 0)
    {
        int index = text.IndexOf(value, from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' is not in the script.");
        return index;
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static TheoryData<string> ScriptNames() => new()
    {
        "mkdir",
        "rename-keep",
        "rename-replace",
    };

    private static string ScriptNamed(string name) => name switch
    {
        "mkdir" => MkdirScript(),
        "rename-keep" => RenameScript(replace: false),
        "rename-replace" => RenameScript(replace: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Fact]
    public void DeleteBody_IsUnchangedByTheSharedStart()
    {
        Assert.Equal(DeleteFolderHash, Hash(PrivilegedFileCommands.BuildHeldDeleteBody(Target, recursive: true)));
        Assert.Equal(DeleteFileHash, Hash(PrivilegedFileCommands.BuildHeldDeleteBody(Target, recursive: false)));
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Script_WalksTheParentOnceAfterTheToolingProbeAndBeforeTheAction(string name)
    {
        string script = ScriptNamed(name);

        int reduce = IndexOf(script, "set -f; IFS=:; clean_path=;");
        int probe = IndexOf(script, "for t in stat cp sync mv ln mktemp chmod chown rm rmdir base64 cat; do");
        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);
        int action = name == "mkdir"
            ? IndexOf(script, "mkdir -- \"./$name\"")
            : IndexOf(script, "mv -", walk);

        Assert.True(reduce < probe && probe < walk && walk < action);
        Assert.Equal(1, Count(script, PrivilegedFileCommands.HeldParentWalk));
        Assert.Equal(1, Count(script, name == "mkdir" ? "mkdir -- " : "mv -"));
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Script_RefusesWithTheEntryMarkerAndStatus73(string name)
    {
        string script = ScriptNamed(name);

        Assert.Contains(
            "refuse() { printf '%s %s\\n' 'HEIMDALL-ENTRY-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "deny() { printf '%s %s\\n' 'HEIMDALL-ENTRY-REFUSED' \"$1\" >&2; "
            + "printf '%s\\n' \"Refusing: $2\" >&2; exit 73; };",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(PrivilegedFileCommands.TransferRefusedMarker, script, StringComparison.Ordinal);
        Assert.Contains(
            "name=${target##*/}; case \"$name\" in ''|.|..|*/*) refuse invalid 'no entry name.';; esac;",
            script,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Script_HoldsNoBangCaretBacktickNewlineOrFolderPrefixedName(string name)
    {
        string script = ScriptNamed(name);

        Assert.DoesNotContain('!', script);
        Assert.DoesNotContain('^', script);
        Assert.DoesNotContain('`', script);
        Assert.DoesNotContain('\n', script);
        Assert.DoesNotContain("$dir/", script, StringComparison.Ordinal);
        string afterWalk = script[(IndexOf(script, PrivilegedFileCommands.HeldParentWalk) + PrivilegedFileCommands.HeldParentWalk.Length)..];
        Assert.DoesNotContain("$target", afterWalk, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistsStatus_IsPinnedInTheScripts()
    {
        Assert.Equal(78, PrivilegedFileCommands.DestinationExistsExitStatus);
        Assert.Contains("; exit 78; fi; ", MkdirScript(), StringComparison.Ordinal);
        Assert.Contains("; exit 78; fi; ", RenameScript(replace: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Mkdir_ChecksTheNameThenCreatesItFromTheHeldFolderWithoutChangingTheMode()
    {
        string script = MkdirScript();

        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);
        int check = IndexOf(
            script,
            "if [ -e \"./$name\" ] || [ -L \"./$name\" ]; then printf '%s\\n' "
            + "'Refusing: an entry with that name already exists.' >&2; exit 78; fi; ");

        Assert.StartsWith("set -eu; target=$1; ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("umask", script, StringComparison.Ordinal);
        Assert.True(walk < check);
        Assert.EndsWith("; fi; exit 1; }", script, StringComparison.Ordinal);
        Assert.Contains("mkdir -- \"./$name\" || { if [ -e \"./$name\" ] || [ -L \"./$name\" ]; then printf '%s\\n' ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("mkdir -p", script, StringComparison.Ordinal);
        Assert.Equal(1, Count(script, "mkdir"));
    }

    [Fact]
    public void RenameKeep_SpellsTheTailOutAndExitsWithTheStatusOfTheMove()
    {
        string script = RenameScript(replace: false);

        Assert.StartsWith("set -eu; umask 077; target=$1; to=$2; ", script, StringComparison.Ordinal);
        Assert.EndsWith(
            "rc=0; mv -nT -- \"./$name\" \"./$to\" || rc=$?; "
            + "if { [ -e \"./$name\" ] || [ -L \"./$name\" ]; } && { [ -e \"./$to\" ] || [ -L \"./$to\" ]; }; then "
            + "printf '%s\\n' 'Refusing: an entry with the new name already exists.' >&2; exit 78; fi; exit \"$rc\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenameReplace_TakesTheNameOverWithoutNoClobber()
    {
        string script = RenameScript(replace: true);

        Assert.EndsWith("mv -T -- \"./$name\" \"./$to\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-nT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("exit 78", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rename_RefusesAnInvalidOrUnchangedNewNameBeforeAnyMove(bool replace)
    {
        string script = RenameScript(replace);

        int walk = IndexOf(script, PrivilegedFileCommands.HeldParentWalk);
        int invalid = IndexOf(script, "case \"$to\" in ''|.|..|*/*|\"$name\") refuse invalid 'no new name.';; esac; ");
        int move = IndexOf(script, "mv -", walk);

        Assert.True(walk < invalid && invalid < move);
    }

    [Theory]
    [InlineData("/srv/with space/it's here")]
    [InlineData("/srv/-leading-dash")]
    [InlineData("/srv/a;b&c|d$e")]
    [InlineData("/srv/double\"quoted name")]
    public void Mkdir_PassesThePathAsADataParameterOutsideTheScript(string path)
    {
        string command = PrivilegedFileCommands.BuildHeldMkdirBody(path);

        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.EndsWith("' sh " + PathEscaper.EscapeForShell(path), command, StringComparison.Ordinal);
        Assert.DoesNotContain("srv", ScriptOf(command), StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
    }

    [Theory]
    [InlineData("/srv/with space/it's here", "new name's")]
    [InlineData("/srv/-leading-dash", "-also-dash")]
    [InlineData("/srv/a;b&c|d$e", "x;y&z|w$v")]
    [InlineData("/srv/double\"quoted name", "say \"hi\"")]
    public void Rename_PassesBothArgumentsAsDataParametersOutsideTheScript(string path, string name)
    {
        string command = PrivilegedFileCommands.BuildHeldRenameBody(path, name, replace: false);
        string script = ScriptOf(command);

        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.EndsWith(
            "' sh " + PathEscaper.EscapeForShell(path) + " " + PathEscaper.EscapeForShell(name),
            command,
            StringComparison.Ordinal);
        Assert.DoesNotContain("srv", script, StringComparison.Ordinal);
        Assert.DoesNotContain("quoted", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
    }

    [Fact]
    public void Mkdir_EscapesABangInThePathOutsideTheScript()
    {
        string command = PrivilegedFileCommands.BuildHeldMkdirBody("/srv/odd!x");

        Assert.EndsWith(@" '/srv/odd'\!'x'", command, StringComparison.Ordinal);
        Assert.DoesNotContain('!', ScriptOf(command));
    }

    [Fact]
    public void Rename_EscapesABangInBothArgumentsOutsideTheScript()
    {
        string command = PrivilegedFileCommands.BuildHeldRenameBody("/srv/odd!x", "new!name", replace: false);

        Assert.EndsWith(@" '/srv/odd'\!'x' 'new'\!'name'", command, StringComparison.Ordinal);
        Assert.DoesNotContain('!', ScriptOf(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Mkdir_RefusesABlankPath(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => PrivilegedFileCommands.BuildHeldMkdirBody(path));
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("/srv/old", "")]
    [InlineData("/srv/old", ".")]
    [InlineData("/srv/old", "..")]
    [InlineData("/srv/old", "a/b")]
    [InlineData("/srv/old", "/abs")]
    [InlineData("/srv/old", "old")]
    public void Rename_RefusesBlankAndInvalidArguments(string path, string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => PrivilegedFileCommands.BuildHeldRenameBody(path, name, replace: false));
        Assert.ThrowsAny<ArgumentException>(() => PrivilegedFileCommands.BuildHeldRenameBody(path, name, replace: true));
    }
}
