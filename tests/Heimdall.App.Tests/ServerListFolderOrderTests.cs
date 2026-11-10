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

using System.IO;
using System.Reflection;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The manual order of folders: how the tree ranks them, and the one-step nudge.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    private static string[] TopLevelPaths(ServerListViewModel viewModel) =>
        [.. viewModel.GroupedServers.Select(folder => folder.FullPath)];

    private static string[] ChildPaths(ServerListSelectionFixture fixture, string path) =>
        [.. fixture.FolderByPath(path).SubFolders.Select(folder => folder.FullPath)];

    private static ServerProfileDto[] MixedFolderTree() =>
    [
        CreateServer("z", "Z", "zeta"),
        CreateServer("a", "A", "Alpha"),
        CreateServer("m", "M", "mid"),
        CreateServer("mb", "MB", "mid/b"),
        CreateServer("ma", "MA", "mid/a"),
        CreateServer("loose", "Loose", ""),
    ];

    [Fact]
    public async Task FolderOrder_WhenEmpty_KeepsTheAlphabeticalTree()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());

        Assert.Equal(["Alpha", "mid", "zeta", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/a", "mid/b"], ChildPaths(fixture, "mid"));
    }

    [Fact]
    public async Task FolderOrder_RanksListedSiblingsFirst_ThenTheOthersAlphabetically()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("mid");
        settings.FolderOrder = ["zeta", "Ghost/Gone", "mid"];
        await fixture.LoadServersAsync(settings, MixedFolderTree());

        // "no folder" keeps its band after every local folder, whatever the list says.
        Assert.Equal(["zeta", "mid", "Alpha", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/a", "mid/b"], ChildPaths(fixture, "mid"));
    }

    [Fact]
    public async Task FolderOrder_OrdersNestedFoldersIndependentlyPerParent()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("mid");
        settings.FolderOrder = ["mid/b"];
        await fixture.LoadServersAsync(settings, MixedFolderTree());

        Assert.Equal(["Alpha", "mid", "zeta", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));
    }

    [Fact]
    public async Task NudgeFolder_FirstMove_WritesTheDisplayedOrderOfAllSiblings_InOneWrite()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());
        int writes = 0;
        fixture.ConfigManager.SettingsChanged += _ => writes++;

        Assert.True(await fixture.ViewModel.NudgeFolderAsync(fixture.FolderByPath("zeta"), -1));

        Assert.Equal(1, writes);
        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Equal(["Alpha", "zeta", "mid"], saved.FolderOrder);
        Assert.Equal(["Alpha", "zeta", "mid", ""], TopLevelPaths(fixture.ViewModel));

        Assert.True(await fixture.ViewModel.NudgeFolderAsync(fixture.FolderByPath("Alpha"), 1));

        Assert.Equal(2, writes);
        Assert.Equal(["zeta", "Alpha", "mid", ""], TopLevelPaths(fixture.ViewModel));
        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        fixture.ViewModel.LoadServers(servers, await fixture.ConfigManager.LoadSettingsAsync());
        Assert.Equal(["zeta", "Alpha", "mid", ""], TopLevelPaths(fixture.ViewModel));
    }

    [Fact]
    public async Task NudgeFolder_StopsAtTheEdges_AndWritesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());
        int writes = 0;
        fixture.ConfigManager.SettingsChanged += _ => writes++;
        FolderViewModel first = fixture.FolderByPath("Alpha");
        FolderViewModel last = fixture.FolderByPath("zeta");

        Assert.False(fixture.ViewModel.CanNudgeFolder(first, -1));
        Assert.True(fixture.ViewModel.CanNudgeFolder(first, 1));
        Assert.False(fixture.ViewModel.CanNudgeFolder(last, 1));
        Assert.True(fixture.ViewModel.CanNudgeFolder(last, -1));
        Assert.False(await fixture.ViewModel.NudgeFolderAsync(first, -1));
        Assert.False(await fixture.ViewModel.NudgeFolderAsync(last, 1));

        Assert.Equal(0, writes);
        Assert.Empty((await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
    }

    [Fact]
    public async Task NudgeFolder_InANestedGroup_LeavesTheTopLevelAlone()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());

        Assert.True(await fixture.ViewModel.NudgeFolderAsync(fixture.FolderByPath("mid/b"), -1));

        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));
        Assert.Equal(["Alpha", "mid", "zeta", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/b", "mid/a"], (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
    }

    [Fact]
    public async Task NudgeFolder_DropsStaleEntriesOnWrite_AndKeepsOtherGroupsRank()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("mid");
        settings.FolderOrder = ["mid/b", "Ghost", "mid/a"];
        await fixture.LoadServersAsync(settings, MixedFolderTree());

        Assert.True(await fixture.ViewModel.NudgeFolderAsync(fixture.FolderByPath("zeta"), -1));

        Assert.Equal(
            ["mid/b", "mid/a", "Alpha", "zeta", "mid"],
            (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));
    }

    [Fact]
    public async Task NudgeFolder_ASharedFolder_IsRefusedWithTheReadOnlyLine()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        FolderViewModel shared = new() { Name = "Team", FullPath = "shared:team", IsShared = true };

        Assert.False(fixture.ViewModel.CanNudgeFolder(shared, 1));
        Assert.False(await fixture.ViewModel.NudgeFolderAsync(shared, 1));

        Assert.Equal(localizer["StatusSharedFolderReadOnly"], Assert.Single(statuses));
        Assert.Empty((await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
    }

    [Fact]
    public async Task NudgeFolder_WhenTheMergeFindsNothingToMove_ReportsNoMove()
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-folder-order", Guid.NewGuid().ToString("N"));
        IConfigManager inner = new ConfigManager(root);
        IConfigManager swallowing = DispatchProxy.Create<IConfigManager, MergeSwallowingProxy>();
        ((MergeSwallowingProxy)(object)swallowing).Inner = inner;
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: swallowing);
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());

        // The merge never runs its mutation, as when the siblings changed under the move.
        Assert.False(await fixture.ViewModel.NudgeFolderAsync(fixture.FolderByPath("zeta"), -1));
        Assert.Equal(["Alpha", "mid", "zeta", ""], TopLevelPaths(fixture.ViewModel));
    }

    [Fact]
    public async Task FolderHelpText_AnnouncesAltArrows_OnlyOnRowsThatMove()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("mid"), MixedFolderTree());
        FolderViewModel shared = new() { Name = "Team", FullPath = "shared:team", IsShared = true };

        Assert.Contains("Alt+", fixture.FolderByPath("zeta").AccessibleHelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("Alt+", fixture.FolderByPath("").AccessibleHelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("Alt+", shared.AccessibleHelpText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task FolderHelpText_EveryLocaleKeepsTheMovingGuidanceApart(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        Assert.DoesNotContain("Alt+", localizer["SessionTreeFolderAccessibleHelp"], StringComparison.Ordinal);
        Assert.Contains("Alt+", localizer["SessionTreeFolderMovableAccessibleHelp"], StringComparison.Ordinal);
    }

    /// <summary>Forwards every call to the real manager except the settings merge, which does nothing.</summary>
    public class MergeSwallowingProxy : DispatchProxy
    {
        public IConfigManager Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IConfigManager.MergeSettingAsync))
            {
                return Task.CompletedTask;
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    [Fact]
    public async Task CanNudgeServer_FollowsTheSessionEdges()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("a", "A", "ops"),
            CreateServer("b", "B", "ops"));

        Assert.False(fixture.ViewModel.CanNudgeServer(fixture.ServerById("a"), -1));
        Assert.True(fixture.ViewModel.CanNudgeServer(fixture.ServerById("a"), 1));
        Assert.True(fixture.ViewModel.CanNudgeServer(fixture.ServerById("b"), -1));
        Assert.False(fixture.ViewModel.CanNudgeServer(fixture.ServerById("b"), 1));
    }
}
