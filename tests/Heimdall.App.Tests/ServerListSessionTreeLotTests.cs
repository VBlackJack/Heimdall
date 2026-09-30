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

    private static async Task<LocalizationManager> LoadEnglishLocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
