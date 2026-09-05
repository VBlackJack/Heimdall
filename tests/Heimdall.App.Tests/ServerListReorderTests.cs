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

using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Manual ordering: the rank of an unordered session, a positioned drop, a keyboard nudge.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task TreeOrder_RanksOrderedSessionsFirst_ThenUnorderedAlphabetically()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("zulu", "Zulu", "ops"),
            CreateServer("second", "Second", "ops", sortOrder: 20),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("first", "First", "ops", sortOrder: 10));

        Assert.Equal(
            ["first", "second", "alpha", "zulu"],
            FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
    }

    [Fact]
    public async Task Reorder_PlacesTheDraggedSessionsAfterTheAnchor_AndRenumbersByTens()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("c", "C", "ops"),
            CreateServer("d", "D", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("d"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("a"));

        bool written = await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("d"), fixture.ServerById("a")],
            fixture.ServerById("b"),
            placeAfter: true);

        Assert.True(written);
        Assert.Equal(
            ["b", "a", "d", "c"],
            FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
        List<ServerProfileDto> saved = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal(10, saved.Single(server => server.Id == "b").SortOrder);
        Assert.Equal(20, saved.Single(server => server.Id == "a").SortOrder);
        Assert.Equal(30, saved.Single(server => server.Id == "d").SortOrder);
        Assert.Equal(40, saved.Single(server => server.Id == "c").SortOrder);
        AssertSelection(fixture.ViewModel, "a", "d");
    }

    [Fact]
    public async Task Reorder_BeforeTheFirstRow_PutsTheSessionAtTheTop()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("c", "C", "ops"));

        await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("c")],
            fixture.ServerById("a"),
            placeAfter: false);

        Assert.Equal(
            ["c", "a", "b"],
            FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
    }

    [Fact]
    public async Task Reorder_OntoARowOfAnotherFolder_MovesAndPositions()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "lab"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("x", "X", "lab"),
            CreateServer("y", "Y", "lab"));

        await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("b")],
            fixture.ServerById("x"),
            placeAfter: true);

        Assert.Equal(["a"], FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
        Assert.Equal(["x", "b", "y"], FolderByPath(fixture.ViewModel, "lab").Servers.Select(server => server.Id));
        List<ServerProfileDto> saved = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal("lab", saved.Single(server => server.Id == "b").Group);
    }

    [Fact]
    public async Task Reorder_OntoItself_WritesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"));

        bool written = await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("a"), fixture.ServerById("b")],
            fixture.ServerById("b"),
            placeAfter: true);

        Assert.False(written);
        List<ServerProfileDto> saved = await fixture.ConfigManager.LoadServersAsync();
        Assert.All(saved, server => Assert.Equal(SessionOrdering.Unordered, server.SortOrder));
    }

    [Fact]
    public async Task Nudge_MovesOneStep_AndStopsAtTheEdges()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("c", "C", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("b"));

        Assert.True(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("b"), -1));
        Assert.Equal(["b", "a", "c"], FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
        Assert.False(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("b"), -1));

        Assert.True(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("b"), 1));
        Assert.True(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("b"), 1));
        Assert.Equal(["a", "c", "b"], FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
        Assert.False(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("b"), 1));
        AssertSelection(fixture.ViewModel, "b");
    }

    [Theory]
    [InlineData(0, 24, DropInsertion.Before)]
    [InlineData(11, 24, DropInsertion.Before)]
    [InlineData(12, 24, DropInsertion.After)]
    [InlineData(23, 24, DropInsertion.After)]
    [InlineData(5, 0, DropInsertion.Before)]
    public void ResolveInsertion_SplitsTheRowAtItsMiddle(double pointerY, double rowHeight, DropInsertion expected)
    {
        Assert.Equal(expected, TreeInteractionState.ResolveInsertion(pointerY, rowHeight));
    }

    [Fact]
    public void CanReorderOnto_RefusesARowAmongTheDragged()
    {
        ServerItemViewModel a = ServerItemViewModel.FromDto(CreateServer("a", "A", "ops"));
        ServerItemViewModel b = ServerItemViewModel.FromDto(CreateServer("b", "B", "ops"));

        Assert.False(TreeInteractionState.CanReorderOnto([a, b], b));
        Assert.True(TreeInteractionState.CanReorderOnto([a], b));
    }

    [Theory]
    [InlineData(0, long.MaxValue)]
    [InlineData(10, 10)]
    [InlineData(-5, -5)]
    public void RankOf_SendsUnorderedLast(int sortOrder, long expected)
    {
        Assert.Equal(expected, SessionOrdering.RankOf(sortOrder));
    }
}
