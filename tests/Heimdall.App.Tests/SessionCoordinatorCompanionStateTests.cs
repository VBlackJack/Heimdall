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
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The SFTP pane opened beside an SSH session is a connection of the same session tree row: the
/// row stays connected while either pane is, and goes dark only when both have ended.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task SftpCompanion_KeepsTheRowConnectedAfterTheTerminalPaneEnds()
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

        await sftp.Started.Task.WaitAsync(TestTimeout);
        string sftpSessionId = sftp.LastServer!.Id;
        // What the real SFTP handler does: it starts at ValidatingConfig, which the machine only
        // accepts from Initializing. The companion used to skip that step, so its whole
        // connection was refused.
        WalkHandlerToConnected(harness, sftpSessionId, ConnectionState.LaunchingSftp);
        sftp.Result.SetResult(new ConnectionResult(false, "recorded", null));

        ServerItemViewModel row = Assert.Single(harness.Main.ServerList.Servers);
        Assert.Equal(nameof(ConnectionState.Connected), row.ConnectionState);

        Assert.True(harness.StateMachine.TryEndSession(sshSessionId, null));
        Assert.Equal(nameof(ConnectionState.Connected), row.ConnectionState);

        Assert.True(harness.StateMachine.TryEndSession(sftpSessionId, null));
        Assert.Equal(nameof(ConnectionState.Disconnected), row.ConnectionState);
    }

    private static void WalkHandlerToConnected(
        TestHarness harness,
        string sessionId,
        ConnectionState launching)
    {
        Assert.Equal(ConnectionState.Initializing, harness.StateMachine.GetState(sessionId));
        Assert.True(harness.StateMachine.TryTransition(sessionId, ConnectionState.ValidatingConfig));
        Assert.True(harness.StateMachine.TryTransition(sessionId, launching));
        Assert.True(harness.StateMachine.TryTransition(sessionId, ConnectionState.Connected));
    }
}
