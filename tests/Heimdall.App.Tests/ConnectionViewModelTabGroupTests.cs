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
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Session tabs grouped by the folder of their profile: one strip, each folder's tabs side by
/// side behind a chip, each group collapsible.
/// </summary>
public sealed class ConnectionViewModelTabGroupTests
{
    private const string Ops = "Ops";
    private const string Lab = "Lab";

    private readonly Dictionary<string, string> _folderByProfile = new(StringComparer.Ordinal)
    {
        ["ops-1"] = Ops,
        ["ops-2"] = Ops,
        ["ops-3"] = Ops,
        ["lab-1"] = Lab,
        ["lab-2"] = Lab,
    };

    [Fact]
    public void GroupingOff_LeavesTheStripInOpeningOrderAndUngrouped()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: false);

        SessionTabViewModel a = Open(sut, "ops-1");
        SessionTabViewModel b = Open(sut, "lab-1");
        SessionTabViewModel c = Open(sut, "ops-2");

        Assert.Equal(new[] { a, b, c }, sut.ActiveSessions);
        Assert.All(sut.ActiveSessions, tab => Assert.False(tab.HasTabGroup));
    }

    [Fact]
    public void GroupingOn_GathersEachFolderWhereItsFirstTabStands()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);

        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel loose = Open(sut, "no-folder");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");

        Assert.Equal(new[] { ops1, ops2, loose, lab1 }, sut.ActiveSessions);
        Assert.Equal(Ops, ops2.TabGroupKey);
        Assert.Equal("Ops label", ops2.TabGroupLabel);
        Assert.Equal("#112233", ops2.TabGroupColor);
        Assert.False(loose.HasTabGroup);
        Assert.Same(ops2, sut.ActiveSession);
    }

    [Fact]
    public void GroupingOn_OnlyTheFirstTabOfAGroupHeadsIt()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);

        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        Assert.True(ops1.IsTabGroupHead);
        Assert.False(ops2.IsTabGroupHead);
        Assert.True(lab1.IsTabGroupHead);
    }

    [Fact]
    public void PinnedTab_StaysOutOfItsFolderGroupAndInFront()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");

        sut.SetPinned(ops2, true);

        Assert.Equal(new[] { ops2, ops1, lab1 }, sut.ActiveSessions);
        Assert.False(ops2.HasTabGroup);
        Assert.True(ops1.IsTabGroupHead);

        sut.SetPinned(ops2, false);

        Assert.Equal(new[] { ops2, ops1, lab1 }, sut.ActiveSessions);
        Assert.Equal(Ops, ops2.TabGroupKey);
        Assert.True(ops2.IsTabGroupHead);
        Assert.False(ops1.IsTabGroupHead);
    }

    [Fact]
    public void ProfileNamedAfterTheTabJoinedTheStrip_StillLandsInItsGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel late = sut.AddSession("session-scoped-id", "late", "SSH");

        late.OriginalServerId = "ops-2";

        Assert.Equal(new[] { ops1, late, lab1 }, sut.ActiveSessions);
        Assert.Equal(Ops, late.TabGroupKey);
    }

    [Fact]
    public void Collapse_FoldsAwayAllButTheHead_AndMovesTheSelectionOutOfTheGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        sut.ActiveSession = ops2;

        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        Assert.True(ops1.IsTabGroupCollapsed);
        Assert.False(ops1.IsHiddenInCollapsedGroup);
        Assert.True(ops2.IsHiddenInCollapsedGroup);
        Assert.False(lab1.IsHiddenInCollapsedGroup);
        Assert.Same(lab1, sut.ActiveSession);
        Assert.Equal("SessionTabGroupChipCollapsedA11y", ops1.TabGroupAccessibleName);
    }

    [Fact]
    public void Collapse_WhenEveryVisibleTabIsInTheGroup_LeavesItOpen()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");

        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        Assert.False(ops1.IsTabGroupCollapsed);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
        Assert.Same(ops2, sut.ActiveSession);
        Assert.Empty(sut.CollapsedTabGroups);
    }

    [Fact]
    public void Collapse_GroupWithoutTheSelection_KeepsTheSelection()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        Assert.True(ops1.IsTabGroupCollapsed);
        Assert.Same(lab1, sut.ActiveSession);
    }

    [Fact]
    public void SelectingATabOfACollapsedGroup_ExpandsTheGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        sut.ActiveSession = ops2;

        Assert.False(ops1.IsTabGroupCollapsed);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
        Assert.Empty(sut.CollapsedTabGroups);
    }

    [Fact]
    public void ToggleTwice_ExpandsAgain()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");

        sut.ToggleTabGroupCollapsedCommand.Execute(ops2);
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        Assert.False(ops1.IsTabGroupCollapsed);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
        Assert.Equal("SessionTabGroupChipExpandedA11y", ops1.TabGroupAccessibleName);
    }

    [Fact]
    public void ClosingTheHeadOfACollapsedGroup_HandsTheChipToTheNextTab()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        sut.RemoveFromStrip(ops1);

        Assert.True(ops2.IsTabGroupHead);
        Assert.True(ops2.IsTabGroupCollapsed);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void TurningGroupingOff_UngroupsEveryTabAndForgetsCollapsedGroups()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);

        sut.GroupTabsByFolder = false;

        Assert.All(sut.ActiveSessions, tab =>
        {
            Assert.False(tab.HasTabGroup);
            Assert.False(tab.IsTabGroupHead);
            Assert.False(tab.IsHiddenInCollapsedGroup);
        });
        Assert.Empty(sut.CollapsedTabGroups);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void TurningGroupingOn_RegroupsTheTabsAlreadyOpen()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: false);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");

        sut.GroupTabsByFolder = true;

        Assert.Equal(new[] { ops1, ops2, lab1 }, sut.ActiveSessions);
        Assert.Same(ops2, sut.ActiveSession);
    }

    [Fact]
    public void RefreshTabGroups_ProfileMovedToAnotherFolder_FollowsTheTree()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        _folderByProfile["ops-1"] = Lab;
        sut.RefreshTabGroups();

        Assert.Equal(new[] { ops1, lab1, ops2 }, sut.ActiveSessions);
        Assert.Equal(Lab, ops1.TabGroupKey);
        Assert.True(ops2.IsTabGroupHead);
    }

    [Fact]
    public void MoveSession_DraggedIntoAnotherGroup_ReturnsToItsOwn()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel lab2 = Open(sut, "lab-2");

        sut.MoveSession(ops2, 2);

        Assert.Equal(new[] { ops1, ops2, lab1, lab2 }, sut.ActiveSessions);
    }

    [Fact]
    public void OrderForStrip_WithoutGroups_IsThePinnedPartition()
    {
        SessionTabViewModel a = new() { Title = "a" };
        SessionTabViewModel b = new() { Title = "b", IsPinned = true };
        SessionTabViewModel c = new() { Title = "c" };
        SessionTabViewModel d = new() { Title = "d", IsPinned = true };

        Assert.Equal(
            ConnectionViewModel.OrderByPinned([a, b, c, d]),
            ConnectionViewModel.OrderForStrip([a, b, c, d]));
    }

    [Fact]
    public void NearestTabOutsideGroup_PrefersTheTabAfterTheGroup_ThenTheOneBefore()
    {
        SessionTabViewModel before = new() { Title = "before" };
        SessionTabViewModel member = new() { Title = "member", TabGroupKey = Ops };
        SessionTabViewModel after = new() { Title = "after" };

        Assert.Same(after, ConnectionViewModel.NearestTabOutsideGroup([before, member, after], Ops));
        Assert.Same(before, ConnectionViewModel.NearestTabOutsideGroup([before, member], Ops));
        Assert.Null(ConnectionViewModel.NearestTabOutsideGroup([member], Ops));
    }

    [Fact]
    public void SelectAdjacentTab_SkipsACollapsedFolderWhole_AndLeavesItCollapsed()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel loose = Open(sut, "no-folder");
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);
        sut.ActiveSession = loose;

        sut.SelectAdjacentTab(1);

        Assert.Same(lab1, sut.ActiveSession);
        Assert.True(ops1.IsTabGroupCollapsed);

        sut.SelectAdjacentTab(1);
        Assert.Same(loose, sut.ActiveSession);

        sut.SelectAdjacentTab(-1);
        Assert.Same(lab1, sut.ActiveSession);
        Assert.True(ops1.IsTabGroupCollapsed);
    }

    [Fact]
    public void SelectAdjacentTab_ExpandedFolder_IsWalkedTabByTab()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        sut.ActiveSession = ops1;

        sut.SelectAdjacentTab(1);
        Assert.Same(ops2, sut.ActiveSession);

        sut.SelectAdjacentTab(1);
        Assert.Same(lab1, sut.ActiveSession);
    }

    [Fact]
    public void NextVisibleTab_NoOtherReachableTab_ReturnsNull()
    {
        SessionTabViewModel only = new() { Title = "only" };
        SessionTabViewModel folded = new() { Title = "folded", IsTabGroupCollapsed = true };

        Assert.Null(ConnectionViewModel.NextVisibleTab([only, folded], only, 1));
        Assert.Null(ConnectionViewModel.NextVisibleTab([only], only, -1));
        Assert.Null(ConnectionViewModel.NextVisibleTab([only], new SessionTabViewModel(), 1));
    }

    [Fact]
    public void CollapseOtherTabGroups_KeepsThisFolderOpen_FoldsTheOthers_AndMovesTheSelectionIn()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel lab2 = Open(sut, "lab-2");
        SessionTabViewModel loose = Open(sut, "no-folder");
        sut.ActiveSession = lab2;

        sut.CollapseOtherTabGroupsCommand.Execute(ops2);

        Assert.False(ops1.IsTabGroupCollapsed);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
        Assert.True(lab1.IsTabGroupCollapsed);
        Assert.True(lab2.IsHiddenInCollapsedGroup);
        Assert.False(loose.IsHiddenInCollapsedGroup);
        Assert.Same(ops2, sut.ActiveSession);
    }

    [Fact]
    public void CollapseOtherTabGroups_SelectionAlreadyInTheFolder_StaysPut()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        sut.ActiveSession = ops1;

        sut.CollapseOtherTabGroupsCommand.Execute(ops2);

        Assert.Same(ops1, sut.ActiveSession);
        Assert.True(lab1.IsTabGroupCollapsed);
    }

    [Fact]
    public void CollapseOtherTabGroups_ReopensThisFolderWhenItWasCollapsed()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);
        Assert.True(ops1.IsTabGroupCollapsed);

        sut.CollapseOtherTabGroupsCommand.Execute(ops1);

        Assert.False(ops1.IsTabGroupCollapsed);
        Assert.True(lab1.IsTabGroupCollapsed);
        Assert.Same(ops1, sut.ActiveSession);
    }

    [Fact]
    public void HasOtherTabGroup_OnlyWhenAnotherFolderIsInTheStrip()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel loose = Open(sut, "no-folder");

        Assert.False(sut.HasOtherTabGroup(ops1));
        Assert.False(sut.HasOtherTabGroup(loose));

        Open(sut, "lab-1");
        Assert.True(sut.HasOtherTabGroup(ops1));
        Assert.False(sut.HasOtherTabGroup(loose));
    }

    /// <summary>
    /// With only one folder's tabs open, a click on the chip leaves the group open, and the chip
    /// used to give no reason why: it says so now, and stops saying it once another tab opens.
    /// </summary>
    [Fact]
    public void TheOnlyGroupOpen_ItsChipSaysItCannotBeFolded()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");

        Assert.Equal("SessionTabGroupChipLoneA11y", ops1.TabGroupAccessibleName);

        Open(sut, "lab-1");

        Assert.Equal("SessionTabGroupChipExpandedA11y", ops1.TabGroupAccessibleName);
    }

    /// <summary>
    /// "Close others" and "Close to the right" are aimed at the strip the user sees: a folder
    /// folded away is not in it, and its tabs used to be closed with the rest.
    /// </summary>
    [Fact]
    public void BulkCloses_LeaveAFoldedGroupAlone()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true);
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab2 = Open(sut, "lab-2");
        sut.ActiveSession = lab1;
        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);
        List<SessionTabViewModel> strip = [.. sut.ActiveSessions];

        IReadOnlyList<SessionTabViewModel> others = SessionTabContextMenuFactory.SessionsToCloseOthers(strip, lab1);
        IReadOnlyList<SessionTabViewModel> right = SessionTabContextMenuFactory.SessionsToCloseToRight(strip, lab1);

        Assert.True(ops1.IsTabGroupCollapsed);
        Assert.DoesNotContain(ops1, others);
        Assert.DoesNotContain(ops2, others);
        Assert.Contains(lab2, others);
        Assert.DoesNotContain(ops1, right);
        Assert.DoesNotContain(ops2, right);
    }

    [Fact]
    public void OwnRow_OnlyTheActiveTabsGroupIsUnfolded()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        Assert.Equal(new[] { Ops }, sut.CollapsedTabGroups);
        Assert.True(ops1.IsHiddenInCollapsedGroup);
        Assert.True(ops2.IsHiddenInCollapsedGroup);
        Assert.False(lab1.IsHiddenInCollapsedGroup);

        sut.ActiveSession = ops2;

        Assert.Equal(new[] { Lab }, sut.CollapsedTabGroups);
        Assert.False(ops1.IsHiddenInCollapsedGroup);
        Assert.False(ops2.IsHiddenInCollapsedGroup);
        Assert.True(lab1.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void OwnRow_BackgroundReintroducedGroupStaysFolded()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        sut.ActiveSessions.Remove(lab1);

        sut.ReintroduceSession(lab1);

        Assert.Same(ops1, sut.ActiveSession);
        Assert.Equal(Lab, lab1.TabGroupKey);
        Assert.True(lab1.IsHiddenInCollapsedGroup);
        Assert.Equal(new[] { Lab }, sut.CollapsedTabGroups);
    }

    [Fact]
    public void OwnRow_PinnedSelection_KeepsLastShownGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel pinned = Open(sut, "no-folder");
        sut.SetPinned(pinned, true);

        sut.ActiveSession = pinned;

        Assert.Equal(new[] { Ops }, sut.CollapsedTabGroups);
        Assert.True(ops1.IsHiddenInCollapsedGroup);
        Assert.False(lab1.IsHiddenInCollapsedGroup);
        Assert.False(pinned.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void OwnRow_HeadOfFoldedGroupIsHidden_AndNoChip()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        Assert.True(ops1.IsTabGroupHead);
        Assert.True(ops1.IsHiddenInCollapsedGroup);
        Assert.False(ops1.ShowsTabGroupChip);
        Assert.True(lab1.IsTabGroupHead);
        Assert.False(lab1.IsHiddenInCollapsedGroup);
        Assert.False(lab1.ShowsTabGroupChip);

        sut.TabGroupsOnOwnRow = false;

        Assert.True(ops1.ShowsTabGroupChip);
        Assert.False(ops1.IsHiddenInCollapsedGroup);
        Assert.True(lab1.ShowsTabGroupChip);
    }

    [Fact]
    public void OwnRow_HeadersFollowStripOrderAndCounts()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        Open(sut, "lab-1");
        Open(sut, "ops-1");
        Open(sut, "ops-2");

        Assert.True(sut.HasTabGroupHeaders);
        Assert.True(sut.ShowTabGroupRow);
        Assert.Equal(new[] { Lab, Ops }, sut.TabGroupHeaders.Select(header => header.Key));
        Assert.Equal(new[] { 1, 2 }, sut.TabGroupHeaders.Select(header => header.TabCount));
        Assert.Equal(new[] { "Lab label", "Ops label" }, sut.TabGroupHeaders.Select(header => header.Label));
        Assert.Equal(new[] { false, true }, sut.TabGroupHeaders.Select(header => header.IsShown));
        Assert.Equal("#112233", sut.TabGroupHeaders[1].Color);
        Assert.True(sut.TabGroupHeaders[1].HasColor);
        Assert.False(sut.TabGroupHeaders[0].HasColor);
        Assert.Equal("SessionTabGroupHeaderHiddenOneA11y", sut.TabGroupHeaders[0].AccessibleName);
        Assert.Equal("SessionTabGroupHeaderShownA11y", sut.TabGroupHeaders[1].AccessibleName);
    }

    [Fact]
    public async Task OwnRow_HeaderAccessibleName_CarriesTheFullPathAndTheCount_SingularIncluded()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(ViewSource.RepoRoot(), "locales"), "en");
        ConnectionViewModel sut = new(
            localizer,
            new ConnectionViewModelCloseTests.TrackingDialogService(true),
            new ConnectionViewModelCloseTests.TrackingSplitService(),
            new PaneCloseArbiter(),
            new SessionWindowService(static (_, _) => { }))
        {
            GroupTabsByFolder = true,
            TabGroupsOnOwnRow = true,
            TabGroupResolver = profileId => _folderByProfile.TryGetValue(profileId, out string? folder)
                ? new SessionTabGroupInfo(folder, folder + " label", "")
                : null,
        };
        Open(sut, "lab-1");
        Open(sut, "ops-1");
        Open(sut, "ops-2");

        SessionTabGroupHeaderViewModel lab = sut.TabGroupHeaders[0];
        SessionTabGroupHeaderViewModel ops = sut.TabGroupHeaders[1];

        Assert.Equal("Folder Lab, 1 tab, hidden; select to show it", lab.AccessibleName);
        Assert.Equal("Folder Ops, 2 tabs, shown", ops.AccessibleName);

        sut.ActiveSession = sut.ActiveSessions.First(tab => tab.TabGroupKey == Lab);

        Assert.Equal("Folder Lab, 1 tab, shown", lab.AccessibleName);
        Assert.Equal("Folder Ops, 2 tabs, hidden; select to show them", ops.AccessibleName);
    }

    [Fact]
    public void OwnRow_HiddenFolderWithAFailedTab_NeedsAttention()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "lab-1");

        ops1.Status = "Failed";

        Assert.Equal(SessionTabHeaderState.Failed, ops1.HeaderState);
        SessionTabGroupHeaderViewModel header = sut.TabGroupHeaders.Single(h => h.Key == Ops);
        Assert.Equal(SessionTabGroupAttention.Failed, header.Attention);
        Assert.True(header.HasFailedTab);
        Assert.False(header.HasDisconnectedTab);
    }

    [Fact]
    public void OwnRow_HiddenFolderWithADisconnectedTab_NeedsAttention()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "lab-1");

        ops1.Status = "Disconnected";

        Assert.Equal(SessionTabHeaderState.Disconnected, ops1.HeaderState);
        SessionTabGroupHeaderViewModel header = sut.TabGroupHeaders.Single(h => h.Key == Ops);
        Assert.Equal(SessionTabGroupAttention.Disconnected, header.Attention);
        Assert.True(header.HasDisconnectedTab);
        Assert.False(header.HasFailedTab);
    }

    [Fact]
    public void OwnRow_ShownFolder_NeverNeedsAttention()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");

        ops1.Status = "Failed";

        Assert.True(sut.TabGroupHeaders.Single().IsShown);
        Assert.False(sut.TabGroupHeaders.Single().NeedsAttention);
    }

    [Fact]
    public void OwnRow_AttentionFollowsTheTabState_WithoutAStripChange()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "lab-1");
        SessionTabGroupHeaderViewModel header = sut.TabGroupHeaders.Single(h => h.Key == Ops);
        Assert.False(header.NeedsAttention);

        ops1.Status = "Failed";
        Assert.True(header.NeedsAttention);

        ops1.Status = "Connected";
        Assert.Equal(SessionTabGroupAttention.None, header.Attention);
        Assert.Same(header, sut.TabGroupHeaders.Single(h => h.Key == Ops));
    }

    [Fact]
    public async Task OwnRow_AttentionAccessibleName_SaysSo()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(ViewSource.RepoRoot(), "locales"), "en");
        ConnectionViewModel sut = new(
            localizer,
            new ConnectionViewModelCloseTests.TrackingDialogService(true),
            new ConnectionViewModelCloseTests.TrackingSplitService(),
            new PaneCloseArbiter(),
            new SessionWindowService(static (_, _) => { }))
        {
            GroupTabsByFolder = true,
            TabGroupsOnOwnRow = true,
            TabGroupResolver = profileId => _folderByProfile.TryGetValue(profileId, out string? folder)
                ? new SessionTabGroupInfo(folder, folder + " label", "")
                : null,
        };
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");
        SessionTabGroupHeaderViewModel ops = sut.TabGroupHeaders.Single(h => h.Key == Ops);

        ops1.Status = "Failed";
        Assert.Equal("Folder Ops, 2 tabs, hidden; at least one tab failed, select to show them", ops.AccessibleName);

        ops1.Status = "Disconnected";
        Assert.Equal("Folder Ops, 2 tabs, hidden; at least one tab is disconnected, select to show them", ops.AccessibleName);

        sut.ActiveSessions.Remove(ops2);
        Assert.Equal("Folder Ops, 1 tab, hidden; it is disconnected, select to show it", ops.AccessibleName);

        ops1.Status = "Failed";
        Assert.Equal("Folder Ops, 1 tab, hidden; it failed, select to show it", ops.AccessibleName);
    }

    [Fact]
    public void OwnRow_FailedWinsOverDisconnected()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");
        SessionTabGroupHeaderViewModel header = sut.TabGroupHeaders.Single(h => h.Key == Ops);

        ops1.Status = "Disconnected";
        ops2.Status = "Failed";
        Assert.Equal(SessionTabGroupAttention.Failed, header.Attention);

        ops2.Status = "Connected";
        Assert.Equal(SessionTabGroupAttention.Disconnected, header.Attention);
    }

    [Fact]
    public void OwnRow_AttentionClears_WhenTheFolderBecomesShown()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "lab-1");
        ops1.Status = "Failed";
        SessionTabGroupHeaderViewModel header = sut.TabGroupHeaders.Single(h => h.Key == Ops);
        Assert.True(header.NeedsAttention);

        sut.ShowTabGroupCommand.Execute(Ops);

        Assert.True(header.IsShown);
        Assert.Equal(SessionTabGroupAttention.None, header.Attention);
    }

    [Fact]
    public void OwnRow_AttentionClears_WhenTheFailedTabIsClosed()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        Open(sut, "lab-1");
        ops1.Status = "Failed";
        SessionTabGroupHeaderViewModel header = sut.TabGroupHeaders.Single(h => h.Key == Ops);
        Assert.True(header.NeedsAttention);

        sut.RemoveFromStrip(ops1);

        Assert.Equal(SessionTabGroupAttention.None, header.Attention);
    }

    [Fact]
    public void OwnRow_AttentionClears_WhenTheFailedTabMovesToAnotherFolder()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        Open(sut, "lab-1");
        ops1.Status = "Failed";
        SessionTabGroupHeaderViewModel opsHeader = sut.TabGroupHeaders.Single(h => h.Key == Ops);
        Assert.True(opsHeader.NeedsAttention);

        _folderByProfile["ops-1"] = Lab;
        sut.RefreshTabGroups();

        Assert.Equal(Lab, ops1.TabGroupKey);
        Assert.Equal(SessionTabGroupAttention.None, opsHeader.Attention);
    }

    [Fact]
    public void OwnRow_ShowTabGroup_SelectsLastActiveTabOfThatGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        sut.ShowTabGroupCommand.Execute(Ops);
        Assert.Same(ops2, sut.ActiveSession);

        sut.ActiveSession = ops1;
        sut.ShowTabGroupCommand.Execute(Lab);
        Assert.Same(lab1, sut.ActiveSession);

        sut.ShowTabGroupCommand.Execute(Ops);
        Assert.Same(ops1, sut.ActiveSession);
        Assert.True(lab1.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void OwnRow_ShowTabGroup_FallsBackToFirstTab()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel lab2 = Open(sut, "lab-2");
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        sut.ActiveSessions.Remove(lab1);
        sut.ActiveSessions.Remove(lab2);
        sut.ReintroduceSession(lab2);
        sut.ReintroduceSession(lab1);
        Assert.Same(ops1, sut.ActiveSession);

        sut.ShowTabGroupCommand.Execute(Lab);

        Assert.Same(sut.ActiveSessions.First(tab => tab.TabGroupKey == Lab), sut.ActiveSession);
        Assert.Same(lab2, sut.ActiveSession);
    }

    [Fact]
    public void OwnRow_ClosedTabIsForgotten()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel ops2 = Open(sut, "ops-2");
        Open(sut, "lab-1");
        sut.ActiveSession = ops2;
        sut.ActiveSession = sut.ActiveSessions.Last();

        sut.RemoveFromStrip(ops2);
        sut.ShowTabGroupCommand.Execute(Ops);

        Assert.Same(ops1, sut.ActiveSession);
    }

    [Fact]
    public void OwnRow_LateProfileLookup_ShowsItsGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel late = sut.AddSession("session-scoped-id", "late", "SSH");
        Assert.Equal(new[] { Ops }, sut.CollapsedTabGroups);

        late.OriginalServerId = "ops-2";

        Assert.Equal(Ops, late.TabGroupKey);
        Assert.Equal(new[] { Lab }, sut.CollapsedTabGroups);
        Assert.False(ops1.IsHiddenInCollapsedGroup);
        Assert.False(late.IsHiddenInCollapsedGroup);
        Assert.True(lab1.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void OwnRow_ClosingLastTabOfShownGroup_SelectsAVisibleTab()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel loose = Open(sut, "no-folder");
        sut.ActiveSession = ops1;
        Assert.True(lab1.IsHiddenInCollapsedGroup);

        sut.RemoveFromStrip(ops1);

        Assert.Same(loose, sut.ActiveSession);
    }

    [Fact]
    public void OwnRow_ToggleCollapseIsInert()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        Open(sut, "ops-2");
        SessionTabViewModel lab1 = Open(sut, "lab-1");

        sut.ToggleTabGroupCollapsedCommand.Execute(ops1);
        sut.ToggleTabGroupCollapsedCommand.Execute(lab1);

        Assert.Equal(new[] { Ops }, sut.CollapsedTabGroups);
        Assert.True(ops1.IsHiddenInCollapsedGroup);
        Assert.Same(lab1, sut.ActiveSession);
        Assert.False(lab1.IsHiddenInCollapsedGroup);
    }

    [Fact]
    public void LeavingOwnRow_UnfoldsEveryGroup()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        Assert.True(ops1.IsHiddenInCollapsedGroup);

        sut.TabGroupsOnOwnRow = false;

        Assert.Empty(sut.CollapsedTabGroups);
        Assert.False(ops1.IsHiddenInCollapsedGroup);
        Assert.False(lab1.IsHiddenInCollapsedGroup);
        Assert.Empty(sut.TabGroupHeaders);

        sut.TabGroupsOnOwnRow = true;
        Assert.True(ops1.IsHiddenInCollapsedGroup);

        sut.GroupTabsByFolder = false;

        Assert.Empty(sut.CollapsedTabGroups);
        Assert.False(ops1.IsHiddenInCollapsedGroup);
        Assert.False(sut.IsTabGroupRowActive);
    }

    [Fact]
    public void GroupingOff_HidesHeaders()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: false, ownRow: true);
        Open(sut, "ops-1");
        Open(sut, "lab-1");

        Assert.False(sut.IsTabGroupRowActive);
        Assert.Empty(sut.TabGroupHeaders);
        Assert.False(sut.ShowTabGroupRow);

        sut.GroupTabsByFolder = true;

        Assert.Equal(2, sut.TabGroupHeaders.Count);
        Assert.True(sut.ShowTabGroupRow);

        sut.GroupTabsByFolder = false;

        Assert.Empty(sut.TabGroupHeaders);
        Assert.False(sut.ShowTabGroupRow);
    }

    [Fact]
    public void Headers_ReconcileInPlace()
    {
        ConnectionViewModel sut = CreateViewModel(groupByFolder: true, ownRow: true);
        SessionTabViewModel lab1 = Open(sut, "lab-1");
        SessionTabViewModel ops1 = Open(sut, "ops-1");
        SessionTabGroupHeaderViewModel labHeader = sut.TabGroupHeaders[0];
        SessionTabGroupHeaderViewModel opsHeader = sut.TabGroupHeaders[1];

        Open(sut, "ops-2");
        Assert.Same(labHeader, sut.TabGroupHeaders[0]);
        Assert.Same(opsHeader, sut.TabGroupHeaders[1]);
        Assert.Equal(2, opsHeader.TabCount);

        sut.MoveSession(ops1, 0);
        Assert.Same(opsHeader, sut.TabGroupHeaders[0]);
        Assert.Same(labHeader, sut.TabGroupHeaders[1]);

        sut.RemoveFromStrip(lab1);
        Assert.Same(opsHeader, Assert.Single(sut.TabGroupHeaders));
    }

    private ConnectionViewModel CreateViewModel(bool groupByFolder, bool ownRow = false)
    {
        ConnectionViewModel viewModel = new(
            new LocalizationManager(),
            new ConnectionViewModelCloseTests.TrackingDialogService(true),
            new ConnectionViewModelCloseTests.TrackingSplitService(),
            new PaneCloseArbiter(),
            new SessionWindowService(static (_, _) => { }))
        {
            GroupTabsByFolder = groupByFolder,
            TabGroupsOnOwnRow = ownRow,
        };

        viewModel.TabGroupResolver = profileId => _folderByProfile.TryGetValue(profileId, out string? folder)
            ? new SessionTabGroupInfo(folder, folder + " label", folder == Ops ? "#112233" : "")
            : null;
        return viewModel;
    }

    private static SessionTabViewModel Open(ConnectionViewModel viewModel, string profileId)
        => viewModel.AddSession(profileId, profileId, "SSH");
}
