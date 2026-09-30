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

using Heimdall.App.Theming;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// The session tree lot of 2026-09-30, seen through the list view model: search tokens and
/// diacritics, selection hidden by a view change, batched selection, health carried across a
/// reload, filtered folder counts and count-aware wording.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task Search_EveryWhitespaceTokenMustMatch_AcrossTheJoinedFields()
    {
        var timeProvider = new FakeTimeProvider();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("Prod", "Lab"),
            CreateServer("web01", "web01", "Prod"),
            CreateServer("web02", "web02", "Lab"),
            CreateServer("db01", "db01", "Prod"));

        fixture.ViewModel.SearchText = "web prod";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        AssertVisibleServerIds(fixture.ViewModel, "web01");
    }

    [Fact]
    public async Task Search_FoldsDiacriticsOnBothSides()
    {
        var timeProvider = new FakeTimeProvider();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("S\u00E9curit\u00E9", "Lab"),
            CreateServer("fw", "Pare-feu", "S\u00E9curit\u00E9"),
            CreateServer("lab", "Lab box", "Lab"));

        fixture.ViewModel.SearchText = "securite";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        AssertVisibleServerIds(fixture.ViewModel, "fw");

        fixture.ViewModel.SearchText = "S\u00C9CURIT\u00C9";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        AssertVisibleServerIds(fixture.ViewModel, "fw");
    }

    [Fact]
    public async Task SearchHidingTheSelection_ClearingTheSearchRestoresIt()
    {
        var timeProvider = new FakeTimeProvider();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));

        fixture.ViewModel.SearchText = "Alpha";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        Assert.Null(fixture.ViewModel.SelectedServer);
        List<ServerItemViewModel> scrolledTo = [];
        fixture.ViewModel.HiddenSelectionRestored += scrolledTo.Add;

        fixture.ViewModel.SearchText = "";

        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        AssertSelection(fixture.ViewModel, "beta");
        Assert.Equal("beta", Assert.Single(scrolledTo).Id);
    }

    [Fact]
    public async Task CollapsingPartOfAMultiSelection_ReExpandingRestoresAllOfIt()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops", "lab"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "lab"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        fixture.CollapseGroup("lab");
        AssertSelection(fixture.ViewModel, "alpha");

        fixture.FolderByPath("lab").IsExpanded = true;

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task SearchHidingTheSelection_ASelectionMadeMeanwhileWins()
    {
        var timeProvider = new FakeTimeProvider();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        fixture.ViewModel.SearchText = "Alpha";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.SearchText = "";

        AssertSelection(fixture.ViewModel, "alpha");
    }

    [Fact]
    public async Task SearchHidingTheSelection_IsNotPersistedAsADeselection()
    {
        var timeProvider = new FakeTimeProvider();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.LastSelectedServerId = "beta";
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);

        fixture.ViewModel.SearchText = "Alpha";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        Assert.Null(fixture.ViewModel.SelectedServer);
        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Equal("beta", saved.LastSelectedServerId);
    }

    [Fact]
    public async Task CollapsingTheFolderOfTheSelection_ReExpandingRestoresIt()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops", "lab"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "lab"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));

        fixture.CollapseGroup("lab");
        Assert.Null(fixture.ViewModel.SelectedServer);

        fixture.FolderByPath("lab").IsExpanded = true;

        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        AssertSelection(fixture.ViewModel, "beta");
    }

    [Fact]
    public async Task SelectAllVisible_NotifiesTheCountOnce()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto[] servers = Enumerable
            .Range(0, 40)
            .Select(index => CreateServer($"s{index:D2}", $"Server {index:D2}", "ops"))
            .ToArray();
        fixture.LoadServers(fixture.ExpandGroups("ops"), servers);
        fixture.ViewModel.SelectSingle(fixture.ServerById("s00"));
        int countNotifications = 0;
        int collectionNotifications = 0;
        fixture.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerListViewModel.SelectionCountText))
            {
                countNotifications++;
            }
        };
        fixture.ViewModel.SelectedItems.CollectionChanged += (_, _) => collectionNotifications++;

        fixture.ViewModel.SelectAllVisible();

        Assert.Equal(40, fixture.ViewModel.SelectionCount);
        Assert.Equal(1, countNotifications);
        Assert.Equal(1, collectionNotifications);
    }

    [Fact]
    public async Task Reload_CarriesTheHealthVerdictOverByServerId()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        fixture.LoadServers(fixture.ExpandGroups("ops"), alpha);
        var verdict = new Heimdall.Core.SessionHealth.HealthState(
            Heimdall.Core.SessionHealth.HealthStatus.Up, DateTime.UtcNow, 12, null);
        Assert.True(fixture.ViewModel.ApplyServerHealthChange(
            new Heimdall.App.Services.HealthStateChange("alpha", verdict, 1)));

        fixture.LoadServers(fixture.ExpandGroups("ops"), alpha);

        Assert.Same(verdict, fixture.ServerById("alpha").HealthState);
    }

    private static async Task<LocalizationManager> LoadEnglishLocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
