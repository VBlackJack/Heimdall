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

using System.Windows;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// A reorder that keeps every row in its folder must not regenerate the tree rows.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task Nudge_KeepsTheFolderInstances_AndDoesNotRebuildTheTree()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("c", "C", "ops"));
        FolderViewModel originalFolder = FolderByPath(fixture.ViewModel, "ops");
        int buildCount = fixture.ViewModel.StableTreeBuildCount;

        Assert.True(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("b"), -1));

        Assert.Same(originalFolder, FolderByPath(fixture.ViewModel, "ops"));
        Assert.Equal(buildCount, fixture.ViewModel.StableTreeBuildCount);
        Assert.Equal(["b", "a", "c"], originalFolder.Servers.Select(server => server.Id));
    }

    [Fact]
    public async Task Reorder_IntoAnotherFolder_StillRebuildsTheTree()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "lab"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("x", "X", "lab"));
        int buildCount = fixture.ViewModel.StableTreeBuildCount;

        Assert.True(await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("b")], fixture.ServerById("x"), placeAfter: true));

        Assert.True(fixture.ViewModel.StableTreeBuildCount > buildCount);
        Assert.Equal(["x", "b"], FolderByPath(fixture.ViewModel, "lab").Servers.Select(server => server.Id));
    }

    [Fact]
    public async Task Reorder_OfARowMovedOnDiskBySomethingElse_StillRebuildsTheTree()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "lab"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "lab"));
        await fixture.ConfigManager.MutateServersAsync(dtos =>
        {
            dtos.Single(dto => dto.Id == "b").Group = "ops";
            return true;
        });

        Assert.True(await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("b")], fixture.ServerById("a"), placeAfter: true));

        Assert.Equal(["a", "b"], FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
        Assert.DoesNotContain(fixture.ViewModel.GroupedServers, folder => folder.FullPath == "lab");
    }

    [Fact]
    public async Task UndoOfANudge_RestoresTheOrder()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"),
            CreateServer("c", "C", "ops"));

        Assert.True(await fixture.ViewModel.NudgeServerAsync(fixture.ServerById("c"), -1));
        Assert.Equal(["a", "c", "b"], FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));

        await fixture.ViewModel.UndoTreeOrganizationCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b", "c"], FolderByPath(fixture.ViewModel, "ops").Servers.Select(server => server.Id));
    }

    [Theory]
    [InlineData(10, 30, false)]
    [InlineData(-5, 15, true)]
    [InlineData(90, 110, true)]
    [InlineData(-30, -10, true)]
    [InlineData(0, 100, false)]
    public void NeedsBringIntoView_IsTrueUnlessTheRowIsVerticallyInsideTheViewport(
        double rowTop,
        double rowBottom,
        bool expected)
    {
        Rect viewport = new(0, 0, 200, 100);
        Rect row = new(0, rowTop, 200, rowBottom - rowTop);

        Assert.Equal(expected, TreeInteractionState.NeedsBringIntoView(row, viewport));
    }

    [Fact]
    public void NeedsBringIntoView_IgnoresASidewaysClippedRow()
    {
        Rect viewport = new(0, 0, 200, 100);

        Assert.False(TreeInteractionState.NeedsBringIntoView(new Rect(150, 20, 200, 20), viewport));
    }

    [Fact]
    public void NeedsBringIntoView_ARowTallerThanTheViewportNeverFits()
    {
        Assert.True(TreeInteractionState.NeedsBringIntoView(new Rect(0, 0, 200, 300), new Rect(0, 0, 200, 100)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 0)]
    [InlineData(0, 20)]
    public void NeedsBringIntoView_FallsBackToTrue_ForARowWithoutSize(double width, double height)
    {
        Assert.True(TreeInteractionState.NeedsBringIntoView(new Rect(0, 10, width, height), new Rect(0, 0, 200, 100)));
        Assert.True(TreeInteractionState.NeedsBringIntoView(Rect.Empty, new Rect(0, 0, 200, 100)));
    }
}
