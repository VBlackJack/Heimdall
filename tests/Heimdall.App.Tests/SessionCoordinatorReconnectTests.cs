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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
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
}
