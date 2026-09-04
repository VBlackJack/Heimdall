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
/// Rows resolve their gateway once, against the inventory they were built with. Renaming a
/// gateway changes that inventory and nothing about the profiles, so nothing rebuilds the rows.
/// Seen live on 2026-09-04: two rows carried two different names for one gateway id at the same
/// instant, the one that happened to have been saved through the editor being the only one to
/// have picked up the rename.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task GatewayRename_ReachesRowsThatWereNotEditedThemselves()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            GatewaySettings("gw-1", "Paris datacentre"),
            ServerVia("alpha", "gw-1"),
            ServerVia("beta", "gw-1"));

        // Every surface the row resolves from the inventory, before the rename.
        foreach (string id in new[] { "alpha", "beta" })
        {
            Assert.Contains("Paris datacentre", fixture.ServerById(id).GatewayBadgeText);
        }

        await fixture.ConfigManager.SaveSettingsAsync(GatewaySettings("gw-1", "Berlin datacentre"));

        foreach (string id in new[] { "alpha", "beta" })
        {
            ServerItemViewModel row = fixture.ServerById(id);

            // The badge, the tooltip, the name a screen reader announces, and the detail pane
            // line all come from one resolution, so all four are asserted: a fix that refreshed
            // only the badge would leave the detail pane saying the old name.
            Assert.Contains("Berlin datacentre", row.GatewayBadgeText);
            Assert.DoesNotContain("Paris datacentre", row.GatewayBadgeText);
            Assert.Contains("Berlin datacentre", row.GatewayBadgeTooltip);
            Assert.Equal("Berlin datacentre", row.GatewayName);
            Assert.Equal("Berlin datacentre", row.GatewayDetailText);
        }
    }

    [Fact]
    public async Task GatewayDeletion_TurnsRowsIntoTheMissingGatewayState()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(GatewaySettings("gw-1", "Paris datacentre"), ServerVia("alpha", "gw-1"));

        Assert.False(fixture.ServerById("alpha").IsGatewayMissing);

        await fixture.ConfigManager.SaveSettingsAsync(new AppSettings());

        ServerItemViewModel row = fixture.ServerById("alpha");
        Assert.True(row.IsGatewayMissing);
        Assert.DoesNotContain("Paris datacentre", row.GatewayBadgeText);
    }

    [Fact]
    public async Task Dispose_ReleasesTheSettingsSubscription()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(GatewaySettings("gw-1", "Paris datacentre"), ServerVia("alpha", "gw-1"));

        fixture.ViewModel.Dispose();
        int scheduledBeforeTheChange = fixture.Dispatcher.InvokeAsyncCalls;
        await fixture.ConfigManager.SaveSettingsAsync(GatewaySettings("gw-1", "Berlin datacentre"));

        // Asserting on the row would prove nothing here: the handler also guards on its disposed
        // flag, so a leaked subscription leaves the row stale exactly as a released one does. What
        // separates the two is whether the handler ran at all, and the dispatcher counts that.
        Assert.Equal(scheduledBeforeTheChange, fixture.Dispatcher.InvokeAsyncCalls);
        Assert.Contains("Paris datacentre", fixture.ServerById("alpha").GatewayBadgeText);
    }

    private static AppSettings GatewaySettings(string gatewayId, string gatewayName)
    {
        AppSettings settings = new();
        settings.SshGateways.Add(new SshGatewayDto
        {
            Id = gatewayId,
            Name = gatewayName,
            Host = "127.0.0.1",
            Port = 22,
            User = "bastion"
        });

        return settings;
    }

    private static ServerProfileDto ServerVia(string id, string gatewayId)
    {
        ServerProfileDto server = CreateServer(id, id, "ops");
        server.SshGatewayId = gatewayId;
        server.UseDirectConnection = false;
        return server;
    }
}
