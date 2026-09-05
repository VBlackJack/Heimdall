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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Select-all, the selection count live text, folder colours and the tool pane texts, seen
/// through the list view model.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task SelectAllVisible_TakesExpandedBranchesOnly_AndKeepsThePrimary()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "lab"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        fixture.ViewModel.SelectAllVisible();

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
        Assert.True(fixture.ViewModel.HasMultiSelection);
    }

    [Fact]
    public async Task SelectionCountText_SpeaksOnlyForMoreThanOneSession()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        List<string?> changed = [];
        fixture.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        Assert.Equal("", fixture.ViewModel.SelectionCountText);

        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        Assert.Equal("2 sessions selected", fixture.ViewModel.SelectionCountText);
        Assert.Contains(nameof(ServerListViewModel.SelectionCountText), changed);
        Assert.Contains(nameof(ServerListViewModel.HasMultiSelection), changed);
    }

    [Fact]
    public async Task FolderColour_ComesFromTheFolderDefaults_AndIsInheritedDownwards()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.ConfigManager.MergeSettingAsync(settings =>
            settings.GroupDefaults["ops"] = new GroupDefaultsDto { Color = "#EF4444" });
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        settings.TreeExpandedNodes.Add("ops");

        fixture.LoadServers(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops/web"),
            CreateServer("gamma", "Gamma", "lab"));

        FolderViewModel ops = FolderByPath(fixture.ViewModel, "ops");
        Assert.Equal("#EF4444", ops.Color);
        Assert.Equal("#EF4444", Assert.Single(ops.SubFolders).Color);
        Assert.Equal("", FolderByPath(fixture.ViewModel, "lab").Color);
    }

    [Fact]
    public async Task SetFolderColour_ShowsAtOnce_Persists_AndReleasesTheInheritedOne()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.ConfigManager.MergeSettingAsync(settings =>
            settings.GroupDefaults["ops"] = new GroupDefaultsDto { Color = "#EF4444" });
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        settings.TreeExpandedNodes.Add("ops");
        fixture.LoadServers(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops/web"));
        FolderViewModel web = Assert.Single(FolderByPath(fixture.ViewModel, "ops").SubFolders);

        await fixture.ViewModel.SetFolderColorAsync("ops/web", "#22C55E");

        Assert.Equal("#22C55E", web.Color);
        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Equal("#22C55E", saved.GroupDefaults["ops/web"].Color);

        await fixture.ViewModel.SetFolderColorAsync("ops/web", null);

        // Back to the parent's colour, not to none: the folder inherits again.
        Assert.Equal("#EF4444", web.Color);
        saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Null(saved.GroupDefaults["ops/web"].Color);
    }

    [Fact]
    public async Task ToolDetailTexts_FollowTheSelectedTool()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto tool = CreateServer("demo", "Demo", "ops");
        tool.ConnectionType = ConnectionTypeCatalog.ToolPrefix + "DEMO";
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            tool);
        fixture.ViewModel.ToolDescriptorResolver = id => id == "DEMO"
            ? new ToolDescriptor(
                "DEMO",
                ToolCategory.Network,
                "ToolCategoryNetwork",
                "Demo tool name",
                null,
                ["demo"],
                true)
            : null;
        List<string?> changed = [];
        fixture.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        fixture.ViewModel.SelectSingle(fixture.ServerById("demo"));

        // An external tool carries its name where a built-in one carries a key.
        Assert.Equal("Demo tool name", fixture.ViewModel.ToolDetailName);
        Assert.NotEqual("", fixture.ViewModel.ToolDetailCategory);
        Assert.NotEqual("ToolCategoryNetwork", fixture.ViewModel.ToolDetailCategory);
        Assert.Equal("", fixture.ViewModel.ToolDetailDescription);
        Assert.Contains(nameof(ServerListViewModel.ToolDetailName), changed);

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        Assert.Equal("", fixture.ViewModel.ToolDetailName);
        Assert.Equal("", fixture.ViewModel.ToolDetailCategory);
    }

    private static FolderViewModel FolderByPath(ServerListViewModel viewModel, string fullPath) =>
        Assert.Single(viewModel.GroupedServers, folder => folder.FullPath == fullPath);
}
