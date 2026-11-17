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

/// <summary>
/// The undo history puts back the folder order entries a change removed, and refuses when the
/// level was arranged again in between.
/// </summary>
public sealed partial class FolderRenameServiceTests
{
    private static FakeConfigManager ConfigWithFolderOrder(params string[] order) => new()
    {
        Servers = [CreateServer("a", "mid"), CreateServer("b", "mid")],
        Settings = new AppSettings { FolderOrder = [.. order] }
    };

    /// <summary>Removes the entries under "mid", as the sort command does, and reports them for undo.</summary>
    private static async Task<FolderOrderRestore?> RemoveMidEntriesAsync(FakeConfigManager config)
    {
        List<string> removed = [];
        await config.MergeSettingAsync(settings =>
        {
            removed = FolderOrdering.ChildEntries(settings.FolderOrder, "mid");
            settings.FolderOrder = FolderOrdering.WithoutChildEntries(settings.FolderOrder, "mid");
        });
        return new FolderOrderRestore("mid", removed);
    }

    [Fact]
    public async Task FolderOrderRestore_UndoAppendsTheRemovedEntries_InTheirOrder_OnTopOfWhatIsThere()
    {
        FakeConfigManager config = ConfigWithFolderOrder("top", "mid/b", "mid/a", "mid/b/x");
        TreeOrganizationHistory history = new(config);

        await history.ExecuteAsync(() => RemoveMidEntriesAsync(config), restoreFolderOrder: restore => restore);
        Assert.Equal(["top", "mid/b/x"], config.Settings.FolderOrder);
        Assert.True(history.CanUndo);
        await config.MergeSettingAsync(settings => settings.FolderOrder.Add("other"));

        Assert.True(await history.UndoAsync());

        Assert.Equal(["top", "mid/b/x", "other", "mid/b", "mid/a"], config.Settings.FolderOrder);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task FolderOrderRestore_UndoIsAConflict_WhenTheLevelWasArrangedAgain_AndWritesNothing()
    {
        FakeConfigManager config = ConfigWithFolderOrder("mid/b", "mid/a");
        config.Servers[0].SortOrder = 10;
        TreeOrganizationHistory history = new(config);
        await history.ExecuteAsync(
            async () =>
            {
                FolderOrderRestore? restore = await RemoveMidEntriesAsync(config);
                await config.MutateServersAsync(rows =>
                {
                    rows.Single(row => row.Id == "a").SortOrder = 0;
                    return true;
                });
                return restore;
            },
            serverIds: new[] { "a" },
            restoreFolderOrder: restore => restore);
        await config.MergeSettingAsync(settings => settings.FolderOrder = ["mid/a", "mid/b"]);
        int callsBeforeUndo = config.PersistenceCalls.Count;

        Assert.False(await history.UndoAsync());

        Assert.Equal(callsBeforeUndo, config.PersistenceCalls.Count);
        Assert.Equal(["mid/a", "mid/b"], config.Settings.FolderOrder);
        Assert.Equal(0, config.Servers.Single(row => row.Id == "a").SortOrder);
    }

    [Fact]
    public async Task FolderOrderRestore_UndoRestoresTheRowsAndTheEntriesTogether()
    {
        FakeConfigManager config = ConfigWithFolderOrder("mid/b", "mid/a");
        config.Servers[0].SortOrder = 10;
        config.Servers[1].SortOrder = 20;
        TreeOrganizationHistory history = new(config);
        await history.ExecuteAsync(
            async () =>
            {
                FolderOrderRestore? restore = await RemoveMidEntriesAsync(config);
                await config.MutateServersAsync(rows =>
                {
                    foreach (ServerProfileDto row in rows)
                    {
                        row.SortOrder = 0;
                    }

                    return true;
                });
                return restore;
            },
            serverIds: new[] { "a", "b" },
            restoreFolderOrder: restore => restore);

        Assert.True(await history.UndoAsync());

        Assert.Equal(["mid/b", "mid/a"], config.Settings.FolderOrder);
        Assert.Equal([10, 20], config.Servers.Select(row => row.SortOrder));
    }

    [Fact]
    public async Task FolderOrderRestore_WithoutEntries_RecordsNothing_AndWithoutTheParameterNothingChanges()
    {
        FakeConfigManager config = ConfigWithFolderOrder("top");
        TreeOrganizationHistory history = new(config);

        await history.ExecuteAsync(
            () => RemoveMidEntriesAsync(config),
            restoreFolderOrder: restore => restore);
        Assert.False(history.CanUndo);
        Assert.Equal(0, history.RecordedCount);

        // An existing caller that does not pass the restore keeps its behavior: rows only.
        config.Servers[0].SortOrder = 10;
        await history.ExecuteAsync(
            async () =>
            {
                await config.MutateServersAsync(rows =>
                {
                    rows.Single(row => row.Id == "a").SortOrder = 20;
                    return true;
                });
                return 0;
            },
            serverIds: new[] { "a" });
        Assert.True(history.CanUndo);
        Assert.True(await history.UndoAsync());
        Assert.Equal(10, config.Servers.Single(row => row.Id == "a").SortOrder);
        Assert.Equal(["top"], config.Settings.FolderOrder);
    }

    [Fact]
    public void FolderOrdering_ChildEntries_AreDirectChildrenOnly_CaseInsensitive_WithoutRepeats()
    {
        string[] stored = ["Mid/B", "mid/b", "mid/a", "mid/b/x", "other/c", "mid"];

        Assert.Equal(["Mid/B", "mid/a"], FolderOrdering.ChildEntries(stored, "mid"));
        Assert.Equal(["mid"], FolderOrdering.ChildEntries(stored, ""));
        Assert.False(FolderOrdering.HasChildEntry(["mid/b/x"], "mid"));
        Assert.True(FolderOrdering.HasChildEntry(["mid/b/x"], "mid/b"));
        Assert.Equal(["mid/b/x", "other/c", "mid"], FolderOrdering.WithoutChildEntries(stored, "mid"));
        Assert.Equal(["mid/a", "mid/b"], FolderOrdering.WithEntriesAppended(["MID/A"], ["mid/a", "mid/b"]).Select(entry => entry.ToLowerInvariant()));
    }
}
