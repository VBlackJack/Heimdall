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

using Heimdall.Sftp;
using Renci.SshNet;

namespace Heimdall.Sftp.Tests;

public sealed class PrivilegedFileCommandsTests
{
    // --- Extended metadata on the privileged path -----------------------------------------------
    // The script already carried owner and mode. Timestamps, POSIX ACLs, extended attributes and
    // file capabilities were dropped on every replacement - a file could come back with its ACL
    // gone. "original" is a hard link to the target, so it IS the target inode, which is what makes
    // a single attribute copy sufficient.

    [Fact]
    public void BuildAtomicWriteBody_CopiesExtendedMetadataFromTheReplacedFile()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        // The set is listed explicitly. --preserve=all does NOT fail when it cannot carry the
        // extended attributes or the SELinux context - GNU cp warns and exits zero - so "all"
        // would let a silent metadata loss through the fail-closed guard below.
        Assert.Contains(
            "cp --attributes-only --preserve=mode,ownership,timestamps,xattr -- original payload",
            command,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--preserve=all", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAtomicWriteBody_SetsOwnerAndModeBeforeCopyingTheAttributes()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        int chown = command.IndexOf("chown -- \"$original_owner\" payload", StringComparison.Ordinal);
        int chmod = command.IndexOf("chmod -- \"$original_mode\" payload", StringComparison.Ordinal);
        int attributes = command.IndexOf("cp --attributes-only", StringComparison.Ordinal);

        // On Linux a chown clears the capabilities held in security.capability. Running it after
        // the attribute copy would strip exactly what that copy had just restored.
        Assert.True(chown > 0 && chown < attributes, "chown runs after the attribute copy.");
        Assert.True(chmod > 0 && chmod < attributes, "chmod runs after the attribute copy.");
    }

    [Fact]
    public void BuildAtomicWriteBody_FlushesAfterTheMetadataIsInPlace()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        int attributes = command.IndexOf("cp --attributes-only", StringComparison.Ordinal);
        int flush = command.IndexOf("sync -f payload", StringComparison.Ordinal);
        int rename = command.IndexOf("mv -fT -- payload", StringComparison.Ordinal);

        // What reaches the disk must be the file as it will be published, metadata included.
        Assert.True(flush > attributes, "The payload is flushed before its metadata is set.");
        Assert.True(rename > flush, "The payload is published before it is flushed.");
    }

    [Fact]
    public void BuildAtomicWriteBody_CopiesMetadataAfterTheContentAndBeforeTheRename()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        int content = command.IndexOf("cat > payload", StringComparison.Ordinal);
        int metadata = command.IndexOf("cp --attributes-only", StringComparison.Ordinal);
        int rename = command.IndexOf("mv -fT -- payload", StringComparison.Ordinal);

        Assert.True(content > 0 && metadata > content, "Metadata is copied before the content exists.");
        Assert.True(rename > metadata, "Metadata is copied after the file has already been published.");
    }

    [Fact]
    public void BuildAtomicWriteBody_CopiesMetadataOnlyWhenThereIsATargetToCopyFrom()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        int guard = command.IndexOf("if [ \"$preserve_metadata\" -eq 1 ]; then", StringComparison.Ordinal);
        int metadata = command.IndexOf("cp --attributes-only", StringComparison.Ordinal);
        // The flush closes the preserve branch and runs for new files too, so it marks the end of
        // the region the attribute copy must stay inside.
        int endOfGuard = command.IndexOf("sync -f payload;", StringComparison.Ordinal);

        // A brand-new file has no "original", so there is nothing to copy from and the attempt
        // would abort the script under set -e.
        Assert.True(guard > 0 && metadata > guard, "The attribute copy is outside the existing-target guard.");
        Assert.True(endOfGuard > metadata, "The attribute copy escaped the existing-target branch.");
    }

    [Fact]
    public void BuildAtomicWriteBody_RefusesTheRenameWhenTheAttributeCopyAltersThePayload()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        // --attributes-only is documented not to touch the data, but this script cannot verify
        // which coreutils build is on the far side: a truncating one would publish an empty file.
        Assert.Contains("payload_size=$(stat -c %s -- payload)", command, StringComparison.Ordinal);
        Assert.Contains(
            "if [ \"$(stat -c %s -- payload)\" != \"$payload_size\" ]; then",
            command,
            StringComparison.Ordinal);
        Assert.Contains(
            $"exit {PrivilegedFileCommands.MetadataPreservationFailedExitStatus}",
            command,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAtomicWriteBody_NeverAbsorbsAMetadataFailure()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        int metadata = command.IndexOf("cp --attributes-only", StringComparison.Ordinal);
        int endOfStatement = command.IndexOf(';', metadata);
        string statement = command[metadata..endOfStatement];

        // "|| :" or "|| true" would turn a missing tool or a refused copy into a silent success,
        // and the replacement would publish a file stripped of its ACLs.
        Assert.DoesNotContain("||", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("2>/dev/null", statement, StringComparison.Ordinal);

        // The copy is wrapped so its failure becomes an explicit refusal rather than relying on
        // set -e alone, and both preservation failures report the same status.
        // Asserted without the surrounding quotes: BuildAtomicWriteBody escapes the whole script
        // for the shell, so every single quote in it comes back as '\'' in the returned command.
        Assert.Contains(
            "Refusing: could not preserve the replaced file metadata.",
            command,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            command.Split($"exit {PrivilegedFileCommands.MetadataPreservationFailedExitStatus};").Length - 1);

        // set -e is what makes a failed copy abort before the rename.
        Assert.Contains("set -eu;", command, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataPreservationFailedExitStatus_DoesNotCollideWithTheNoFollowReadRefusal()
    {
        // 74 is what the no-follow read path exits with for a non-regular source; reusing it would
        // make two unrelated refusals indistinguishable to the caller.
        Assert.Equal(76, PrivilegedFileCommands.MetadataPreservationFailedExitStatus);
        Assert.NotEqual(74, PrivilegedFileCommands.MetadataPreservationFailedExitStatus);
        Assert.NotEqual(
            PrivilegedFileCommands.FileTooLargeExitStatus,
            PrivilegedFileCommands.MetadataPreservationFailedExitStatus);
    }

    [Fact]
    public void BuildAtomicWriteBody_KeepsOwnerAndModePreservation()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/srv/app.conf");

        // The attribute copy already carries both when run as root; keeping the explicit calls
        // means a build whose cp ignores ownership still lands the mode this path promised.
        Assert.Contains("chown -- \"$original_owner\" payload", command, StringComparison.Ordinal);
        Assert.Contains("chmod -- \"$original_mode\" payload", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAtomicWriteBody_UsesRootOwnedTempAndAtomicSymlinkSafeRename()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/etc/app/config");

        Assert.Contains("umask 077", command, StringComparison.Ordinal);
        Assert.Contains(
            "mktemp -d -- \"$dir/.heimdall-write.XXXXXXXXXX\"",
            command,
            StringComparison.Ordinal);
        Assert.Contains("chmod 700 -- \"$work\"", command, StringComparison.Ordinal);
        Assert.Contains("ln -P -- \"$target\" original", command, StringComparison.Ordinal);
        Assert.Contains("[ -L original ]", command, StringComparison.Ordinal);
        Assert.Contains("original_owner=$(stat -c %u:%g -- original)", command, StringComparison.Ordinal);
        Assert.Contains("original_mode=$(stat -c %a -- original)", command, StringComparison.Ordinal);
        Assert.Contains("cat > payload", command, StringComparison.Ordinal);
        Assert.Contains("sync -f payload", command, StringComparison.Ordinal);
        Assert.Contains("chown -- \"$original_owner\" payload", command, StringComparison.Ordinal);
        Assert.Contains("chmod -- \"$original_mode\" payload", command, StringComparison.Ordinal);
        Assert.Contains("[ -L \"$target\" ]", command, StringComparison.Ordinal);
        Assert.Contains("mv -fT -- payload \"$target\"", command, StringComparison.Ordinal);
        Assert.Contains("sync -f \"$dir\"", command, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT HUP INT TERM", command, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteTempPaths.Prefix, command, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo tee", command, StringComparison.Ordinal);

        // This forbade "cp --" outright to keep the write atomic: copying content into place is
        // exactly what the rename exists to avoid. Narrowed rather than dropped, because the
        // metadata step introduced a cp that copies NO content - every other form stays banned,
        // so a content copy still fails here.
        foreach (int index in IndexesOf(command, "cp --"))
        {
            Assert.StartsWith("cp --attributes-only ", command[index..], StringComparison.Ordinal);
        }
    }

    /// <summary>Every occurrence of a needle, so an assertion can inspect all of them.</summary>
    private static IEnumerable<int> IndexesOf(string haystack, string needle)
    {
        for (int index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    [Fact]
    public void BuildAtomicWriteBody_EscapesTargetAsPositionalArgument()
    {
        string command = PrivilegedFileCommands.BuildAtomicWriteBody("/etc/it's here; touch /tmp/pwned");

        Assert.EndsWith(
            @"sh '/etc/it'\''s here; touch /tmp/pwned'",
            command,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNoFollowBase64ReadBody_UsesHardLinkAndHeldDescriptor()
    {
        string command = PrivilegedFileCommands.BuildNoFollowBase64ReadBody("/etc/shadow");

        Assert.Contains("ln -P -- \"$target\" source", command, StringComparison.Ordinal);
        Assert.Contains("else ln -- \"$target\" source", command, StringComparison.Ordinal);
        Assert.Contains("[ -L source ]", command, StringComparison.Ordinal);
        Assert.Contains("[ ! -f source ]", command, StringComparison.Ordinal);
        Assert.Contains("exec 3< source", command, StringComparison.Ordinal);
        Assert.Contains("rm -f -- source", command, StringComparison.Ordinal);
        Assert.Contains("base64 <&3", command, StringComparison.Ordinal);
        Assert.DoesNotContain("base64 -- '/etc/shadow'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNoFollowBase64ReadBody_WithLimitChecksHeldDescriptorSize()
    {
        string command = PrivilegedFileCommands.BuildNoFollowBase64ReadBody(
            "/etc/shadow",
            maximumBytes: 4096);

        Assert.Contains("stat -Lc %s /proc/self/fd/3", command, StringComparison.Ordinal);
        Assert.Contains("if [ \"$size\" -gt 4096 ]", command, StringComparison.Ordinal);
        Assert.Contains(
            $"exit {PrivilegedFileCommands.FileTooLargeExitStatus}",
            command,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNoFollowBase64ReadBody_WithoutLimitDoesNotStatSize()
    {
        string command = PrivilegedFileCommands.BuildNoFollowBase64ReadBody("/etc/shadow");

        Assert.DoesNotContain("stat -Lc %s", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommands_RejectInvalidPaths()
    {
        Assert.ThrowsAny<ArgumentException>(() => PrivilegedFileCommands.BuildAtomicWriteBody(" "));
        Assert.ThrowsAny<ArgumentException>(() => PrivilegedFileCommands.BuildNoFollowBase64ReadBody(""));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PrivilegedFileCommands.BuildNoFollowBase64ReadBody("/etc/shadow", -1));
        Assert.Throws<ArgumentException>(
            () => PrivilegedFileCommands.BuildAtomicWriteBody("/etc/line\nbreak"));
    }

    [Fact]
    public void SudoInvocations_ConsumePasswordBeforeStreamingPayload()
    {
        Assert.Equal(
            "sudo -S -p '' -v",
            PrivilegedFileCommands.BuildPasswordSudoInvocation("-v"));
        Assert.Equal(
            "sudo -n sh -c 'body'",
            PrivilegedFileCommands.BuildNonInteractiveSudoInvocation("sh -c 'body'"));

        string streaming = PrivilegedFileCommands.BuildPasswordStreamingSudoInvocation(
            "sh -c 'body'");
        Assert.Contains("sudo -n -v", streaming, StringComparison.Ordinal);
        Assert.Contains("IFS= read -r _heimdall_password", streaming, StringComparison.Ordinal);
        Assert.Contains("exec sudo -n sh -c 'body'", streaming, StringComparison.Ordinal);
        Assert.EndsWith(
            "else sudo -k; exec sudo -S -p '' sh -c 'body'; fi",
            streaming,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SshNet_ExposesPublicCommandInputStream()
    {
        var method = typeof(SshCommand).GetMethod(
            nameof(SshCommand.CreateInputStream),
            Type.EmptyTypes);

        Assert.NotNull(method);
        Assert.True(typeof(Stream).IsAssignableFrom(method.ReturnType));
    }
}
