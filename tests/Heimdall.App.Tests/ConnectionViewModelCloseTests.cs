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
        TrackingSplitService splitService)
    {
        LocalizationManager localizer = new();
        return new ConnectionViewModel(localizer, dialogService, splitService);
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

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCallCount++;
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
