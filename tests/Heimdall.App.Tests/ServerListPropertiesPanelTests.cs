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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The properties panel preference and the credential provider fact, seen through the list view
/// model (issue #557).
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task ThePropertiesPanel_IsOffByDefault()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        Assert.False(fixture.ViewModel.ShowPropertiesPanel);
    }

    [Fact]
    public async Task ShowingThePropertiesPanel_ReachesTheSettings()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        fixture.ViewModel.ShowPropertiesPanel = true;
        await fixture.ViewModel.ViewPreferencePersistence;

        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.True(saved.ShowSessionPropertiesPanel);
    }

    [Fact]
    public async Task APersistedPropertiesPanel_IsAppliedOnLoadWithoutBeingWrittenBack()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.ConfigManager.MergeSettingAsync(persisted =>
        {
            persisted.ShowSessionPropertiesPanel = true;
            persisted.SessionPropertiesPanelHeight = 333;
        });
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        Task persistenceBefore = fixture.ViewModel.ViewPreferencePersistence;

        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));

        Assert.True(fixture.ViewModel.ShowPropertiesPanel);
        Assert.Equal(333d, fixture.ViewModel.PropertiesPanelHeight);
        Assert.Same(persistenceBefore, fixture.ViewModel.ViewPreferencePersistence);
    }

    [Fact]
    public async Task TheCompactSessionTree_IsOffByDefault()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        Assert.False(fixture.ViewModel.CompactSessionTree);
        Assert.False(new AppSettings().CompactSessionTree);
        Assert.False((await fixture.ConfigManager.LoadSettingsAsync()).CompactSessionTree);
    }

    [Fact]
    public async Task TurningOnTheCompactSessionTree_ReachesTheSettings_AndOffClearsIt()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        fixture.ViewModel.CompactSessionTree = true;
        await fixture.ViewModel.ViewPreferencePersistence;
        Assert.True((await fixture.ConfigManager.LoadSettingsAsync()).CompactSessionTree);

        fixture.ViewModel.CompactSessionTree = false;
        await fixture.ViewModel.ViewPreferencePersistence;
        Assert.False((await fixture.ConfigManager.LoadSettingsAsync()).CompactSessionTree);
    }

    [Fact]
    public async Task APersistedCompactSessionTree_IsAppliedOnLoadWithoutBeingWrittenBack()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        List<string?> raised = [];
        fixture.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await fixture.ConfigManager.MergeSettingAsync(persisted => persisted.CompactSessionTree = true);
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        Task persistenceBefore = fixture.ViewModel.ViewPreferencePersistence;

        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));

        Assert.True(fixture.ViewModel.CompactSessionTree);
        Assert.Contains(nameof(fixture.ViewModel.CompactSessionTree), raised);
        Assert.Same(persistenceBefore, fixture.ViewModel.ViewPreferencePersistence);
    }

    /// <summary>A tool row said "Select a session to see its properties", as if nothing were selected.</summary>
    [Fact]
    public async Task ASelectedTool_IsNamedAsATool_NotAsNothingSelected()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto tool = CreateServer("ping", "Ping", "ops");
        tool.ConnectionType = "TOOL:PING";
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), tool);
        List<string?> raised = [];
        fixture.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        fixture.ViewModel.SelectedServer = fixture.ViewModel.AllServers.Single(server => server.Id == "ping");

        Assert.True(fixture.ViewModel.PropertiesPanelShowsTool);
        Assert.False(fixture.ViewModel.PropertiesPanelShowsEmpty);
        Assert.False(fixture.ViewModel.PropertiesPanelShowsSession);
        Assert.Contains(nameof(fixture.ViewModel.PropertiesPanelShowsTool), raised);
    }

    [Fact]
    public async Task NothingSelected_ShowsTheEmptyHint_NotTheToolOne()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        fixture.ViewModel.SelectedServer = null;

        Assert.True(fixture.ViewModel.PropertiesPanelShowsEmpty);
        Assert.False(fixture.ViewModel.PropertiesPanelShowsTool);
    }

    /// <summary>
    /// The window lays the panel out when the height is announced: a height arriving from the
    /// settings, by an import or a reset, used to wait for the next toggle of the panel.
    /// </summary>
    [Fact]
    public async Task AHeightFromTheSettings_IsAnnounced()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        List<string?> raised = [];
        fixture.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Whichever of the two applies it first, the settings change or the load, must announce it.
        await fixture.ConfigManager.MergeSettingAsync(persisted => persisted.SessionPropertiesPanelHeight = 333);
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        Assert.Equal(333d, fixture.ViewModel.PropertiesPanelHeight);
        Assert.Contains(nameof(fixture.ViewModel.PropertiesPanelHeight), raised);
    }

    [Fact]
    public async Task ADraggedPanelHeight_IsClampedAndSaved()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        fixture.ViewModel.CommitPropertiesPanelHeight(310.6);
        await fixture.ViewModel.ViewPreferencePersistence;
        Assert.Equal(311d, fixture.ViewModel.PropertiesPanelHeight);
        Assert.Equal(311, (await fixture.ConfigManager.LoadSettingsAsync()).SessionPropertiesPanelHeight);

        fixture.ViewModel.CommitPropertiesPanelHeight(10);
        await fixture.ViewModel.ViewPreferencePersistence;
        Assert.Equal(Heimdall.App.Services.SessionPropertiesPanelLayout.MinHeight, fixture.ViewModel.PropertiesPanelHeight);
        Assert.Equal(120, (await fixture.ConfigManager.LoadSettingsAsync()).SessionPropertiesPanelHeight);
    }

    [Fact]
    public async Task TheCredentialProviderSetting_ReachesTheAuthenticationLineOfEveryRow()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.ConfigManager.MergeSettingAsync(persisted => persisted.UseExternalCredentialProvider = true);
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        Assert.Equal("SSH agent + Credential provider", fixture.ServerById("alpha").AuthSummary);

        await fixture.ConfigManager.MergeSettingAsync(persisted => persisted.UseExternalCredentialProvider = false);
        fixture.LoadServers(await fixture.ConfigManager.LoadSettingsAsync(), CreateServer("alpha", "Alpha", "ops"));

        Assert.Equal("SSH agent", fixture.ServerById("alpha").AuthSummary);
    }
}
