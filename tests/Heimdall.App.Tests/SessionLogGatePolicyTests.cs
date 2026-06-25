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

using FluentAssertions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Tests for <see cref="SessionLogGatePolicy"/>: only the interactive text terminals
/// (SSH, Telnet, Local Shell) auto-start, and only when logging is enabled. WinRM and the
/// graphical/transfer protocols never auto-start.
/// </summary>
public sealed class SessionLogGatePolicyTests
{
    [Theory]
    [InlineData("SSH")]
    [InlineData("TELNET")]
    [InlineData("LOCAL")]
    [InlineData("ssh")]
    [InlineData("Telnet")]
    public void ShouldAutoStart_EnabledAndEligibleTerminal_ReturnsTrue(string connectionType)
    {
        SessionLogGatePolicy.ShouldAutoStart(sessionLoggingEnabled: true, connectionType)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoStart_EnabledAndWinRm_ReturnsFalse()
    {
        // WinRM credential bootstrap output could be replayed into the stream — manual-only.
        SessionLogGatePolicy.ShouldAutoStart(sessionLoggingEnabled: true, "WINRM")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("RDP")]
    [InlineData("VNC")]
    [InlineData("CITRIX")]
    [InlineData("SFTP")]
    [InlineData("FTP")]
    [InlineData("TOOL:PING")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldAutoStart_EnabledAndIneligibleType_ReturnsFalse(string? connectionType)
    {
        SessionLogGatePolicy.ShouldAutoStart(sessionLoggingEnabled: true, connectionType)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("TELNET")]
    [InlineData("LOCAL")]
    [InlineData("WINRM")]
    [InlineData("RDP")]
    public void ShouldAutoStart_Disabled_ReturnsFalseForAll(string connectionType)
    {
        SessionLogGatePolicy.ShouldAutoStart(sessionLoggingEnabled: false, connectionType)
            .Should().BeFalse();
    }
}
