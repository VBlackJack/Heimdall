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
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task Filter_OpensEveryBranchHoldingAMatch_OnAProfileWithNothingExpanded()
    {
        FakeTimeProvider timeProvider = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha Node", "ops/web"),
            CreateServer("beta", "Beta Node", "ops/db"));
        FolderViewModel ops = fixture.FolderByPath("ops");
        FolderViewModel web = fixture.FolderByPath("ops/web");
        Assert.False(ops.IsExpanded);
        Assert.False(web.IsExpanded);

        fixture.ViewModel.SearchText = "Alpha";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        Assert.True(ops.IsExpanded);
        Assert.True(web.IsExpanded);
        Assert.Equal(
            ["alpha"],
            SelectionHelpers
                .EnumerateVisibleLeaves(fixture.ViewModel.GroupedServers)
                .Select(server => server.Id));
    }

    [Fact]
    public async Task Filter_OpensEveryMatchingBranch_WithNoResultCountCeiling()
    {
        FakeTimeProvider timeProvider = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("n0", "Node 0", "ops/f0"),
            CreateServer("n1", "Node 1", "ops/f1"),
            CreateServer("n2", "Node 2", "ops/f2"),
            CreateServer("n3", "Node 3", "ops/f3"),
            CreateServer("n4", "Node 4", "ops/f4"),
            CreateServer("n5", "Node 5", "ops/f5"));
        FolderViewModel[] branches =
        [
            .. Enumerable
                .Range(0, 6)
                .Select(index => fixture.FolderByPath($"ops/f{index}"))
        ];

        fixture.ViewModel.SearchText = "Node";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        Assert.All(branches, branch => Assert.True(branch.IsExpanded));
        Assert.Equal(6, fixture.ViewModel.FilteredCount);

        // Read through the tree the way the UI does: every match must be reachable, which also
        // fails if the ceiling is placed on the parent branch rather than on the leaf ones.
        Assert.Equal(
            ["n0", "n1", "n2", "n3", "n4", "n5"],
            SelectionHelpers
                .EnumerateVisibleLeaves(fixture.ViewModel.GroupedServers)
                .Select(server => server.Id)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FilterClear_RestoresTheExpansionTheUserChose()
    {
        FakeTimeProvider timeProvider = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops/web"),
            CreateServer("beta", "Beta Node", "ops/db"));
        FolderViewModel ops = fixture.FolderByPath("ops");
        FolderViewModel web = fixture.FolderByPath("ops/web");
        FolderViewModel db = fixture.FolderByPath("ops/db");

        fixture.ViewModel.SearchText = "Node";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        Assert.True(web.IsExpanded);
        Assert.True(db.IsExpanded);

        fixture.ViewModel.SearchText = "";

        Assert.True(ops.IsExpanded);
        Assert.False(web.IsExpanded);
        Assert.False(db.IsExpanded);
    }

    [Fact]
    public async Task FilterExpansion_NeverReachesSettings_EvenWhenTheAppClosesFiltered()
    {
        FakeTimeProvider timeProvider = new();
        RecordingConfigManager configManager = new();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(
            configManager: configManager,
            timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops/web"),
            CreateServer("beta", "Beta Node", "ops/db"));
        FolderViewModel web = fixture.FolderByPath("ops/web");

        fixture.ViewModel.SearchText = "Node";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        Assert.True(web.IsExpanded);

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal(0, configManager.MergeSettingCallCount);
        Assert.Empty(configManager.PersistedSnapshots);
    }

    [Fact]
    public async Task FilterCollapse_ByHand_SurvivesLaterPassesOfTheSameFilter()
    {
        FakeTimeProvider timeProvider = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops/web"),
            CreateServer("beta", "Beta Node", "ops/db"));
        FolderViewModel web = fixture.FolderByPath("ops/web");
        FolderViewModel db = fixture.FolderByPath("ops/db");

        fixture.ViewModel.SearchText = "Node";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        Assert.True(web.IsExpanded);

        web.IsExpanded = false;
        fixture.ViewModel.SearchText = "Nod";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        Assert.Equal(2, fixture.ViewModel.FilteredCount);
        Assert.False(web.IsExpanded);
        Assert.True(db.IsExpanded);
    }

    [Fact]
    public async Task FilterCollapse_ByHand_DoesNotOutliveTheFilterOrReachSettings()
    {
        FakeTimeProvider timeProvider = new();
        RecordingConfigManager configManager = new();
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync(
            configManager: configManager,
            timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops", "ops/web"),
            CreateServer("alpha", "Alpha Node", "ops/web"),
            CreateServer("beta", "Beta Node", "ops/db"));
        FolderViewModel web = fixture.FolderByPath("ops/web");
        FolderViewModel db = fixture.FolderByPath("ops/db");

        fixture.ViewModel.SearchText = "Node";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        Assert.True(db.IsExpanded);

        web.IsExpanded = false;
        fixture.ViewModel.SearchText = "";

        Assert.True(web.IsExpanded);
        Assert.False(db.IsExpanded);

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal(0, configManager.MergeSettingCallCount);
    }

    [Fact]
    public async Task UnfilteredCollapse_StillOutlivesALaterFilter()
    {
        FakeTimeProvider timeProvider = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops", "ops/web"),
            CreateServer("alpha", "Alpha Node", "ops/web"),
            CreateServer("beta", "Beta Node", "ops/db"));
        FolderViewModel web = fixture.FolderByPath("ops/web");

        web.IsExpanded = false;
        fixture.ViewModel.SearchText = "Node";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        Assert.True(web.IsExpanded);

        fixture.ViewModel.SearchText = "";

        Assert.False(web.IsExpanded);
    }
}
