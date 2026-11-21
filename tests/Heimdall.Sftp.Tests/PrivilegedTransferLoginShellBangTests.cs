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

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The streamed write and read reach the remote account's login shell with the path as an
/// argument. csh and tcsh expand <c>!</c> as history even inside single quotes, so the path must
/// carry it escaped. The scripts hold their own <c>!</c> only before a space or an equals sign,
/// which those shells do not expand, so this cannot join the shared builders list that refuses
/// every bare <c>!</c>.
/// </summary>
public sealed class PrivilegedTransferLoginShellBangTests
{
    private const string BangPath = "/srv/wow!x";

    private const string EscapedBangPath = "'/srv/wow'\\!'x'";

    private const string HistoryEscape = "'\\!'";

    public static IEnumerable<object[]> Builders =>
    [
        ["atomic-write", (Func<string, string>)(path => PrivilegedFileCommands.BuildAtomicWriteBody(path))],
        ["read", (Func<string, string>)(path => PrivilegedFileCommands.BuildNoFollowBase64ReadBody(path))],
        ["read-limited", (Func<string, string>)(path => PrivilegedFileCommands.BuildNoFollowBase64ReadBody(path, 1024))],
    ];

    public static IEnumerable<object[]> Shells => LoginShells.Available().Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Builders))]
    public void ThePathTravelsWithItsBangEscaped(string name, Func<string, string> build)
    {
        string command = build(BangPath);

        Assert.True(
            command.EndsWith(" sh " + EscapedBangPath, StringComparison.Ordinal),
            $"{name} does not end with the escaped path: {command}");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void NoBangIsLeftBareExceptTheScriptsOwn(string name, Func<string, string> build)
    {
        string command = build(BangPath);

        string rest = command
            .Replace(HistoryEscape, string.Empty, StringComparison.Ordinal)
            .Replace("! ", string.Empty, StringComparison.Ordinal)
            .Replace("!=", string.Empty, StringComparison.Ordinal);

        Assert.False(rest.Contains('!', StringComparison.Ordinal), $"{name} leaves a bare ! for the login shell: {command}");
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Read_OfAPathWithABangReturnsTheFile(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/wow!x", "bang-content");

        ShellRun run = await tree.RunAsync(shell, PrivilegedFileCommands.BuildNoFollowBase64ReadBody("srv/wow!x"));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal("bang-content", Encoding.UTF8.GetString(Convert.FromBase64String(run.StandardOutput.Trim())));
    }
}
