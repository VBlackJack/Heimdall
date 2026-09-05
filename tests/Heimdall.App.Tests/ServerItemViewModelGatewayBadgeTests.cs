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
/// The gateway badge is optional for a resolved gateway and mandatory for a missing one.
/// </summary>
/// <remarks>
/// One gateway in front of every session painted the same badge on every row. Hiding it must not
/// hide the warning a missing gateway carries, and must leave the route reachable on hover.
/// </remarks>
public sealed class ServerItemViewModelGatewayBadgeTests
{
    private static readonly IReadOnlyDictionary<string, SshGatewayDto> Gateways =
        new Dictionary<string, SshGatewayDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["gw1"] = new SshGatewayDto { Id = "gw1", Name = "Bastion", Host = "bastion.example.test" }
        };

    [Fact]
    public void HidingTheBadge_HidesAResolvedGatewayOnly()
    {
        ServerItemViewModel routed = CreateServer("routed", "gw1");
        ServerItemViewModel orphaned = CreateServer("orphaned", "gone");
        Assert.True(routed.IsGatewayBadgeVisible);
        Assert.True(orphaned.IsGatewayBadgeVisible);

        routed.ShowGatewayBadge = false;
        orphaned.ShowGatewayBadge = false;

        Assert.False(routed.IsGatewayBadgeVisible);
        Assert.True(orphaned.IsGatewayBadgeVisible);
    }

    [Fact]
    public void HidingTheBadge_MovesTheGatewayIntoTheRowTooltip()
    {
        ServerItemViewModel routed = CreateServer("routed", "gw1");
        List<string?> changed = [];
        routed.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        string shown = Assert.IsType<string>(routed.RowTooltipText);
        Assert.DoesNotContain("Bastion", shown, StringComparison.Ordinal);

        routed.ShowGatewayBadge = false;

        string hidden = Assert.IsType<string>(routed.RowTooltipText);
        Assert.Contains("Gateway: Bastion", hidden, StringComparison.Ordinal);
        Assert.Contains(nameof(ServerItemViewModel.RowTooltipText), changed);
    }

    [Fact]
    public void ThePreferenceSurvivesAGatewayRefresh()
    {
        ServerItemViewModel routed = CreateServer("routed", "gw1");
        routed.ShowGatewayBadge = false;

        routed.RefreshGatewayState(Gateways);

        Assert.True(routed.HasGateway);
        Assert.False(routed.IsGatewayBadgeVisible);
    }

    [Fact]
    public void ADirectSession_NeverShowsABadgeNorAGatewayLine()
    {
        ServerItemViewModel direct = CreateServer("direct", null);
        direct.ShowGatewayBadge = false;

        Assert.False(direct.IsGatewayBadgeVisible);
        Assert.DoesNotContain("Gateway", direct.RowTooltipText ?? "", StringComparison.Ordinal);
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
