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
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

public sealed class ConnectionViewModelCloseTests
{
    [Fact]
    public async Task CloseSessionAsync_HandedOffSecondaryLeaf_PromptsAndDeclinePreservesSession()
    {
        TrackingDialogService dialogService = new(false);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("Disconnected", "RemoteSessionHandedOff");
        AddActiveSession(sut, session);

        await sut.CloseSessionAsync(session, DisconnectReason.TabClose);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Contains(session, sut.ActiveSessions);
        Assert.Equal(0, splitService.CloseAllPanesCallCount);
    }

    [Fact]
    public async Task CloseSessionsAsync_ConnectedLikeStatuses_ReportsExactCountAndDeclinePreservesSessions()
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        TrackingDialogService dialogService = new(false);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService, localizer);
        SessionTabViewModel handedOff = CreateSplitSession("RemoteSessionHandedOff", "Disconnected");
        SessionTabViewModel connected = CreateSplitSession("Connected", "Disconnected");
        SessionTabViewModel error = CreateSplitSession("Error", "Disconnected");
        AddTabs(sut, handedOff, connected, error);

        await sut.CloseSessionsAsync(
            [handedOff, connected, error],
            DisconnectReason.TabClose);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Equal(
            localizer.Format("ConfirmCloseSessionGroupMessage", 3, 2),
            dialogService.LastConfirmMessage);
        Assert.Equal(new[] { handedOff, connected, error }, sut.ActiveSessions);
        Assert.Equal(0, splitService.CloseAllPanesCallCount);
    }

    [Fact]
    public async Task CloseAllSessions_HandedOffSession_PromptsAndDeclinePreservesSession()
    {
        TrackingDialogService dialogService = new(false);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("RemoteSessionHandedOff", "Disconnected");
        AddActiveSession(sut, session);

        await sut.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Contains(session, sut.ActiveSessions);
        Assert.Equal(0, splitService.CloseAllPanesCallCount);
    }

    [Theory]
    [InlineData("Connecting")]
    [InlineData("Disconnected")]
    [InlineData("Error")]
    public async Task CloseFlows_NonConnectedStatuses_DoNotPrompt(string status)
    {
        TrackingDialogService singleDialogService = new(true);
        TrackingSplitService singleSplitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel singleSut = CreateViewModel(singleDialogService, singleSplitService);
        SessionTabViewModel singleSession = CreateSplitSession(status, "Disconnected");
        AddActiveSession(singleSut, singleSession);

        await singleSut.CloseSessionAsync(singleSession, DisconnectReason.TabClose);

        Assert.Equal(0, singleDialogService.ConfirmCallCount);

        TrackingDialogService groupDialogService = new(true);
        TrackingSplitService groupSplitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel groupSut = CreateViewModel(groupDialogService, groupSplitService);
        SessionTabViewModel groupSession = CreateSplitSession(status, "Disconnected");
        AddActiveSession(groupSut, groupSession);

        await groupSut.CloseSessionsAsync([groupSession], DisconnectReason.TabClose);

        Assert.Equal(0, groupDialogService.ConfirmCallCount);

        TrackingDialogService allDialogService = new(true);
        TrackingSplitService allSplitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel allSut = CreateViewModel(allDialogService, allSplitService);
        SessionTabViewModel allSession = CreateSplitSession(status, "Disconnected");
        AddActiveSession(allSut, allSession);

        await allSut.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Equal(0, allDialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task CloseAllSessions_SplitSessionWithConnectedSecondaryLeaf_PromptsAndDeclinePreservesSession()
    {
        TrackingDialogService dialogService = new(false);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("Disconnected", "Connected");
        AddActiveSession(sut, session);

        await sut.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Contains(session, sut.ActiveSessions);
        Assert.Equal(0, splitService.CloseAllPanesCallCount);
    }

    [Fact]
    public async Task CloseAllSessions_ConnectedSession_Accepted_ClosesViaCloseAllPanes()
    {
        TrackingDialogService dialogService = new(true);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("Connected", "Disconnected");
        AddActiveSession(sut, session);

        await sut.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Equal(1, splitService.CloseAllPanesCallCount);
        Assert.Same(session, splitService.LastClosedSession);
        Assert.Empty(sut.ActiveSessions);
        Assert.False(sut.HasActiveSessions);
    }

    [Fact]
    public async Task CloseAllSessions_NoConnectedLeaf_ClosesWithoutPrompt()
    {
        TrackingDialogService dialogService = new(true);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("Disconnected", "Error");
        AddActiveSession(sut, session);

        await sut.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogService.ConfirmCallCount);
        Assert.Equal(1, splitService.CloseAllPanesCallCount);
        Assert.Same(session, splitService.LastClosedSession);
        Assert.Empty(sut.ActiveSessions);
        Assert.False(sut.HasActiveSessions);
    }

    [Fact]
    public async Task CloseAllSessions_BlockedByBusyToolPane_KeepsSession()
    {
        TrackingDialogService dialogService = new(true);
        TrackingSplitService splitService = new() { CloseAllPanesResult = false };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("Connected", "Disconnected");
        AddActiveSession(sut, session);

        await sut.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Equal(1, splitService.CloseAllPanesCallCount);
        Assert.Contains(session, sut.ActiveSessions);
        Assert.True(sut.HasActiveSessions);
    }

    [Fact]
    public async Task CloseSessionAsync_SplitSessionWithConnectedSecondaryLeaf_PromptsAndDeclinePreservesSession()
    {
        TrackingDialogService dialogService = new(false);
        TrackingSplitService splitService = new() { CloseAllPanesResult = true };
        ConnectionViewModel sut = CreateViewModel(dialogService, splitService);
        SessionTabViewModel session = CreateSplitSession("Disconnected", "Connected");
        AddActiveSession(sut, session);

        await sut.CloseSessionAsync(session, DisconnectReason.TabClose);

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Contains(session, sut.ActiveSessions);
        Assert.Equal(0, splitService.CloseAllPanesCallCount);
    }

    [Fact]
    public void SetPinned_PinsTab_MovesToFront_PreservesSelection()
    {
        ConnectionViewModel sut = CreateViewModel(new TrackingDialogService(true), new TrackingSplitService());
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        AddTabs(sut, a, b, c);
        sut.ActiveSession = b;

        sut.SetPinned(c, true);

        Assert.Equal(new[] { c, a, b }, sut.ActiveSessions);
        Assert.Same(b, sut.ActiveSession);
        Assert.True(c.IsPinned);
    }

    [Fact]
    public void SetPinned_MultiplePins_KeepRelativeOrderWithinGroups()
    {
        ConnectionViewModel sut = CreateViewModel(new TrackingDialogService(true), new TrackingSplitService());
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        var d = Tab("d");
        AddTabs(sut, a, b, c, d);

        sut.SetPinned(a, true);
        sut.SetPinned(c, true);

        // Pinned block keeps insertion order [a, c]; unpinned keeps [b, d].
        Assert.Equal(new[] { a, c, b, d }, sut.ActiveSessions);
    }

    [Fact]
    public void SetPinned_Unpin_MovesTabAfterLastPinned()
    {
        ConnectionViewModel sut = CreateViewModel(new TrackingDialogService(true), new TrackingSplitService());
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        var d = Tab("d");
        AddTabs(sut, a, b, c, d);
        sut.SetPinned(a, true);
        sut.SetPinned(c, true); // order now [a, c, b, d]

        sut.SetPinned(a, false);

        // a unpinned: pinned [c] first, then unpinned in relative order [a, b, d].
        Assert.Equal(new[] { c, a, b, d }, sut.ActiveSessions);
        Assert.False(a.IsPinned);
    }

    [Fact]
    public void SetPinned_SessionNotInCollection_IsNoOp()
    {
        ConnectionViewModel sut = CreateViewModel(new TrackingDialogService(true), new TrackingSplitService());
        var a = Tab("a");
        var b = Tab("b");
        AddTabs(sut, a, b);
        var stranger = Tab("stranger");

        sut.SetPinned(stranger, true);

        Assert.Equal(new[] { a, b }, sut.ActiveSessions);
        Assert.False(stranger.IsPinned);
    }

    [Fact]
    public void OrderByPinned_IsStablePartition()
    {
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        var d = Tab("d");
        b.IsPinned = true;
        d.IsPinned = true;

        var ordered = ConnectionViewModel.OrderByPinned([a, b, c, d]);

        Assert.Equal(new[] { b, d, a, c }, ordered);
    }

    private static SessionTabViewModel Tab(string title) => new SessionTabViewModel { Title = title };

    [Fact]
    public void MoveSession_UnpinnedDraggedAboveThePinnedGroup_StaysBelowEveryPinnedTab()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinnedA = CreatePinnableSession("pinned-a", pinned: true);
        SessionTabViewModel pinnedB = CreatePinnableSession("pinned-b", pinned: true);
        SessionTabViewModel loose = CreatePinnableSession("loose", pinned: false);
        AddTabs(sut, pinnedA, pinnedB, loose);
        sut.ActiveSession = pinnedA;

        sut.MoveSession(loose, 0);

        Assert.Equal(new[] { pinnedA, pinnedB, loose }, sut.ActiveSessions);
        Assert.Same(pinnedA, sut.ActiveSession);
    }

    [Fact]
    public void MoveSession_PinnedDraggedBelowTheUnpinnedGroup_StaysAboveEveryUnpinnedTab()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinned = CreatePinnableSession("pinned", pinned: true);
        SessionTabViewModel looseA = CreatePinnableSession("loose-a", pinned: false);
        SessionTabViewModel looseB = CreatePinnableSession("loose-b", pinned: false);
        AddTabs(sut, pinned, looseA, looseB);
        sut.ActiveSession = looseB;

        sut.MoveSession(pinned, 2);

        Assert.Equal(new[] { pinned, looseA, looseB }, sut.ActiveSessions);
        Assert.Same(looseB, sut.ActiveSession);
    }

    [Fact]
    public void MoveSession_WithinThePinnedGroup_ReordersAndIsNotClampedAway()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinnedA = CreatePinnableSession("pinned-a", pinned: true);
        SessionTabViewModel pinnedB = CreatePinnableSession("pinned-b", pinned: true);
        SessionTabViewModel pinnedC = CreatePinnableSession("pinned-c", pinned: true);
        SessionTabViewModel loose = CreatePinnableSession("loose", pinned: false);
        AddTabs(sut, pinnedA, pinnedB, pinnedC, loose);
        sut.ActiveSession = loose;

        sut.MoveSession(pinnedC, 0);

        Assert.Equal(new[] { pinnedC, pinnedA, pinnedB, loose }, sut.ActiveSessions);
        Assert.Same(loose, sut.ActiveSession);
    }

    [Fact]
    public void MoveSession_SessionAbsentFromTheCollection_IsANoOp()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinned = CreatePinnableSession("pinned", pinned: true);
        SessionTabViewModel loose = CreatePinnableSession("loose", pinned: false);
        SessionTabViewModel orphan = CreatePinnableSession("orphan", pinned: false);
        AddTabs(sut, pinned, loose);
        sut.ActiveSession = pinned;

        sut.MoveSession(orphan, 0);

        Assert.Equal(new[] { pinned, loose }, sut.ActiveSessions);
        Assert.DoesNotContain(orphan, sut.ActiveSessions);
        Assert.Same(pinned, sut.ActiveSession);
    }

    [Fact]
    public void ReintroduceSession_PinnedSession_LandsAfterTheLastPinnedTab()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinned = CreatePinnableSession("pinned", pinned: true);
        SessionTabViewModel looseA = CreatePinnableSession("loose-a", pinned: false);
        SessionTabViewModel looseB = CreatePinnableSession("loose-b", pinned: false);
        SessionTabViewModel returning = CreatePinnableSession("returning", pinned: true);
        AddTabs(sut, pinned, looseA, looseB);
        sut.ActiveSession = looseA;

        sut.ReintroduceSession(returning);

        Assert.Equal(new[] { pinned, returning, looseA, looseB }, sut.ActiveSessions);
        Assert.Same(looseA, sut.ActiveSession);
    }

    [Fact]
    public void ReintroduceSession_UnpinnedSession_AppendsAtTheTail()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinned = CreatePinnableSession("pinned", pinned: true);
        SessionTabViewModel loose = CreatePinnableSession("loose", pinned: false);
        SessionTabViewModel returning = CreatePinnableSession("returning", pinned: false);
        AddTabs(sut, pinned, loose);
        sut.ActiveSession = pinned;

        sut.ReintroduceSession(returning);

        Assert.Equal(new[] { pinned, loose, returning }, sut.ActiveSessions);
        Assert.Same(pinned, sut.ActiveSession);
    }

    [Fact]
    public void ReintroduceSession_SessionAlreadyPresent_LeavesTheCollectionUnchanged()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel pinned = CreatePinnableSession("pinned", pinned: true);
        SessionTabViewModel looseA = CreatePinnableSession("loose-a", pinned: false);
        SessionTabViewModel looseB = CreatePinnableSession("loose-b", pinned: false);
        AddTabs(sut, pinned, looseA, looseB);
        sut.ActiveSession = looseB;

        sut.ReintroduceSession(looseA);

        Assert.Equal(new[] { pinned, looseA, looseB }, sut.ActiveSessions);
        Assert.Equal(3, sut.ActiveSessions.Count);
        Assert.Same(looseB, sut.ActiveSession);
    }

    [Fact]
    public void AccessibleName_TwoTabsOfTheSameProfile_AreDisambiguated()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second);

        Assert.NotEqual(first.AccessibleName, second.AccessibleName);
        Assert.Contains(first.DisplayTitle, first.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(second.DisplayTitle, second.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibleName_UniqueTitle_StaysEqualToTheDisplayTitle()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel only = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel other = CreatePinnableSession("srv02", pinned: false);
        AddTabs(sut, only, other);

        Assert.Equal(only.DisplayTitle, only.AccessibleName);
        Assert.Equal(other.DisplayTitle, other.AccessibleName);
    }

    [Fact]
    public void AccessibleName_ClosingOneOfTwoCollidingTabs_RestoresThePlainNameOfTheSurvivor()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second);

        sut.ActiveSessions.Remove(second);

        Assert.Equal(first.DisplayTitle, first.AccessibleName);
    }

    [Fact]
    public void AccessibleName_ThreeCollidingTabs_YieldThreeDistinctNames()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel third = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second, third);

        string[] names = [first.AccessibleName, second.AccessibleName, third.AccessibleName];

        Assert.Equal(3, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AccessibleName_RenamingATabOutOfCollision_RestoresBothPlainNames()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second);

        second.Title = "srv02";

        Assert.Equal(first.DisplayTitle, first.AccessibleName);
        Assert.Equal(second.DisplayTitle, second.AccessibleName);
    }

    // Direct mutation from outside ConnectionViewModel: the shape used by
    // SessionWindowService, MainViewModel and SessionCoordinator, which never call a
    // ConnectionViewModel method. A per-method hook would miss every one of them.
    [Fact]
    public void AccessibleName_DirectRemoveFromOutside_StillRefreshesTheRemainingNames()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second);
        Assert.NotEqual(first.DisplayTitle, first.AccessibleName);

        sut.ActiveSessions.Remove(second);

        Assert.Equal(first.DisplayTitle, first.AccessibleName);
        Assert.Equal(1, sut.TrackedAccessibleNameSubscriptionCount);
    }

    [Fact]
    public void AccessibleName_DirectAddFromOutside_StillRefreshesTheNames()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first);
        Assert.Equal(first.DisplayTitle, first.AccessibleName);

        SessionTabViewModel returning = CreatePinnableSession("srv01", pinned: false);
        sut.ActiveSessions.Add(returning);

        Assert.NotEqual(first.AccessibleName, returning.AccessibleName);
        Assert.Equal(2, sut.TrackedAccessibleNameSubscriptionCount);
    }

    [Fact]
    public void AccessibleName_AfterReorder_OrdinalsFollowTheCurrentVisualOrder()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second);

        // Absolute values, not a comparison between the two names: a mutant that reverses the
        // assignment order consistently would keep any relative property intact, so only tying
        // the ordinal to the index discriminates.
        Assert.Equal("srv01 (1)", first.AccessibleName);
        Assert.Equal("srv01 (2)", second.AccessibleName);

        // Pinning moves the second tab ahead of the first, so the ordinals must swap with them.
        sut.SetPinned(second, pinned: true);

        Assert.Same(second, sut.ActiveSessions[0]);
        Assert.Equal("srv01 (1)", second.AccessibleName);
        Assert.Equal("srv01 (2)", first.AccessibleName);
    }

    [Fact]
    public void AccessibleName_AfterRemove_MutatingTheRemovedSessionDoesNotAffectTheSurvivors()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel kept = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel removed = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, kept, removed);

        sut.ActiveSessions.Remove(removed);
        string keptNameAfterRemoval = kept.AccessibleName;
        removed.Title = "srv01";

        Assert.Equal(keptNameAfterRemoval, kept.AccessibleName);
        Assert.Equal(1, sut.TrackedAccessibleNameSubscriptionCount);
    }

    // Primary Reset assertion. Clear() raises Reset with OldItems == null, so a handler that
    // unsubscribes only from e.OldItems leaks every session and passes every other test here.
    // The registry count is the only observable that discriminates: on an emptied collection a
    // leaked handler recomputes nothing and changes nothing.
    [Fact]
    public void AccessibleName_AfterClear_TheSubscriptionRegistryIsEmpty()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel third = CreatePinnableSession("srv02", pinned: true);
        AddTabs(sut, first, second, third);
        Assert.Equal(3, sut.TrackedAccessibleNameSubscriptionCount);

        sut.ActiveSessions.Clear();

        Assert.Equal(0, sut.TrackedAccessibleNameSubscriptionCount);
    }

    // Secondary control for the same case, kept deliberately: it passes whether or not the
    // handler leaked, so it is evidence of no crash, never proof of unsubscription.
    [Fact]
    public void AccessibleName_AfterClear_MutatingAClearedSessionThrowsNothing()
    {
        ConnectionViewModel sut = CreatePinningViewModel();
        SessionTabViewModel first = CreatePinnableSession("srv01", pinned: false);
        SessionTabViewModel second = CreatePinnableSession("srv01", pinned: false);
        AddTabs(sut, first, second);

        sut.ActiveSessions.Clear();
        first.Title = "renamed-after-clear";

        Assert.Empty(sut.ActiveSessions);
    }

    private static ConnectionViewModel CreatePinningViewModel()
        => CreateViewModel(new TrackingDialogService(false), new TrackingSplitService());

    private static SessionTabViewModel CreatePinnableSession(string title, bool pinned)
        => new() { Title = title, IsPinned = pinned };

    private static void AddTabs(ConnectionViewModel viewModel, params SessionTabViewModel[] sessions)
    {
        foreach (var session in sessions)
        {
            viewModel.ActiveSessions.Add(session);
        }

        viewModel.ActiveSession = sessions.LastOrDefault();
        viewModel.HasActiveSessions = sessions.Length > 0;
    }

    private static ConnectionViewModel CreateViewModel(
        TrackingDialogService dialogService,
        TrackingSplitService splitService,
        LocalizationManager? localizer = null)
    {
        return new ConnectionViewModel(
            localizer ?? new LocalizationManager(),
            dialogService,
            splitService);
    }

    private static void AddActiveSession(ConnectionViewModel viewModel, SessionTabViewModel session)
    {
        viewModel.ActiveSessions.Add(session);
        viewModel.ActiveSession = session;
        viewModel.HasActiveSessions = true;
    }

    private static SessionTabViewModel CreateSplitSession(string primaryStatus, string secondaryStatus)
    {
        SessionPaneModel primary = new()
        {
            PaneId = "primary",
            Status = primaryStatus,
            Title = "Primary"
        };
        SessionPaneModel secondary = new()
        {
            PaneId = "secondary",
            Status = secondaryStatus,
            Title = "Secondary"
        };

        return new SessionTabViewModel
        {
            Title = "Split",
            RootContent = new SplitContainerModel
            {
                First = primary,
                Second = secondary
            }
        };
    }

    private sealed class TrackingSplitService : ISplitService
    {
        public SplitLayoutMemory LayoutMemory { get; } = new(Path.GetTempPath());

        public bool CloseAllPanesResult { get; init; }

        public int CloseAllPanesCallCount { get; private set; }

        public SessionTabViewModel? LastClosedSession { get; private set; }

        public void RegisterSession(SessionTabViewModel session)
        {
            throw new NotSupportedException();
        }

        public void CancelSession(SessionTabViewModel session)
        {
            throw new NotSupportedException();
        }

        public Task SplitSessionWithServerAsync(
            SessionTabViewModel session,
            string serverId,
            SplitOrientation orientation,
            string? paneId = null)
        {
            throw new NotSupportedException();
        }

        public void SplitSessionWithTool(
            SessionTabViewModel session,
            string paletteToolPayload,
            SplitOrientation orientation,
            string? paneId = null)
        {
            throw new NotSupportedException();
        }

        public void MergeExistingSession(
            SessionTabViewModel target,
            string sourceSessionId,
            SplitOrientation orientation,
            string? targetPaneId = null)
        {
            throw new NotSupportedException();
        }

        public void ClosePane(
            SessionTabViewModel session,
            string paneId,
            DisconnectReason reason = DisconnectReason.UserAction)
        {
            throw new NotSupportedException();
        }

        public Task ReconnectPaneAsync(SessionTabViewModel session, string paneId)
        {
            throw new NotSupportedException();
        }

        public Task SwapSplitPanesAsync(SessionTabViewModel session, string? paneId = null)
        {
            throw new NotSupportedException();
        }

        public void ToggleSplitOrientation(SessionTabViewModel session, string? paneId = null)
        {
            throw new NotSupportedException();
        }

        public void CleanupOrphanedPane(string serverId)
        {
            throw new NotSupportedException();
        }

        public bool CloseAllPanes(
            SessionTabViewModel session,
            DisconnectReason reason = DisconnectReason.UserAction)
        {
            CloseAllPanesCallCount++;
            LastClosedSession = session;
            return CloseAllPanesResult;
        }
    }

    private sealed class TrackingDialogService(bool confirmResult) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCallCount++;
            LastConfirmMessage = message;
            return Task.FromResult(confirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
        {
            throw new NotSupportedException();
        }

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
        {
            throw new NotSupportedException();
        }

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
        {
            throw new NotSupportedException();
        }

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
        {
            throw new NotSupportedException();
        }

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
        {
            throw new NotSupportedException();
        }

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
        {
            throw new NotSupportedException();
        }

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
        {
            throw new NotSupportedException();
        }

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
        {
            throw new NotSupportedException();
        }

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
        {
            throw new NotSupportedException();
        }

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
        {
            throw new NotSupportedException();
        }

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
        {
            throw new NotSupportedException();
        }

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
        {
            throw new NotSupportedException();
        }

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
        {
            throw new NotSupportedException();
        }

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void ShowError(string title, string message)
        {
            throw new NotSupportedException();
        }

        public void ShowInfo(string title, string message)
        {
            throw new NotSupportedException();
        }

        public void ShowWarning(string title, string message)
        {
            throw new NotSupportedException();
        }
    }
}
