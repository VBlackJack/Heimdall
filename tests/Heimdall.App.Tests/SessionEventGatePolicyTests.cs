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
/// Tests for <see cref="SessionEventGatePolicy"/>: only the graphical protocols (RDP, VNC,
/// Citrix) log lifecycle events, and only when logging is enabled. The text-terminal and
/// transfer protocols are never event-logged (they fall under the transcript gate instead).
/// </summary>
public sealed class SessionEventGatePolicyTests
{
    [Theory]
    [InlineData("RDP")]
    [InlineData("VNC")]
    [InlineData("CITRIX")]
    [InlineData("rdp")]
    [InlineData("Vnc")]
    [InlineData("citrix")]
    public void ShouldLog_EnabledAndGraphicalProtocol_ReturnsTrue(string connectionType)
    {
        SessionEventGatePolicy.ShouldLog(sessionLoggingEnabled: true, connectionType)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("LOCAL")]
    [InlineData("TELNET")]
    [InlineData("WINRM")]
    [InlineData("SFTP")]
    [InlineData("FTP")]
    [InlineData("TOOL:PING")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldLog_EnabledAndIneligibleType_ReturnsFalse(string? connectionType)
    {
        SessionEventGatePolicy.ShouldLog(sessionLoggingEnabled: true, connectionType)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("RDP")]
    [InlineData("VNC")]
    [InlineData("CITRIX")]
    [InlineData("SSH")]
    [InlineData(null)]
    public void ShouldLog_Disabled_ReturnsFalseForAll(string? connectionType)
    {
        SessionEventGatePolicy.ShouldLog(sessionLoggingEnabled: false, connectionType)
            .Should().BeFalse();
    }
}
