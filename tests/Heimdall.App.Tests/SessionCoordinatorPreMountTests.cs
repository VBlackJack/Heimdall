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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Session;
using Heimdall.Core.Configuration;
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task RunConnectionPipelineAsync_Ssh_MountsTabBeforeConnectCompletes()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");

        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, "session-ssh-connecting");
        await sshHandler.Started.Task.WaitAsync(TestTimeout);

        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal("session-ssh-connecting", tab.ServerId);
        Assert.Equal("Connecting", tab.Status);
        Assert.Equal(1, harness.EmbeddedSessionManager.CreateConnectingSshHostControlCalls);
        Assert.False(pipeline.IsCompleted);

        sshHandler.Result.SetResult(SuccessWithTerminalSession());
        BulkConnectOutcome outcome = await pipeline.WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task RunConnectionPipelineAsync_NonSsh_DoesNotPreMountTabBeforeConnectCompletes()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler rdpHandler = harness.GetHandler("RDP");
        ServerProfileDto server = harness.CreateServer("RDP");

        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, "session-rdp");
        await rdpHandler.Started.Task.WaitAsync(TestTimeout);

        Assert.Empty(harness.Main.Connection.ActiveSessions);
        Assert.Equal(0, harness.EmbeddedSessionManager.CreateConnectingSshHostControlCalls);
        Assert.False(pipeline.IsCompleted);

        rdpHandler.Result.SetResult(new ConnectionResult(
            true,
            null,
            new RdpSessionResult(server)));
        BulkConnectOutcome outcome = await pipeline.WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task RunConnectionPipelineAsync_PreflightFailureDoesNotCreateStateEntry()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        server.UseDirectConnection = false;
        server.SshGatewayId = "missing-gateway";

        BulkConnectOutcome outcome = await harness.RunPipelineAsync(
            server,
            "session-preflight-failed").WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.PreflightFailed, outcome.Status);
        Assert.Null(harness.StateMachine.GetStateData("session-preflight-failed"));
    }

    [Fact]
    public async Task RunConnectionPipelineAsync_RdpForcedExternalCreatesLightweightTabWithSuffix()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler rdpHandler = harness.GetHandler("RDP");
        ServerProfileDto server = harness.CreateServer("RDP");
        rdpHandler.Result.SetResult(new ConnectionResult(true, null, null));

        BulkConnectOutcome outcome = await harness.RunPipelineAsync(
            server,
            "session-rdp-forced-external",
            RdpModeOverride.ForceExternal).WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.Success, outcome.Status);
        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal(RdpModeOverride.ForceExternal, tab.RdpModeOverride);
        Assert.Equal("Demo RDP (forced external)", tab.DisplayTitle);
        Assert.Equal("External client launched", tab.Status);
        Assert.Null(harness.StateMachine.GetStateData("session-rdp-forced-external"));
    }

    [Fact]
    public async Task RunConnectionPipelineAsync_SshFailure_RemovesPlaceholderTab()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        sshHandler.Result.SetResult(new ConnectionResult(false, "connection refused", null));

        BulkConnectOutcome outcome = await harness.RunPipelineAsync(server, "session-ssh-failed").WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.ConnectionFailed, outcome.Status);
        Assert.Empty(harness.Main.Connection.ActiveSessions);
        Assert.Equal(1, harness.EmbeddedSessionManager.CreateConnectingSshHostControlCalls);
        Assert.Null(harness.StateMachine.GetStateData("session-ssh-failed"));
    }

    [Fact]
    public async Task RunConnectionPipelineAsync_ExceptionRemovesStateEntry()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        sshHandler.Result.SetException(new InvalidOperationException("connect failed"));

        BulkConnectOutcome outcome = await harness.RunPipelineAsync(
            server,
            "session-ssh-exception").WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.ConnectionFailed, outcome.Status);
        Assert.Null(harness.StateMachine.GetStateData("session-ssh-exception"));
    }

    [Fact]
    public async Task RunConnectionPipelineAsync_SshReady_AttachesExistingTabWithoutAddingSecondTab()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");
        sshHandler.Result.SetResult(SuccessWithTerminalSession());

        BulkConnectOutcome outcome = await harness.RunPipelineAsync(server, "session-ssh-ready").WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.Success, outcome.Status);
        Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal(1, harness.EmbeddedSessionManager.CreateConnectingSshHostControlCalls);
        Assert.Equal(1, harness.EmbeddedSessionManager.AttachSshSessionCalls);
        Assert.Equal(0, harness.EmbeddedSessionManager.CreateHostControlCalls);
        Assert.NotNull(harness.StateMachine.GetStateData("session-ssh-ready"));
    }

    [Fact]
    public async Task CloseSessionCommand_WhileSshConnecting_CancelsPipelineToken()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sshHandler = harness.GetHandler("SSH");
        ServerProfileDto server = harness.CreateServer("SSH");

        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, "session-ssh-cancel");
        CancellationToken connectToken = await sshHandler.Started.Task.WaitAsync(TestTimeout);
        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);

        await harness.Main.Connection.CloseSessionCommand.ExecuteAsync(tab);

        await WaitUntilAsync(() => connectToken.IsCancellationRequested);
        BulkConnectOutcome outcome = await pipeline.WaitAsync(TestTimeout);

        Assert.Equal(BulkConnectOutcomeStatus.Cancelled, outcome.Status);
        Assert.Empty(harness.Main.Connection.ActiveSessions);
        Assert.Null(harness.StateMachine.GetStateData("session-ssh-cancel"));
    }

    [Fact]
    public void OnSessionFailed_WhenRaisedOffUiThread_MarshalsThroughDispatcher()
    {
        using TestHarness harness = TestHarness.Create(checkAccess: false);
        SessionDiagnostic diagnostic = new(
            SessionFailureStage.SshAuth,
            "ErrorSshAuthRejected",
            7,
            "Access denied");
        MethodInfo method = typeof(SessionCoordinator).GetMethod(
            "OnSessionFailed",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(
            harness.Main.Session,
            [
                "session-ssh-failed",
                "server-ssh",
                "Demo SSH",
                "SSH",
                "Access denied",
                diagnostic
            ]);

        Assert.Equal(1, harness.Dispatcher.InvokeCalls);
        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal("session-ssh-failed", tab.ServerId);
        Assert.Equal("server-ssh", tab.OriginalServerId);
        Assert.Equal("Access denied", tab.Status);
        Assert.Same(diagnostic, tab.FailureDetails);
        Assert.Equal("Access denied", harness.Main.StatusText);
    }

    [Fact]
    public async Task RestoreServerAsync_SftpFailureWithDiagnostic_RaisesFailedSession()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sftpHandler = harness.GetHandler("SFTP");
        ServerProfileDto server = harness.CreateServer("SFTP");
        await harness.PersistServerAsync(server);
        SessionDiagnostic diagnostic = new(
            SessionFailureStage.SshGateway,
            "ErrorConnectionFailed",
            null,
            "gateway refused");
        int failedSessionEvents = 0;
        (string SessionId, string OriginalId, string DisplayName, string ConnectionType, string StatusText, SessionDiagnostic Failure)? failed = null;
        harness.Main.ServerList.SessionFailed += (
            sessionId,
            originalId,
            displayName,
            connectionType,
            statusText,
            failure) =>
        {
            failedSessionEvents++;
            failed = (sessionId, originalId, displayName, connectionType, statusText, failure);
        };

        // Real producer: src/Heimdall.App/Services/Handlers/SftpHandler.cs:101 and :166
        // now attach SFTP tunnel/generic diagnostics to ConnectionResult.Failure.
        sftpHandler.Result.SetResult(new ConnectionResult(false, "gateway refused", null, diagnostic));

        bool restored = await harness.Main.ServerList
            .RestoreServerAsync(server.Id, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.False(restored);
        Assert.Equal(1, failedSessionEvents);
        Assert.NotNull(failed);
        Assert.False(string.IsNullOrWhiteSpace(failed.Value.SessionId));
        Assert.Equal(server.Id, failed.Value.OriginalId);
        Assert.Equal("Demo SFTP", failed.Value.DisplayName);
        Assert.Equal("SFTP", failed.Value.ConnectionType);
        Assert.Equal("gateway refused", failed.Value.StatusText);
        Assert.Same(diagnostic, failed.Value.Failure);
        Assert.Equal(1, harness.DialogService.ErrorCallCount);

        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal("SFTP", tab.ConnectionType);
        Assert.Equal(server.Id, tab.OriginalServerId);
        Assert.Equal("gateway refused", tab.Status);
        Assert.Same(diagnostic, tab.FailureDetails);
    }

    [Fact]
    public async Task RestoreServerAsync_SftpFailureWithoutDiagnostic_SuppressesFailedSession()
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler sftpHandler = harness.GetHandler("SFTP");
        ServerProfileDto server = harness.CreateServer("SFTP");
        await harness.PersistServerAsync(server);
        int failedSessionEvents = 0;
        harness.Main.ServerList.SessionFailed += (_, _, _, _, _, _) => failedSessionEvents++;

        sftpHandler.Result.SetResult(new ConnectionResult(false, "plain failure", null));

        bool restored = await harness.Main.ServerList
            .RestoreServerAsync(server.Id, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.False(restored);
        Assert.Equal(0, failedSessionEvents);
        Assert.Equal(1, harness.DialogService.ErrorCallCount);
        Assert.Empty(harness.Main.Connection.ActiveSessions);
    }
}
