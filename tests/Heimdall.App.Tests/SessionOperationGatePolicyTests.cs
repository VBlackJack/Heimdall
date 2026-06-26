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
/// Tests for <see cref="SessionOperationGatePolicy"/>: only the transfer protocols (SFTP, FTP) log
/// file operations, and only when logging is enabled. The text-terminal and graphical protocols are
/// never operation-logged (they fall under the transcript and event gates instead).
/// </summary>
public sealed class SessionOperationGatePolicyTests
{
    [Theory]
    [InlineData("SFTP")]
    [InlineData("FTP")]
    [InlineData("sftp")]
    [InlineData("Ftp")]
    public void ShouldLog_EnabledAndTransferProtocol_ReturnsTrue(string connectionType)
    {
        SessionOperationGatePolicy.ShouldLog(sessionLoggingEnabled: true, connectionType)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("LOCAL")]
    [InlineData("TELNET")]
    [InlineData("WINRM")]
    [InlineData("RDP")]
    [InlineData("VNC")]
    [InlineData("CITRIX")]
    [InlineData("TOOL:PING")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldLog_EnabledAndIneligibleType_ReturnsFalse(string? connectionType)
    {
        SessionOperationGatePolicy.ShouldLog(sessionLoggingEnabled: true, connectionType)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("SFTP")]
    [InlineData("FTP")]
    [InlineData("SSH")]
    [InlineData(null)]
    public void ShouldLog_Disabled_ReturnsFalseForAll(string? connectionType)
    {
        SessionOperationGatePolicy.ShouldLog(sessionLoggingEnabled: false, connectionType)
            .Should().BeFalse();
    }
}
