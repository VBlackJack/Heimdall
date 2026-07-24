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
        Assert.DoesNotContain("cp --", command, StringComparison.Ordinal);
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
