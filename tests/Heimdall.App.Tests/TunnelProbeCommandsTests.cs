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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The probe commands sent through an SSH tunnel pass the host and the port to a single-quoted
/// script as positional parameters, so no value is ever part of the script text.
/// </summary>
public sealed class TunnelProbeCommandsTests
{
    private const string ArgumentSeparator = " _ ";

    public static TheoryData<string> HostileHosts => new()
    {
        "$(id)",
        "`id`",
        "host; rm -rf /",
        "a'b",
        "a\"b",
        "host name",
        "$(touch /tmp/x)'$(id)'",
    };

    [Theory]
    [MemberData(nameof(HostileHosts))]
    public void ConnectCommand_KeepsAHostileHostOutOfTheScriptText(string host)
    {
        string command = TunnelProbeCommands.BuildConnectCommand(host, 443);

        AssertValueOnlyInTheArguments(command, host);
    }

    [Theory]
    [MemberData(nameof(HostileHosts))]
    public void BannerCommand_KeepsAHostileHostOutOfTheScriptText(string host)
    {
        string command = TunnelProbeCommands.BuildBannerCommand(host, 22, 256);

        AssertValueOnlyInTheArguments(command, host);
    }

    [Fact]
    public void ConnectCommand_PassesHostAndPortAsSeparatelyQuotedArguments()
    {
        string command = TunnelProbeCommands.BuildConnectCommand("$(id)", 8080);

        Assert.Equal(
            "timeout 2 bash -c 'echo >/dev/tcp/\"$1\"/\"$2\"' _ '$(id)' '8080' 2>/dev/null && echo OPEN || echo CLOSED",
            command);
    }

    [Fact]
    public void ConnectCommand_EscapesASingleQuoteInTheHost()
    {
        string command = TunnelProbeCommands.BuildConnectCommand("a'b", 80);

        Assert.Contains("_ 'a'\\''b' '80' ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BannerCommand_ReadsTheBannerThroughTheSameParameters()
    {
        string command = TunnelProbeCommands.BuildBannerCommand("example.com", 25, 512);

        Assert.Equal(
            "timeout 2 bash -c 'cat < /dev/tcp/\"$1\"/\"$2\"' _ 'example.com' '25' 2>/dev/null | head -c 512",
            command);
    }

    [Theory]
    [InlineData("example.com", 443, true)]
    [InlineData("10.0.0.1", 1, true)]
    [InlineData("$(id)", 443, false)]
    [InlineData("`id`", 443, false)]
    [InlineData("host name", 443, false)]
    [InlineData("a'b", 443, false)]
    [InlineData("", 443, false)]
    [InlineData("example.com", 0, false)]
    [InlineData("example.com", 65536, false)]
    public void IsProbeTargetValid_AcceptsOnlyAHostnameOrAddressAndAPortInRange(string host, int port, bool expected)
    {
        Assert.Equal(expected, TunnelProbeCommands.IsProbeTargetValid(host, port));
    }

    private static void AssertValueOnlyInTheArguments(string command, string value)
    {
        int argumentsStart = command.IndexOf(ArgumentSeparator, StringComparison.Ordinal);
        Assert.True(argumentsStart > 0);
        string scriptPart = command[..argumentsStart];
        Assert.DoesNotContain(value, scriptPart, StringComparison.Ordinal);
        Assert.DoesNotContain("$(", scriptPart, StringComparison.Ordinal);
        Assert.DoesNotContain("`", scriptPart, StringComparison.Ordinal);
    }
}
