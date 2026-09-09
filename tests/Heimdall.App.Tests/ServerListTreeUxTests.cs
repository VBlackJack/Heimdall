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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task TreeFilters_RemovableChipsAndResetPreserveViewPreferences()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(fixture.ExpandGroups("ops"), CreateServer("a", "A", "ops"));
        ServerListViewModel vm = fixture.ViewModel;
        vm.SearchText = "missing";
        vm.FavoriteFilterEnabled = true;
        vm.GatewayFilterEnabled = true;
        Assert.Equal(3, vm.ActiveFilterChips.Count);
        Assert.True(vm.HasNoTreeResults);
        Assert.False(vm.HasNoInventory);

        vm.ActiveFilterChips[0].RemoveCommand.Execute(null);
        Assert.Empty(vm.SearchText);
        Assert.True(vm.FavoriteFilterEnabled);
        Assert.True(vm.GatewayFilterEnabled);
        Assert.Equal(2, vm.ActiveFilterChips.Count);

        vm.SearchText = "A";
        Assert.True(vm.IsFilterPending);
        Assert.False(vm.HasNoTreeResults);
        vm.ResetTreeFiltersCommand.Execute(null);
        Assert.False(vm.HasTreeFilters);
        Assert.False(vm.HasNoTreeResults);
        Assert.False(vm.IsFilterPending);
        Assert.False(vm.ShowSearchContext);
        Assert.True(vm.ShowGatewayBadge);
        Assert.Single(vm.Servers);
    }

    [Fact]
    public async Task TreeOrganizationUndo_RestoresMoveAndOrder()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops", "lab"),
            CreateServer("a", "A", "ops"), CreateServer("b", "B", "lab"));
        Assert.True(await fixture.ViewModel.ReorderServersAsync(
            [fixture.ServerById("a")], fixture.ServerById("b"), true));
        Assert.True(fixture.ViewModel.CanUndoTreeOrganization);
        await fixture.ViewModel.UndoTreeOrganizationCommand.ExecuteAsync(null);
        List<ServerProfileDto> rows = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal("ops", rows.Single(row => row.Id == "a").Group);
        Assert.Equal("lab", rows.Single(row => row.Id == "b").Group);
        Assert.All(rows, row => Assert.Equal(0, row.SortOrder));
        Assert.False(fixture.ViewModel.CanUndoTreeOrganization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TreeOrganizationUndo_RenamePreservesOtherFieldsAndRejectsConflicts(bool conflict)
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), CreateServer("a", "Original", "ops"));
        TreeOrganizationHistory history = new(fixture.ConfigManager);
        await history.ExecuteAsync(() => new ServerRenameService(fixture.ConfigManager).RenameAsync("a", "Renamed"),
            serverIds: new[] { "a" });
        await fixture.ConfigManager.MutateServersAsync(rows =>
        {
            rows[0].RemoteServer = "updated.example.test";
            if (conflict) rows[0].DisplayName = "Later edit";
            return true;
        });
        Assert.Equal(!conflict, await history.UndoAsync());
        ServerProfileDto saved = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal(conflict ? "Later edit" : "Original", saved.DisplayName);
        Assert.Equal("updated.example.test", saved.RemoteServer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TreeOrganizationUndo_FolderRestoresMetadataOrRefusesLaterChange(bool conflict)
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("A", "Parent");
        settings.EmptyGroups.Add("A/Empty");
        settings.EmptyGroups.Add("Parent");
        settings.GroupDefaults["A"] = new GroupDefaultsDto { Color = "#FF0000" };
        await fixture.LoadServersAsync(settings, CreateServer("a", "A", "A/Child"));
        TreeOrganizationHistory history = new(fixture.ConfigManager);
        await history.ExecuteAsync(() => new FolderMoveService(fixture.ConfigManager).MoveAsync("A", "Parent"),
            result => result.Status == FolderMoveStatus.Moved ? new FolderRenamePlan(result.NewPath!, "A") : null);
        if (conflict)
        {
            await fixture.ConfigManager.MergeSettingAsync(current => current.GroupDefaults["Parent/A"].Color = "#00FF00");
        }
        Assert.Equal(!conflict, await history.UndoAsync());
        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        string path = conflict ? "Parent/A" : "A";
        Assert.Contains(path + "/Empty", saved.EmptyGroups);
        Assert.Equal(conflict ? "#00FF00" : "#FF0000", saved.GroupDefaults[path].Color);
        Assert.Equal(path + "/Child", Assert.Single(await fixture.ConfigManager.LoadServersAsync()).Group);
    }

    [Theory]
    [InlineData(-1, 400, 0)]
    [InlineData(0, 400, -1)]
    [InlineData(200, 400, 0)]
    [InlineData(399, 400, 1)]
    [InlineData(401, 400, 0)]
    [InlineData(1, 0, 0)]
    public void TreeDragScroll_OnlyScrollsInsideTheEdge(double y, double height, int expected)
    {
        Assert.Equal(expected, MainWindow.ResolveTreeDragScroll(y, height));
    }
}
