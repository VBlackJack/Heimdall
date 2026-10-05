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

using System.Net.Sockets;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Tests;

/// <summary>
/// The health monitor leaves gateway-fronted servers alone, and every reason it publishes is one
/// the tooltip can name.
/// </summary>
public sealed class SessionHealthMonitorReasonTests
{
    [Fact]
    public void UnprobedReason_AnSshGatewayOnTheProfile_IsBehindGateway()
    {
        ServerProfileDto dto = new() { Id = "s", ConnectionType = "SSH", SshGatewayId = "gw" };

        Assert.Equal("behind-gateway", SessionHealthMonitor.UnprobedReason(dto, new AppSettings()));
    }

    [Fact]
    public void UnprobedReason_AnSshGatewayInheritedFromTheGroup_IsBehindGateway()
    {
        ServerProfileDto dto = new() { Id = "s", ConnectionType = "SSH", Group = "PROD/Linux" };
        AppSettings settings = new();
        settings.GroupDefaults["PROD"] = new GroupDefaultsDto { SshGatewayId = "gw" };

        Assert.Equal("behind-gateway", SessionHealthMonitor.UnprobedReason(dto, settings));
    }

    [Fact]
    public void UnprobedReason_AnRdpProfileBehindAnRdGateway_IsBehindRdGateway()
    {
        ServerProfileDto dto = new() { Id = "s", ConnectionType = "RDP", RdpGateway = "rdgw.example.com" };

        Assert.Equal("behind-rd-gateway", SessionHealthMonitor.UnprobedReason(dto, new AppSettings()));
    }

    [Fact]
    public void UnprobedReason_ADirectServer_IsProbed()
    {
        ServerProfileDto dto = new() { Id = "s", ConnectionType = "RDP", Group = "PROD" };
        AppSettings settings = new();
        settings.GroupDefaults["PROD"] = new GroupDefaultsDto { SshUsername = "admin" };

        Assert.Null(SessionHealthMonitor.UnprobedReason(dto, settings));
    }

    [Theory]
    [InlineData(SocketError.TryAgain, "dns")]
    [InlineData(SocketError.NoData, "dns")]
    [InlineData(SocketError.NetworkDown, "unreachable")]
    [InlineData(SocketError.AddressNotAvailable, "unreachable")]
    [InlineData(SocketError.ConnectionReset, "refused")]
    [InlineData(SocketError.AccessDenied, "other")]
    public void ReasonFor_EverySocketError_MapsToATagTheTooltipNames(SocketError code, string expected)
    {
        Assert.Equal(expected, TcpHealthProbe.ReasonFor(code));
    }

    [Fact]
    public void EverySocketError_LandsOnALocalizedReason()
    {
        foreach (SocketError code in Enum.GetValues<SocketError>())
        {
            Assert.NotNull(HealthReasonLocalizer.ReasonKey(TcpHealthProbe.ReasonFor(code)));
        }
    }
}
