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

using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests;

public sealed class SshStartupCommandPolicyTests
{
    [Fact]
    public void BuildPosixExportLine_SingleQuotesMetacharactersAndSingleQuotes()
    {
        string? line = SshStartupCommandPolicy.BuildPosixExportLine(
        [
            "APP_ENV=prod;id",
            "GREETING=it's $(whoami) `uname`",
            "1INVALID=ignored",
            "BAD_NL=line1\nline2"
        ]);

        Assert.Equal(
            "export APP_ENV='prod;id' GREETING='it'\\''s $(whoami) `uname`'",
            line);
    }

    [Fact]
    public void TryNormalizeStartupCommand_RejectsNewlinesAndControlChars()
    {
        Assert.True(SshStartupCommandPolicy.TryNormalizeStartupCommand("  tmux new -As ops  ", out string cmd));
        Assert.Equal("tmux new -As ops", cmd);

        Assert.False(SshStartupCommandPolicy.TryNormalizeStartupCommand("echo ok\nrm -rf /", out _));
        Assert.False(SshStartupCommandPolicy.TryNormalizeStartupCommand("echo\0ok", out _));
        Assert.False(SshStartupCommandPolicy.TryNormalizeStartupCommand("   ", out _));
    }

    [Fact]
    public void BuildInitialShellInput_CombinesExportsAndStartupCommand()
    {
        string? payload = SshStartupCommandPolicy.BuildInitialShellInput(
            ["EDITOR=vim"],
            "tmux new -As ops");

        Assert.Equal("export EDITOR='vim'\ntmux new -As ops\n", payload);
        Assert.Null(SshStartupCommandPolicy.BuildInitialShellInput([], null));
    }

    [Fact]
    public void SshConfigExporter_EmitsSetEnvAndRemoteCommand()
    {
        string exported = SshConfigExporter.ExportSingleHost(new SshConfigHostEntry(
            Alias: "ops-prod",
            HostName: "10.0.0.5",
            User: "admin",
            EnvironmentVariables: ["EDITOR=vim", "APP_ENV=prod value", "BAD=a\nb"],
            StartupCommand: "tmux new -As ops"));

        Assert.Contains("SetEnv EDITOR=vim", exported, StringComparison.Ordinal);
        Assert.Contains("SetEnv \"APP_ENV=prod value\"", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("BAD=", exported, StringComparison.Ordinal);
        Assert.Contains("RemoteCommand tmux new -As ops", exported, StringComparison.Ordinal);
    }
}
