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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The "via gateway" facet keeps every routed session, whether its gateway still exists or not.
/// </summary>
public sealed class ServerFilterSpecGatewayTests
{
    private static readonly IReadOnlyDictionary<string, SshGatewayDto> Gateways =
        new Dictionary<string, SshGatewayDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["gw1"] = new SshGatewayDto { Id = "gw1", Name = "Bastion", Host = "bastion.example.test" }
        };

    [Fact]
    public void GatewayOnly_KeepsRoutedSessionsAndDropsDirectOnes()
    {
        ServerFilterSpec spec = ServerFilterSpec.Create(null, gatewayOnly: true);

        Assert.True(spec.IsActive);
        Assert.True(spec.Matches(CreateServer("routed", "gw1")));
        Assert.True(spec.Matches(CreateServer("orphaned", "gone")));
        Assert.False(spec.Matches(CreateServer("direct", null)));
    }

    [Fact]
    public void GatewayOnly_IsOffByDefault()
    {
        ServerFilterSpec spec = ServerFilterSpec.Create(null);

        Assert.False(spec.GatewayOnly);
        Assert.False(spec.IsActive);
        Assert.True(spec.Matches(CreateServer("direct", null)));
    }

    [Fact]
    public void HasGateway_ReportsTheRoute_NotTheBadge()
    {
        ServerItemViewModel routed = CreateServer("routed", "gw1");
        ServerItemViewModel orphaned = CreateServer("orphaned", "gone");
        ServerItemViewModel direct = CreateServer("direct", null);

        Assert.True(routed.HasGateway);
        Assert.False(routed.IsGatewayMissing);
        Assert.True(orphaned.HasGateway);
        Assert.True(orphaned.IsGatewayMissing);
        Assert.False(direct.HasGateway);
    }

    private static ServerItemViewModel CreateServer(string id, string? gatewayId) =>
        ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = id,
                DisplayName = id,
                RemoteServer = $"{id}.example.test",
                ConnectionType = "SSH",
                SshGatewayId = gatewayId
            },
            gatewayMap: Gateways);
}
