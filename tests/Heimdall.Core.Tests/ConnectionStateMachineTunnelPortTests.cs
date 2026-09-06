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

using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;

namespace Heimdall.Core.Tests;

/// <summary>
/// The tunnel port recorded for a session is handed out once: the process exit
/// handler and the pane close both release a pane's tunnel, and only one of them may.
/// </summary>
public sealed class ConnectionStateMachineTunnelPortTests
{
    private const string ServerId = "server-1";

    [Fact]
    public void TryTakeTunnelLocalPort_HandsThePortOutOnce()
    {
        ConnectionStateMachine machine = new();
        machine.SetTunnelInfo(ServerId, 45170, processId: 77);

        Assert.True(machine.TryTakeTunnelLocalPort(ServerId, out int first));
        Assert.Equal(45170, first);

        Assert.False(machine.TryTakeTunnelLocalPort(ServerId, out int second));
        Assert.Equal(0, second);
        Assert.Null(machine.GetStateData(ServerId)?.TunnelLocalPort);
        Assert.Null(machine.GetStateData(ServerId)?.TunnelProcessId);
    }

    [Fact]
    public void TryTakeTunnelLocalPort_UnknownServerOrNoTunnel_ReturnsFalse()
    {
        ConnectionStateMachine machine = new();

        Assert.False(machine.TryTakeTunnelLocalPort("unknown", out _));

        Assert.True(machine.TryTransition(ServerId, ConnectionState.Initializing));
        Assert.False(machine.TryTakeTunnelLocalPort(ServerId, out _));
    }

    [Fact]
    public void TryTakeTunnelLocalPort_KeepsTheRestOfTheState()
    {
        ConnectionStateMachine machine = new();
        Assert.True(machine.TryTransition(ServerId, ConnectionState.Initializing));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.ValidatingConfig));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.EstablishingTunnel));
        machine.SetTunnelInfo(ServerId, 45171, processId: 0);

        Assert.True(machine.TryTakeTunnelLocalPort(ServerId, out _));

        Assert.Equal(ConnectionState.EstablishingTunnel, machine.GetStateData(ServerId)?.CurrentState);
    }
}
