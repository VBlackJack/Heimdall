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
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task AutoReconnect_FirstFailure_SchedulesSecondAttemptWithSecondDelay()
    {
        using TestHarness harness = TestHarness.Create();
        ManualReconnectDelayScheduler scheduler = new ManualReconnectDelayScheduler();
        harness.Main.Session.ReconnectDelayAsync = scheduler.DelayAsync;
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel source = AddReconnectSource(harness, server);
        ControlledProtocolHandler firstHandler = harness.GetHandler("SSH");

        RaiseAutomaticReconnect(harness, source, attempt: 1, maxAttempts: 3);

        await firstHandler.Started.Task.WaitAsync(TestTimeout);
        firstHandler.Result.SetResult(new ConnectionResult(false, "first failure", null));
        ScheduledReconnectDelay secondDelay = await scheduler.TakeAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), secondDelay.Delay);

        harness.ResetHandler("SSH");
        ControlledProtocolHandler secondHandler = harness.GetHandler("SSH");
        secondDelay.Release();
        await secondHandler.Started.Task.WaitAsync(TestTimeout);
        secondHandler.Result.SetResult(SuccessWithTerminalSession());

        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
        Assert.Equal(0, harness.Main.Session.ActiveReconnectChainCount);
        Assert.Equal(1, scheduler.ScheduledCount);
    }

    [Fact]
    public async Task AutoReconnect_MaxAttemptsReached_DoesNotScheduleAnotherAttempt()
    {
        using TestHarness harness = TestHarness.Create();
        ManualReconnectDelayScheduler scheduler = new ManualReconnectDelayScheduler();
        harness.Main.Session.ReconnectDelayAsync = scheduler.DelayAsync;
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel source = AddReconnectSource(harness, server);
        ControlledProtocolHandler firstHandler = harness.GetHandler("SSH");

        RaiseAutomaticReconnect(harness, source, attempt: 1, maxAttempts: 2);

        await firstHandler.Started.Task.WaitAsync(TestTimeout);
        firstHandler.Result.SetResult(new ConnectionResult(false, "first failure", null));
        ScheduledReconnectDelay secondDelay = await scheduler.TakeAsync();

        harness.ResetHandler("SSH");
        ControlledProtocolHandler secondHandler = harness.GetHandler("SSH");
        secondDelay.Release();
        await secondHandler.Started.Task.WaitAsync(TestTimeout);
        secondHandler.Result.SetResult(new ConnectionResult(false, "second failure", null));

        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
        Assert.Equal(1, scheduler.ScheduledCount);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task AutoReconnect_ThreeAttempts_UsesFirstSecondAndSubsequentDelays()
    {
        using TestHarness harness = TestHarness.Create();
        ManualReconnectDelayScheduler scheduler = new ManualReconnectDelayScheduler();
        harness.Main.Session.ReconnectDelayAsync = scheduler.DelayAsync;
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel source = AddReconnectSource(harness, server);
        ControlledProtocolHandler firstHandler = harness.GetHandler("SSH");

        Assert.Equal(2, EmbeddedSshView.ComputeAutoReconnectDelaySeconds(null, attempt: 1));
        RaiseAutomaticReconnect(harness, source, attempt: 1, maxAttempts: 3);

        await firstHandler.Started.Task.WaitAsync(TestTimeout);
        firstHandler.Result.SetResult(new ConnectionResult(false, "first failure", null));
        ScheduledReconnectDelay secondDelay = await scheduler.TakeAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), secondDelay.Delay);

        harness.ResetHandler("SSH");
        ControlledProtocolHandler secondHandler = harness.GetHandler("SSH");
        secondDelay.Release();
        await secondHandler.Started.Task.WaitAsync(TestTimeout);
        secondHandler.Result.SetResult(new ConnectionResult(false, "second failure", null));
        ScheduledReconnectDelay thirdDelay = await scheduler.TakeAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), thirdDelay.Delay);

        harness.ResetHandler("SSH");
        ControlledProtocolHandler thirdHandler = harness.GetHandler("SSH");
        thirdDelay.Release();
        await thirdHandler.Started.Task.WaitAsync(TestTimeout);
        thirdHandler.Result.SetResult(SuccessWithTerminalSession());

        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
        Assert.Equal(0, harness.Main.Session.ActiveReconnectChainCount);
        Assert.Equal(2, scheduler.ScheduledCount);
    }

    [Fact]
    public async Task AutoReconnect_UserClosesConnectingPlaceholder_CancelsChain()
    {
        using TestHarness harness = TestHarness.Create();
        ManualReconnectDelayScheduler scheduler = new ManualReconnectDelayScheduler();
        harness.Main.Session.ReconnectDelayAsync = scheduler.DelayAsync;
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel source = AddReconnectSource(harness, server);
        ControlledProtocolHandler handler = harness.GetHandler("SSH");

        RaiseAutomaticReconnect(harness, source, attempt: 1, maxAttempts: 3);

        CancellationToken token = await handler.Started.Task.WaitAsync(TestTimeout);
        SessionTabViewModel placeholder = Assert.Single(harness.Main.Connection.ActiveSessions);
        await harness.Main.Connection.CloseSessionAsync(
            placeholder,
            DisconnectReason.UserAction,
            confirm: false);

        await WaitUntilAsync(() => token.IsCancellationRequested);
        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
        Assert.Equal(0, scheduler.ScheduledCount);
    }

    [Fact]
    public async Task AutoReconnect_DeferredByVault_ResumesWithoutDoubleCounting()
    {
        using TestHarness harness = TestHarness.Create();
        ManualReconnectDelayScheduler scheduler = new ManualReconnectDelayScheduler();
        harness.Main.Session.ReconnectDelayAsync = scheduler.DelayAsync;
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel source = AddReconnectSource(harness, server);
        ControlledProtocolHandler firstHandler = harness.GetHandler("SSH");
        harness.Main.IsWorkspaceLocked = true;

        RaiseAutomaticReconnect(harness, source, attempt: 1, maxAttempts: 2);

        Assert.False(firstHandler.Started.Task.IsCompleted);
        Assert.Contains(source, harness.Main.Connection.ActiveSessions);
        harness.Main.IsWorkspaceLocked = false;
        harness.Main.Session.ResumeDeferredReconnects();

        await firstHandler.Started.Task.WaitAsync(TestTimeout);
        firstHandler.Result.SetResult(new ConnectionResult(false, "first failure", null));
        ScheduledReconnectDelay secondDelay = await scheduler.TakeAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), secondDelay.Delay);

        harness.ResetHandler("SSH");
        ControlledProtocolHandler secondHandler = harness.GetHandler("SSH");
        secondDelay.Release();
        await secondHandler.Started.Task.WaitAsync(TestTimeout);
        secondHandler.Result.SetResult(SuccessWithTerminalSession());

        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
        Assert.Equal(1, scheduler.ScheduledCount);
    }

    [Fact]
    public void ReconnectAttemptSeed_IsAppliedToReplacementView()
    {
        SessionTabViewModel tab = new SessionTabViewModel
        {
            ServerId = "runtime-reconnect",
            OriginalServerId = "server-ssh",
            ConnectionType = "SSH"
        };
        EmbeddedSshView view = (EmbeddedSshView)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(EmbeddedSshView));

        EmbeddedSessionManager.QueueReconnectAttempt(tab, attempt: 2);
        EmbeddedSessionManager.ApplyQueuedReconnectAttempt(view, tab);

        Assert.Equal(2, view.AutoReconnectAttempt);
    }

    [Fact]
    public async Task ReconnectSession_Ssh_RemovesOldTabBeforeNewConnect()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        sshHandler.Result.SetResult(SuccessWithTerminalSession());

        // Establish first session and wait for tab to be present
        BulkConnectOutcome firstOutcome = await harness.RunPipelineAsync(server, "session-ssh-first").WaitAsync(TestTimeout);
        Assert.Equal(BulkConnectOutcomeStatus.Success, firstOutcome.Status);
        SessionTabViewModel oldTab = Assert.Single(harness.Main.Connection.ActiveSessions);

        // Reset the handler so the reconnect attempt can be observed independently
        harness.ResetHandler("SSH");

        // Trigger reconnect via the same entry point as the tab context menu
        harness.Main.Session.ReconnectSession(oldTab);

        // The old tab must be removed synchronously (Close is sync via CloseSessionInternal)
        await WaitUntilAsync(() => !harness.Main.Connection.ActiveSessions.Contains(oldTab));

        Assert.DoesNotContain(oldTab, harness.Main.Connection.ActiveSessions);

        CancellationToken reconnectToken = await sshHandler.Started.Task.WaitAsync(TestTimeout);
        Assert.False(reconnectToken.IsCancellationRequested);
        sshHandler.Result.SetResult(SuccessWithTerminalSession());
        await WaitUntilAsync(() => harness.EmbeddedSessionManager.AttachSshSessionCalls == 2);
    }

    [Fact]
    public void ReconnectSession_NullTab_DoesNothing()
    {
        using TestHarness harness = TestHarness.Create();
        string initialStatus = harness.Main.StatusText;

        harness.Main.Session.ReconnectSession(null);

        Assert.Empty(harness.Main.Connection.ActiveSessions);
        Assert.Equal(initialStatus, harness.Main.StatusText);
    }

    [Fact]
    public void ReconnectSession_TabWithEmptyServerId_DoesNothing()
    {
        using TestHarness harness = TestHarness.Create();
        LocalizationManager localizer = harness.Main.GetLocalizer();
        SessionTabViewModel bareTab = new SessionTabViewModel
        {
            Title = "Bare",
            ConnectionType = "SSH"
        };

        harness.Main.Session.ReconnectSession(bareTab);

        Assert.Empty(harness.Main.Connection.ActiveSessions);
        Assert.Equal(localizer["StatusReady"], harness.Main.StatusText);
    }

    [Fact]
    public async Task ReconnectSession_FallsBackToServerId_WhenOriginalServerIdIsEmpty()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        SessionTabViewModel tab = new SessionTabViewModel
        {
            ServerId = server.Id,
            OriginalServerId = "",
            ConnectionType = "SSH",
            Title = "fallback"
        };

        harness.Main.Session.ReconnectSession(tab);

        CancellationToken token = await sshHandler.Started.Task.WaitAsync(TestTimeout);
        Assert.False(token.IsCancellationRequested);

        sshHandler.Result.SetResult(SuccessWithTerminalSession());
        await WaitUntilAsync(() => harness.EmbeddedSessionManager.AttachSshSessionCalls == 1);
    }

    // A tab opened as "force embedded" over a profile whose mode is External reconnected from
    // the profile: the overlay's Reconnect launched the external client and closed the embedded
    // tab, under a title that still carried the forced suffix.
    [Fact]
    public async Task ReconnectSession_KeepsTheForcedRdpMode()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler rdpHandler = harness.GetHandler("RDP");
        ServerProfileDto server = harness.CreateServer("RDP");
        server.RdpMode = "External";
        await harness.PersistServerAsync(server);
        SessionTabViewModel tab = new SessionTabViewModel
        {
            ServerId = server.Id,
            OriginalServerId = server.Id,
            ConnectionType = "RDP",
            Title = "forced",
            RdpModeOverride = RdpModeOverride.ForceEmbedded
        };

        harness.Main.Session.ReconnectSession(tab);

        await rdpHandler.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal(RdpModeOverride.ForceEmbedded, rdpHandler.LastRdpModeOverride);
        rdpHandler.Result.SetResult(new ConnectionResult(false, "stopped by the test", null));
    }

    [Fact]
    public async Task ReconnectSession_ServerMissingFromInventory_SetsServerNotFoundStatus()
    {
        using TestHarness harness = TestHarness.Create();
        LocalizationManager localizer = harness.Main.GetLocalizer();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        sshHandler.Result.SetResult(SuccessWithTerminalSession());

        BulkConnectOutcome firstOutcome = await harness.RunPipelineAsync(
            server,
            "session-ssh-first").WaitAsync(TestTimeout);
        Assert.Equal(BulkConnectOutcomeStatus.Success, firstOutcome.Status);
        SessionTabViewModel oldTab = Assert.Single(harness.Main.Connection.ActiveSessions);

        harness.Main.Session.ReconnectSession(oldTab);

        await WaitUntilAsync(() => harness.Main.StatusText == localizer["ErrorServerNotFound"]);
        Assert.Equal(localizer["ErrorServerNotFound"], harness.Main.StatusText);
        Assert.DoesNotContain(oldTab, harness.Main.Connection.ActiveSessions);
    }

    [Fact]
    public async Task ReconnectSession_ServerPresent_StartsNewConnection()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        sshHandler.Result.SetResult(SuccessWithTerminalSession());

        BulkConnectOutcome firstOutcome = await harness.RunPipelineAsync(
            server,
            "session-ssh-first").WaitAsync(TestTimeout);
        Assert.Equal(BulkConnectOutcomeStatus.Success, firstOutcome.Status);
        SessionTabViewModel oldTab = Assert.Single(harness.Main.Connection.ActiveSessions);
        harness.ResetHandler("SSH");
        ControlledProtocolHandler reconnectHandler = harness.GetHandler("SSH");

        harness.Main.Session.ReconnectSession(oldTab);

        CancellationToken newConnectToken = await reconnectHandler.Started.Task.WaitAsync(TestTimeout);
        Assert.False(newConnectToken.IsCancellationRequested);
        await WaitUntilAsync(() => !harness.Main.Connection.ActiveSessions.Contains(oldTab));
        Assert.DoesNotContain(oldTab, harness.Main.Connection.ActiveSessions);

        reconnectHandler.Result.SetResult(SuccessWithTerminalSession());
        await WaitUntilAsync(() => harness.EmbeddedSessionManager.AttachSshSessionCalls == 2);
        Assert.Equal(0, harness.Main.Session.ActiveReconnectChainCount);
    }

    [Fact]
    public async Task ReconnectSession_AdHoc_RuntimeCopyKeepsTheLegacySshCredentialMapping()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler protocolHandler = harness.GetHandler("SSH");
        ServerProfileDto snapshot = harness.CreateServer("SSH");
        snapshot.Id = "adhoc-ssh-demo.example.com";
        snapshot.SshKeyPath = "/home/ops/id_ed25519";
        snapshot.SshPasswordEncrypted = "ssh-secret";

        // SshKeyPassphraseEncrypted is deliberately never assigned. That absence is what makes the
        // stored password be offered as the key passphrase, and assigning the field - even to null -
        // would raise its presence flag and turn the mapping off.
        Assert.True(snapshot.UsesLegacySshCredentialMapping);

        SessionTabViewModel source = harness.Main.Connection.AddSession(
            snapshot.Id,
            snapshot.DisplayName,
            snapshot.ConnectionType);
        source.MarkAsAdHoc(snapshot);

        harness.Main.Session.ReconnectSession(source);
        await protocolHandler.Started.Task.WaitAsync(TestTimeout);

        ServerProfileDto runtime = Assert.IsType<ServerProfileDto>(protocolHandler.LastServer);

        // The runtime copy is what the handler authenticates with, and unlike a duplicated or
        // imported profile it is never persisted, so nothing later repairs a flag the copy invented.
        // The JSON round-trip this replaced raised the passphrase presence flag on every copy, so an
        // ad-hoc session that connected once could fail to reconnect.
        Assert.NotSame(snapshot, runtime);
        Assert.False(runtime.HasSshKeyPassphraseEncryptedField);
        Assert.True(runtime.UsesLegacySshCredentialMapping);
        Assert.Equal("/home/ops/id_ed25519", runtime.SshKeyPath);
        Assert.Equal("ssh-secret", runtime.SshPasswordEncrypted);

        protocolHandler.Result.SetResult(SuccessWithTerminalSession());
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("RDP")]
    public async Task ReconnectSession_AdHoc_UsesSnapshotWithoutInventoryLookup(string protocol)
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler protocolHandler = harness.GetHandler(protocol);
        ServerProfileDto snapshot = harness.CreateServer(protocol);
        string expectedSnapshotId = $"adhoc-{protocol.ToLowerInvariant()}-demo.example.com";
        snapshot.Id = expectedSnapshotId;
        SessionTabViewModel source = harness.Main.Connection.AddSession(
            snapshot.Id,
            snapshot.DisplayName,
            snapshot.ConnectionType);
        source.MarkAsAdHoc(snapshot);

        TaskCompletionSource<bool> serverNotFound = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(MainViewModel.StatusText), StringComparison.Ordinal)
                && string.Equals(
                    harness.Main.StatusText,
                    harness.Main.GetLocalizer()["ErrorServerNotFound"],
                    StringComparison.Ordinal))
            {
                serverNotFound.TrySetResult(true);
            }
        };

        TaskCompletionSource<SessionTabViewModel> replacementAdded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Main.Connection.ActiveSessions.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is null)
            {
                return;
            }

            foreach (object item in args.NewItems)
            {
                if (item is SessionTabViewModel added && !ReferenceEquals(added, source))
                {
                    added.PropertyChanged += (_, changeArgs) =>
                    {
                        if (string.Equals(
                                changeArgs.PropertyName,
                                nameof(SessionTabViewModel.AdHocProfileSnapshot),
                                StringComparison.Ordinal)
                            && added.IsAdHoc)
                        {
                            replacementAdded.TrySetResult(added);
                        }
                    };
                }
            }
        };

        harness.Main.Session.ReconnectSession(source);

        Task firstOutcome = await Task.WhenAny(protocolHandler.Started.Task, serverNotFound.Task)
            .WaitAsync(TestTimeout);
        Assert.Same(protocolHandler.Started.Task, firstOutcome);
        Assert.DoesNotContain(source, harness.Main.Connection.ActiveSessions);
        Assert.Equal(expectedSnapshotId, snapshot.Id);

        protocolHandler.Result.SetResult(SuccessWithTerminalSession());
        SessionTabViewModel replacement = await replacementAdded.Task.WaitAsync(TestTimeout);

        Assert.True(replacement.IsAdHoc);
        Assert.Same(snapshot, replacement.AdHocProfileSnapshot);
        Assert.NotEqual(snapshot.Id, replacement.ServerId);
        Assert.Equal(expectedSnapshotId, snapshot.Id);
    }

    [Fact]
    public async Task SftpPaneReconnectCallback_ReconnectsPaneWithoutClosingSshTab()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ControlledProtocolHandler sftpHandler = harness.GetHandler("SFTP");
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);

        SessionTabViewModel tab = harness.Main.Connection.AddSession("ssh-session", server.DisplayName, "SSH");
        tab.OriginalServerId = server.Id;
        SessionPaneModel sshPane = tab.PrimaryPane;
        sshPane.ServerId = "ssh-session";
        sshPane.OriginalServerId = server.Id;
        sshPane.ConnectionType = "SSH";
        sshPane.Title = "SSH";
        sshPane.HostControl = new object();

        SessionPaneModel sftpPane = new()
        {
            PaneId = "sftp-pane",
            ServerId = "sftp-session",
            OriginalServerId = server.Id,
            ConnectionType = "SFTP",
            Title = "SFTP",
            HostControl = new object()
        };
        tab.RootContent = new SplitContainerModel
        {
            First = sshPane,
            Second = sftpPane,
            Orientation = SplitOrientation.Vertical
        };

        harness.EmbeddedSessionManager.ReconnectPaneRequestedCallback?.Invoke(tab, sftpPane);

        CancellationToken paneReconnectToken = await sftpHandler.Started.Task.WaitAsync(TestTimeout);
        Assert.False(paneReconnectToken.IsCancellationRequested);
        Assert.False(sshHandler.Started.Task.IsCompleted);
        Assert.Contains(tab, harness.Main.Connection.ActiveSessions);
        Assert.Single(harness.Main.Connection.ActiveSessions);

        sftpHandler.Result.SetResult(SuccessWithTerminalSession());
        await WaitUntilAsync(() => sftpPane.Status == "Connected");

        Assert.Contains(tab, harness.Main.Connection.ActiveSessions);
        Assert.Same(sshPane, ((SplitContainerModel)tab.RootContent).First);
    }

    private static SessionTabViewModel AddReconnectSource(TestHarness harness, ServerProfileDto server)
    {
        SessionTabViewModel source = harness.Main.Connection.AddSession(
            "source-runtime",
            server.DisplayName,
            "SSH");
        source.OriginalServerId = server.Id;
        return source;
    }

    private static void RaiseAutomaticReconnect(
        TestHarness harness,
        SessionTabViewModel source,
        int attempt,
        int maxAttempts)
    {
        Action<SessionTabViewModel, string, string> callback = Assert.IsType<Action<SessionTabViewModel, string, string>>(
            harness.EmbeddedSessionManager.ReconnectRequestedCallback);
        EmbeddedSessionManager.ForwardReconnectRequest(
            source,
            ReconnectRequestContext.Automatic(attempt, maxAttempts),
            callback);
    }

    private sealed class ManualReconnectDelayScheduler
    {
        private readonly Queue<ScheduledReconnectDelay> _pending = new Queue<ScheduledReconnectDelay>();
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);

        public int ScheduledCount { get; private set; }

        public int PendingCount
        {
            get
            {
                lock (_pending)
                {
                    return _pending.Count;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            ScheduledReconnectDelay scheduled = new ScheduledReconnectDelay(delay, cancellationToken);
            lock (_pending)
            {
                _pending.Enqueue(scheduled);
                ScheduledCount++;
            }

            _available.Release();
            return scheduled.Task;
        }

        public async Task<ScheduledReconnectDelay> TakeAsync()
        {
            bool available = await _available.WaitAsync(TestTimeout);
            Assert.True(available, "Reconnect delay was not scheduled before the test timeout.");
            lock (_pending)
            {
                return _pending.Dequeue();
            }
        }
    }

    private sealed class ScheduledReconnectDelay
    {
        private readonly TaskCompletionSource _completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public ScheduledReconnectDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay = delay;
            _registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                _completion);
        }

        public TimeSpan Delay { get; }

        public Task Task => _completion.Task;

        public void Release()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }
}
