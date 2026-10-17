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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// A pane reconnect runs again under the key its first connection used, after that connection's
/// state entry was torn down. The session tree row has to follow the new connection: it used to
/// stay on the state the first one ended in, because every change of the new entry looked older
/// than that end.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task SplitPaneReconnect_BringsTheRowBackToConnected()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto local = harness.CreateServer("SSH");
        await harness.PersistServerAsync(local);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");

        Task<bool> restore = harness.Main.ServerList.RestoreServerAsync(local.Id, CancellationToken.None);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        string tabSessionId = ssh.LastServer!.Id;
        WalkHandlerToConnected(harness, tabSessionId, ConnectionState.LaunchingSsh);
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await restore.WaitAsync(TestTimeout);
        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);

        ssh.Reset();
        Task split = harness.Main.Split.SplitSessionWithServerAsync(tab, local.Id, SplitOrientation.Vertical);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        string paneSessionId = ssh.LastServer!.Id;
        WalkHandlerToConnected(harness, paneSessionId, ConnectionState.LaunchingSsh);
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await split.WaitAsync(TestTimeout);
        SessionPaneModel pane = Assert.Single(
            SplitTreeHelper.EnumerateLeaves(tab.RootContent),
            leaf => string.Equals(leaf.ServerId, paneSessionId, StringComparison.Ordinal));

        ServerItemViewModel row = Assert.Single(harness.Main.ServerList.Servers);
        Assert.True(harness.StateMachine.TryEndSession(tabSessionId, null));
        Assert.True(harness.StateMachine.TryEndSession(paneSessionId, null));
        Assert.Equal(nameof(ConnectionState.Disconnected), row.ConnectionState);

        await ReconnectToConnectedAsync(harness, ssh, tab, pane, paneSessionId, ConnectionState.LaunchingSsh);

        Assert.Equal(nameof(ConnectionState.Connected), row.ConnectionState);

        // And the row keeps following it: the reconnected pane ending takes the row down again.
        Assert.True(harness.StateMachine.TryEndSession(paneSessionId, null));
        Assert.Equal(nameof(ConnectionState.Disconnected), row.ConnectionState);
    }

    [Fact]
    public async Task SftpCompanionReconnect_BringsTheRowBackToConnected()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto local = harness.CreateServer("SSH");
        await harness.PersistServerAsync(local);
        await EnableSftpAutoOpenAsync(harness);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        Task<bool> restore = harness.Main.ServerList.RestoreServerAsync(local.Id, CancellationToken.None);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        string sshSessionId = ssh.LastServer!.Id;
        WalkHandlerToConnected(harness, sshSessionId, ConnectionState.LaunchingSsh);
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await restore.WaitAsync(TestTimeout);
        SessionTabViewModel tab = Assert.Single(harness.Main.Connection.ActiveSessions);

        await sftp.Started.Task.WaitAsync(TestTimeout);
        string sftpSessionId = sftp.LastServer!.Id;
        WalkHandlerToConnected(harness, sftpSessionId, ConnectionState.LaunchingSftp);
        sftp.Result.SetResult(SuccessWithTerminalSession());
        await WaitUntilAsync(() => SplitTreeHelper.EnumerateLeaves(tab.RootContent)
            .Any(leaf => string.Equals(leaf.ServerId, sftpSessionId, StringComparison.Ordinal)));
        SessionPaneModel sftpPane = Assert.Single(
            SplitTreeHelper.EnumerateLeaves(tab.RootContent),
            leaf => string.Equals(leaf.ServerId, sftpSessionId, StringComparison.Ordinal));

        ServerItemViewModel row = Assert.Single(harness.Main.ServerList.Servers);
        Assert.True(harness.StateMachine.TryEndSession(sshSessionId, null));
        Assert.True(harness.StateMachine.TryEndSession(sftpSessionId, null));
        Assert.Equal(nameof(ConnectionState.Disconnected), row.ConnectionState);

        await ReconnectToConnectedAsync(harness, sftp, tab, sftpPane, sftpSessionId, ConnectionState.LaunchingSftp);

        Assert.Equal(nameof(ConnectionState.Connected), row.ConnectionState);

        Assert.True(harness.StateMachine.TryEndSession(sftpSessionId, null));
        Assert.Equal(nameof(ConnectionState.Disconnected), row.ConnectionState);
    }

    /// <summary>
    /// Runs the pane's Reconnect and walks the new attempt to Connected, after proving it runs
    /// under the very key the first connection used: a reconnect that minted a new key would pass
    /// these tests without the revision ever being reused.
    /// </summary>
    private static async Task ReconnectToConnectedAsync(
        TestHarness harness,
        ControlledProtocolHandler handler,
        SessionTabViewModel tab,
        SessionPaneModel pane,
        string paneSessionId,
        ConnectionState launching)
    {
        handler.Reset();
        Task reconnect = harness.Main.Split.ReconnectPaneAsync(tab, pane.PaneId);
        await handler.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal(paneSessionId, handler.LastServer!.Id);

        WalkHandlerToConnected(harness, paneSessionId, launching);
        handler.Result.SetResult(SuccessWithTerminalSession());
        await reconnect.WaitAsync(TestTimeout);
        Assert.Equal(paneSessionId, pane.ServerId);
    }
}
