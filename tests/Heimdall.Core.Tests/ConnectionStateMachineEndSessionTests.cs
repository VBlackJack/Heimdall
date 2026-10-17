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
/// A pane whose session ends while it stays open moves its own entry: Disconnected when it
/// ended cleanly, Error with the reason when it was lost, and never a torn-down entry.
/// </summary>
public sealed class ConnectionStateMachineEndSessionTests
{
    private const string ServerId = "session-1";

    [Fact]
    public void TryEndSession_WithoutReason_MovesConnectedToDisconnected()
    {
        ConnectionStateMachine machine = CreateConnected();
        List<ConnectionStateChange> changes = [];
        machine.StateChanged += changes.Add;

        Assert.True(machine.TryEndSession(ServerId, null));

        Assert.Equal(ConnectionState.Disconnected, machine.GetState(ServerId));
        ConnectionStateChange change = Assert.Single(changes);
        Assert.Equal(ConnectionState.Connected, change.PreviousState);
        Assert.Equal(ConnectionState.Disconnected, change.NewState);
    }

    [Fact]
    public void TryEndSession_WithReason_MovesConnectedToErrorCarryingIt()
    {
        ConnectionStateMachine machine = CreateConnected();

        Assert.True(machine.TryEndSession(ServerId, "lost"));

        ConnectionStateData? data = machine.GetStateData(ServerId);
        Assert.NotNull(data);
        Assert.Equal(ConnectionState.Error, data.CurrentState);
        Assert.Equal("lost", data.ErrorMessage);
    }

    [Fact]
    public void TryEndSession_WhitespaceReason_IsAnEndWithoutReason()
    {
        ConnectionStateMachine machine = CreateConnected();

        Assert.True(machine.TryEndSession(ServerId, "  "));

        Assert.Equal(ConnectionState.Disconnected, machine.GetState(ServerId));
    }

    [Fact]
    public void TryEndSession_UntrackedSession_StaysUntracked()
    {
        ConnectionStateMachine machine = new();

        Assert.False(machine.TryEndSession(ServerId, "lost"));
        Assert.False(machine.TryEndSession(ServerId, null));

        Assert.Null(machine.GetStateData(ServerId));
    }

    [Fact]
    public void TryEndSession_AfterTeardown_DoesNotTrackTheSessionAgain()
    {
        ConnectionStateMachine machine = CreateConnected();
        machine.Teardown(ServerId);

        Assert.False(machine.TryEndSession(ServerId, "lost"));

        Assert.Null(machine.GetStateData(ServerId));
    }

    [Fact]
    public void TryEndSession_DuringTeardownPublication_DoesNotKeepTheEntryAlive()
    {
        ConnectionStateMachine machine = CreateConnected();
        bool? endedDuringTeardown = null;
        machine.StateChanged += change =>
        {
            if (change.NewState == ConnectionState.Disconnected && endedDuringTeardown is null)
            {
                endedDuringTeardown = machine.TryEndSession(ServerId, "lost");
            }
        };

        machine.Teardown(ServerId);

        Assert.False(endedDuringTeardown);
        Assert.Null(machine.GetStateData(ServerId));
    }

    [Fact]
    public void TryEndSession_AlreadyEnded_KeepsTheFirstEnd()
    {
        ConnectionStateMachine machine = CreateConnected();
        Assert.True(machine.TryEndSession(ServerId, "host key changed"));

        Assert.False(machine.TryEndSession(ServerId, null));
        Assert.False(machine.TryEndSession(ServerId, "no longer responding"));

        ConnectionStateData? data = machine.GetStateData(ServerId);
        Assert.NotNull(data);
        Assert.Equal(ConnectionState.Error, data.CurrentState);
        Assert.Equal("host key changed", data.ErrorMessage);
    }

    [Fact]
    public void TryEndSession_AfterReconnect_EndsTheNewSession()
    {
        ConnectionStateMachine machine = CreateConnected();
        Assert.True(machine.TryEndSession(ServerId, null));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.Initializing));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.ValidatingConfig));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.LaunchingSsh));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.Connected));

        Assert.True(machine.TryEndSession(ServerId, null));

        Assert.Equal(ConnectionState.Disconnected, machine.GetState(ServerId));
    }

    private static ConnectionStateMachine CreateConnected()
    {
        ConnectionStateMachine machine = new();
        Assert.True(machine.TryTransition(ServerId, ConnectionState.Initializing));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.ValidatingConfig));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.LaunchingSsh));
        Assert.True(machine.TryTransition(ServerId, ConnectionState.Connected));
        return machine;
    }
}
