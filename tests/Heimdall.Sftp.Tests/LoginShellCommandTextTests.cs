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
/// The text of every command that reaches the remote account's login shell: csh and tcsh refuse a
/// leading <c>VAR=value</c> word, and csh, tcsh and zsh read <c>!</c> as history expansion.
/// </summary>
public sealed class LoginShellCommandTextTests
{
    private const string HistoryEscape = "'\\!'";

    public static IEnumerable<object[]> Builders =>
    [
        ["listing", (Func<string, string>)SudoDirectoryListing.Build],
        ["delete", (Func<string, string>)RemoteDeleteCommand.Build],
        ["preflight", (Func<string, string>)SftpMetadataPreflight.Build],
        ["reservation", (Func<string, string>)ServerSideNoClobberPublishCommand.BuildDirectoryReservation],
        ["publish", (Func<string, string>)(path => ServerSideNoClobberPublishCommand.BuildFilePublish(path + ".part", path))],
        ["copy-file", (Func<string, string>)(path => ServerSideCopyCommand.Build("/srv/source", path, recursive: false))],
        ["copy-directory", (Func<string, string>)(path => ServerSideCopyCommand.Build("/srv/source", path, recursive: true))],
    ];

    [Theory]
    [MemberData(nameof(Builders))]
    public void NoBuilderStartsWithAVariableAssignment(string name, Func<string, string> build)
    {
        string command = build("/srv/a b");

        Assert.False(
            Regex.IsMatch(command, @"^\s*[A-Za-z_][A-Za-z0-9_]*="),
            $"{name} starts with a VAR=value word, which csh and tcsh refuse: {command}");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void EveryBangTravelsEscaped(string name, Func<string, string> build)
    {
        string command = build("/srv/wow!/it's!");

        Assert.Contains(HistoryEscape, command, StringComparison.Ordinal);
        Assert.False(
            command.Replace(HistoryEscape, string.Empty, StringComparison.Ordinal).Contains('!', StringComparison.Ordinal),
            $"{name} leaves a bare ! for the login shell to expand: {command}");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void NoBuilderLetsTheLoginShellSeeAScriptOperator(string name, Func<string, string> build)
    {
        string command = build("/srv/a");

        // The login shell reads the whole command line. Only the sh -c word, env, find, mkdir, rm
        // and quoted arguments may be unquoted there; the chain itself lives inside single quotes.
        string unquoted = Regex.Replace(command, "'[^']*'", "''");
        foreach (string token in new[] { "&&", "||", "$?", "$(", "2>", "{ ", "if [" })
        {
            Assert.False(unquoted.Contains(token, StringComparison.Ordinal), $"{name} exposes {token} to the login shell: {command}");
        }
    }

    [Fact]
    public void TheDeleteCommand_AssignsTheLocaleThroughEnv()
    {
        Assert.Equal("env LC_ALL=C rm -r -- '/srv/a'", RemoteDeleteCommand.Build("/srv/a"));
    }

    [Fact]
    public void ThePreflight_AssignsTheLocaleThroughEnv_AndNeutralizesTheNotOfItsScript()
    {
        string command = SftpMetadataPreflight.Build("/srv/a");

        Assert.StartsWith("env LC_ALL=C sh -c '", command, StringComparison.Ordinal);
        Assert.Contains("if [ '\\!' -e ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishCommand_IsAConstantScriptWithTheTwoPathsAsArguments()
    {
        string command = ServerSideNoClobberPublishCommand.BuildFilePublish("/s/t.part", "/s/t");

        Assert.Equal(
            "sh -c 'ln -T -- \"$1\" \"$2\"; status=$?; rm -f -- \"$1\"; exit $status' sh '/s/t.part' '/s/t'",
            command);
    }

    [Fact]
    public void TheDirectoryReservation_HasNoShellOperatorsOfItsOwn()
    {
        Assert.Equal("mkdir -- '/srv/a'", ServerSideNoClobberPublishCommand.BuildDirectoryReservation("/srv/a"));
    }
}
