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
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The tab of an SSH session that opens an SFTP companion is laid out before the connect, so the
/// terminal page measures the width it will keep. A Plink launch fixes the remote PTY width for
/// good: a terminal that opened full width and was halved afterwards would wrap the remote at twice
/// the width it shows.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string CompanionSessionId = "companion-layout-session";
    private const int ReplacedTabColumns = 132;
    private const int ReplacedTabRows = 41;
    private static readonly TimeSpan HintWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NoConnectGrace = TimeSpan.FromMilliseconds(300);

    private static AppSettings CompanionSettings(bool autoOpen = true) =>
        new() { SftpBrowserEnabled = true, SftpAutoOpenOnSsh = autoOpen };

    private static async Task<(Task<BulkConnectOutcome> Pipeline, ControlledProtocolHandler Ssh, SessionTabViewModel Tab)>
        StartSshWithCompanionSettingsAsync(
            TestHarness harness,
            ServerProfileDto server,
            AppSettings settings)
    {
        await harness.PersistServerAsync(server);
        await harness.Main.ReloadConfigurationAsync(settings);
        harness.Main.ServerList.RememberInventoryProfile(server, server.CloneFaithfully());
        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, CompanionSessionId);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        await ssh.Started.Task.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => harness.Main.Connection.ActiveSessions
            .Any(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal)));
        SessionTabViewModel tab = harness.Main.Connection.ActiveSessions
            .Single(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal));
        return (pipeline, ssh, tab);
    }

    private static SessionPaneModel CompanionOf(SessionTabViewModel tab)
    {
        SplitContainerModel container = Assert.IsType<SplitContainerModel>(tab.RootContent);
        Assert.Same(tab.PrimaryPane, container.First);
        return Assert.IsType<SessionPaneModel>(container.Second);
    }

    [Fact]
    public async Task SshWithCompanion_TabIsSplitBeforeTheConnect()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        string profileId = server.Id;

        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());

        // The connect has not completed: the layout is already the final one.
        SplitContainerModel container = Assert.IsType<SplitContainerModel>(tab.RootContent);
        SessionPaneModel companion = CompanionOf(tab);
        Assert.Equal(0.5, container.SplitRatio);
        Assert.Equal("SFTP", companion.ConnectionType);
        Assert.Equal(profileId, companion.OriginalServerId);
        Assert.Null(companion.HostControl);
        Assert.StartsWith("sftp-", companion.ServerId, StringComparison.Ordinal);

        ssh.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Placeholder_IsFilledByTheCompanion_NotWrappedAgain()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        ISplitContent rootBefore = tab.RootContent;
        SessionPaneModel companion = CompanionOf(tab);
        string companionKey = companion.ServerId;
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await sftp.Started.Task.WaitAsync(TestTimeout);
        Assert.Null(companion.HostControl);
        sftp.Result.SetResult(new ConnectionResult(true, null, new CompanionTestSession()));
        await WaitUntilAsync(() => companion.HostControl is not null);
        await pipeline.WaitAsync(TestTimeout);

        Assert.Same(rootBefore, tab.RootContent);
        Assert.Same(companion, CompanionOf(tab));
        Assert.Equal(companionKey, companion.ServerId);
        Assert.Equal(SessionStatusTokens.Connected, companion.Status);
    }

    [Theory]
    [InlineData(CompanionSkipReason.SettingTurnedOffBeforeReady)]
    public async Task CompanionSkipped_RemovesThePlaceholder(CompanionSkipReason reason)
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        SessionPaneModel companion = CompanionOf(tab);

        switch (reason)
        {
            case CompanionSkipReason.SettingTurnedOffBeforeReady:
                await harness.Main.ReloadConfigurationAsync(CompanionSettings(autoOpen: false));
                break;

        }

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await pipeline.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => tab.RootContent is SessionPaneModel);

        Assert.Same(tab.PrimaryPane, tab.RootContent);
        Assert.Null(SplitTreeHelper.FindPane(tab.RootContent, companion.PaneId));
    }

    [Fact]
    public async Task CompanionConnectFails_RemovesThePlaceholder()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        SessionPaneModel companion = CompanionOf(tab);
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await sftp.Started.Task.WaitAsync(TestTimeout);
        sftp.Result.SetResult(new ConnectionResult(false, "sftp refused", null));
        await pipeline.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => tab.RootContent is SessionPaneModel);

        Assert.Same(tab.PrimaryPane, tab.RootContent);
        Assert.Null(SplitTreeHelper.FindPane(tab.RootContent, companion.PaneId));
    }

    [Fact]
    public async Task SshConnectFails_RemovesThePlaceholder()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        SessionPaneModel companion = CompanionOf(tab);

        ssh.Result.SetResult(new ConnectionResult(false, "ssh refused", null));
        await pipeline.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => !harness.Main.Connection.ActiveSessions.Contains(tab));

        Assert.Null(SplitTreeHelper.FindPane(tab.RootContent, companion.PaneId));
    }

    [Fact]
    public async Task Placeholder_IsNotCountedNorPersistedNorReconnected()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        string profileId = server.Id;
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        SessionPaneModel companion = CompanionOf(tab);
        tab.HostControl = new object();

        // Counted: the terminal is the one connection of the tab.
        Assert.Equal(1, ConnectionViewModel.CountEmbeddedConnections([tab]));

        // Persisted: one entry for the tab, taken from its primary pane.
        IReadOnlyList<SessionSnapshotEntry> snapshot = SessionSnapshotProjection.FromSessions([tab]);
        Assert.Single(snapshot);
        Assert.Equal(profileId, snapshot[0].ServerId);
        Assert.Equal("SSH", snapshot[0].ConnectionType);

        // Reconnected: a pane still waiting for its host is already being connected.
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        await harness.Main.Split.ReconnectPaneAsync(tab, companion.PaneId);
        Assert.False(sftp.Started.Task.IsCompleted);
        Assert.Null(companion.HostControl);
        Assert.Equal(SessionStatusTokens.Connecting, companion.Status);

        ssh.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task NoCompanionProfile_RootIsTheTerminalAsBefore()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings(autoOpen: false));

        Assert.Same(tab.PrimaryPane, tab.RootContent);

        ssh.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task TabReconnect_WithCompanion_MeasuresTheSplitWidth()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        await harness.Main.ReloadConfigurationAsync(CompanionSettings());
        SessionTabViewModel source = harness.Main.Connection.AddSession("source-runtime", server.DisplayName, "SSH");
        source.OriginalServerId = server.Id;
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");

        harness.Main.Session.ReconnectSession(source);
        await ssh.Started.Task.WaitAsync(TestTimeout);

        // The reconnected tab is split before the connect, as a first connect is.
        SessionTabViewModel reconnected = harness.Main.Connection.ActiveSessions
            .Single(session => !ReferenceEquals(session, source));
        Assert.IsType<SplitContainerModel>(reconnected.RootContent);
        Assert.Null(CompanionOf(reconnected).HostControl);

        ssh.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await WaitUntilAsync(() => !harness.Main.Connection.ActiveSessions.Contains(reconnected));
    }

    [Fact]
    public async Task TabReconnect_PlinkTimeout_LaunchesAtReplacedTabSize()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel source = harness.Main.Connection.AddSession("source-runtime", server.DisplayName, "SSH");
        source.OriginalServerId = server.Id;
        source.HostControl = new SizedTerminalHost(new TerminalSize(ReplacedTabColumns, ReplacedTabRows));
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");

        harness.Main.Session.ReconnectSession(source);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        SessionTabViewModel reconnected = harness.Main.Connection.ActiveSessions
            .Single(session => !ReferenceEquals(session, source));

        // What the SSH handler's size lookup asks for this session while its page has not reported.
        TerminalSizeLookup lookup = await harness.Main.ServerList.ConnectionService.ResolveInitialTerminalSize!(
            reconnected.ServerId, HintWait, CancellationToken.None);

        Assert.Equal(TerminalSizeSource.PreviousView, lookup.Source);
        Assert.Equal(new TerminalSize(ReplacedTabColumns, ReplacedTabRows), lookup.Size);

        ssh.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await WaitUntilAsync(() => !harness.Main.Connection.ActiveSessions.Contains(reconnected));
    }

    /// <summary>
    /// The connect already read the inventory profile: the companion decision uses it as carried,
    /// with no second read. The inventory is emptied after the carried profile is noted, so a
    /// second read would find nothing and decide against the companion.
    /// </summary>
    [Fact]
    public async Task CompanionDecision_UsesTheInventoryProfileCarriedByTheConnect_NoSecondRead()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        await harness.Main.ReloadConfigurationAsync(CompanionSettings());
        harness.Main.ServerList.RememberInventoryProfile(server, server.CloneFaithfully());
        await harness.Main.ConfigManager.SaveServersAsync([]);
        harness.Main.ServerList.LoadServers([], CompanionSettings());
        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, CompanionSessionId);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        await ssh.Started.Task.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => harness.Main.Connection.ActiveSessions
            .Any(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal)));
        SessionTabViewModel tab = harness.Main.Connection.ActiveSessions
            .Single(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal));

        Assert.IsType<SplitContainerModel>(tab.RootContent);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await sftp.Started.Task.WaitAsync(TestTimeout);
        sftp.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CompanionDecisionFailure_DoesNotFailTheSshConnect()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        await harness.Main.ReloadConfigurationAsync(CompanionSettings());
        harness.Main.Session.CompanionProfileSource = _ => throw new InvalidOperationException("inventory unreadable");
        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, CompanionSessionId);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        await ssh.Started.Task.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => harness.Main.Connection.ActiveSessions
            .Any(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal)));
        SessionTabViewModel tab = harness.Main.Connection.ActiveSessions
            .Single(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal));

        Assert.Same(tab.PrimaryPane, tab.RootContent);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        BulkConnectOutcome outcome = await pipeline.WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.Success, outcome.Status);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
    }

    /// <summary>
    /// The companion deliberately uses the profile the connect started with, so the placeholder and
    /// the companion cannot disagree: an edit made while the session connects does not reach it.
    /// </summary>
    [Fact]
    public async Task ProfileEditedDuringTheConnect_CompanionUsesTheConnectTimeProfile()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        string connectTimeUser = server.SshUsername!;
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        ServerProfileDto edited = harness.CreateServer("SSH");
        edited.SshUsername = "edited-during-connect";
        await harness.PersistServerAsync(edited);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await sftp.Started.Task.WaitAsync(TestTimeout);

        Assert.Equal(connectTimeUser, sftp.LastServer!.SshUsername);
        sftp.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Merge_FromATabWithAPendingCompanion_IsRefused()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel source) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        source.HostControl = new object();
        SessionTabViewModel target = harness.Main.Connection.AddSession("merge-target", "Target", "SSH");
        target.HostControl = new object();
        ISplitContent sourceRoot = source.RootContent;
        ISplitContent targetRoot = target.RootContent;

        harness.Main.Split.MergeExistingSession(target, CompanionSessionId, SplitOrientation.Horizontal);

        Assert.Same(sourceRoot, source.RootContent);
        Assert.Same(targetRoot, target.RootContent);
        Assert.Contains(source, harness.Main.Connection.ActiveSessions);
        Assert.Equal(harness.Main.GetLocalizer()["SplitMergeBlockedByConnecting"], harness.Main.StatusText);

        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await sftp.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
        sftp.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Merge_FromAConnectingTab_IsRefused()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel source) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings(autoOpen: false));
        source.HostControl = new object();
        SessionTabViewModel target = harness.Main.Connection.AddSession("merge-target", "Target", "SSH");
        target.HostControl = new object();

        harness.Main.Split.MergeExistingSession(target, CompanionSessionId, SplitOrientation.Horizontal);

        Assert.Contains(source, harness.Main.Connection.ActiveSessions);
        Assert.Same(target.PrimaryPane, target.RootContent);
        Assert.Equal(harness.Main.GetLocalizer()["SplitMergeBlockedByConnecting"], harness.Main.StatusText);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await pipeline.WaitAsync(TestTimeout);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
        Assert.Equal(SessionStatusTokens.Connected, source.Status);
    }

    public enum CompanionSkipReason
    {
        SettingTurnedOffBeforeReady,
    }

    public enum PendingCompanionCommand
    {
        ToggleOrientation,
        SplitWithServer,
        Merge,
        Unsplit,
        DetachSecondary,
    }

    /// <summary>
    /// The credential vault writes the user name it found onto the connection copy, while the
    /// companion is built from the inventory profile and cannot open without a user name in it. The
    /// layout follows the companion, not the copy: with no inventory user name there is no
    /// placeholder, the terminal is full width from the start, and no companion is connected.
    /// </summary>
    [Fact]
    public async Task VaultProvidedUsername_PlaceholderAndCompanionAgree()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto inventoryEntry = harness.CreateServer("SSH");
        inventoryEntry.SshUsername = string.Empty;
        await harness.PersistServerAsync(inventoryEntry);
        await harness.Main.ReloadConfigurationAsync(CompanionSettings());
        ServerProfileDto connectionCopy = harness.CreateServer("SSH");
        connectionCopy.SshUsername = "vault-user";
        harness.Main.ServerList.RememberInventoryProfile(connectionCopy, inventoryEntry);
        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(connectionCopy, CompanionSessionId);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        await ssh.Started.Task.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => harness.Main.Connection.ActiveSessions
            .Any(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal)));
        SessionTabViewModel tab = harness.Main.Connection.ActiveSessions
            .Single(session => string.Equals(session.ServerId, CompanionSessionId, StringComparison.Ordinal));

        Assert.Same(tab.PrimaryPane, tab.RootContent);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await pipeline.WaitAsync(TestTimeout);
        await Task.Delay(NoConnectGrace);

        Assert.False(sftp.Started.Task.IsCompleted);
        Assert.Same(tab.PrimaryPane, tab.RootContent);
    }

    /// <summary>
    /// Swapping would make the placeholder the first pane, and the SSH session is found by the
    /// tab's first pane: the live session would be discarded as arriving after its tab closed.
    /// </summary>
    [Fact]
    public async Task PendingCompanion_SwapIsRefused_AndTheSessionStillAttaches()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        ISplitContent rootBefore = tab.RootContent;
        SessionPaneModel companion = CompanionOf(tab);
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        await harness.Main.Split.SwapSplitPanesAsync(tab);

        Assert.Same(rootBefore, tab.RootContent);
        Assert.Same(tab.PrimaryPane, Assert.IsType<SplitContainerModel>(tab.RootContent).First);
        Assert.Equal(harness.Main.GetLocalizer()["SplitBlockedByPendingCompanion"], harness.Main.StatusText);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await sftp.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
        sftp.Result.SetResult(new ConnectionResult(true, null, new CompanionTestSession()));
        await WaitUntilAsync(() => companion.HostControl is not null);
        await pipeline.WaitAsync(TestTimeout);
        Assert.False(companion.IsPendingCompanion);
    }

    [Theory]
    [InlineData(PendingCompanionCommand.ToggleOrientation)]
    [InlineData(PendingCompanionCommand.SplitWithServer)]
    [InlineData(PendingCompanionCommand.Merge)]
    [InlineData(PendingCompanionCommand.Unsplit)]
    [InlineData(PendingCompanionCommand.DetachSecondary)]
    public async Task PendingCompanion_SplitToggleMergeDetach_AreRefused(PendingCompanionCommand command)
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        ISplitContent rootBefore = tab.RootContent;
        SessionPaneModel companion = CompanionOf(tab);
        tab.HostControl = new object();
        harness.Main.StatusText = string.Empty;

        switch (command)
        {
            case PendingCompanionCommand.ToggleOrientation:
                harness.Main.Split.ToggleSplitOrientation(tab);
                break;
            case PendingCompanionCommand.SplitWithServer:
                await harness.Main.Split.SplitSessionWithServerAsync(tab, "server-ssh", SplitOrientation.Horizontal);
                break;
            case PendingCompanionCommand.Merge:
                harness.Main.Split.MergeExistingSession(tab, "another-session", SplitOrientation.Horizontal);
                break;
            case PendingCompanionCommand.Unsplit:
                new SessionWindowService().UnsplitSession(tab, harness.Main);
                break;
            case PendingCompanionCommand.DetachSecondary:
                new SessionWindowService().DetachSecondaryToFloatingWindow(tab, harness.Main);
                break;
        }

        Assert.Same(rootBefore, tab.RootContent);
        Assert.Equal(SplitOrientation.Vertical, Assert.IsType<SplitContainerModel>(tab.RootContent).Orientation);
        Assert.Same(companion, CompanionOf(tab));
        Assert.True(companion.IsPendingCompanion);
        Assert.Equal(harness.Main.GetLocalizer()["SplitBlockedByPendingCompanion"], harness.Main.StatusText);

        ssh.Result.SetResult(new ConnectionResult(false, "stop here", null));
        await pipeline.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task PlaceholderGone_CompanionDoesNotConnect()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        tab.SetRootContent(tab.PrimaryPane);

        ssh.Result.SetResult(SuccessWithTerminalSession());
        await pipeline.WaitAsync(TestTimeout);
        await Task.Delay(NoConnectGrace);

        Assert.False(sftp.Started.Task.IsCompleted);
        Assert.Same(tab.PrimaryPane, tab.RootContent);
    }

    /// <summary>
    /// A companion that never connected has no session to tear down: closing the tab during the
    /// connect leaves its state alone (and records no disconnect for it).
    /// </summary>
    [Fact]
    public async Task ClosingATabWhileConnecting_RecordsNoDisconnectForTheCompanion()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        (Task<BulkConnectOutcome> pipeline, ControlledProtocolHandler ssh, SessionTabViewModel tab) =
            await StartSshWithCompanionSettingsAsync(harness, server, CompanionSettings());
        SessionPaneModel companion = CompanionOf(tab);
        harness.StateMachine.TryTransition(companion.ServerId, ConnectionState.Initializing);

        await harness.Main.Connection.CloseSessionAsync(
            tab,
            DisconnectReason.UserAction,
            confirm: false,
            CloseIntent.Silent);

        Assert.Equal(ConnectionState.Initializing, harness.StateMachine.GetState(companion.ServerId));

        try
        {
            await pipeline.WaitAsync(TestTimeout);
        }
        catch (OperationCanceledException)
        {
            // The connect was cancelled with its tab.
        }
    }

    private sealed class CompanionTestSession : ISessionResult
    {
    }

    private sealed class SizedTerminalHost(TerminalSize size) : IDisposable, ITerminalSizeSource
    {
        public TerminalSize? LastKnownTerminalSize { get; } = size;

        public void Dispose()
        {
        }
    }
}
