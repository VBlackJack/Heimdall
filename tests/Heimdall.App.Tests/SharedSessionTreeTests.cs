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
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The shared sources in the session tree: one root per source, keyed apart from the local
/// folders, and every write refused aloud on a shared row.
/// </summary>
public sealed partial class SharedSessionConnectTests
{
    /// <summary>The source's name, spelled like a local folder on purpose.</summary>
    private const string TeamSourceName = "Team";

    private const string LocalInSharedFolderId = "local-prod-1";
    private const string LocalInTeamFolderId = "local-team-1";

    // --- The projection ---

    [Fact]
    public async Task SharedRoot_StandsApartFromLocalFoldersOfTheSameName()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder), LocalProfile(LocalInTeamFolderId, TeamSourceName));

        IReadOnlyList<FolderViewModel> roots = fixture.ViewModel.GroupedServers;
        FolderViewModel sharedRoot = Assert.Single(roots, folder => folder.IsSharedRoot);
        FolderViewModel localTeam = Assert.Single(roots, folder => folder.Name == TeamSourceName && !folder.IsShared);
        FolderViewModel localProd = Assert.Single(roots, folder => folder.Name == SharedSessionTestData.SharedFolder);

        // Two roots named "Team", one local and one shared; the shared one after the local ones.
        Assert.Equal(TeamSourceName, sharedRoot.Name);
        Assert.Same(roots[^1], sharedRoot);
        Assert.NotEqual(localTeam.ExpansionKey, sharedRoot.ExpansionKey);
        Assert.Equal(LocalInTeamFolderId, Assert.Single(localTeam.Servers).Id);

        // The shared "PROD" folder sits under its source and holds the shared rows only; the local
        // "PROD" folder holds the local row only.
        FolderViewModel sharedProd = Assert.Single(sharedRoot.SubFolders);
        Assert.True(sharedProd.IsShared);
        Assert.False(sharedProd.IsSharedRoot);
        Assert.Equal(SharedSessionTestData.SharedFolder, sharedProd.Name);
        Assert.Equal(
            ServerListViewModel.SharedFolderKey(SharedSessionTestData.SourceId, SharedSessionTestData.SharedFolder),
            sharedProd.FullPath);
        Assert.Equal(
            new[] { fixture.Shared.DirectProfile.Id, fixture.Shared.Profile.Id }.Order(),
            sharedProd.Servers.Select(server => server.Id).Order());
        Assert.False(localProd.IsShared);
        Assert.Equal(LocalInSharedFolderId, Assert.Single(localProd.Servers).Id);
    }

    [Fact]
    public async Task SourceInError_KeepsItsRootWithTheReasonAndNoChildren()
    {
        const string brokenId = "brokensrc";
        const string reason = "The file was not found.";
        await using Fixture fixture = await Fixture.CreateAsync(shared => new StubSharedSessionProvider(
            [shared.Profile, shared.DirectProfile],
            [shared.Gateway],
            [
                StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 2),
                StubSharedSessionProvider.Failed(brokenId, "Broken", reason),
            ]));
        await LoadTreeAsync(fixture);

        FolderViewModel broken = Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.SharedSourceId == brokenId);

        Assert.True(broken.IsSharedRoot);
        Assert.True(broken.HasError);
        Assert.Equal(reason, broken.ErrorMessage);
        Assert.Equal(reason, broken.TooltipText);
        Assert.Empty(broken.Children);
        Assert.Contains(reason, broken.AccessibleName, StringComparison.Ordinal);
        Assert.False(Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.Name == TeamSourceName).HasError);
    }

    [Fact]
    public async Task DisabledSource_HasNoRoot()
    {
        await using Fixture fixture = await Fixture.CreateAsync(shared => new StubSharedSessionProvider(
            [],
            [],
            [StubSharedSessionProvider.Disabled(SharedSessionTestData.SourceId, TeamSourceName)]));
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));

        Assert.DoesNotContain(fixture.ViewModel.GroupedServers, folder => folder.IsShared);
    }

    [Fact]
    public async Task SharedRow_IsReadOnlyAndNamesItsSource()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));

        ServerItemViewModel shared = Row(fixture, fixture.Shared.Profile.Id);
        ServerItemViewModel local = Row(fixture, LocalInSharedFolderId);
        string notice = fixture.Localizer.Format("SessionTreeSharedReadOnlyTooltip", TeamSourceName);

        Assert.True(shared.IsShared);
        Assert.Equal(TeamSourceName, shared.SharedSourceName);
        Assert.Equal(notice, shared.SharedReadOnlyText);
        Assert.Contains(notice, shared.RowTooltipText, StringComparison.Ordinal);
        Assert.Contains(TeamSourceName, shared.AccessibleName, StringComparison.Ordinal);
        Assert.False(local.IsShared);
        Assert.Empty(local.SharedReadOnlyText);
    }

    [Fact]
    public async Task Search_ReachesSharedRows()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));

        fixture.ViewModel.SearchText = fixture.Shared.Profile.DisplayName;
        fixture.ViewModel.ApplyPendingSearchNow();

        Assert.Equal(fixture.Shared.Profile.Id, Assert.Single(fixture.ViewModel.Servers).Id);
        FolderViewModel root = Assert.Single(fixture.ViewModel.GroupedServers);
        Assert.True(root.IsSharedRoot);
    }

    [Fact]
    public async Task LocalFolderOperations_NeverCountOrOfferSharedFolders()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));

        // The local "PROD" holds one session; the two shared ones under the same name are not its.
        Assert.Equal(1, fixture.ViewModel.GetCanonicalFolderEntryCount(SharedSessionTestData.SharedFolder));
        Assert.DoesNotContain(
            fixture.ViewModel.GetGroupTargets(includeNoGroup: false),
            target => ServerListViewModel.IsSharedFolderKey(target.GroupName));
        Assert.DoesNotContain(fixture.ViewModel.Groups, ServerListViewModel.IsSharedFolderKey);
    }

    [Fact]
    public async Task TabGroup_OfASharedRow_IsItsSourceFolder()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));

        SessionTabGroupInfo shared = fixture.ViewModel.ResolveTabGroup(fixture.Shared.Profile.Id)!;
        SessionTabGroupInfo local = fixture.ViewModel.ResolveTabGroup(LocalInSharedFolderId)!;

        Assert.Equal(
            ServerListViewModel.SharedFolderKey(SharedSessionTestData.SourceId, SharedSessionTestData.SharedFolder),
            shared.Key);
        Assert.Equal(SharedSessionTestData.SharedFolder, local.Key);
    }

    // --- Read-only ---

    [Theory]
    [InlineData("delete")]
    [InlineData("duplicate")]
    [InlineData("edit")]
    [InlineData("move-to-folder")]
    [InlineData("move-to-project")]
    [InlineData("favorite")]
    [InlineData("nudge")]
    [InlineData("reorder")]
    public async Task WriteOnASharedRow_IsRefusedAloud_BeforeAnyDialog_AndWritesNothing(string action)
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        string before = await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath);
        ServerItemViewModel shared = Row(fixture, fixture.Shared.Profile.Id);
        ServerItemViewModel otherShared = Row(fixture, fixture.Shared.DirectProfile.Id);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        // The dialog service of the fixture throws on any confirmation: reaching one fails here.
        switch (action)
        {
            case "delete": await fixture.ViewModel.DeleteServerCommand.ExecuteAsync(shared); break;
            case "duplicate": await fixture.ViewModel.DuplicateServerCommand.ExecuteAsync(shared); break;
            case "edit": await fixture.ViewModel.EditServerCommand.ExecuteAsync(shared); break;
            case "move-to-folder": Assert.False(await fixture.ViewModel.MoveServerToGroupAsync(shared, "Elsewhere")); break;
            case "move-to-project": Assert.False(await fixture.ViewModel.MoveServerToProjectAsync(shared, "project-1")); break;
            case "favorite": await fixture.ViewModel.SetFavoriteCommand.ExecuteAsync(new FavoriteChangeRequest([shared], true)); break;
            case "nudge": Assert.False(await fixture.ViewModel.NudgeServerAsync(shared, 1)); break;
            case "reorder": Assert.False(await fixture.ViewModel.ReorderServersAsync([shared], otherShared, placeAfter: true)); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        Assert.Equal(fixture.Localizer["StatusSharedSessionReadOnly"], Assert.Single(statuses));
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath));
        Assert.False(shared.IsFavorite);
        Assert.Contains(fixture.ViewModel.Servers, server => server.Id == shared.Id);
    }

    [Fact]
    public async Task MixedSelection_DisablesThePersistedBulkActions_AndKeepsConnect()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(
            fixture,
            LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder),
            LocalProfile(LocalInTeamFolderId, TeamSourceName));
        ServerItemViewModel local = Row(fixture, LocalInSharedFolderId);
        ServerItemViewModel otherLocal = Row(fixture, LocalInTeamFolderId);
        ServerItemViewModel shared = Row(fixture, fixture.Shared.Profile.Id);
        ServerListViewModel list = fixture.ViewModel;

        // The control: two local rows enable every bulk write.
        list.SelectSingle(local);
        list.ToggleSelection(otherLocal);
        Assert.False(list.SelectionHasShared);
        Assert.True(list.DeleteSelectedCommand.CanExecute(null));

        list.ToggleSelection(otherLocal);
        list.ToggleSelection(shared);

        Assert.True(list.SelectionHasShared);
        Assert.False(list.DeleteSelectedCommand.CanExecute(null));
        Assert.False(list.DuplicateSelectedCommand.CanExecute(null));
        Assert.False(list.MoveSelectedToGroupCommand.CanExecute(new BulkMoveToGroupRequest("Elsewhere")));
        Assert.False(list.MoveSelectedToProjectCommand.CanExecute(new BulkMoveToProjectRequest("project-1")));
        Assert.False(list.BulkEditPortCommand.CanExecute(null));
        Assert.False(list.BulkEditUsernameCommand.CanExecute(null));
        Assert.False(list.BulkEditPasswordCommand.CanExecute(null));
        Assert.False(list.BulkEditGatewayCommand.CanExecute(null));
        Assert.Equal(2, list.GetBulkConnectTargetCount(list.SelectedItems.ToList()));
    }

    [Fact]
    public async Task MixedDragOrBulkWrite_IsRefusedWithTheCount_AndWritesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        string before = await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath);
        ServerItemViewModel local = Row(fixture, LocalInSharedFolderId);
        ServerItemViewModel shared = Row(fixture, fixture.Shared.Profile.Id);
        ServerItemViewModel otherShared = Row(fixture, fixture.Shared.DirectProfile.Id);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        Assert.Equal(0, await fixture.ViewModel.MoveServersToGroupAsync([local, shared, otherShared], "Elsewhere"));
        await fixture.ViewModel.SetFavoriteCommand.ExecuteAsync(new FavoriteChangeRequest([local, shared], true));

        string expectedTwo = fixture.Localizer.FormatCount(2, "StatusSharedSelectionReadOnlyOne", "StatusSharedSelectionReadOnly", 2);
        string expectedOne = fixture.Localizer.FormatCount(1, "StatusSharedSelectionReadOnlyOne", "StatusSharedSelectionReadOnly", 1);
        Assert.Equal([expectedTwo, expectedOne], statuses);
        Assert.Contains("2", expectedTwo, StringComparison.Ordinal);
        Assert.Contains("1", expectedOne, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath));
        Assert.False(local.IsFavorite);
    }

    [Fact]
    public async Task DropOnASharedFolder_IsRefusedAloud()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        string before = await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath);
        ServerItemViewModel local = Row(fixture, LocalInSharedFolderId);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        int moved = await fixture.ViewModel.MoveServersToGroupAsync(
            [local],
            ServerListViewModel.SharedFolderKey(SharedSessionTestData.SourceId, SharedSessionTestData.SharedFolder));

        Assert.Equal(0, moved);
        Assert.Equal(fixture.Localizer["StatusSharedFolderReadOnly"], Assert.Single(statuses));
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath));
    }

    [Fact]
    public async Task TreeGestures_RefuseSharedRowsAndFolders()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(
            fixture,
            LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder),
            LocalProfile(LocalInTeamFolderId, SharedSessionTestData.SharedFolder));
        ServerItemViewModel local = Row(fixture, LocalInSharedFolderId);
        ServerItemViewModel otherLocal = Row(fixture, LocalInTeamFolderId);
        ServerItemViewModel shared = Row(fixture, fixture.Shared.Profile.Id);
        ServerItemViewModel otherShared = Row(fixture, fixture.Shared.DirectProfile.Id);
        FolderViewModel sharedRoot = Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.IsSharedRoot);
        FolderViewModel sharedFolder = Assert.Single(sharedRoot.SubFolders);
        FolderViewModel localFolder = Assert.Single(fixture.ViewModel.GroupedServers, folder => !folder.IsShared);

        // Drag start.
        Assert.True(TreeInteractionState.CanStartDrag(local, [local]));
        Assert.False(TreeInteractionState.CanStartDrag(shared, [shared]));
        Assert.False(TreeInteractionState.CanStartDrag(local, [local, shared]));
        Assert.True(TreeInteractionState.CanStartDrag(localFolder, null));
        Assert.False(TreeInteractionState.CanStartDrag(sharedFolder, null));
        Assert.False(TreeInteractionState.CanStartDrag(sharedRoot, null));

        // Drop on a folder, and placement among rows.
        Assert.True(TreeInteractionState.TryResolveGroupDropTarget(localFolder, acceptsRootTarget: false, out _));
        Assert.False(TreeInteractionState.TryResolveGroupDropTarget(sharedFolder, acceptsRootTarget: true, out string? target));
        Assert.Null(target);
        Assert.True(TreeInteractionState.CanReorderOnto([local], otherLocal));
        Assert.False(TreeInteractionState.CanReorderOnto([local], shared));
        Assert.False(TreeInteractionState.CanReorderOnto([shared], local));
        Assert.False(TreeInteractionState.CanReorderOnto([shared], otherShared));

        // Inline rename.
        Assert.False(SessionTreeInlineRename.TryBeginEdit(shared));
        Assert.False(shared.IsEditing);
        Assert.False(SessionTreeInlineRename.TryBeginEdit(sharedFolder));
        Assert.False(sharedFolder.IsEditing);
        Assert.True(SessionTreeInlineRename.TryBeginEdit(local));
        Assert.True(local.IsEditing);
    }

    [Fact]
    public async Task FolderColour_OnASharedFolder_IsRefusedAndWritesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        string key = ServerListViewModel.SharedFolderKey(SharedSessionTestData.SourceId, SharedSessionTestData.SharedFolder);

        await fixture.ViewModel.SetFolderColorAsync(key, "#FF0000");

        Assert.Equal(fixture.Localizer["StatusSharedFolderReadOnly"], Assert.Single(statuses));
        Assert.False((await fixture.ConfigManager.LoadSettingsAsync()).GroupDefaults.ContainsKey(key));
    }

    [Fact]
    public async Task SchedulerRules_ReachSharedRows_ByIdentifierAndByName()
    {
        await using Fixture fixture = await Fixture.CreateAsync(LoadedTeamProvider);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));

        ServerItemViewModel? byId = Heimdall.App.ViewModels.Scheduled.ScheduledTaskServerResolver.Resolve(
            fixture.Shared.Profile.Id,
            "renamed since",
            fixture.ViewModel.AllServers,
            server => server.Id,
            server => server.DisplayName,
            out _);
        ServerItemViewModel? byName = Heimdall.App.ViewModels.Scheduled.ScheduledTaskServerResolver.Resolve(
            null,
            fixture.Shared.Profile.DisplayName,
            fixture.ViewModel.AllServers,
            server => server.Id,
            server => server.DisplayName,
            out _);

        Assert.Equal(fixture.Shared.Profile.Id, byId?.Id);
        Assert.Equal(fixture.Shared.Profile.Id, byName?.Id);
    }

    // --- Reload ---

    [Fact]
    public async Task ProviderReload_RefreshesTheSharedRows_AndLeavesTheLocalOnes()
    {
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        ServerItemViewModel localBefore = Row(fixture, LocalInSharedFolderId);
        ServerProfileDto added = fixture.Shared.DirectProfile.CloneFaithfully();
        added.Id = SharedSessionIds.PrefixFor(SharedSessionTestData.SourceId) + "profile3";
        added.DisplayName = "srv-web01";
        added.Group = "PREPROD";

        provider!.SetContents(
            [added],
            [],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, "Team renamed", 1)]);
        await provider.LoadAsync(new AppSettings());

        FolderViewModel root = Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.IsSharedRoot);
        Assert.Equal("Team renamed", root.Name);
        Assert.Equal("PREPROD", Assert.Single(root.SubFolders).Name);
        Assert.Equal(added.Id, Assert.Single(fixture.ViewModel.Servers, server => server.IsShared).Id);
        Assert.Same(localBefore, Row(fixture, LocalInSharedFolderId));
    }

    [Fact]
    public async Task TreeLoadedBeforeTheSources_SelectsTheSharedSessionLeftSelected_OnceTheyAreRead()
    {
        // At startup the local tree no longer waits for the shared sources.
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(_ => provider = new StubSharedSessionProvider([], [], []));
        string sharedId = fixture.Shared.Profile.Id;
        await fixture.ConfigManager.MergeSettingAsync(settings => settings.LastSelectedServerId = sharedId);
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        Assert.Null(fixture.ViewModel.EffectiveSelectedServerId);

        provider!.SetContents(
            [fixture.Shared.Profile, fixture.Shared.DirectProfile],
            [fixture.Shared.Gateway],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 2)]);
        await provider.LoadAsync(new AppSettings());

        // Its folder starts closed, so the selection is held back as a start holds any other.
        Assert.Equal(sharedId, fixture.ViewModel.EffectiveSelectedServerId);
    }

    [Fact]
    public async Task SettingsChange_RereadsTheSources_OnlyWhenTheSourcesOrVariablesChanged()
    {
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture);
        int baseline = provider!.LoadCount;

        await fixture.ConfigManager.MergeSettingAsync(settings => settings.ShowGatewayBadge = !settings.ShowGatewayBadge);
        await fixture.ViewModel.SharedSessionReload;
        Assert.Equal(baseline, provider.LoadCount);

        await fixture.ConfigManager.MergeSettingAsync(settings => settings.SharedSessionSources.Add(
            new SharedSessionSourceDto { Id = "addedsrc", Name = "Added", Path = @"C:\shares\added.mxtsessions" }));
        await fixture.ViewModel.SharedSessionReload;
        Assert.Equal(baseline + 1, provider.LoadCount);
        Assert.Contains(provider.LastLoadedSettings!.SharedSessionSources, source => source.Id == "addedsrc");

        await fixture.ConfigManager.MergeSettingAsync(settings => settings.MobaXtermUserVariables["SITE"] = "paris");
        await fixture.ViewModel.SharedSessionReload;
        Assert.Equal(baseline + 2, provider.LoadCount);
    }

    [Fact]
    public async Task SettingsChange_ToTheReportThresholds_RereadsTheSources()
    {
        // The report of a source is built when its file is read: a new threshold shows only once
        // the file is read again.
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture);
        int baseline = provider!.LoadCount;

        await fixture.ConfigManager.MergeSettingAsync(settings => settings.SharedReportGatewayMajorityPercent = 75);
        await fixture.ViewModel.SharedSessionReload;
        Assert.Equal(baseline + 1, provider.LoadCount);
        Assert.Equal(75, provider.LastLoadedSettings!.SharedReportGatewayMajorityPercent);

        await fixture.ConfigManager.MergeSettingAsync(settings => settings.SharedReportGatewayMinFolderSessions = 5);
        await fixture.ViewModel.SharedSessionReload;
        Assert.Equal(baseline + 2, provider.LoadCount);
        Assert.Equal(5, provider.LastLoadedSettings!.SharedReportGatewayMinFolderSessions);
    }

    [Fact]
    public async Task SettingsChange_ToTheReloadDebounce_ReachesTheProvider_WithoutARead()
    {
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture);
        int baseline = provider!.LoadCount;

        await fixture.ConfigManager.MergeSettingAsync(settings => settings.SharedSourceReloadDebounceMs = 4000);
        await fixture.ViewModel.SharedSessionReload;
        Assert.Equal(4000, provider.AppliedReloadDebounces.LastOrDefault());

        Assert.Equal(baseline, provider.LoadCount);
    }

    // --- Live reload ---

    [Fact]
    public async Task SourceReload_KeepsTheRowsTheSelectionAndTheExpandedFolders()
    {
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        FolderViewModel sharedRoot = Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.IsSharedRoot);
        sharedRoot.IsExpanded = true;
        Assert.Single(sharedRoot.SubFolders).IsExpanded = true;
        ServerItemViewModel selected = Row(fixture, fixture.Shared.Profile.Id);
        fixture.ViewModel.SelectSingle(selected);
        ServerProfileDto added = fixture.Shared.DirectProfile.CloneFaithfully();
        added.Id = SharedSessionIds.PrefixFor(SharedSessionTestData.SourceId) + "profile3";
        added.DisplayName = "srv-web01";
        SharedSessionSourceState state = StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 3);
        provider!.SetContents([fixture.Shared.Profile, fixture.Shared.DirectProfile, added], [fixture.Shared.Gateway], [state]);

        provider.RaiseSourceReloaded(state);

        FolderViewModel rootAfter = Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.IsSharedRoot);
        Assert.True(rootAfter.IsExpanded);
        Assert.True(Assert.Single(rootAfter.SubFolders).IsExpanded);
        Assert.Same(selected, Row(fixture, fixture.Shared.Profile.Id));
        Assert.Same(selected, fixture.ViewModel.SelectedServer);
        Assert.Contains(selected, fixture.ViewModel.SelectedItems);
        Assert.Contains(fixture.ViewModel.Servers, server => server.Id == added.Id);
    }

    [Fact]
    public async Task SourceReload_SaysWhatItCameTo_OnTheStatusLine()
    {
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        SharedSessionSourceState updated = StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 2);
        SharedSessionSourceState kept = updated with { Notice = "The previous version is kept. The file is locked." };

        provider!.RaiseSourceReloaded(updated);
        provider.RaiseSourceReloaded(kept);

        Assert.Equal(
            [
                fixture.Localizer.FormatCount(2, "StatusSharedSourceUpdatedOne", "StatusSharedSourceUpdated", TeamSourceName, 2),
                fixture.Localizer.Format("StatusSharedSourceNotUpdated", TeamSourceName, kept.Notice!),
            ],
            statuses);
        Assert.Equal("Shared sessions updated: Team, 2 sessions", statuses[0]);
    }

    [Fact]
    public async Task OfflineSource_RootSaysFromWhen_AndKeepsItsSessions()
    {
        DateTime takenAtUtc = new(2026, 10, 14, 7, 30, 0, DateTimeKind.Utc);
        const string notice = "Offline copy from then. The file was not found.";
        await using Fixture fixture = await Fixture.CreateAsync(shared => new StubSharedSessionProvider(
            [shared.Profile, shared.DirectProfile],
            [shared.Gateway],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 2) with
            {
                Notice = notice,
                OfflineCopyFromUtc = takenAtUtc,
            }]));
        await LoadTreeAsync(fixture);

        FolderViewModel root = Assert.Single(fixture.ViewModel.GroupedServers, folder => folder.IsSharedRoot);

        Assert.False(root.HasError);
        Assert.True(root.HasNotice);
        Assert.Contains(notice, root.TooltipText, StringComparison.Ordinal);
        Assert.Equal(
            fixture.Localizer.Format(
                "SessionTreeSharedRootOfflineBadge",
                takenAtUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)),
            root.NoticeBadgeText);
        Assert.Equal(2, root.ServerCount);
    }

    [Fact]
    public async Task SharedServerCount_FollowsTheSharedRows()
    {
        StubSharedSessionProvider? provider = null;
        await using Fixture fixture = await Fixture.CreateAsync(shared => provider = LoadedTeamProvider(shared));
        await LoadTreeAsync(fixture, LocalProfile(LocalInSharedFolderId, SharedSessionTestData.SharedFolder));
        List<string?> raised = [];
        fixture.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal(2, fixture.ViewModel.SharedServerCount);

        provider!.SetContents(
            [fixture.Shared.DirectProfile],
            [],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 1)]);
        await provider.LoadAsync(new AppSettings());

        Assert.Equal(1, fixture.ViewModel.SharedServerCount);
        Assert.Contains(nameof(ServerListViewModel.SharedServerCount), raised);
    }

    [Theory]
    [InlineData("en", 12, 0, "12 sessions")]
    [InlineData("en", 12, 79, "12 sessions, 79 shared")]
    [InlineData("en", 1, 1, "1 session, 1 shared")]
    [InlineData("fr", 2, 1, "2 sessions, 1 partagée")]
    [InlineData("fr", 0, 3, "0 session, 3 partagées")]
    public async Task StatusBarCount_NamesTheSharedSessions_WhenThereAreAny(string locale, int local, int shared, string expected)
    {
        var localizer = new Heimdall.Core.Localization.LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        Assert.Equal(expected, MainViewModel.FormatServerCount(localizer, local, shared));
    }

    // --- Helpers ---

    private static StubSharedSessionProvider LoadedTeamProvider(SharedSessionTestData shared) =>
        new(
            [shared.Profile, shared.DirectProfile],
            [shared.Gateway],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, TeamSourceName, 2)]);

    private static ServerProfileDto LocalProfile(string id, string group) => new()
    {
        Id = id,
        DisplayName = id,
        Group = group,
        ConnectionType = "SSH",
        RemoteServer = id + ".example.test",
        SshPort = DefaultPorts.Ssh,
        UseDirectConnection = true,
    };

    private static async Task LoadTreeAsync(Fixture fixture, params ServerProfileDto[] local)
    {
        await fixture.ConfigManager.SaveServersAsync([.. local]);
        fixture.ViewModel.LoadServers(
            await fixture.ConfigManager.LoadServersAsync(),
            await fixture.ConfigManager.LoadSettingsAsync());
    }

    private static ServerItemViewModel Row(Fixture fixture, string id) =>
        Assert.Single(fixture.ViewModel.Servers, server => string.Equals(server.Id, id, StringComparison.Ordinal));
}
