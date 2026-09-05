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
/// The gateway facet and the badge preference, seen through the list view model.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task GatewayFilter_KeepsRoutedSessions_MissingGatewayIncluded()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            await SettingsWithBastionAsync(fixture),
            RoutedServer("alpha", "gw1"),
            RoutedServer("beta", "gone"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.GatewayFilterEnabled = true;

        Assert.True(fixture.ViewModel.HasActiveFacetFilter);
        Assert.Equal(2, fixture.ViewModel.FilteredCount);
        Assert.Equal(
            ["alpha", "beta"],
            fixture.ViewModel.Servers.Select(server => server.Id).OrderBy(id => id, StringComparer.Ordinal));

        fixture.ViewModel.GatewayFilterEnabled = false;

        Assert.False(fixture.ViewModel.HasActiveFacetFilter);
        Assert.Equal(3, fixture.ViewModel.FilteredCount);
    }

    [Fact]
    public async Task HidingTheGatewayBadge_ReachesEveryRowAndTheSettings()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            await SettingsWithBastionAsync(fixture),
            RoutedServer("alpha", "gw1"),
            RoutedServer("beta", "gone"),
            CreateServer("gamma", "Gamma", "ops"));
        Assert.True(fixture.ServerById("alpha").IsGatewayBadgeVisible);

        fixture.ViewModel.ShowGatewayBadge = false;
        await fixture.ViewModel.ViewPreferencePersistence;

        Assert.False(fixture.ServerById("alpha").IsGatewayBadgeVisible);
        Assert.True(fixture.ServerById("beta").IsGatewayBadgeVisible);
        Assert.False(fixture.ServerById("gamma").IsGatewayBadgeVisible);
        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.False(saved.ShowGatewayBadge);
    }

    [Fact]
    public async Task APersistedBadgePreference_IsAppliedOnLoadWithoutBeingWrittenBack()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.ConfigManager.MergeSettingAsync(persisted => persisted.ShowGatewayBadge = false);
        AppSettings settings = await SettingsWithBastionAsync(fixture);
        Task persistenceBefore = fixture.ViewModel.ViewPreferencePersistence;

        fixture.LoadServers(settings, RoutedServer("alpha", "gw1"));

        Assert.False(fixture.ViewModel.ShowGatewayBadge);
        Assert.False(fixture.ServerById("alpha").IsGatewayBadgeVisible);
        Assert.Same(persistenceBefore, fixture.ViewModel.ViewPreferencePersistence);
    }

    [Fact]
    public async Task ABadgePreference_ReachesRowsAddedAfterItWasSet()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await SettingsWithBastionAsync(fixture), RoutedServer("alpha", "gw1"));
        fixture.ViewModel.ShowGatewayBadge = false;
        await fixture.ViewModel.ViewPreferencePersistence;

        // A reload builds fresh rows through the same path every add, edit and move ends in.
        AppSettings settings = await SettingsWithBastionAsync(fixture);
        fixture.LoadServers(settings, RoutedServer("alpha", "gw1"), RoutedServer("delta", "gw1"));

        Assert.False(fixture.ServerById("delta").IsGatewayBadgeVisible);
    }

    /// <summary>
    /// The gateway has to be on disk, not only in the object handed to LoadServers: saving the
    /// badge preference raises a settings change, and the rows re-resolve their gateway from the
    /// settings that change carries. A gateway known only in memory would come back as missing.
    /// </summary>
    private static async Task<AppSettings> SettingsWithBastionAsync(ServerListSelectionFixture fixture)
    {
        await fixture.ConfigManager.MergeSettingAsync(settings =>
        {
            settings.TreeExpandedNodes.Add("ops");
            settings.SshGateways.Add(new SshGatewayDto
            {
                Id = "gw1",
                Name = "Bastion",
                Host = "bastion.example.com"
            });
        });
        return await fixture.ConfigManager.LoadSettingsAsync();
    }

    private static ServerProfileDto RoutedServer(string id, string gatewayId)
    {
        ServerProfileDto server = CreateServer(id, id, "ops");
        server.SshGatewayId = gatewayId;
        return server;
    }
}
