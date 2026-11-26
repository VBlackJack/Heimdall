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
/// The manual folder order follows a folder through a rename, a move and an undo.
/// </summary>
public sealed partial class FolderRenameServiceTests
{
    [Fact]
    public async Task RenameFolder_RewritesTheManualOrder_KeepingPlacesAndDescendants()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("a", "Beta"), CreateServer("b", "Alpha/Sub"), CreateServer("c", "Gamma")],
            Settings = new AppSettings
            {
                FolderOrder = ["Gamma", "Alpha", "Alpha/Sub", "Alpha/Other", "Beta"]
            }
        };

        FolderRenameResult result = await new FolderRenameService(config).RenameAsync("Alpha", "Omega");

        Assert.Equal(FolderRenameStatus.Renamed, result.Status);
        Assert.Equal(
            ["Gamma", "Omega", "Omega/Sub", "Omega/Other", "Beta"],
            config.Settings.FolderOrder);
    }

    [Fact]
    public async Task RenameFolder_OntoAStaleEntry_TakesTheRenamedFoldersPlace_NotTheStaleOne()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("a", "A/X"), CreateServer("b", "B")],
            Settings = new AppSettings { FolderOrder = ["A/Y", "B", "A/X"] }
        };

        await new FolderRenameService(config).RenameAsync("A/X", "Y");

        Assert.Equal(["B", "A/Y"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task RenameFolder_CaseOnly_KeepsItsPlaceInTheManualOrder()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("a", "Prod"), CreateServer("b", "B"), CreateServer("c", "C")],
            Settings = new AppSettings { FolderOrder = ["B", "Prod", "C"] }
        };

        await new FolderRenameService(config).RenameAsync("Prod", "prod");

        Assert.Equal(["B", "prod", "C"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task RenameFolder_DoesNotTouchAPrefixSiblingInTheManualOrder()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("a", "Prod"), CreateServer("b", "Prod2")],
            Settings = new AppSettings { FolderOrder = ["Prod2", "Prod"] }
        };

        await new FolderRenameService(config).RenameAsync("Prod", "Live");

        Assert.Equal(["Prod2", "Live"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task UndoOfARename_RestoresTheManualOrder_EvenAfterANudge()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("a", "Alpha"), CreateServer("b", "Beta"), CreateServer("c", "Gamma")],
            Settings = new AppSettings { FolderOrder = ["Gamma", "Alpha", "Beta"] }
        };
        TreeOrganizationHistory history = new(config);
        var service = new FolderRenameService(config);

        await history.ExecuteAsync(
            () => service.RenameAsync("Alpha", "Omega"),
            result => result.Status == FolderRenameStatus.Renamed
                ? new FolderRenamePlan(result.NewPath!, "Alpha") : null);
        Assert.Equal(["Gamma", "Omega", "Beta"], config.Settings.FolderOrder);

        // A later nudge writes the list but is not part of the undo fingerprint.
        await config.MergeSettingAsync(settings => settings.FolderOrder = ["Omega", "Gamma", "Beta"]);

        Assert.True(await history.UndoAsync());
        Assert.Equal(["Alpha", "Gamma", "Beta"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task MoveFolder_UnderAnotherParent_DropsItsRankAndRewritesItsDescendants()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("a", "Prod/Linux"),
                CreateServer("b", "Prod/Linux/Web"),
                CreateServer("c", "Prod/Linux/Db"),
                CreateServer("d", "Archive")
            ],
            Settings = new AppSettings
            {
                FolderOrder = ["Archive", "Prod/Linux", "Prod/Linux/Web", "Prod/Linux/Db", "Prod"]
            }
        };

        FolderMoveResult result = await new FolderMoveService(config).MoveAsync("Prod/Linux", "Archive");

        Assert.Equal(FolderMoveStatus.Moved, result.Status);
        Assert.Equal(
            ["Archive", "Archive/Linux/Web", "Archive/Linux/Db", "Prod"],
            config.Settings.FolderOrder);
    }

    private static async Task<FolderMoveResult> MoveWithUndoAsync(
        FakeConfigManager config,
        TreeOrganizationHistory history,
        string folder,
        string? targetParent)
    {
        return await history.ExecuteAsync(
            () => new FolderMoveService(config).MoveAsync(folder, targetParent),
            result => result.Status == FolderMoveStatus.Moved
                ? new FolderRenamePlan(result.NewPath!, folder) : null,
            restoreMovedRank: result => result.DroppedRank);
    }

    private static FakeConfigManager ConfigForRankedMove() => new()
    {
        Servers =
        [
            CreateServer("a", "Prod/B"),
            CreateServer("b", "Prod/Linux"),
            CreateServer("c", "Prod/A"),
            CreateServer("d", "Archive")
        ],
        Settings = new AppSettings { FolderOrder = ["Prod/B", "Prod/Linux", "Prod/A", "Archive"] }
    };

    [Fact]
    public async Task UndoOfAMove_RestoresTheFoldersManualRank()
    {
        FakeConfigManager config = ConfigForRankedMove();
        TreeOrganizationHistory history = new(config);

        await MoveWithUndoAsync(config, history, "Prod/Linux", "Archive");
        Assert.Equal(["Prod/B", "Prod/A", "Archive"], config.Settings.FolderOrder);

        Assert.True(await history.UndoAsync());

        Assert.Equal(["Prod/B", "Prod/Linux", "Prod/A", "Archive"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task UndoOfAMove_OfATopLevelFolder_RestoresTheRankAmongTheTopLevelFolders()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("a", "B"),
                CreateServer("b", "Linux"),
                CreateServer("c", "A"),
                CreateServer("d", "Archive")
            ],
            Settings = new AppSettings { FolderOrder = ["B", "Linux", "A", "Archive"] }
        };
        TreeOrganizationHistory history = new(config);

        await MoveWithUndoAsync(config, history, "Linux", "Archive");
        Assert.Equal(["B", "A", "Archive"], config.Settings.FolderOrder);

        Assert.True(await history.UndoAsync());

        Assert.Equal(["B", "Linux", "A", "Archive"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task UndoOfAMove_RestoresTheRank_EvenAfterTheOldLevelWasNudged()
    {
        FakeConfigManager config = ConfigForRankedMove();
        TreeOrganizationHistory history = new(config);

        await MoveWithUndoAsync(config, history, "Prod/Linux", "Archive");

        // A later nudge writes the list but is not part of the undo fingerprint.
        await config.MergeSettingAsync(settings => settings.FolderOrder = ["Prod/A", "Prod/B", "Archive"]);

        Assert.True(await history.UndoAsync());

        Assert.Equal(["Prod/Linux", "Prod/A", "Prod/B", "Archive"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task UndoOfAMove_RestoresTheRankAndTheSubFolderRanks()
    {
        List<string> original = ["Archive", "Prod/Linux", "Prod/Linux/Web", "Prod/Linux/Db", "Prod"];
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("a", "Prod/Linux"),
                CreateServer("b", "Prod/Linux/Web"),
                CreateServer("c", "Prod/Linux/Db"),
                CreateServer("d", "Archive")
            ],
            Settings = new AppSettings { FolderOrder = [.. original] }
        };
        TreeOrganizationHistory history = new(config);

        await MoveWithUndoAsync(config, history, "Prod/Linux", "Archive");
        Assert.True(await history.UndoAsync());

        // Only the order among siblings matters, so each parent's entries are compared.
        foreach (string parent in new[] { "", "Prod", "Prod/Linux" })
        {
            Assert.Equal(
                FolderOrdering.ChildEntries(original, parent),
                FolderOrdering.ChildEntries(config.Settings.FolderOrder, parent));
        }

        Assert.Equal(original.Count, config.Settings.FolderOrder.Count);
    }

    [Fact]
    public async Task UndoOfAMove_OfAnUnrankedFolder_AddsNoEntry()
    {
        FakeConfigManager config = ConfigForRankedMove();
        config.Settings.FolderOrder = ["Prod/B", "Prod/A", "Archive"];
        TreeOrganizationHistory history = new(config);

        await MoveWithUndoAsync(config, history, "Prod/Linux", "Archive");
        Assert.True(await history.UndoAsync());

        Assert.Equal(["Prod/B", "Prod/A", "Archive"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task UndoOfAMove_WhenRefused_RestoresNoRank()
    {
        FakeConfigManager config = ConfigForRankedMove();
        TreeOrganizationHistory history = new(config);

        await MoveWithUndoAsync(config, history, "Prod/Linux", "Archive");
        await config.MutateServersAsync(rows =>
        {
            rows.Single(row => row.Id == "a").DisplayName = "Renamed since";
            return true;
        });

        Assert.False(await history.UndoAsync());

        Assert.Equal(["Prod/B", "Prod/A", "Archive"], config.Settings.FolderOrder);
    }

    [Fact]
    public async Task MoveFolder_ToTheTopLevel_DropsItsRankToo()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("a", "Prod/Linux"), CreateServer("b", "Other")],
            Settings = new AppSettings { FolderOrder = ["Prod/Linux", "Other"] }
        };

        await new FolderMoveService(config).MoveAsync("Prod/Linux", null);

        Assert.Equal(["Other"], config.Settings.FolderOrder);
    }
}
