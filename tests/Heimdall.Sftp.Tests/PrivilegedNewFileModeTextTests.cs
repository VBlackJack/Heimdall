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

namespace Heimdall.Sftp.Tests;

/// <summary>
/// A new file written as root is published with mode 0644, where the filesystem allows it. These
/// tests read the script text; the behaviour is run by <see cref="PrivilegedNewFileModeRunTests"/>.
/// </summary>
public sealed class PrivilegedNewFileModeTextTests
{
    private const string Target = "/srv/shared/report.txt";

    private const string NewFileChmod = "chmod -- 0644 payload 2>/dev/null || :";

    private const string AclSkip = "case \"$payload_acl\" in ??????????+*) ;;";

    private const string WorkAclSkip = "case \"$work_acl\" in ??????????+*) ;;";

    private const string Umask = "umask 077";

    private static string WriteScript(bool overwrite = true)
        => PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildAtomicWriteBody(Target, overwrite));

    private static int IndexOf(string text, string value, int from = 0)
    {
        int index = text.IndexOf(value, from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' is not in the script.");
        return index;
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Write_ChangesTheModeOfTheNewFileOnce(bool overwrite)
    {
        Assert.Equal(1, CountOf(WriteScript(overwrite), NewFileChmod));
    }

    [Fact]
    public void Write_TheChmodIsOnlyOnTheNewFileBranch()
    {
        string script = WriteScript();

        int chmod = IndexOf(script, NewFileChmod);
        int metadataCopy = IndexOf(script, "cp --attributes-only");
        int elseBranch = IndexOf(script, "exit 76; fi; else ");
        int sync = IndexOf(script, "sync -f payload;");

        // The branch of a replaced file ends before the else; the chmod is after it, not before.
        Assert.True(metadataCopy < elseBranch, "the attribute copy is not on the replaced-file branch.");
        Assert.True(elseBranch < chmod, "the chmod is not behind the else of the replaced-file branch.");
        Assert.True(chmod < sync, "the chmod is not before the data is synced.");

        // Neither the replaced-file branch nor the code before it carries the new mode.
        Assert.DoesNotContain("0644", script[..elseBranch], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_TheChmodComesAfterTheContentAndTheVerifiedWorkDirectory()
    {
        string script = WriteScript();

        int chmod = IndexOf(script, NewFileChmod);
        int content = IndexOf(script, "cat > payload;");
        int workDirectory = IndexOf(script, "mktemp -d -- \"./.heimdall-write.");
        int verified = IndexOf(script, "work=${made#./}; entered=1;");

        Assert.True(workDirectory < verified, "the work directory is entered before it is verified.");
        Assert.True(verified < content, "the content is written before the work directory is verified.");
        Assert.True(content < chmod, "the mode is changed before the content is written.");
    }

    [Fact]
    public void Write_AWorkDirectoryOrPayloadWithAnAclIsLeftAloneBeforeTheChmod()
    {
        string script = WriteScript();

        int workListing = IndexOf(script, "work_acl=$(ls -ld -- . 2>/dev/null) || work_acl=----------+;");
        int payloadListing = IndexOf(script, "payload_acl=$(ls -ld -- payload 2>/dev/null) || payload_acl=----------+;");
        int workSkip = IndexOf(script, WorkAclSkip);
        int payloadSkip = IndexOf(script, AclSkip);
        int chmod = IndexOf(script, NewFileChmod);

        Assert.Equal(1, CountOf(script, WorkAclSkip));
        Assert.Equal(1, CountOf(script, AclSkip));
        Assert.True(workListing < payloadListing, "the listings are not in order.");
        Assert.True(payloadListing < workSkip, "a listing is read after the test of it.");
        Assert.True(workSkip < payloadSkip, "the tests are not nested in order.");
        Assert.True(payloadSkip < chmod, "the chmod is not behind the tests for an ACL.");

        // The empty branch of each case is the one that skips: the chmod is on the last other one.
        Assert.Equal(" *) ", script[(workSkip + WorkAclSkip.Length)..payloadSkip]);
        Assert.Equal(
            " *) if [ \"$(stat -c %a -- payload)\" = 600 ]; then ",
            script[(payloadSkip + AclSkip.Length)..chmod]);
    }

    [Fact]
    public void Write_AnLsThatFailsCountsAsAnAcl()
    {
        string script = WriteScript();

        Assert.Contains("|| work_acl=----------+;", script, StringComparison.Ordinal);
        Assert.Contains("|| payload_acl=----------+;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ABestEffortChmodNeverFailsTheWrite()
    {
        string script = WriteScript();

        int chmod = IndexOf(script, NewFileChmod);
        string tail = script[(chmod + NewFileChmod.Length)..];

        Assert.StartsWith("; fi ;; esac ;; esac; fi; sync -f payload;", tail, StringComparison.Ordinal);
    }
    [Fact]
    public void Write_TheUmaskStaysTheOneAtTheStart()
    {
        string script = WriteScript();

        Assert.StartsWith("replace=1; set -eu; umask 077; ", script, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(script, Umask));
    }

    [Fact]
    public void Write_TheScriptStaysFreeOfTheCharactersALoginShellActsOn()
    {
        string script = PrivilegedFileCommands.StagedInstallScript;

        Assert.DoesNotContain('`', script);
        Assert.DoesNotContain('\n', script);
        Assert.DoesNotContain('^', WriteScript());

        // The new fragment holds no placeholder character, in the writer or in the installer.
        foreach (string text in new[] { WriteScript(), PrivilegedFileCommands.StagedInstallScript })
        {
            int from = IndexOf(text, "else work_acl=");
            int to = IndexOf(text, "sync -f payload;", from);
            Assert.DoesNotContain('^', text[from..to]);
            Assert.DoesNotContain('!', text[from..to]);
        }
        Assert.DoesNotContain('`', WriteScript());
    }

    [Fact]
    public void Read_AndTheChmodScriptsNeverSetTheNewFileMode()
    {
        string read = PrivilegedWorkDirectoryTextTests.ScriptOf(
            PrivilegedFileCommands.BuildNoFollowBase64ReadBody(Target));
        string chmod = PrivilegedFileCommands.BuildNoFollowChmodBody(Target, "640");

        Assert.DoesNotContain("0644", read, StringComparison.Ordinal);
        Assert.DoesNotContain("0644", chmod, StringComparison.Ordinal);
    }

    [Fact]
    public void StagedInstall_RunsTheSameNewFileMode()
    {
        string installer = PrivilegedFileCommands.StagedInstallScript;

        Assert.Equal(1, CountOf(installer, NewFileChmod));
        Assert.Contains(AclSkip, installer, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(installer, Umask));
    }
}
