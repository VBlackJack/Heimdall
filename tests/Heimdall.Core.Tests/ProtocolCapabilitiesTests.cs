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

using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public sealed class ProtocolCapabilitiesTests
{
    public static TheoryData<string?, bool> ConnectionTypes =>
        new()
        {
            { "RDP", true },
            { "SSH", true },
            { "SFTP", true },
            { "WINRM", true },
            { "TELNET", false },
            { "VNC", false },
            { "FTP", false },
            { "CITRIX", false },
            { "LOCAL", false },
            { "TOOL:PING", false },
            { "", false },
            { null, false }
        };

    [Theory]
    [MemberData(nameof(ConnectionTypes))]
    public void SupportsSshGateway_ReturnsTrueOnlyForTunnelingProtocols(
        string? connectionType,
        bool expected)
    {
        Assert.Equal(expected, ProtocolCapabilities.SupportsSshGateway(connectionType));
    }

    [Theory]
    [InlineData("rdp")]
    [InlineData("sSh")]
    [InlineData("Sftp")]
    [InlineData("winrm")]
    public void SupportsSshGateway_MatchesProtocolCaseInsensitively(string connectionType)
    {
        Assert.True(ProtocolCapabilities.SupportsSshGateway(connectionType));
    }
}
