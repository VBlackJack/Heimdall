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
/// The session selected at close is the one selected at the next start.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task LoadServers_RestoresTheLastSelectedSession()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.LastSelectedServerId = "beta";

        fixture.LoadServers(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));

        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.True(fixture.ViewModel.ShowSessionDetail);
    }

    [Fact]
    public async Task LoadServers_IgnoresAnUnknownLastSelection()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.LastSelectedServerId = "ghost";

        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));

        Assert.Null(fixture.ViewModel.SelectedServer);
    }

    [Fact]
    public async Task LoadServers_KeepsTheCurrentSelectionOverThePersistedOne()
    {
        // A reload after a folder operation must not jump back to what the settings remember.
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.LastSelectedServerId = "beta";

        fixture.LoadServers(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));

        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task CloseFlush_PersistsTheSelection_OnlyWhenItChanged()
    {
        var configManager = new RecordingConfigManager();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal("beta", configManager.Settings.LastSelectedServerId);
        int writesAfterFirstFlush = configManager.PersistedSnapshots.Count;

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal(writesAfterFirstFlush, configManager.PersistedSnapshots.Count);
    }

    [Fact]
    public async Task CloseFlush_PersistsAClearedSelection()
    {
        var configManager = new RecordingConfigManager();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.LastSelectedServerId = "alpha";
        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);

        fixture.ViewModel.ClearSelection();
        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Null(configManager.Settings.LastSelectedServerId);
    }
}
