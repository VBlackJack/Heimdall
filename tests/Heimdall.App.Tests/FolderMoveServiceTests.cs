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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// A move runs the rename migration under another parent, refusals included.
/// </summary>
public sealed partial class FolderRenameServiceTests
{
    [Fact]
    public async Task MoveFolder_UnderAnotherFolder_CarriesSessionsDefaultsExpansionAndEmptyGroups()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("root", "Prod"),
                CreateServer("linux", "Prod/Linux"),
                CreateServer("web", "Prod/Linux/Web"),
                CreateServer("archive", "Archive")
            ],
            Settings = new AppSettings
            {
                EmptyGroups = ["Prod/Linux/Empty"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod/Linux"] = new() { Color = "#EF4444", SshUsername = "deploy" }
                },
                TreeExpandedNodes = ["Prod", "Prod/Linux", "Archive"]
            }
        };
        var service = new FolderMoveService(config);

        FolderMoveResult result = await service.MoveAsync("Prod/Linux", "Archive");

        Assert.Equal(FolderMoveStatus.Moved, result.Status);
        Assert.Equal("Archive/Linux", result.NewPath);
        Assert.Equal(["settings", "servers", "settings"], config.PersistenceCalls);
        Assert.Equal(
            ["Prod", "Archive/Linux", "Archive/Linux/Web", "Archive"],
            config.Servers.Select(server => server.Group));
        Assert.Equal(["Archive/Linux/Empty"], config.Settings.EmptyGroups);
        Assert.Equal("#EF4444", config.Settings.GroupDefaults["Archive/Linux"].Color);
        Assert.Equal("deploy", config.Settings.GroupDefaults["Archive/Linux"].SshUsername);
        Assert.DoesNotContain("Prod/Linux", config.Settings.GroupDefaults.Keys);
        Assert.Equal(["Prod", "Archive/Linux", "Archive"], config.Settings.TreeExpandedNodes);
        Assert.NotNull(result.Servers);
        Assert.NotNull(result.Settings);
    }

    [Fact]
    public async Task MoveFolder_ToTheTopLevel_DropsTheParent()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("linux", "Prod/Linux"), CreateServer("root", "Prod")]
        };
        var service = new FolderMoveService(config);

        FolderMoveResult result = await service.MoveAsync("Prod/Linux", null);

        Assert.Equal(FolderMoveStatus.Moved, result.Status);
        Assert.Equal("Linux", result.NewPath);
        Assert.Equal(["Linux", "Prod"], config.Servers.Select(server => server.Group));
    }

    [Fact]
    public async Task MoveFolder_ToItsCurrentParent_ChangesNothingAndSavesNothing()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("linux", "Prod/Linux")]
        };
        var service = new FolderMoveService(config);

        FolderMoveResult result = await service.MoveAsync("Prod/Linux", "Prod");

        Assert.Equal(FolderMoveStatus.NoChange, result.Status);
        Assert.Empty(config.PersistenceCalls);
    }

    [Fact]
    public async Task MoveFolder_IntoItsOwnDescendant_IsRefusedWithoutSave()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("web", "Prod/Linux/Web")]
        };
        var service = new FolderMoveService(config);

        FolderMoveResult result = await service.MoveAsync("Prod/Linux", "Prod/Linux/Web");

        Assert.Equal(FolderMoveStatus.IntoItself, result.Status);
        Assert.Empty(config.PersistenceCalls);
        Assert.Equal("Prod/Linux/Web", Assert.Single(config.Servers).Group);
    }

    [Fact]
    public async Task MoveFolder_OntoAnExistingSibling_IsRefusedWithoutSave()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("linux", "Prod/Linux"), CreateServer("other", "Archive/linux")]
        };
        var service = new FolderMoveService(config);

        FolderMoveResult result = await service.MoveAsync("Prod/Linux", "Archive");

        Assert.Equal(FolderMoveStatus.SiblingCollision, result.Status);
        Assert.Empty(config.PersistenceCalls);
    }
}
