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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

// Bulk password mutation encrypts via CredentialProtector; serialized with the vault tests.
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerListBulkActionTests
{
    [Fact]
    public async Task DeleteSelectedAsync_WhenConfirmed_RemovesSelectedAndClearsSelection()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["gamma"], fixture.VisibleIds());
        Assert.Empty(fixture.ViewModel.SelectedItems);
        Assert.Null(fixture.ViewModel.SelectedServer);

        var persistedIds = (await fixture.ConfigManager.LoadServersAsync())
            .Select(server => server.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["gamma"], persistedIds);
    }

    [Fact]
    public async Task DeleteSelectedAsync_WhenCancelled_DoesNothing()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: false);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["alpha", "beta"], fixture.VisibleIds());
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("Delete Selected Items", fixture.DialogService.LastConfirmTitle);
    }

    [Fact]
    public async Task DeleteSelectedAsync_ForSmallSelection_ListsNamesInConfirmation()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: false);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Contains("- Alpha", fixture.DialogService.LastConfirmMessage);
        Assert.Contains("- Beta", fixture.DialogService.LastConfirmMessage);
    }

    [Fact]
    public async Task DeleteSelectedAsync_DeletesToolEntriesToo()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateTool("chmod", "Chmod Tool", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));

        await fixture.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(fixture.VisibleIds());
        Assert.Empty(await fixture.ConfigManager.LoadServersAsync());
    }

    [Fact]
    public async Task DeleteSelectedAsync_WhenSaveFails_DoesNotMutateViewModelOrPersistedState()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));
        configManager.FailOnSaveServers = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await Assert.ThrowsAsync<IOException>(() => fixture.ViewModel.DeleteSelectedCommand.ExecuteAsync(null));

        Assert.Equal(["alpha", "beta", "gamma"], fixture.VisibleIds());
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);

        var persistedIds = (await fixture.ConfigManager.LoadServersAsync())
            .Select(server => server.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["alpha", "beta", "gamma"], persistedIds);
    }

    [Fact]
    public async Task MoveSelectedToGroupAsync_MovesAllSelectedAndPreservesSelection()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateServer("beta", "Beta", "ops/source"),
            CreateServer("gamma", "Gamma", "ops/other"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToGroupCommand.ExecuteAsync(new BulkMoveToGroupRequest("ops/target"));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("ops/target", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/target", fixture.ServerById("beta").Group);

        var persistedGroups = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.Group)
            .ToArray();
        Assert.Collection(
            persistedGroups,
            group => Assert.Equal("ops/target", group),
            group => Assert.Equal("ops/target", group));
    }

    [Fact]
    public async Task MoveSelectedToGroupAsync_NoOpWhenSelectionAlreadyInTarget()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/target"),
            CreateServer("beta", "Beta", "ops/target"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToGroupCommand.ExecuteAsync(new BulkMoveToGroupRequest("ops/target"));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal(["alpha", "beta"], fixture.VisibleIds());
    }

    [Fact]
    public async Task MoveSelectedToGroupAsync_WhenSaveFails_DoesNotMutateViewModelOrPersistedState()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateServer("beta", "Beta", "ops/source"),
            CreateServer("gamma", "Gamma", "ops/other"));
        configManager.FailOnSaveServers = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await Assert.ThrowsAsync<IOException>(() => fixture.ViewModel.MoveSelectedToGroupCommand.ExecuteAsync(new BulkMoveToGroupRequest("ops/target")));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("ops/source", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/source", fixture.ServerById("beta").Group);

        var persistedGroups = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.Group)
            .ToArray();
        Assert.Collection(
            persistedGroups,
            group => Assert.Equal("ops/source", group),
            group => Assert.Equal("ops/source", group));
    }

    [Fact]
    public async Task BulkMutation_StillSavesBeforeUpdatingVms_AndRestoresSelectionById()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateServer("beta", "Beta", "ops/source"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        bool observedPreSaveState = false;
        configManager.BeforeSaveServers = () =>
        {
            observedPreSaveState = true;
            Assert.Equal("ops/source", fixture.ServerById("alpha").Group);
            Assert.Equal("ops/source", fixture.ServerById("beta").Group);
            AssertSelection(fixture.ViewModel, "alpha", "beta");
            Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        };

        await fixture.ViewModel.MoveSelectedToGroupCommand.ExecuteAsync(
            new BulkMoveToGroupRequest("ops/target"));

        Assert.True(observedPreSaveState);
        Assert.Equal("ops/target", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/target", fixture.ServerById("beta").Group);
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task BulkMutation_WhenSettingsLoadFails_PersistsNothingAndLeavesStateIntact()
    {
        FailingSaveConfigManager configManager = new();
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateServer("beta", "Beta", "ops/source"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        int saveCountBeforeMutation = configManager.SaveServersCallCount;
        configManager.FailOnLoadSettings = true;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.ViewModel.MoveSelectedToGroupCommand.ExecuteAsync(
                new BulkMoveToGroupRequest("ops/target")));

        // Failing the settings load can only stop the save if the load happens first: this is the
        // ordering oracle. With the load after the mutation, the servers would already be persisted.
        Assert.Equal(saveCountBeforeMutation, configManager.SaveServersCallCount);
        Assert.Equal("ops/source", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/source", fixture.ServerById("beta").Group);
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);

        configManager.FailOnLoadSettings = false;
        string?[] persistedGroups = (await configManager.LoadServersAsync())
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.Group)
            .ToArray();
        Assert.Equal(new string?[] { "ops/source", "ops/source" }, persistedGroups);
    }

    [Fact]
    public async Task MoveSelectedToGroupAsync_MovesToolEntriesToo()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateTool("chmod", "Chmod Tool", "ops/source"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));

        await fixture.ViewModel.MoveSelectedToGroupCommand.ExecuteAsync(new BulkMoveToGroupRequest("ops/target"));

        AssertSelection(fixture.ViewModel, "alpha", "chmod");
        Assert.Equal("ops/target", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/target", fixture.ServerById("chmod").Group);

        var persistedGroups = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "chmod")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.Group)
            .ToArray();
        Assert.Collection(
            persistedGroups,
            group => Assert.Equal("ops/target", group),
            group => Assert.Equal("ops/target", group));
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_MovesAllSelectedAndPreservesSelection()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"), ("project-b", "Project B"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/source", projectId: "project-a"),
            CreateServer("gamma", "Gamma", "ops/source", projectId: "project-a"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest("project-b"));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("project-b", fixture.ServerById("alpha").ProjectId);
        Assert.Equal("project-b", fixture.ServerById("beta").ProjectId);
        Assert.Equal("Project B", fixture.ServerById("alpha").ProjectName);
        Assert.Equal("Project B", fixture.ServerById("beta").ProjectName);
        Assert.Equal("ops/source", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/source", fixture.ServerById("beta").Group);
        Assert.Equal("Moved 2 item(s) to project \"Project B\".", fixture.LastStatusMessage);

        var persistedProjects = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.ProjectId)
            .ToArray();
        Assert.Collection(
            persistedProjects,
            projectId => Assert.Equal("project-b", projectId),
            projectId => Assert.Equal("project-b", projectId));
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_NoOpWhenSelectionAlreadyInTargetProject()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-b", "Project B"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-b"),
            CreateServer("beta", "Beta", "ops/source", projectId: "project-b"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest("project-b"));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("project-b", fixture.ServerById("alpha").ProjectId);
        Assert.Equal("project-b", fixture.ServerById("beta").ProjectId);
        Assert.Null(fixture.LastStatusMessage);
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_MovesCrossProjectSelection()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"), ("project-b", "Project B"), ("project-c", "Project C"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/source", projectId: "project-c"),
            CreateServer("gamma", "Gamma", "ops/source", projectId: "project-c"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest("project-b"));

        Assert.Equal("project-b", fixture.ServerById("alpha").ProjectId);
        Assert.Equal("project-b", fixture.ServerById("beta").ProjectId);
        Assert.Equal("project-c", fixture.ServerById("gamma").ProjectId);
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_WhenSaveFails_DoesNotMutateViewModelOrPersistedState()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"), ("project-b", "Project B"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/source", projectId: "project-a"),
            CreateServer("gamma", "Gamma", "ops/source", projectId: "project-a"));
        configManager.FailOnSaveServers = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await Assert.ThrowsAsync<IOException>(() => fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest("project-b")));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("project-a", fixture.ServerById("alpha").ProjectId);
        Assert.Equal("project-a", fixture.ServerById("beta").ProjectId);
        Assert.Equal("ops/source", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/source", fixture.ServerById("beta").Group);
        Assert.Null(fixture.LastStatusMessage);

        var persistedProjects = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.ProjectId)
            .ToArray();
        Assert.Collection(
            persistedProjects,
            projectId => Assert.Equal("project-a", projectId),
            projectId => Assert.Equal("project-a", projectId));
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_MovesToolEntriesToo()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"), ("project-b", "Project B"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateTool("chmod", "Chmod Tool", "ops/source", projectId: "project-a"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));

        await fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest("project-b"));

        AssertSelection(fixture.ViewModel, "alpha", "chmod");
        Assert.Equal("project-b", fixture.ServerById("alpha").ProjectId);
        Assert.Equal("project-b", fixture.ServerById("chmod").ProjectId);
    }

    [Fact]
    public async Task MoveToProjectAsync_SingleServerPreservesGroup()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"), ("project-b", "Project B"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"));

        var alpha = fixture.ServerById("alpha");

        await fixture.ViewModel.MoveToProjectCommand.ExecuteAsync(new ServerMoveToProjectRequest(alpha, "project-b"));

        Assert.Equal("project-b", alpha.ProjectId);
        Assert.Equal("ops/source", alpha.Group);
        Assert.Equal("Project B", alpha.ProjectName);
        Assert.Null(fixture.LastStatusMessage);

        var persisted = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("project-b", persisted.ProjectId);
        Assert.Equal("ops/source", persisted.Group);
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_PreservesGroupsWhenProjectChanges()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source", "ops/other");
        AddProjects(settings, ("project-a", "Project A"), ("project-b", "Project B"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/other", projectId: "project-a"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest("project-b"));

        Assert.Equal("ops/source", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/other", fixture.ServerById("beta").Group);

        var persisted = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.Group)
            .ToArray();
        Assert.Collection(
            persisted,
            group => Assert.Equal("ops/source", group),
            group => Assert.Equal("ops/other", group));
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_MovesToNoProjectAndUsesDedicatedStatus()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/source", projectId: "project-a"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.MoveSelectedToProjectCommand.ExecuteAsync(new BulkMoveToProjectRequest(null));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal(string.Empty, fixture.ServerById("alpha").ProjectId);
        Assert.Equal(string.Empty, fixture.ServerById("beta").ProjectId);
        Assert.Equal(string.Empty, fixture.ServerById("alpha").ProjectName);
        Assert.Equal(string.Empty, fixture.ServerById("beta").ProjectName);
        Assert.Equal("Moved 2 item(s) to no project.", fixture.LastStatusMessage);
    }

    [Fact]
    public async Task GetBulkGroupTargets_UnionsProjectsAndIncludesRoot()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/red", "ops/blue"),
            CreateServer("alpha", "Alpha", "ops/red", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/blue", projectId: "project-b"),
            CreateServer("gamma", "Gamma", "ops/shared", projectId: "project-b"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        var targets = fixture.ViewModel.GetBulkGroupTargets(fixture.ViewModel.SelectedItems.ToList(), includeNoGroup: true);

        Assert.Contains(targets, target => target.IsVirtualGroup && string.IsNullOrEmpty(target.GroupName));
        Assert.Contains(targets, target => string.Equals(target.GroupName, "ops/red", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => string.Equals(target.GroupName, "ops/blue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => string.Equals(target.GroupName, "ops/shared", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A folder created from the tree is written to settings.EmptyGroups and stays there once a
    /// session moves in, so the two sources this method reads overlap. Appending one after the
    /// other listed such a folder twice - two identical, interchangeable rows carrying the same
    /// destination - and left the folders that really are empty at the bottom of the submenu in
    /// settings order instead of in place.
    /// </summary>
    [Fact]
    public async Task GetGroupTargets_ListsAPopulatedFolderOnceEvenWhenSettingsStillCallItEmpty()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = new AppSettings();
        settings.EmptyGroups.AddRange(["Prod/DB", "Prod/Archive"]);
        await fixture.LoadServersAsync(settings, CreateServer("alpha", "Alpha", "Prod/DB"));

        var targets = fixture.ViewModel.GetGroupTargets(includeNoGroup: true);

        Assert.Equal(
            [string.Empty, "Prod/Archive", "Prod/DB"],
            targets.Select(target => target.GroupName).ToArray());
        Assert.True(targets[0].IsVirtualGroup);
    }

    /// <summary>
    /// Folder paths are matched case-insensitively everywhere else a move is decided
    /// (IsBulkMoveTargetEnabled, MoveServersToGroupCoreAsync), so two spellings of one folder are
    /// one destination here too. The sessions' own spelling is the one the tree displays.
    /// </summary>
    [Fact]
    public async Task GetGroupTargets_TreatsACaseVariantAsTheSameFolder()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = new AppSettings();
        settings.EmptyGroups.Add("prod/db");
        await fixture.LoadServersAsync(settings, CreateServer("alpha", "Alpha", "Prod/DB"));

        var targets = fixture.ViewModel.GetGroupTargets(includeNoGroup: false);

        Assert.Equal(["Prod/DB"], targets.Select(target => target.GroupName).ToArray());
    }

    [Fact]
    public async Task ShouldOpenBulkContextMenu_RequiresClickedItemInCurrentMultiSelection()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        Assert.True(fixture.ViewModel.ShouldOpenBulkContextMenu(fixture.ServerById("alpha")));
        Assert.False(fixture.ViewModel.ShouldOpenBulkContextMenu(fixture.ServerById("gamma")));
    }

    /// <summary>
    /// The tree entry that reaches this command on a tool says "Remove", and a tool is not a
    /// connection profile: nothing is stored for it beyond the row. Asking whether to delete a
    /// session named a kind of object the user had not clicked, so the safe reading was that
    /// they had hit the wrong entry and were about to destroy a connection.
    /// </summary>
    [Fact]
    public async Task DeleteServerAsync_ForATool_AsksAboutRemovingTheTool()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: false);
        await fixture.LoadServersAsync(new AppSettings(), CreateTool("base64", "Base64", "ops"));
        LocalizationManager localizer = await CreateLocalizerAsync();

        await fixture.ViewModel.DeleteServerCommand.ExecuteAsync(fixture.ServerById("base64"));

        // Two wordings that read alike would satisfy the equalities below while showing the user
        // the very sentence this test exists to reject.
        Assert.NotEqual(localizer["DialogTitleDeleteServer"], localizer["DialogTitleRemoveTool"]);
        Assert.NotEqual(
            localizer.Format("ConfirmDeleteServer", "Base64"),
            localizer.Format("ConfirmRemoveTool", "Base64"));
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal(localizer["DialogTitleRemoveTool"], fixture.DialogService.LastConfirmTitle);
        Assert.Equal(
            localizer.Format("ConfirmRemoveTool", "Base64"),
            fixture.DialogService.LastConfirmMessage);
        Assert.Contains("Base64", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the branch: a connection profile is still a session, and the bulk path
    /// stays kind-neutral, so nothing else may drift onto the tool wording.
    /// </summary>
    [Fact]
    public async Task DeleteServerAsync_ForASession_StillAsksAboutDeletingTheSession()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: false);
        await fixture.LoadServersAsync(new AppSettings(), CreateServer("alpha", "Alpha", "ops"));
        LocalizationManager localizer = await CreateLocalizerAsync();

        await fixture.ViewModel.DeleteServerCommand.ExecuteAsync(fixture.ServerById("alpha"));

        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal(localizer["DialogTitleDeleteServer"], fixture.DialogService.LastConfirmTitle);
        Assert.Equal(
            localizer.Format("ConfirmDeleteServer", "Alpha"),
            fixture.DialogService.LastConfirmMessage);
    }

    [Fact]
    public async Task DeleteSelectedAsync_ForLargeSelection_UsesSummaryMessage()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: false);
        var servers = Enumerable.Range(1, 11)
            .Select(index => CreateServer($"server-{index:00}", $"Server {index:00}", "ops"))
            .ToArray();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            servers);

        fixture.ViewModel.SelectSingle(fixture.ServerById("server-01"));
        foreach (var server in servers.Skip(1))
        {
            fixture.ViewModel.ToggleSelection(fixture.ServerById(server.Id));
        }

        await fixture.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Are you sure you want to delete 11 selected item(s)?", fixture.DialogService.LastConfirmMessage);
        Assert.DoesNotContain("- Server", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectSelected_AllSucceed_ReportsSummary()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            protocolHandlers: [new ScriptedProtocolHandler("SSH", Success(), Success(), Success())]);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                "Connecting 1/3: Alpha...",
                "Connecting 2/3: Beta...",
                "Connecting 3/3: Gamma...",
                "Connected 3, failed 0, skipped 0."
            ],
            fixture.StatusMessages);
        Assert.Equal(0, fixture.DialogService.ErrorCallCount);
    }

    [Fact]
    public async Task ConnectSelected_FiltersToolItems()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            protocolHandlers: [new ScriptedProtocolHandler("SSH", Success(), Success())]);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateTool("chmod", "Chmod Tool", "ops"),
            CreateServer("beta", "Beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Connect to all 2 sessions in this group?", fixture.DialogService.LastConfirmMessage);
        // BL-0082(b): the count alone said something had been left out and nothing about
        // whether the user could act on it. The reason is named; the server is not.
        Assert.Equal(
            "Connected 2, failed 0, skipped 1. Skipped: 1 (tool entry).",
            fixture.LastStatusMessage);
        Assert.DoesNotContain("chmod", fixture.LastStatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectSelected_ConfirmationDeclined_NoOp()
    {
        var handler = new ScriptedProtocolHandler("SSH", Success());
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: false,
            protocolHandlers: [handler]);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.Empty(handler.ConnectedServerIds);
        Assert.Empty(fixture.StatusMessages);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task ConnectSelected_PreflightFailureSilent_ContinuesSequence()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            protocolHandlers: [new ScriptedProtocolHandler("SSH", Success(), Success())]);
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshGatewayId = "missing-gateway";
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            beta,
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Connected 2, failed 1, skipped 0.", fixture.LastStatusMessage);
        Assert.Equal(0, fixture.DialogService.ErrorCallCount);
    }

    [Fact]
    public async Task ConnectSelected_ConnectionFailureSilent_ContinuesSequence()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            protocolHandlers: [new ScriptedProtocolHandler("SSH", Success(), Fail("boom"), Success())]);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Connected 2, failed 1, skipped 0.", fixture.LastStatusMessage);
        Assert.Equal(0, fixture.DialogService.ErrorCallCount);
    }

    [Fact]
    public async Task ConnectSelected_MissingCredentials_Skipped()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            protocolHandlers: [new ScriptedProtocolHandler("SSH", Success(), Success())]);
        var settings = fixture.ExpandGroups("ops");
        settings.UseExternalCredentialProvider = true;
        settings.CredentialProviderCommand = "cmd.exe /c exit 1";

        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshPasswordEncrypted = "pw-alpha";
        var beta = CreateServer("beta", "Beta", "ops");
        var gamma = CreateServer("gamma", "Gamma", "ops");
        gamma.SshPasswordEncrypted = "pw-gamma";
        await fixture.LoadServersAsync(settings, alpha, beta, gamma);

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Connect to all 2 sessions in this group?", fixture.DialogService.LastConfirmMessage);
        // The actionable family: an external credential provider that could not answer
        // silently now reads differently from a tool entry, which it did not before.
        Assert.Equal(
            "Connected 2, failed 0, skipped 1. Skipped: 1 (credentials not resolved).",
            fixture.LastStatusMessage);
        Assert.Equal(0, fixture.DialogService.WarningCallCount);
        Assert.Equal(0, fixture.DialogService.ErrorCallCount);
    }

    [Fact]
    public async Task ConnectSelected_CancellationDuringLoop_StopsAndSummarizes()
    {
        using var cts = new CancellationTokenSource();
        await using var fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            protocolHandlers:
            [
                new ScriptedProtocolHandler(
                    "SSH",
                    Success(server =>
                    {
                        if (server.Id.StartsWith("alpha_", StringComparison.Ordinal))
                        {
                            cts.Cancel();
                        }
                    }),
                    Success(),
                    Success())
            ]);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(),
            cts.Token);

        // This assertion used to read "Connected 1, failed 0, skipped 0." for a selection
        // of three. Two servers were never attempted and appeared in no counter at all, so
        // the summary was FALSE rather than merely terse - and this test froze it. The run
        // must now account for every selected server and say it was cut short.
        Assert.Equal(
            [
                "Connecting 1/3: Alpha...",
                "Connected 1, failed 0, skipped 0. Cancelled: 2 not attempted."
            ],
            fixture.StatusMessages);
    }

    [Fact]
    public async Task ConnectSelected_AllToolItems_ShowsNothingToConnectStatus()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateTool("chmod-1", "Chmod Tool 1", "ops"),
            CreateTool("chmod-2", "Chmod Tool 2", "ops"),
            CreateTool("chmod-3", "Chmod Tool 3", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("chmod-1"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod-2"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod-3"));

        await fixture.ViewModel.ConnectSelectedCommand.ExecuteAsync(null);

        // Three servers were selected and every one was skipped. Saying only "Nothing to
        // connect." names an empty selection, which is not what happened: this path used to
        // discard the skip count entirely.
        Assert.Equal(
            "Nothing to connect: 3 selected server(s) were skipped. Skipped: 3 (tool entry).",
            fixture.LastStatusMessage);
        Assert.Equal(0, fixture.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_DuplicatesSelectedAndSelectsClones()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(2, fixture.ViewModel.SelectedItems.Count);
        Assert.All(
            fixture.ViewModel.SelectedItems,
            item => Assert.False(new[] { "alpha", "beta", "gamma" }.Contains(item.Id, StringComparer.Ordinal)));
        Assert.Equal(
            ["Alpha (copy)", "Beta (copy)"],
            fixture.ViewModel.SelectedItems.Select(item => item.DisplayName).ToArray());
        Assert.Equal(
            fixture.ViewModel.SelectedItems.Last().Id,
            fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("Duplicated 2 item(s).", fixture.LastStatusMessage);

        var persistedNames = (await fixture.ConfigManager.LoadServersAsync())
            .Select(server => server.DisplayName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["Alpha", "Alpha (copy)", "Beta", "Beta (copy)", "Gamma"],
            persistedNames);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_UsesGloballyUniqueNamesAcrossBatch()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha-1", "Alpha", "ops"),
            CreateServer("alpha-2", "Alpha", "ops"),
            CreateServer("alpha-copy", "Alpha (copy)", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha-1"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("alpha-2"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Alpha (copy) 2", "Alpha (copy) 3"],
            fixture.ViewModel.SelectedItems.Select(item => item.DisplayName).ToArray());

        var persistedNames = (await fixture.ConfigManager.LoadServersAsync())
            .Select(server => server.DisplayName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["Alpha", "Alpha", "Alpha (copy)", "Alpha (copy) 2", "Alpha (copy) 3"],
            persistedNames);
    }

    [Fact]
    public async Task DuplicateServerAsync_SingleServerUsesSharedUniqueNaming()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("alpha-copy", "Alpha (copy)", "ops"));

        await fixture.ViewModel.DuplicateServerCommand.ExecuteAsync(fixture.ServerById("alpha"));

        Assert.Single(fixture.ViewModel.SelectedItems);
        Assert.Equal("Alpha (copy) 2", fixture.ViewModel.SelectedServer?.DisplayName);
        Assert.Equal("Alpha (copy) 2", fixture.ViewModel.SelectedItems.Single().DisplayName);
        Assert.Null(fixture.LastStatusMessage);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_TheCopyStillUsesTheLegacySshCredentialMapping()
    {
        FailingSaveConfigManager configManager = new();
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager);
        ServerProfileDto legacy = new()
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.com",
            ConnectionType = "SSH",
            Group = "ops",
            Origin = ProfileOrigin.Manual,
            SshKeyPath = "/home/ops/id_ed25519",
            SshPasswordEncrypted = "ssh-secret"
        };

        // SshKeyPassphraseEncrypted is deliberately never assigned: the mapping keys on the absence
        // of the field, and assigning it - even to null - would raise the presence flag.
        Assert.True(legacy.UsesLegacySshCredentialMapping);

        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            legacy,
            CreateServer("beta", "Beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        List<ServerProfileDto> saved = Assert.IsType<List<ServerProfileDto>>(configManager.LastSavedServers);
        ServerProfileDto clone = Assert.Single(saved, server => server.DisplayName == "Alpha (copy)");

        // The duplicate used to be a JSON round-trip, which raised the passphrase presence flag on
        // the copy even though the source never declared the field. UsesLegacySshCredentialMapping
        // then read false, so the duplicate stopped offering the stored password as the key
        // passphrase and failed to authenticate where the original succeeded.
        Assert.Equal("/home/ops/id_ed25519", clone.SshKeyPath);
        Assert.Equal("ssh-secret", clone.SshPasswordEncrypted);
        Assert.False(clone.HasSshKeyPassphraseEncryptedField);
        Assert.True(clone.UsesLegacySshCredentialMapping);
        Assert.False(clone.HasWinRmPortField);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_ClearsOnlyRdpPasswordAndPreservesOtherSecrets()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var sensitiveServer = new ServerProfileDto
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.com",
            ConnectionType = "SSH",
            Group = "ops",
            Origin = ProfileOrigin.Manual,
            RdpPasswordEncrypted = "rdp-secret",
            SshPasswordEncrypted = "ssh-secret",
            FtpPasswordEncrypted = "ftp-secret"
        };

        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            sensitiveServer,
            CreateServer("beta", "Beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        var clones = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is not "alpha" and not "beta")
            .OrderBy(server => server.DisplayName, StringComparer.Ordinal)
            .ToList();
        var alphaClone = Assert.Single(clones, server => server.DisplayName == "Alpha (copy)");

        Assert.Null(alphaClone.RdpPasswordEncrypted);
        Assert.Equal("ssh-secret", alphaClone.SshPasswordEncrypted);
        Assert.Equal("ftp-secret", alphaClone.FtpPasswordEncrypted);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_PreservesGroupAndProjectOnClones()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var settings = fixture.ExpandGroups("ops", "ops/source");
        AddProjects(settings, ("project-a", "Project A"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops/source", projectId: "project-a"),
            CreateServer("beta", "Beta", "ops/other", projectId: "project-a"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        var alphaClone = Assert.Single(fixture.ViewModel.SelectedItems, item => item.DisplayName == "Alpha (copy)");
        var betaClone = Assert.Single(fixture.ViewModel.SelectedItems, item => item.DisplayName == "Beta (copy)");

        Assert.Equal("ops/source", alphaClone.Group);
        Assert.Equal("ops/other", betaClone.Group);
        Assert.Equal("project-a", alphaClone.ProjectId);
        Assert.Equal("project-a", betaClone.ProjectId);
        Assert.Equal("Project A", alphaClone.ProjectName);
        Assert.Equal("Project A", betaClone.ProjectName);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_DuplicatesToolEntriesToo()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateTool("chmod", "Chmod Tool", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Alpha (copy)", "Chmod Tool (copy)"],
            fixture.ViewModel.SelectedItems.Select(item => item.DisplayName).ToArray());
        Assert.Equal(
            ["SSH", "TOOL:CHMOD"],
            fixture.ViewModel.SelectedItems.Select(item => item.ConnectionType).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task DuplicateSelectedAsync_WhenSaveFails_DoesNotMutateViewModelOrPersistedState()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));
        configManager.FailOnSaveServers = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await Assert.ThrowsAsync<IOException>(() => fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null));

        Assert.Equal(["alpha", "beta", "gamma"], fixture.VisibleIds());
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Null(fixture.LastStatusMessage);

        var persistedIds = (await fixture.ConfigManager.LoadServersAsync())
            .Select(server => server.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["alpha", "beta", "gamma"], persistedIds);
    }

    [Fact]
    public async Task DuplicateSelectedAsync_AssignsDistinctIdsToAllClones()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.DuplicateSelectedCommand.ExecuteAsync(null);

        var cloneIds = fixture.ViewModel.SelectedItems.Select(item => item.Id).ToArray();
        Assert.Equal(3, cloneIds.Length);
        Assert.Equal(3, cloneIds.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("alpha", cloneIds);
        Assert.DoesNotContain("beta", cloneIds);
        Assert.DoesNotContain("gamma", cloneIds);
    }

    [Fact]
    public async Task ServerBulkEditViewModel_ValidatesMixedAndPrefilledStates()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        var mixedVm = new ServerBulkEditViewModel(localizer, 3, null);
        Assert.True(mixedVm.ShowMixedValuesHint);
        Assert.False(mixedVm.IsApplyEnabled);

        mixedVm.Input = "70000";
        Assert.Equal("Port must be between 1 and 65535.", mixedVm.ValidationError);
        Assert.False(mixedVm.IsApplyEnabled);

        mixedVm.Input = "2022";
        Assert.Null(mixedVm.ValidationError);
        Assert.Equal(2022, mixedVm.ResolvedPort);
        Assert.True(mixedVm.IsApplyEnabled);

        var prefilledVm = new ServerBulkEditViewModel(localizer, 2, 22);
        Assert.Equal("22", prefilledVm.Input);
        Assert.False(prefilledVm.IsApplyEnabled);

        prefilledVm.Input = "2222";
        Assert.True(prefilledVm.IsApplyEnabled);
        Assert.Equal(2222, prefilledVm.ResolvedPort);
    }

    [Fact]
    public async Task BulkEditPortAsync_WhenAllSelectedAlreadyMatch_ShowsNoOpWithoutSaving()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshPort = 2222;
        alpha.RemotePort = 2222;
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshPort = 2222;
        beta.RemotePort = 2222;

        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            alpha,
            beta);
        configManager.FailOnSaveServers = true;
        fixture.DialogService.NextBulkEditPortResult = 2222;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.BulkEditPortCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditPortCallCount);
        Assert.Equal(2, fixture.DialogService.LastBulkEditPortCount);
        Assert.Equal(2222, fixture.DialogService.LastBulkEditPortInitialPort);
        Assert.Equal("No port changes were applied.", fixture.LastStatusMessage);
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task BulkEditPortAsync_UpdatesOnlyDirtyItemsAndPreservesSelection()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshPort = 2022;
        beta.RemotePort = 2022;
        var gamma = CreateServer("gamma", "Gamma", "ops");

        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            alpha,
            beta,
            gamma);
        fixture.DialogService.NextBulkEditPortResult = 2022;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.BulkEditPortCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        AssertSelection(fixture.ViewModel, "alpha", "beta", "gamma");
        Assert.Equal("gamma", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal(2022, fixture.ServerById("alpha").EffectivePort);
        Assert.Equal(2022, fixture.ServerById("beta").EffectivePort);
        Assert.Equal(2022, fixture.ServerById("gamma").EffectivePort);
        Assert.Equal("Updated port on 2 item(s).", fixture.LastStatusMessage);

        var storedPorts = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta" or "gamma")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(GetStoredEditablePort)
            .ToArray();
        Assert.Equal([2022, 2022, 2022], storedPorts);
    }

    [Fact]
    public async Task BulkEditPortAsync_IncludesToolEntries()
    {
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateTool("chmod", "Chmod Tool", "ops"));
        fixture.DialogService.NextBulkEditPortResult = 2200;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));

        await fixture.ViewModel.BulkEditPortCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        AssertSelection(fixture.ViewModel, "alpha", "chmod");
        Assert.Equal(2200, fixture.ServerById("alpha").EffectivePort);
        Assert.Equal(2200, fixture.ServerById("chmod").EffectivePort);
        Assert.Equal("Updated port on 2 item(s).", fixture.LastStatusMessage);

        var storedPorts = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "chmod")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(GetStoredEditablePort)
            .ToArray();
        Assert.Equal([2200, 2200], storedPorts);
    }

    [Fact]
    public async Task BulkEditPortAsync_WhenSaveFails_DoesNotMutateViewModelOrPersistedState()
    {
        var configManager = new FailingSaveConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateTool("chmod", "Chmod Tool", "ops"));
        fixture.DialogService.NextBulkEditPortResult = 2022;
        configManager.FailOnSaveServers = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("chmod"));

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.ViewModel.BulkEditPortCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList()));

        AssertSelection(fixture.ViewModel, "alpha", "chmod");
        Assert.Equal("chmod", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal(22, fixture.ServerById("alpha").EffectivePort);
        Assert.Equal(DefaultPorts.Rdp, fixture.ServerById("chmod").EffectivePort);
        Assert.Null(fixture.LastStatusMessage);

        var storedPorts = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "chmod")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(GetStoredEditablePort)
            .ToArray();
        Assert.Equal([22, DefaultPorts.Rdp], storedPorts);
    }

    [Fact]
    public async Task ServerBulkEditUsernameViewModel_ValidatesMixedPrefilledTrimAndControlCharacterStates()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        var mixedVm = new ServerBulkEditUsernameViewModel(localizer, 3, null);
        Assert.True(mixedVm.ShowMixedValuesHint);
        Assert.False(mixedVm.IsApplyEnabled);

        mixedVm.Input = "   ";
        Assert.Null(mixedVm.ValidationError);
        Assert.Null(mixedVm.ResolvedUsername);
        Assert.False(mixedVm.IsApplyEnabled);

        mixedVm.Input = "ops\nadmin";
        Assert.Equal(
            "Username cannot be empty and cannot contain control characters (including line breaks and tabs).",
            mixedVm.ValidationError);
        Assert.False(mixedVm.IsApplyEnabled);

        mixedVm.Input = "  ops  ";
        Assert.Null(mixedVm.ValidationError);
        Assert.Equal("ops", mixedVm.ResolvedUsername);
        Assert.True(mixedVm.IsApplyEnabled);

        var prefilledVm = new ServerBulkEditUsernameViewModel(localizer, 2, "admin");
        Assert.Equal("admin", prefilledVm.Input);
        Assert.False(prefilledVm.IsApplyEnabled);

        prefilledVm.Input = "Admin";
        Assert.Equal("Admin", prefilledVm.ResolvedUsername);
        Assert.True(prefilledVm.IsApplyEnabled);
    }

    [Theory]
    [InlineData("TOOL:PING")]
    [InlineData("TOOL:EXT:SYSINTERNALS:PSEXEC")]
    [InlineData("LOCAL")]
    [InlineData("UNKNOWN")]
    [InlineData("CITRIX")]
    [InlineData("TELNET")]
    [InlineData("")]
    [InlineData(null)]
    public void BulkCredentialBoundary_UnsupportedType_DoesNotMutateRdpFields(string? connectionType)
    {
        ServerProfileDto server = CreatePasswordServer("alpha", "Alpha", "ops", connectionType);
        server.RdpUsername = "rdp-user-before";
        server.RdpPasswordEncrypted = "rdp-password-before";

        bool usernameAccepted = ServerListViewModel.TrySetEditableUsername(server, "new-user");
        bool passwordAccepted = ServerListViewModel.TrySetEditablePassword(server, "new-password");

        Assert.False(usernameAccepted);
        Assert.False(passwordAccepted);
        Assert.Equal("rdp-user-before", server.RdpUsername);
        Assert.Equal("rdp-password-before", server.RdpPasswordEncrypted);
    }

    [Fact]
    public void BulkCredentialBoundary_VncAcceptsOnlyPassword_AndPreservesRdpFields()
    {
        ServerProfileDto server = CreatePasswordServer("alpha", "Alpha", "ops", "VNC");
        server.RdpUsername = "rdp-user-before";
        server.RdpPasswordEncrypted = "rdp-password-before";
        server.VncPassword = "vnc-password-before";

        bool usernameAccepted = ServerListViewModel.TrySetEditableUsername(server, "new-user");
        bool passwordAccepted = ServerListViewModel.TrySetEditablePassword(server, "vnc-password-after");

        Assert.False(usernameAccepted);
        Assert.True(passwordAccepted);
        Assert.Equal("rdp-user-before", server.RdpUsername);
        Assert.Equal("rdp-password-before", server.RdpPasswordEncrypted);
        Assert.Equal("vnc-password-after", server.VncPassword);
    }

    [Fact]
    public void BulkCredentialBoundary_WinRmWithoutUsername_RejectsPasswordAndPreservesIdentity()
    {
        ServerProfileDto server = CreatePasswordServer("alpha", "Alpha", "ops", "WINRM");
        server.WinRmUsername = "  ";
        server.WinRmPasswordEncrypted = "password-before";

        bool passwordAccepted = ServerListViewModel.TrySetEditablePassword(server, "password-after");

        Assert.False(passwordAccepted);
        Assert.Equal("password-before", server.WinRmPasswordEncrypted);
        Assert.Equal(WinRmIdentityMode.CurrentUser, server.WinRmIdentityMode);
    }

    [Fact]
    public async Task ServerBulkEditUsername_BasicBulkUpdate_AllUsernamesUpdated()
    {
        var configManager = new UsernameAwareConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshUsername = "root";
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshUsername = "admin";
        var gamma = CreateServer("gamma", "Gamma", "ops");
        gamma.SshUsername = "user1";

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), alpha, beta, gamma);
        configManager.SaveServersCallCount = 0;
        fixture.DialogService.NextBulkEditUsernameResult = "ops";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditUsernameCallCount);
        Assert.Equal(3, fixture.DialogService.LastBulkEditUsernameCount);
        Assert.Null(fixture.DialogService.LastBulkEditUsernameInitialUsername);
        Assert.Equal(1, configManager.SaveServersCallCount);
        AssertSelection(fixture.ViewModel, "alpha", "beta", "gamma");
        Assert.Equal("gamma", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("ops", fixture.ServerById("alpha").Username);
        Assert.Equal("ops", fixture.ServerById("beta").Username);
        Assert.Equal("ops", fixture.ServerById("gamma").Username);
        Assert.Equal("Username updated on 3 server(s).", fixture.LastStatusMessage);

        var storedUsernames = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta" or "gamma")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(GetStoredEditableUsername)
            .ToArray();
        Assert.Equal(["ops", "ops", "ops"], storedUsernames);
    }

    [Fact]
    public async Task ServerBulkEditUsername_NoOp_AllAlreadyAtTarget_NoSave()
    {
        var configManager = new UsernameAwareConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshUsername = "admin";
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshUsername = "admin";
        var gamma = CreateServer("gamma", "Gamma", "ops");
        gamma.SshUsername = "admin";

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), alpha, beta, gamma);
        configManager.SaveServersCallCount = 0;
        fixture.DialogService.NextBulkEditUsernameResult = "admin";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditUsernameCallCount);
        Assert.Equal(3, fixture.DialogService.LastBulkEditUsernameCount);
        Assert.Equal("admin", fixture.DialogService.LastBulkEditUsernameInitialUsername);
        Assert.Equal(0, configManager.SaveServersCallCount);
        AssertSelection(fixture.ViewModel, "alpha", "beta", "gamma");
        Assert.Equal("gamma", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("No changes applied — every selected server already uses this username.", fixture.LastStatusMessage);

        var storedUsernames = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta" or "gamma")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(GetStoredEditableUsername)
            .ToArray();
        Assert.Equal(["admin", "admin", "admin"], storedUsernames);
    }

    [Fact]
    public async Task ServerBulkEditUsername_CaseSensitiveDelta_IsNotNoOp()
    {
        var configManager = new UsernameAwareConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshUsername = "admin";
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshUsername = "admin";
        var gamma = CreateServer("gamma", "Gamma", "ops");
        gamma.SshUsername = "admin";

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), alpha, beta, gamma);
        configManager.SaveServersCallCount = 0;
        fixture.DialogService.NextBulkEditUsernameResult = "Admin";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, configManager.SaveServersCallCount);
        Assert.Equal("Username updated on 3 server(s).", fixture.LastStatusMessage);

        var storedUsernames = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta" or "gamma")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(GetStoredEditableUsername)
            .ToArray();
        Assert.Equal(["Admin", "Admin", "Admin"], storedUsernames);
    }

    [Fact]
    public async Task ServerBulkEditUsername_RdpDispatch_UsesRdpUsername_AndPreservesLegacySshUsername()
    {
        var configManager = new UsernameAwareConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.ConnectionType = "RDP";
        alpha.SshUsername = "legacy-ssh";
        alpha.RdpUsername = "old-rdp";
        var beta = CreateServer("beta", "Beta", "ops");
        beta.ConnectionType = "RDP";
        beta.RdpUsername = "beta-rdp";

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), alpha, beta);
        configManager.SaveServersCallCount = 0;
        fixture.DialogService.NextBulkEditUsernameResult = "newuser";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, configManager.SaveServersCallCount);

        var storedServers = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("legacy-ssh", storedServers[0].SshUsername);
        Assert.Equal("newuser", storedServers[0].RdpUsername);
        Assert.Equal("newuser", storedServers[1].RdpUsername);
    }

    [Theory]
    [InlineData("SSH", "alpha")]
    [InlineData("SFTP", "alpha")]
    [InlineData("FTP", "alpha")]
    [InlineData("WINRM", "alpha")]
    [InlineData("RDP", "alpha")]
    public async Task ServerBulkEditUsername_DispatchesByConnectionType_OnlyExpectedUsernameFieldChanges(
        string connectionType,
        string newUsername)
    {
        var configManager = new UsernameAwareConfigManager();
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var server = CreateServer("alpha", "Alpha", "ops");
        server.ConnectionType = connectionType;

        switch (connectionType)
        {
            case "SSH":
            case "SFTP":
                server.SshUsername = "old";
                break;
            case "FTP":
                server.FtpUsername = "old";
                break;
            case "WINRM":
                server.WinRmUsername = "old";
                server.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;
                break;
            default:
                server.RdpUsername = "old";
                break;
        }

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), server, CreateServer("beta", "Beta", "ops"));
        configManager.SaveServersCallCount = 0;
        fixture.DialogService.NextBulkEditUsernameResult = newUsername;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        var stored = (await fixture.ConfigManager.LoadServersAsync())
            .Single(dto => dto.Id == "alpha");

        switch (connectionType)
        {
            case "SSH":
            case "SFTP":
                Assert.Equal(newUsername, stored.SshUsername);
                Assert.True(string.IsNullOrEmpty(stored.RdpUsername));
                Assert.True(string.IsNullOrEmpty(stored.FtpUsername));
                Assert.True(string.IsNullOrEmpty(stored.TelnetUsername));
                break;
            case "FTP":
                Assert.Equal(newUsername, stored.FtpUsername);
                Assert.True(string.IsNullOrEmpty(stored.SshUsername));
                Assert.True(string.IsNullOrEmpty(stored.RdpUsername));
                Assert.True(string.IsNullOrEmpty(stored.TelnetUsername));
                break;
            case "WINRM":
                Assert.Equal(newUsername, stored.WinRmUsername);
                Assert.Equal(WinRmIdentityMode.Credential, stored.WinRmIdentityMode);
                Assert.True(string.IsNullOrEmpty(stored.SshUsername));
                Assert.True(string.IsNullOrEmpty(stored.RdpUsername));
                Assert.True(string.IsNullOrEmpty(stored.FtpUsername));
                Assert.True(string.IsNullOrEmpty(stored.TelnetUsername));
                break;
            default:
                Assert.Equal(newUsername, stored.RdpUsername);
                Assert.True(string.IsNullOrEmpty(stored.SshUsername));
                Assert.True(string.IsNullOrEmpty(stored.FtpUsername));
                Assert.True(string.IsNullOrEmpty(stored.TelnetUsername));
                break;
        }
    }

    [Fact]
    public async Task ServerBulkEditUsername_SavePersistFails_RollsBackInMemory()
    {
        var configManager = new UsernameAwareConfigManager
        {
            FailOnSaveServers = true
        };
        await using var fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true, configManager: configManager);
        var alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshUsername = "root";
        var beta = CreateServer("beta", "Beta", "ops");
        beta.SshUsername = "admin";
        var gamma = CreateServer("gamma", "Gamma", "ops");
        gamma.SshUsername = "user1";

        configManager.FailOnSaveServers = false;
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), alpha, beta, gamma);
        configManager.SaveServersCallCount = 0;
        configManager.FailOnSaveServers = true;
        fixture.DialogService.NextBulkEditUsernameResult = "ops";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList()));

        AssertSelection(fixture.ViewModel, "alpha", "beta", "gamma");
        Assert.Equal("gamma", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("root", fixture.ServerById("alpha").Username);
        Assert.Equal("admin", fixture.ServerById("beta").Username);
        Assert.Equal("user1", fixture.ServerById("gamma").Username);
        Assert.Null(fixture.LastStatusMessage);
    }

    [Theory]
    [InlineData("RDP", nameof(ServerProfileDto.RdpPasswordEncrypted))]
    [InlineData("SSH", nameof(ServerProfileDto.SshPasswordEncrypted))]
    [InlineData("SFTP", nameof(ServerProfileDto.SshPasswordEncrypted))]
    [InlineData("FTP", nameof(ServerProfileDto.FtpPasswordEncrypted))]
    [InlineData("WINRM", nameof(ServerProfileDto.WinRmPasswordEncrypted))]
    [InlineData("VNC", nameof(ServerProfileDto.VncPassword))]
    public async Task BulkEditPasswordAsync_RoutesPasswordByConnectionTypeAndPreservesSelection(
        string? connectionType,
        string expectedPasswordField)
    {
        const string NewPassword = "new-secret";
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        ServerProfileDto alpha = CreatePasswordServer("alpha", "Alpha", "ops", connectionType);
        ServerProfileDto beta = CreatePasswordServer("beta", "Beta", "ops", connectionType);
        if (string.Equals(connectionType, "WINRM", StringComparison.Ordinal))
        {
            alpha.WinRmUsername = "operator";
            beta.WinRmUsername = "operator";
        }

        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            alpha,
            beta);
        fixture.DialogService.NextBulkEditPasswordResult = NewPassword;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditPasswordCallCount);
        Assert.Equal(2, fixture.DialogService.LastBulkEditPasswordCount);
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("Password updated on 2 server(s).", fixture.LastStatusMessage);

        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync())
            .Where(server => server.Id is "alpha" or "beta")
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, storedServers.Length);
        foreach (ServerProfileDto storedServer in storedServers)
        {
            AssertOnlyPasswordFieldSet(storedServer, expectedPasswordField, NewPassword);
            if (string.Equals(expectedPasswordField, nameof(ServerProfileDto.WinRmPasswordEncrypted), StringComparison.Ordinal))
            {
                Assert.Equal(WinRmIdentityMode.Credential, storedServer.WinRmIdentityMode);
            }
        }
    }

    [Fact]
    public async Task BulkEditPasswordAsync_MixedWinRmUsernames_UpdatesOnlyCredentialProfilesAndReportsSkipped()
    {
        const string NewPassword = "new-secret";
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        ServerProfileDto credential = CreatePasswordServer("credential", "Credential", "ops", "WINRM");
        credential.WinRmUsername = "operator";
        ServerProfileDto currentUser = CreatePasswordServer("current", "Current", "ops", "WINRM");
        currentUser.WinRmUsername = " ";

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), credential, currentUser);
        fixture.DialogService.NextBulkEditPasswordResult = NewPassword;
        fixture.ViewModel.SelectSingle(fixture.ServerById("credential"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("current"));
        Assert.Equal(1, fixture.ViewModel.GetBulkPasswordTargetCount(fixture.ViewModel.SelectedItems.ToList()));

        await fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditPasswordCallCount);
        Assert.Equal(1, fixture.DialogService.LastBulkEditPasswordCount);
        Assert.Equal(
            "Password updated on 1 server(s). Skipped 1 WinRM profile(s) because no username is configured.",
            fixture.LastStatusMessage);
        Dictionary<string, ServerProfileDto> stored = (await fixture.ConfigManager.LoadServersAsync())
            .ToDictionary(server => server.Id, StringComparer.Ordinal);
        Assert.Equal(NewPassword, CredentialProtector.Unprotect(stored["credential"].WinRmPasswordEncrypted));
        Assert.Equal(WinRmIdentityMode.Credential, stored["credential"].WinRmIdentityMode);
        Assert.Null(stored["current"].WinRmPasswordEncrypted);
        Assert.Equal(WinRmIdentityMode.CurrentUser, stored["current"].WinRmIdentityMode);
    }

    [Fact]
    public async Task BulkEditPasswordAsync_OnlyWinRmWithoutUsername_SkipsDialogAndMutation()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        ServerProfileDto alpha = CreatePasswordServer("alpha", "Alpha", "ops", "WINRM");
        ServerProfileDto beta = CreatePasswordServer("beta", "Beta", "ops", "WINRM");

        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), alpha, beta);
        fixture.DialogService.NextBulkEditPasswordResult = "new-secret";
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        Assert.Equal(0, fixture.ViewModel.GetBulkPasswordTargetCount(fixture.ViewModel.SelectedItems.ToList()));

        await fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(0, fixture.DialogService.BulkEditPasswordCallCount);
        Assert.Equal(
            "Password updated on 0 server(s). Skipped 2 WinRM profile(s) because no username is configured.",
            fixture.LastStatusMessage);
        ServerProfileDto[] stored = (await fixture.ConfigManager.LoadServersAsync())
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.All(stored, server => Assert.Null(server.WinRmPasswordEncrypted));
        Assert.All(stored, server => Assert.Equal(WinRmIdentityMode.CurrentUser, server.WinRmIdentityMode));
    }

    [Fact]
    public async Task BulkCredentialEdit_HeterogeneousSelection_MutatesOnlyEligibleAndReportsActualCounts()
    {
        const string NewPassword = "new-secret";
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        ServerProfileDto rdp = CreatePasswordServer("rdp", "RDP", "ops", "RDP");
        rdp.RdpUsername = "rdp-user-before";
        rdp.RdpPasswordEncrypted = "rdp-password-before";
        ServerProfileDto vnc = CreatePasswordServer("vnc", "VNC", "ops", "VNC");
        vnc.RdpUsername = "vnc-rdp-user-before";
        vnc.RdpPasswordEncrypted = "vnc-rdp-password-before";
        ServerProfileDto tool = CreatePasswordServer("tool", "Tool", "ops", "TOOL:PING");
        tool.RdpUsername = "tool-rdp-user-before";
        tool.RdpPasswordEncrypted = "tool-rdp-password-before";
        ServerProfileDto local = CreatePasswordServer("local", "Local", "ops", "LOCAL");
        local.RdpUsername = "local-rdp-user-before";
        local.RdpPasswordEncrypted = "local-rdp-password-before";
        ServerProfileDto unknown = CreatePasswordServer("unknown", "Unknown", "ops", "UNKNOWN");
        unknown.RdpUsername = "unknown-rdp-user-before";
        unknown.RdpPasswordEncrypted = "unknown-rdp-password-before";
        ServerProfileDto citrix = CreatePasswordServer("citrix", "Citrix", "ops", "CITRIX");
        citrix.RdpUsername = "citrix-rdp-user-before";
        citrix.RdpPasswordEncrypted = "citrix-rdp-password-before";
        ServerProfileDto telnet = CreatePasswordServer("telnet", "Telnet", "ops", "TELNET");
        telnet.TelnetUsername = "telnet-user-before";
        telnet.TelnetPasswordEncrypted = "telnet-password-before";

        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            rdp,
            vnc,
            tool,
            local,
            unknown,
            citrix,
            telnet);
        fixture.ViewModel.SelectSingle(fixture.ServerById("rdp"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("vnc"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("tool"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("local"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("unknown"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("citrix"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("telnet"));

        fixture.DialogService.NextBulkEditUsernameResult = "rdp-user-after";
        await fixture.ViewModel.BulkEditUsernameCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditUsernameCallCount);
        Assert.Equal(1, fixture.DialogService.LastBulkEditUsernameCount);
        Assert.Equal("rdp-user-before", fixture.DialogService.LastBulkEditUsernameInitialUsername);
        Assert.Equal("Username updated on 1 server(s).", fixture.LastStatusMessage);
        AssertSelection(fixture.ViewModel, "rdp", "vnc", "tool", "local", "unknown", "citrix", "telnet");

        fixture.DialogService.NextBulkEditPasswordResult = NewPassword;
        await fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditPasswordCallCount);
        Assert.Equal(2, fixture.DialogService.LastBulkEditPasswordCount);
        Assert.Equal("Password updated on 2 server(s).", fixture.LastStatusMessage);
        AssertSelection(fixture.ViewModel, "rdp", "vnc", "tool", "local", "unknown", "citrix", "telnet");

        Dictionary<string, ServerProfileDto> stored = (await fixture.ConfigManager.LoadServersAsync())
            .ToDictionary(server => server.Id, StringComparer.Ordinal);
        Assert.Equal("rdp-user-after", stored["rdp"].RdpUsername);
        Assert.Equal("vnc-rdp-user-before", stored["vnc"].RdpUsername);
        Assert.Equal("tool-rdp-user-before", stored["tool"].RdpUsername);
        Assert.Equal("local-rdp-user-before", stored["local"].RdpUsername);
        Assert.Equal("unknown-rdp-user-before", stored["unknown"].RdpUsername);
        Assert.Equal("citrix-rdp-user-before", stored["citrix"].RdpUsername);
        Assert.Equal("telnet-user-before", stored["telnet"].TelnetUsername);
        Assert.Equal(NewPassword, CredentialProtector.Unprotect(stored["rdp"].RdpPasswordEncrypted));
        Assert.Equal(NewPassword, CredentialProtector.Unprotect(stored["vnc"].VncPassword));
        Assert.Equal(stored["rdp"].RdpPasswordEncrypted, stored["vnc"].VncPassword);
        Assert.Equal("vnc-rdp-password-before", stored["vnc"].RdpPasswordEncrypted);
        Assert.Equal("tool-rdp-password-before", stored["tool"].RdpPasswordEncrypted);
        Assert.Equal("local-rdp-password-before", stored["local"].RdpPasswordEncrypted);
        Assert.Equal("unknown-rdp-password-before", stored["unknown"].RdpPasswordEncrypted);
        Assert.Equal("citrix-rdp-password-before", stored["citrix"].RdpPasswordEncrypted);
        Assert.Equal("telnet-password-before", stored["telnet"].TelnetPasswordEncrypted);
    }

    [Fact]
    public async Task BulkEditPasswordAsync_SingleSelection_NoDialogAndNoMutation()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreatePasswordServer("alpha", "Alpha", "ops", "SSH"));
        fixture.DialogService.NextBulkEditPasswordResult = "new-secret";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        await fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(0, fixture.DialogService.BulkEditPasswordCallCount);
        ServerProfileDto storedServer = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        AssertNoStoredPasswords(storedServer);
    }

    [Fact]
    public async Task BulkEditPasswordAsync_WhenDialogCancelled_DoesNotMutateAndPreservesSelection()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreatePasswordServer("alpha", "Alpha", "ops", "SSH"),
            CreatePasswordServer("beta", "Beta", "ops", "FTP"));
        fixture.DialogService.NextBulkEditPasswordResult = null;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(1, fixture.DialogService.BulkEditPasswordCallCount);
        Assert.Equal(2, fixture.DialogService.LastBulkEditPasswordCount);
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync())
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.All(storedServers, AssertNoStoredPasswords);
    }

    [Fact]
    public async Task BulkEditPasswordAsync_WhenSaveFails_DoesNotMutatePersistedStateOrSelection()
    {
        FailingSaveConfigManager configManager = new();
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager: configManager);
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreatePasswordServer("alpha", "Alpha", "ops", "SSH"),
            CreatePasswordServer("beta", "Beta", "ops", "RDP"));
        fixture.DialogService.NextBulkEditPasswordResult = "new-secret";
        configManager.FailOnSaveServers = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.ViewModel.BulkEditPasswordCommand.ExecuteAsync(fixture.ViewModel.SelectedItems.ToList()));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Null(fixture.LastStatusMessage);
        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync())
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.All(storedServers, AssertNoStoredPasswords);
    }

    [Fact]
    public async Task UpdateGatewayReferencesAsync_ReassignsGatewayAndRefreshesBadges()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshGatewayId = "gw-missing";
        ServerProfileDto beta = CreateServer("beta", "Beta", "ops");
        beta.SshGatewayId = "gw-missing";
        ServerProfileDto gamma = CreateServer("gamma", "Gamma", "ops");
        await fixture.LoadServersAsync(settings, alpha, beta, gamma);
        fixture.ViewModel.SelectSingle(fixture.ServerById("gamma"));

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(
            ["alpha", "beta"],
            "gw-target");

        Assert.Equal(2, updatedCount);
        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync())
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.All(
            storedServers.Where(server => server.Id is "alpha" or "beta"),
            server =>
            {
                Assert.Equal("gw-target", server.SshGatewayId);
                Assert.False(server.UseDirectConnection);
            });
        ServerItemViewModel alphaVm = fixture.ServerById("alpha");
        Assert.Equal("via Bastion", alphaVm.GatewayBadgeText);
        Assert.False(alphaVm.IsGatewayMissing);
        AssertSelection(fixture.ViewModel, "gamma");
    }

    [Fact]
    public async Task UpdateGatewayReferences_TelnetOrIneligible_IsSkippedAndReported()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        ServerProfileDto telnet = CreateServer("telnet", "Telnet", "ops");
        telnet.ConnectionType = "TELNET";
        ServerProfileDto ssh = CreateServer("ssh", "SSH", "ops");
        await fixture.LoadServersAsync(settings, telnet, ssh);

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(
            ["telnet", "ssh"],
            "gw-target");

        Assert.Equal(1, updatedCount);
        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        ServerProfileDto storedTelnet = Assert.Single(storedServers, server => server.Id == "telnet");
        ServerProfileDto storedSsh = Assert.Single(storedServers, server => server.Id == "ssh");
        Assert.Null(storedTelnet.SshGatewayId);
        Assert.Equal("gw-target", storedSsh.SshGatewayId);
        Assert.Equal(
            "Skipped 1 item(s): their protocols do not support SSH gateways.",
            fixture.LastStatusMessage);
    }

    [Fact]
    public async Task UpdateGatewayReferences_MissingGatewayId_DoesNotSave()
    {
        FailingSaveConfigManager configManager = new();
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager: configManager);
        AppSettings settings = fixture.ExpandGroups("ops");
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshGatewayId = "gw-original";
        await fixture.LoadServersAsync(settings, alpha);
        int saveCountBeforeUpdate = configManager.SaveServersCallCount;

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(
            ["alpha"],
            "gw-removed");

        Assert.Equal(0, updatedCount);
        Assert.Equal(saveCountBeforeUpdate, configManager.SaveServersCallCount);
        ServerProfileDto storedServer = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("gw-original", storedServer.SshGatewayId);
        Assert.Equal(
            "Skipped 1 item(s): SSH gateway \"gw-removed\" no longer exists.",
            fixture.LastStatusMessage);
    }

    [Fact]
    public async Task UpdateGatewayReferences_CaseInsensitiveId_UsesCanonicalStoredId()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("GW-Target", "Bastion"));
        await fixture.LoadServersAsync(settings, CreateServer("alpha", "Alpha", "ops"));

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(
            ["alpha"],
            "gw-target");

        Assert.Equal(1, updatedCount);
        ServerProfileDto storedServer = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("GW-Target", storedServer.SshGatewayId);
        Assert.Equal("via Bastion", fixture.ServerById("alpha").GatewayBadgeText);
    }

    [Fact]
    public async Task BulkMutation_PreservesVmIdentity_ViaUpdateFromDto()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        ServerItemViewModel alphaVm = fixture.ServerById("alpha");
        ServerItemViewModel betaVm = fixture.ServerById("beta");
        fixture.ViewModel.SelectSingle(alphaVm);
        fixture.ViewModel.ToggleSelection(betaVm);

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(
            ["alpha", "beta"],
            "gw-target");

        Assert.Equal(2, updatedCount);
        Assert.Same(alphaVm, fixture.ServerById("alpha"));
        Assert.Same(betaVm, fixture.ServerById("beta"));
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Same(betaVm, fixture.ViewModel.SelectedServer);
    }

    [Fact]
    public async Task UpdateGatewayReferencesAsync_ClearSetsDirectStateAndRefreshesBadges()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshGatewayId = "gw-missing";
        alpha.UseDirectConnection = false;
        await fixture.LoadServersAsync(settings, alpha);

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(["alpha"], targetGatewayId: null);

        Assert.Equal(1, updatedCount);
        ServerProfileDto storedServer = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Null(storedServer.SshGatewayId);
        Assert.True(storedServer.UseDirectConnection);
        ServerItemViewModel alphaVm = fixture.ServerById("alpha");
        Assert.False(alphaVm.IsGatewayBadgeVisible);
        Assert.False(alphaVm.IsGatewayMissing);
    }

    [Fact]
    public async Task UpdateGatewayReferencesAsync_WhenSaveFails_DoesNotMutateViewModelOrPersistedState()
    {
        FailingSaveConfigManager configManager = new();
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager: configManager);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshGatewayId = "gw-missing";
        await fixture.LoadServersAsync(settings, alpha);
        configManager.FailOnSaveServers = true;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.ViewModel.UpdateGatewayReferencesAsync(["alpha"], "gw-target"));

        ServerProfileDto storedServer = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("gw-missing", storedServer.SshGatewayId);
        Assert.False(storedServer.UseDirectConnection);
        ServerItemViewModel alphaVm = fixture.ServerById("alpha");
        Assert.True(alphaVm.IsGatewayMissing);
        Assert.Equal("gateway missing", alphaVm.GatewayBadgeText);
    }

    [Fact]
    public async Task BulkEditGateway_AppliesToEligibleSelectedServers_Only()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("GW-Target", "Bastion"));
        ServerProfileDto ssh = CreateServer("ssh", "SSH", "ops");
        ServerProfileDto rdp = CreateServer("rdp", "RDP", "ops");
        rdp.ConnectionType = "RDP";
        ServerProfileDto unselected = CreateServer("unselected", "Unselected", "ops");
        await fixture.LoadServersAsync(settings, ssh, rdp, unselected);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.UseGateway,
            "gw-target");
        fixture.ViewModel.SelectSingle(fixture.ServerById("ssh"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("rdp"));

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        Assert.All(
            storedServers.Where(server => server.Id is "ssh" or "rdp"),
            server =>
            {
                Assert.Equal("GW-Target", server.SshGatewayId);
                Assert.False(server.UseDirectConnection);
            });
        Assert.Null(Assert.Single(storedServers, server => server.Id == "unselected").SshGatewayId);
    }

    [Fact]
    public async Task UpdateGatewayReferences_WinRmHttps_IsSkippedWhileEligiblePeerUpdates()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        ServerProfileDto winRm = CreateServer("winrm", "WinRM HTTPS", "ops");
        winRm.ConnectionType = "WINRM";
        winRm.WinRmUseSsl = true;
        ServerProfileDto ssh = CreateServer("ssh", "SSH", "ops");
        await fixture.LoadServersAsync(settings, winRm, ssh);

        int updatedCount = await fixture.ViewModel.UpdateGatewayReferencesAsync(
            ["winrm", "ssh"],
            "gw-target");

        Assert.Equal(1, updatedCount);
        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        ServerProfileDto storedWinRm = Assert.Single(storedServers, server => server.Id == "winrm");
        ServerProfileDto storedSsh = Assert.Single(storedServers, server => server.Id == "ssh");
        Assert.Null(storedWinRm.SshGatewayId);
        Assert.False(storedWinRm.UseDirectConnection);
        Assert.Equal("gw-target", storedSsh.SshGatewayId);
        Assert.Equal(
            "Skipped 1 WinRM HTTPS profile(s): SSH gateways support WinRM over HTTP only.",
            fixture.LastStatusMessage);
    }

    [Fact]
    public async Task BulkEditGateway_InheritChoice_WinRmHttpsWithFolderGateway_RemainsDirect()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-default", "Default"));
        settings.GroupDefaults["ops"] = new GroupDefaultsDto { SshGatewayId = "gw-default" };
        ServerProfileDto winRm = CreateServer("winrm", "WinRM HTTPS", "ops");
        winRm.ConnectionType = "WINRM";
        winRm.WinRmUseSsl = true;
        winRm.UseDirectConnection = true;
        ServerProfileDto ssh = CreateServer("ssh", "SSH", "ops");
        ssh.UseDirectConnection = true;
        await fixture.LoadServersAsync(settings, winRm, ssh);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.InheritFolderDefault,
            GatewayId: null);
        fixture.ViewModel.SelectSingle(fixture.ServerById("winrm"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("ssh"));

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        ServerProfileDto storedWinRm = Assert.Single(storedServers, server => server.Id == "winrm");
        ServerProfileDto storedSsh = Assert.Single(storedServers, server => server.Id == "ssh");
        Assert.Null(storedWinRm.SshGatewayId);
        Assert.True(storedWinRm.UseDirectConnection);
        Assert.Null(storedSsh.SshGatewayId);
        Assert.False(storedSsh.UseDirectConnection);
        Assert.Equal(
            "Skipped 1 WinRM HTTPS profile(s): SSH gateways support WinRM over HTTP only.",
            fixture.LastStatusMessage);
    }

    [Fact]
    public async Task BulkEditGateway_IneligibleProtocols_SkippedAndReported_NotBadged()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        ServerProfileDto telnet = CreateServer("telnet", "Telnet", "ops");
        telnet.ConnectionType = "TELNET";
        ServerProfileDto ssh = CreateServer("ssh", "SSH", "ops");
        await fixture.LoadServersAsync(settings, telnet, ssh);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.UseGateway,
            "gw-target");
        fixture.ViewModel.SelectSingle(fixture.ServerById("telnet"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("ssh"));

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        ServerProfileDto storedTelnet = Assert.Single(storedServers, server => server.Id == "telnet");
        Assert.Null(storedTelnet.SshGatewayId);
        Assert.False(storedTelnet.UseDirectConnection);
        ServerItemViewModel telnetVm = fixture.ServerById("telnet");
        Assert.False(telnetVm.IsGatewayBadgeVisible);
        Assert.False(telnetVm.IsGatewayMissing);
        Assert.Equal(
            "Skipped 1 item(s): their protocols do not support SSH gateways.",
            fixture.LastStatusMessage);
    }

    [Fact]
    public async Task BulkEditGateway_DirectChoice_ClearsGatewayAndSetsDirect_AllSelected()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        ServerProfileDto ssh = CreateServer("ssh", "SSH", "ops");
        ssh.SshGatewayId = "gw-old";
        ServerProfileDto telnet = CreateServer("telnet", "Telnet", "ops");
        telnet.ConnectionType = "TELNET";
        telnet.SshGatewayId = "gw-residual";
        ServerProfileDto winRm = CreateServer("winrm", "WinRM HTTPS", "ops");
        winRm.ConnectionType = "WINRM";
        winRm.WinRmUseSsl = true;
        winRm.SshGatewayId = "gw-invalid";
        await fixture.LoadServersAsync(settings, ssh, telnet, winRm);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.DirectConnection,
            GatewayId: null);
        fixture.ViewModel.SelectSingle(fixture.ServerById("ssh"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("telnet"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("winrm"));

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        Assert.All(
            storedServers,
            server =>
            {
                Assert.Null(server.SshGatewayId);
                Assert.True(server.UseDirectConnection);
            });
        Assert.Null(fixture.LastStatusMessage);
    }

    [Fact]
    public async Task BulkEditGateway_InheritChoice_ClearsExplicitGateway_ResolvesGroupDefault()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-default", "Default"));
        settings.SshGateways.Add(CreateGateway("gw-explicit", "Explicit"));
        settings.GroupDefaults["ops"] = new GroupDefaultsDto { SshGatewayId = "gw-default" };
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshGatewayId = "gw-explicit";
        ServerProfileDto beta = CreateServer("beta", "Beta", "ops");
        beta.ConnectionType = "TELNET";
        beta.UseDirectConnection = true;
        await fixture.LoadServersAsync(settings, alpha, beta);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.InheritFolderDefault,
            GatewayId: null);
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        AppSettings storedSettings = await fixture.ConfigManager.LoadSettingsAsync();
        ServerProfileDto[] storedServers = (await fixture.ConfigManager.LoadServersAsync()).ToArray();
        Assert.All(
            storedServers,
            server =>
            {
                Assert.Null(server.SshGatewayId);
                Assert.False(server.UseDirectConnection);
            });
        ServerProfileDto storedAlpha = Assert.Single(storedServers, server => server.Id == "alpha");
        GroupDefaultsDto defaults = GroupDefaultsDto.Resolve(
            storedAlpha.Group,
            storedSettings.GroupDefaults);
        defaults.ApplyTo(storedAlpha);
        Assert.Equal("gw-default", storedAlpha.SshGatewayId);
    }

    [Fact]
    public async Task BulkEditGateway_DeletedGatewayAfterDialogOpen_Aborts()
    {
        FailingSaveConfigManager configManager = new();
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(
            confirmResult: true,
            configManager: configManager);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.SshGatewayId = "gw-original";
        ServerProfileDto beta = CreateServer("beta", "Beta", "ops");
        beta.SshGatewayId = "gw-original";
        await fixture.LoadServersAsync(settings, alpha, beta);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.UseGateway,
            "gw-target");
        fixture.DialogService.OnBulkEditGatewayShown = async cancellationToken =>
        {
            AppSettings currentSettings = await configManager.LoadSettingsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            currentSettings.SshGateways.RemoveAll(gateway =>
                string.Equals(gateway.Id, "gw-target", StringComparison.OrdinalIgnoreCase));
            await configManager.SaveSettingsAsync(currentSettings);
        };
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        int saveCountBeforeCommand = configManager.SaveServersCallCount;

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        Assert.Equal(saveCountBeforeCommand, configManager.SaveServersCallCount);
        Assert.All(
            await fixture.ConfigManager.LoadServersAsync(),
            server => Assert.Equal("gw-original", server.SshGatewayId));
        Assert.Equal(
            "Skipped 2 item(s): SSH gateway \"gw-target\" no longer exists.",
            fixture.LastStatusMessage);
    }

    [Fact]
    public async Task BulkEditGateway_PreservesVmIdentityAndSelection()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        settings.SshGateways.Add(CreateGateway("gw-target", "Bastion"));
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        ServerItemViewModel alphaVm = fixture.ServerById("alpha");
        ServerItemViewModel betaVm = fixture.ServerById("beta");
        fixture.ViewModel.SelectSingle(alphaVm);
        fixture.ViewModel.ToggleSelection(betaVm);
        fixture.DialogService.NextBulkEditGatewayResult = new ServerBulkEditGatewayResult(
            ServerBulkEditGatewayChoice.UseGateway,
            "gw-target");

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        Assert.Same(alphaVm, fixture.ServerById("alpha"));
        Assert.Same(betaVm, fixture.ServerById("beta"));
        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Same(betaVm, fixture.ViewModel.SelectedServer);
    }

    [Fact]
    public async Task BulkEditGateway_DialogBindsCredentialFreeProjection_NotSshGatewayDto()
    {
        await using ServerListBulkFixture fixture = await ServerListBulkFixture.CreateAsync(confirmResult: true);
        AppSettings settings = fixture.ExpandGroups("ops");
        SshGatewayDto gateway = CreateGateway("gw-target", "Bastion");
        gateway.SshPasswordEncrypted = "encrypted-password";
        gateway.SshKeyPassphraseEncrypted = "encrypted-passphrase";
        settings.SshGateways.Add(gateway);
        await fixture.LoadServersAsync(
            settings,
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.DialogService.NextBulkEditGatewayResult = null;

        await fixture.ViewModel.BulkEditGatewayCommand.ExecuteAsync(
            fixture.ViewModel.SelectedItems.ToList());

        GatewayOption option = Assert.Single(fixture.DialogService.LastBulkEditGatewayOptions);
        Assert.Equal("gw-target", option.Id);
        Assert.Equal("Bastion", option.Name);
        Assert.DoesNotContain(
            typeof(GatewayOption).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            typeof(GatewayOption),
            typeof(ServerBulkEditGatewayViewModel)
                .GetProperty(nameof(ServerBulkEditGatewayViewModel.AvailableGateways))!
                .PropertyType
                .GetGenericArguments()
                .Single());
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static ServerProfileDto CreateServer(
        string id,
        string displayName,
        string group,
        string? projectId = null) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = $"{id}.example.com",
            ConnectionType = "SSH",
            Group = group,
            ProjectId = projectId,
            Origin = ProfileOrigin.Manual
        };

    private static SshGatewayDto CreateGateway(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Host = $"{id}.example.com",
            Port = 22,
            User = "ops"
        };

    private static int GetStoredEditablePort(ServerProfileDto server) =>
        server.ConnectionType?.ToUpperInvariant() switch
        {
            "SSH" or "SFTP" => server.SshPort,
            "FTP" => server.FtpPort,
            "VNC" => server.VncPort,
            "TELNET" => server.TelnetPort,
            _ => server.RemotePort
        };

    private static string GetStoredEditableUsername(ServerProfileDto server)
    {
        if (!string.IsNullOrWhiteSpace(server.SshUsername)) return server.SshUsername;
        if (!string.IsNullOrWhiteSpace(server.RdpUsername)) return server.RdpUsername;
        if (!string.IsNullOrWhiteSpace(server.FtpUsername)) return server.FtpUsername;
        if (!string.IsNullOrWhiteSpace(server.TelnetUsername)) return server.TelnetUsername;
        return string.Empty;
    }

    private static ServerProfileDto CreatePasswordServer(
        string id,
        string displayName,
        string group,
        string? connectionType)
    {
        ServerProfileDto server = CreateServer(id, displayName, group);
        server.ConnectionType = connectionType is null ? null! : connectionType;
        server.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;
        return server;
    }

    private static void AssertOnlyPasswordFieldSet(
        ServerProfileDto server,
        string expectedPasswordField,
        string expectedPlaintextPassword)
    {
        string? encryptedPassword = GetStoredPasswordField(server, expectedPasswordField);
        Assert.NotNull(encryptedPassword);
        Assert.Equal(expectedPlaintextPassword, CredentialProtector.Unprotect(encryptedPassword));
        Assert.Equal(expectedPasswordField == nameof(ServerProfileDto.RdpPasswordEncrypted), server.RdpPasswordEncrypted is not null);
        Assert.Equal(expectedPasswordField == nameof(ServerProfileDto.SshPasswordEncrypted), server.SshPasswordEncrypted is not null);
        Assert.Equal(expectedPasswordField == nameof(ServerProfileDto.FtpPasswordEncrypted), server.FtpPasswordEncrypted is not null);
        Assert.Equal(expectedPasswordField == nameof(ServerProfileDto.WinRmPasswordEncrypted), server.WinRmPasswordEncrypted is not null);
        Assert.Equal(expectedPasswordField == nameof(ServerProfileDto.TelnetPasswordEncrypted), server.TelnetPasswordEncrypted is not null);
        Assert.Equal(expectedPasswordField == nameof(ServerProfileDto.VncPassword), server.VncPassword is not null);
    }

    private static void AssertNoStoredPasswords(ServerProfileDto server)
    {
        Assert.Null(server.RdpPasswordEncrypted);
        Assert.Null(server.SshPasswordEncrypted);
        Assert.Null(server.FtpPasswordEncrypted);
        Assert.Null(server.WinRmPasswordEncrypted);
        Assert.Null(server.TelnetPasswordEncrypted);
        Assert.Null(server.VncPassword);
    }

    private static string? GetStoredPasswordField(ServerProfileDto server, string passwordField)
    {
        return passwordField switch
        {
            nameof(ServerProfileDto.RdpPasswordEncrypted) => server.RdpPasswordEncrypted,
            nameof(ServerProfileDto.SshPasswordEncrypted) => server.SshPasswordEncrypted,
            nameof(ServerProfileDto.FtpPasswordEncrypted) => server.FtpPasswordEncrypted,
            nameof(ServerProfileDto.WinRmPasswordEncrypted) => server.WinRmPasswordEncrypted,
            nameof(ServerProfileDto.TelnetPasswordEncrypted) => server.TelnetPasswordEncrypted,
            nameof(ServerProfileDto.VncPassword) => server.VncPassword,
            _ => throw new ArgumentOutOfRangeException(nameof(passwordField), passwordField, "Unsupported password field.")
        };
    }

    private static ServerProfileDto CreateTool(
        string id,
        string displayName,
        string group,
        string? projectId = null) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = id,
            ConnectionType = "TOOL:CHMOD",
            Group = group,
            ProjectId = projectId,
            Origin = ProfileOrigin.Manual
        };

    private static void AddProjects(AppSettings settings, params (string Id, string Name)[] projects)
    {
        foreach (var (id, name) in projects)
        {
            settings.Projects.Add(new ProjectDto
            {
                Id = id,
                Name = name,
                Color = string.Empty
            });
        }
    }

    private static void AssertSelection(ServerListViewModel viewModel, params string[] expectedIds)
    {
        var actualIds = viewModel.SelectedItems
            .Select(item => item.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var sortedExpected = expectedIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(sortedExpected, actualIds);
        Assert.All(viewModel.SelectedItems, item => Assert.True(item.IsSelected));
    }

    private sealed class ServerListBulkFixture : IAsyncDisposable
    {
        private readonly string? _rootPath;

        private ServerListBulkFixture(
            string? rootPath,
            IConfigManager configManager,
            ServerListViewModel viewModel,
            TrackingDialogService dialogService)
        {
            _rootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            DialogService = dialogService;
        }

        public IConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public TrackingDialogService DialogService { get; }

        public string? LastStatusMessage { get; private set; }

        public List<string> StatusMessages { get; } = [];

        public static async Task<ServerListBulkFixture> CreateAsync(
            bool confirmResult,
            IConfigManager? configManager = null,
            IEnumerable<IProtocolHandler>? protocolHandlers = null)
        {
            var rootPath = configManager is null
                ? Path.Combine(Path.GetTempPath(), "heimdall-b66-bulk", Guid.NewGuid().ToString("N"))
                : null;
            configManager ??= new ConfigManager(rootPath!);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                configManager,
                localizer,
                new NullTunnelService(),
                protocolHandlers ?? Array.Empty<IProtocolHandler>());
            var dialogService = new TrackingDialogService(confirmResult);
            var puttyImporter = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager);
            var knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            var uiDispatcher = new FakeUiDispatcher();

            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialogService,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter);
            var fixture = new ServerListBulkFixture(rootPath, configManager, viewModel, dialogService);
            viewModel.StatusMessageRequested += fixture.HandleStatusMessage;

            return fixture;
        }

        public AppSettings ExpandGroups(params string[] groups)
        {
            var settings = new AppSettings();
            foreach (var group in groups)
            {
                settings.TreeExpandedNodes.Add(group);
            }

            return settings;
        }

        public async Task LoadServersAsync(AppSettings settings, params ServerProfileDto[] servers)
        {
            await ConfigManager.SaveSettingsAsync(settings);
            await ConfigManager.SaveServersAsync(servers.ToList());
            ViewModel.LoadServers(servers.ToList(), settings);
        }

        public ServerItemViewModel ServerById(string id) =>
            Assert.Single(ViewModel.Servers, server => string.Equals(server.Id, id, StringComparison.Ordinal));

        public string[] VisibleIds() =>
            ViewModel.Servers
                .Select(server => server.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();

            try
            {
                if (_rootPath is not null && Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }

        private void HandleStatusMessage(string message)
        {
            LastStatusMessage = message;
            StatusMessages.Add(message);
        }
    }

    private sealed class TrackingDialogService(bool confirmResult) : IDialogService
    {
        public string LastConfirmTitle { get; private set; } = string.Empty;

        public string LastConfirmMessage { get; private set; } = string.Empty;

        public int ConfirmCallCount { get; private set; }

        public int ErrorCallCount { get; private set; }

        public int WarningCallCount { get; private set; }

        public int InfoCallCount { get; private set; }

        public int BulkEditPortCallCount { get; private set; }

        public int LastBulkEditPortCount { get; private set; }

        public int? LastBulkEditPortInitialPort { get; private set; }

        public int? NextBulkEditPortResult { get; set; }

        public int BulkEditUsernameCallCount { get; private set; }

        public int LastBulkEditUsernameCount { get; private set; }

        public string? LastBulkEditUsernameInitialUsername { get; private set; }

        public string? NextBulkEditUsernameResult { get; set; }

        public int BulkEditPasswordCallCount { get; private set; }

        public int LastBulkEditPasswordCount { get; private set; }

        public string? NextBulkEditPasswordResult { get; set; }

        public int BulkEditGatewayCallCount { get; private set; }

        public int LastBulkEditGatewayCount { get; private set; }

        public IReadOnlyList<GatewayOption> LastBulkEditGatewayOptions { get; private set; } = [];

        public ServerBulkEditGatewayResult? NextBulkEditGatewayResult { get; set; }

        public Func<CancellationToken, Task>? OnBulkEditGatewayShown { get; set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            ConfirmCallCount++;
            return Task.FromResult(confirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
        {
            LastBulkEditPortCount = count;
            LastBulkEditPortInitialPort = initialPort;
            BulkEditPortCallCount++;
            return Task.FromResult(NextBulkEditPortResult);
        }

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
        {
            LastBulkEditUsernameCount = count;
            LastBulkEditUsernameInitialUsername = initialUsername;
            BulkEditUsernameCallCount++;
            return Task.FromResult(NextBulkEditUsernameResult);
        }

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
        {
            LastBulkEditPasswordCount = count;
            BulkEditPasswordCallCount++;
            return Task.FromResult(NextBulkEditPasswordResult);
        }

        public async Task<ServerBulkEditGatewayResult?> ShowBulkEditGatewayAsync(
            int count,
            IReadOnlyList<GatewayOption> availableGateways,
            CancellationToken cancellationToken)
        {
            LastBulkEditGatewayCount = count;
            LastBulkEditGatewayOptions = availableGateways;
            BulkEditGatewayCallCount++;
            if (OnBulkEditGatewayShown is not null)
            {
                await OnBulkEditGatewayShown(cancellationToken);
            }

            return NextBulkEditGatewayResult;
        }

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null) => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public void ShowError(string title, string message)
        {
            ErrorCallCount++;
        }

        public void ShowInfo(string title, string message)
        {
            InfoCallCount++;
        }

        public void ShowWarning(string title, string message)
        {
            WarningCallCount++;
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NullRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            Task.FromResult(new RdpImportPreview
            {
                Entries = [],
                FilesNotFound = [],
                FilesUnreadable = []
            });

        public Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct) =>
            Task.FromResult(new RdpImportResult());
    }

    private sealed class ScriptedProtocolHandler(
        string protocol,
        params Func<ServerProfileDto, CancellationToken, Task<ConnectionResult>>[] behaviors) : IProtocolHandler
    {
        private readonly Queue<Func<ServerProfileDto, CancellationToken, Task<ConnectionResult>>> _behaviors =
            new(behaviors);

        public string Protocol { get; } = protocol;

        public List<string> ConnectedServerIds { get; } = [];

        public async Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            ConnectedServerIds.Add(server.Id);
            var behavior = _behaviors.Count > 0
                ? _behaviors.Dequeue()
                : Success();
            return await behavior(server, ct);
        }
    }

    private sealed class FailingSaveConfigManager : IConfigManager
    {
        private AppSettings _settings = new();
        private List<ServerProfileDto> _servers = [];

        public bool FailOnSaveServers { get; set; }

        /// <summary>
        /// Makes the settings load fail. Because the bulk mutation loads settings before it persists
        /// anything, this must abort the whole operation without reaching the servers file.
        /// </summary>
        public bool FailOnLoadSettings { get; set; }

        public int SaveServersCallCount { get; private set; }

        /// <summary>
        /// The exact list instance the last successful persistence was handed, kept by reference.
        /// </summary>
        /// <remarks>
        /// The presence flags do not survive serialization, so a test that reloads the profile
        /// cannot observe them: reading them back requires the instance production code built.
        /// </remarks>
        public List<ServerProfileDto>? LastSavedServers { get; private set; }

        public Action? BeforeSaveServers { get; set; }

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync()
        {
            if (FailOnLoadSettings)
            {
                throw new IOException("Simulated LoadSettingsAsync failure");
            }

            return Task.FromResult(CloneSettings(_settings));
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _settings = CloneSettings(settings);
            SettingsChanged?.Invoke(CloneSettings(_settings));
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) => Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            mutate(_settings);
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(_servers.Select(CloneServer).ToList());

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
        {
            List<ServerProfileDto> servers = _servers.Select(CloneServer).ToList();
            string originalJson = System.Text.Json.JsonSerializer.Serialize(servers);
            TResult result = mutate(servers);
            string mutatedJson = System.Text.Json.JsonSerializer.Serialize(servers);
            if (string.Equals(originalJson, mutatedJson, StringComparison.Ordinal))
            {
                return Task.FromResult(result);
            }

            BeforeSaveServers?.Invoke();
            if (FailOnSaveServers)
            {
                throw new IOException("Simulated SaveServersAsync failure");
            }

            SaveServersCallCount++;
            LastSavedServers = servers;
            _servers = servers.Select(CloneServer).ToList();
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers)
        {
            if (FailOnSaveServers)
            {
                throw new IOException("Simulated SaveServersAsync failure");
            }

            SaveServersCallCount++;
            LastSavedServers = servers;
            _servers = servers.Select(CloneServer).ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class UsernameAwareConfigManager : IConfigManager
    {
        private AppSettings _settings = new();
        private List<ServerProfileDto> _servers = [];

        public bool FailOnSaveServers { get; set; }

        public int SaveServersCallCount { get; set; }

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(CloneSettings(_settings));

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _settings = CloneSettings(settings);
            SettingsChanged?.Invoke(CloneSettings(_settings));
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) => Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            mutate(_settings);
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            Task.FromResult(_servers.Select(CloneUsernameServer).ToList());

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
        {
            List<ServerProfileDto> servers = _servers.Select(CloneUsernameServer).ToList();
            string originalJson = System.Text.Json.JsonSerializer.Serialize(servers);
            TResult result = mutate(servers);
            string mutatedJson = System.Text.Json.JsonSerializer.Serialize(servers);
            if (string.Equals(originalJson, mutatedJson, StringComparison.Ordinal))
            {
                return Task.FromResult(result);
            }

            if (FailOnSaveServers)
            {
                throw new IOException("Simulated SaveServersAsync failure");
            }

            SaveServersCallCount++;
            _servers = servers.Select(CloneUsernameServer).ToList();
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers)
        {
            if (FailOnSaveServers)
            {
                throw new IOException("Simulated SaveServersAsync failure");
            }

            SaveServersCallCount++;
            _servers = servers.Select(CloneUsernameServer).ToList();
            return Task.CompletedTask;
        }
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return new AppSettings
        {
            TreeExpandedNodes = [.. settings.TreeExpandedNodes],
            TrustedHostKeys = new Dictionary<string, string>(settings.TrustedHostKeys, StringComparer.Ordinal),
            SshGateways = settings.SshGateways
                .Select(gateway => new SshGatewayDto
                {
                    Id = gateway.Id,
                    Name = gateway.Name,
                    Host = gateway.Host,
                    Port = gateway.Port,
                    User = gateway.User,
                    KeyPath = gateway.KeyPath,
                    SshPasswordEncrypted = gateway.SshPasswordEncrypted,
                    SshKeyPassphraseEncrypted = gateway.SshKeyPassphraseEncrypted,
                    IsDefault = gateway.IsDefault,
                    ParentGatewayId = gateway.ParentGatewayId,
                    HostKeyFingerprint = gateway.HostKeyFingerprint
                })
                .ToList()
        };
    }

    /// <summary>
    /// Copy used by the in-memory config manager doubles to emulate the isolation a real save and
    /// reload gives.
    /// </summary>
    /// <remarks>
    /// This was a hand-written assignment list that dropped most of the profile, including
    /// <see cref="ServerProfileDto.SshKeyPath"/>. A double that loses fields cannot be used to
    /// observe whether production code preserved them, so it goes through the same fidelity
    /// primitive as production.
    /// </remarks>
    private static ServerProfileDto CloneServer(ServerProfileDto server) => server.CloneFaithfully();

    private static Func<ServerProfileDto, CancellationToken, Task<ConnectionResult>> Success(
        Action<ServerProfileDto>? afterConnect = null)
    {
        return (server, _) =>
        {
            afterConnect?.Invoke(server);
            return Task.FromResult(new ConnectionResult(true, null, null));
        };
    }

    private static Func<ServerProfileDto, CancellationToken, Task<ConnectionResult>> Fail(string message)
    {
        return (_, _) => Task.FromResult(new ConnectionResult(false, message, null));
    }

    private static ServerProfileDto CloneUsernameServer(ServerProfileDto server)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(server);
        return System.Text.Json.JsonSerializer.Deserialize<ServerProfileDto>(json)
            ?? throw new InvalidOperationException("Server clone failed.");
    }
}
