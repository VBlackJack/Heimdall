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
/// "Sort A to Z": one level goes back to alphabetical order, nothing deeper changes, and the
/// undo bar can put the arrangement back.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    private static readonly string[] SortedTreeFolderOrder =
        ["zeta", "mid", "mid/b", "mid/a", "mid/b/y", "mid/b/x"];

    /// <summary>
    /// A tree arranged by hand on every level: sessions of "mid", of "mid/b" and of the root hold a
    /// manual place, and the folder order ranks top-level, child and grandchild folders.
    /// </summary>
    private static ServerProfileDto[] ArrangedTree() =>
    [
        CreateServer("zs", "Zs", "zeta", sortOrder: 10),
        CreateServer("al", "Al", "Alpha"),
        CreateServer("ms1", "Bravo", "mid", sortOrder: 10),
        CreateServer("ms2", "Alpha1", "mid", sortOrder: 20),
        CreateServer("mas", "Mas", "mid/a"),
        CreateServer("mbx", "Xb", "mid/b", sortOrder: 20),
        CreateServer("mby", "Ya", "mid/b", sortOrder: 10),
        CreateServer("gx", "Gx", "mid/b/x"),
        CreateServer("gy", "Gy", "mid/b/y"),
        CreateServer("r1", "Rz", "", sortOrder: 10),
        CreateServer("r2", "Ra", "", sortOrder: 20),
    ];

    private static AppSettings ArrangedSettings(ServerListSelectionFixture fixture)
    {
        AppSettings settings = fixture.ExpandGroups("mid", "mid/b");
        settings.FolderOrder = [.. SortedTreeFolderOrder];
        return settings;
    }

    private static async Task<Dictionary<string, int>> StoredSortOrdersAsync(ServerListSelectionFixture fixture) =>
        (await fixture.ConfigManager.LoadServersAsync()).ToDictionary(row => row.Id, row => row.SortOrder);

    [Fact]
    public async Task SortAlphabetically_OnAFolder_ClearsItsSessionsAndChildEntries_AndLeavesTheTopLevel()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        Assert.Equal(["zeta", "mid", "Alpha", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));
        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Equal(["zeta", "mid", "mid/b/y", "mid/b/x"], saved.FolderOrder);
        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(0, orders["ms1"]);
        Assert.Equal(0, orders["ms2"]);
        Assert.Equal(["zeta", "mid", "Alpha", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/a", "mid/b"], ChildPaths(fixture, "mid"));
        Assert.Equal(["ms2", "ms1"], fixture.FolderByPath("mid").Servers.Select(server => server.Id));
        Assert.True(fixture.ViewModel.CanUndoTreeOrganization);
    }

    [Fact]
    public async Task SortAlphabetically_DoesNotTouchGrandchildEntriesOrGrandchildSessions()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());

        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        Assert.Contains("mid/b/y", (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.Contains("mid/b/x", (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(20, orders["mbx"]);
        Assert.Equal(10, orders["mby"]);
        Assert.Equal(10, orders["zs"]);
        Assert.Equal(10, orders["r1"]);
        Assert.Equal(["mby", "mbx"], fixture.FolderByPath("mid/b").Servers.Select(server => server.Id));
    }

    [Fact]
    public async Task CanSortAlphabetically_LooksAtDirectChildrenOnly()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("kids", "deep", "deep/x");
        settings.FolderOrder = ["kids/b", "deep/x/q", "deep/x/p"];
        await fixture.LoadServersAsync(
            settings,
            CreateServer("pl", "Pl", "plain"),
            CreateServer("rk", "Rk", "ranked", sortOrder: 10),
            CreateServer("ka", "Ka", "kids/a"),
            CreateServer("kb", "Kb", "kids/b"),
            CreateServer("dp", "Dp", "deep/x/p", sortOrder: 10),
            CreateServer("dq", "Dq", "deep/x/q", sortOrder: 20),
            CreateServer("lo", "Lo", "", sortOrder: 10));

        Assert.False(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("plain")));
        Assert.True(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("ranked")));
        Assert.True(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("kids")));
        Assert.False(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("kids/a")));

        // Only the grandchildren are arranged: neither "deep" nor "deep/x" lists a direct child.
        Assert.False(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("deep")));
        Assert.True(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("deep/x")));
        Assert.True(fixture.ViewModel.CanSortFolderAlphabetically(fixture.FolderByPath("")));
        Assert.False(fixture.ViewModel.CanSortTopLevelAlphabetically());
    }

    [Fact]
    public async Task SortAlphabetically_Undo_RestoresTheSessionsAndTheFolderOrder()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));
        Assert.Equal(["mid/a", "mid/b"], ChildPaths(fixture, "mid"));

        await fixture.ViewModel.UndoTreeOrganizationCommand.ExecuteAsync(null);

        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(10, orders["ms1"]);
        Assert.Equal(20, orders["ms2"]);
        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Equal(
            ["zeta", "mid", "mid/b/y", "mid/b/x", "mid/b", "mid/a"],
            saved.FolderOrder);
        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));
        Assert.Equal(["ms1", "ms2"], fixture.FolderByPath("mid").Servers.Select(server => server.Id));
        Assert.False(fixture.ViewModel.CanUndoTreeOrganization);
    }

    [Fact]
    public async Task SortAlphabetically_UndoAfterTheLevelWasRearranged_IsAConflictAndDuplicatesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));
        Assert.True(await fixture.ViewModel.NudgeFolderAsync(fixture.FolderByPath("mid/a"), 1));
        string[] afterNudge = [.. (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder];

        await fixture.ViewModel.UndoTreeOrganizationCommand.ExecuteAsync(null);

        Assert.Equal(localizer["TreeUxUndoConflict"], statuses[^1]);
        Assert.Equal(afterNudge, (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(0, orders["ms1"]);
        Assert.Equal(0, orders["ms2"]);
        Assert.False(fixture.ViewModel.CanUndoTreeOrganization);
    }

    [Fact]
    public async Task SortAlphabetically_ASharedFolder_IsRefusedWithTheReadOnlyLine_AndWritesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        int settingsWrites = 0;
        fixture.ConfigManager.SettingsChanged += _ => settingsWrites++;
        FolderViewModel shared = new() { Name = "Team", FullPath = "shared:team", IsShared = true };

        Assert.False(fixture.ViewModel.CanSortFolderAlphabetically(shared));
        Assert.False(await fixture.ViewModel.SortFolderAlphabeticallyAsync(shared));

        Assert.Equal(localizer["StatusSharedFolderReadOnly"], Assert.Single(statuses));
        Assert.Equal(0, settingsWrites);
        Assert.Equal(SortedTreeFolderOrder, (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.Equal(10, (await StoredSortOrdersAsync(fixture))["ms1"]);
        Assert.False(fixture.ViewModel.CanUndoTreeOrganization);
    }

    [Fact]
    public async Task SortAlphabetically_OnNoFolder_ClearsTheRootSessionsOnly()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());

        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("")));

        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(0, orders["r1"]);
        Assert.Equal(0, orders["r2"]);
        Assert.Equal(10, orders["ms1"]);
        Assert.Equal(10, orders["zs"]);
        Assert.Equal(SortedTreeFolderOrder, (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.Equal(["r2", "r1"], fixture.FolderByPath("").Servers.Select(server => server.Id));
        Assert.Equal(["zeta", "mid", "Alpha", ""], TopLevelPaths(fixture.ViewModel));
    }

    [Fact]
    public async Task SortTopLevelAlphabetically_ClearsOnlyTheEntriesWithoutASeparator()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        Assert.True(fixture.ViewModel.CanSortTopLevelAlphabetically());
        Assert.True(await fixture.ViewModel.SortTopLevelAlphabeticallyAsync());

        Assert.Equal(
            ["mid/b", "mid/a", "mid/b/y", "mid/b/x"],
            (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.Equal(["Alpha", "mid", "zeta", ""], TopLevelPaths(fixture.ViewModel));
        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));
        Assert.Equal(10, (await StoredSortOrdersAsync(fixture))["ms1"]);
        Assert.Equal(localizer["StatusTopLevelSortedAlphabetically"], Assert.Single(statuses));
        Assert.False(fixture.ViewModel.CanSortTopLevelAlphabetically());
    }

    [Fact]
    public async Task SortAlphabetically_StatusLineNamesTheFolder()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        Assert.Equal(localizer.Format("StatusFolderSortedAlphabetically", "mid"), Assert.Single(statuses));
    }

    [Fact]
    public async Task SortAlphabetically_WhenNothingWasArranged_WritesNothingAndOffersNoUndo()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("plain"),
            CreateServer("pl", "Pl", "plain"),
            CreateServer("pm", "Pm", "plain"));
        int writes = 0;
        fixture.ConfigManager.SettingsChanged += _ => writes++;
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        Assert.False(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("plain")));

        Assert.Equal(0, writes);
        Assert.Empty(statuses);
        Assert.False(fixture.ViewModel.CanUndoTreeOrganization);
    }

    [Fact]
    public async Task SortAlphabetically_AMovedOutRow_IsLeftAlone()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());

        // The row leaves "mid" on disk after the tree was built; the tree still lists it there.
        await fixture.ConfigManager.MutateServersAsync(rows =>
        {
            rows.Single(row => row.Id == "ms1").Group = "elsewhere";
            return true;
        });

        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        ServerProfileDto moved = (await fixture.ConfigManager.LoadServersAsync()).Single(row => row.Id == "ms1");
        Assert.Equal(10, moved.SortOrder);
        Assert.Equal("elsewhere", moved.Group);
        Assert.Equal(0, (await StoredSortOrdersAsync(fixture))["ms2"]);
    }

    [Fact]
    public async Task SortAlphabetically_LeavesASiblingWithTheSamePrefixUntouched()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("mid", "mid2");
        settings.FolderOrder = ["mid2/y", "mid/b", "mid2/x", "mid/a"];
        await fixture.LoadServersAsync(
            settings,
            CreateServer("ma", "Ma", "mid/a"),
            CreateServer("mb", "Mb", "mid/b"),
            CreateServer("m1", "Mone", "mid", sortOrder: 10),
            CreateServer("n1", "None", "mid2", sortOrder: 10),
            CreateServer("nx", "Nx", "mid2/x"),
            CreateServer("ny", "Ny", "mid2/y"));

        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        Assert.Equal(["mid2/y", "mid2/x"], (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.Equal(["mid2/y", "mid2/x"], ChildPaths(fixture, "mid2"));
        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(0, orders["m1"]);
        Assert.Equal(10, orders["n1"]);
    }

    [Fact]
    public async Task SortAlphabetically_WhenTheSessionWriteFails_PutsTheFolderOrderBack_AndKeepsTheEarlierUndo()
    {
        await using FailingFixture failing = await FailingFixture.CreateAsync();
        ServerListSelectionFixture fixture = failing.Fixture;
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        Assert.True(await fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("")));
        Assert.True(fixture.ViewModel.CanUndoTreeOrganization);
        failing.Proxy.FailMutations = true;

        await Assert.ThrowsAsync<IOException>(
            () => fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Equal(["zeta", "mid", "mid/b/y", "mid/b/x", "mid/b", "mid/a"], saved.FolderOrder);
        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(10, orders["ms1"]);
        Assert.Equal(20, orders["ms2"]);
        Assert.Equal(["mid/b", "mid/a"], ChildPaths(fixture, "mid"));

        // Nothing new was recorded: the bar still offers the earlier "(No Folder)" sort, not this one.
        failing.Proxy.FailMutations = false;
        Assert.True(fixture.ViewModel.CanUndoTreeOrganization);
        await fixture.ViewModel.UndoTreeOrganizationCommand.ExecuteAsync(null);
        orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(10, orders["r1"]);
        Assert.Equal(20, orders["r2"]);
        Assert.Equal(saved.FolderOrder, (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
    }

    [Fact]
    public async Task SortAlphabetically_WhenThePutBackFailsToo_KeepsTheEntriesRecordedForUndo()
    {
        await using FailingFixture failing = await FailingFixture.CreateAsync();
        ServerListSelectionFixture fixture = failing.Fixture;
        await fixture.LoadServersAsync(ArrangedSettings(fixture), ArrangedTree());
        failing.Proxy.FailMutations = true;
        failing.Proxy.AllowedMerges = 1;

        await Assert.ThrowsAsync<IOException>(
            () => fixture.ViewModel.SortFolderAlphabeticallyAsync(fixture.FolderByPath("mid")));

        Assert.Equal(
            ["zeta", "mid", "mid/b/y", "mid/b/x"],
            (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Assert.True(fixture.ViewModel.CanUndoTreeOrganization);
        failing.Proxy.FailMutations = false;
        failing.Proxy.AllowedMerges = int.MaxValue;

        await fixture.ViewModel.UndoTreeOrganizationCommand.ExecuteAsync(null);

        Assert.Equal(
            ["zeta", "mid", "mid/b/y", "mid/b/x", "mid/b", "mid/a"],
            (await fixture.ConfigManager.LoadSettingsAsync()).FolderOrder);
        Dictionary<string, int> orders = await StoredSortOrdersAsync(fixture);
        Assert.Equal(10, orders["ms1"]);
        Assert.Equal(20, orders["ms2"]);
        Assert.False(fixture.ViewModel.CanUndoTreeOrganization);
    }

    /// <summary>
    /// A fixture over a failing proxy. The proxy's real manager lives in its own temporary folder,
    /// which this wrapper deletes; the fixture's own dispose only removes the folder it created
    /// itself (unused here, as the manager is passed in) and disposes the view model.
    /// </summary>
    private sealed class FailingFixture : IAsyncDisposable
    {
        private readonly string _root;

        private FailingFixture(string root, FailingServerMutationProxy proxy, ServerListSelectionFixture fixture)
        {
            _root = root;
            Proxy = proxy;
            Fixture = fixture;
        }

        public FailingServerMutationProxy Proxy { get; }

        public ServerListSelectionFixture Fixture { get; }

        public static async Task<FailingFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "heimdall-folder-sort", Guid.NewGuid().ToString("N"));
            IConfigManager failing = DispatchProxy.Create<IConfigManager, FailingServerMutationProxy>();
            FailingServerMutationProxy proxy = (FailingServerMutationProxy)(object)failing;
            proxy.Inner = new ConfigManager(root);
            return new FailingFixture(root, proxy, await ServerListSelectionFixture.CreateAsync(configManager: failing));
        }

        public async ValueTask DisposeAsync()
        {
            await Fixture.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Forwards every call to the real manager, and fails the inventory mutations (and merges past a count) when asked.</summary>
    public class FailingServerMutationProxy : DispatchProxy
    {
        public IConfigManager Inner { get; set; } = null!;

        public bool FailMutations { get; set; }

        public int AllowedMerges { get; set; } = int.MaxValue;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (FailMutations && targetMethod!.Name == nameof(IConfigManager.MutateServersAsync))
            {
                throw new IOException("Simulated inventory interruption.");
            }

            if (targetMethod!.Name == nameof(IConfigManager.MergeSettingAsync) && AllowedMerges != int.MaxValue)
            {
                if (AllowedMerges-- <= 0)
                {
                    throw new IOException("Simulated settings interruption.");
                }
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
