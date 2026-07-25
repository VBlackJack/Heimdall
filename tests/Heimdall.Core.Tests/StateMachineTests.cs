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

public class ConnectionStateMachineTests
{
    /// <summary>
    /// Failure bound, not a synchronisation point. The workers complete on their own,
    /// and this only has to be generous enough that a loaded machine cannot exhaust it
    /// before eight threads have each run a hundred transitions. It is ONE budget for
    /// all eight joins together, not one per join, and it is paid only on failure.
    /// </summary>
    private static readonly TimeSpan WorkerCompletionBackstop = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Failure bound, not a synchronisation point. The wait it bounds completes on an
    /// event: the probe task finishing its read of the state from another thread. It is
    /// paid only on failure.
    /// </summary>
    private static readonly TimeSpan CrossThreadReadBackstop = TimeSpan.FromSeconds(2);

    private readonly ConnectionStateMachine _sm = new();

    [Fact]
    public void InitialState_IsDisconnected()
    {
        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("server-1"));
    }

    [Fact]
    public void GetStateData_ReturnsNull_ForUnknownServer()
    {
        Assert.Null(_sm.GetStateData("unknown"));
    }

    [Fact]
    public void ValidTransition_Succeeds()
    {
        bool result = _sm.TryTransition("server-1", ConnectionState.Initializing);

        Assert.True(result);
        Assert.Equal(ConnectionState.Initializing, _sm.GetState("server-1"));
    }

    [Fact]
    public void InvalidTransition_ReturnsFalse()
    {
        bool result = _sm.TryTransition("server-1", ConnectionState.Connected);

        Assert.False(result);
        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("server-1"));
    }

    [Fact]
    public void SetError_TransitionsToError()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);

        bool result = _sm.SetError("server-1", "Something failed");

        Assert.True(result);
        Assert.Equal(ConnectionState.Error, _sm.GetState("server-1"));
        ConnectionStateData? data = _sm.GetStateData("server-1");
        Assert.NotNull(data);
        Assert.Equal("Something failed", data.ErrorMessage);
    }

    [Fact]
    public void SetError_ReturnsFalse_WhenTransitionInvalid()
    {
        // Error -> Error is not a valid transition; server starts at Disconnected,
        // transition to Error first, then try again.
        _sm.SetError("server-1", "first error");

        bool result = _sm.SetError("server-1", "second error");

        Assert.False(result);
    }

    [Fact]
    public void Reset_ReturnsToDisconnected()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.TryTransition("server-1", ConnectionState.ValidatingConfig);

        _sm.Reset("server-1");

        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("server-1"));
    }

    [Fact]
    public void Reset_ClearsTunnelInfoAndError()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.TryTransition("server-1", ConnectionState.ValidatingConfig);
        _sm.TryTransition("server-1", ConnectionState.EstablishingTunnel);
        _sm.SetTunnelInfo("server-1", 12345, 9876);
        _sm.SetError("server-1", "tunnel failed");

        _sm.Reset("server-1");

        ConnectionStateData? data = _sm.GetStateData("server-1");
        Assert.NotNull(data);
        Assert.Null(data.ErrorMessage);
        Assert.Null(data.TunnelLocalPort);
        Assert.Null(data.TunnelProcessId);
    }

    [Fact]
    public void Reset_NoOp_ForUnknownServer()
    {
        // Should not throw
        _sm.Reset("unknown-server");
    }

    [Fact]
    public void Reset_NoOp_WhenAlreadyDisconnected()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.TryTransition("server-1", ConnectionState.Disconnected);

        // Already disconnected, should be a no-op
        _sm.Reset("server-1");

        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("server-1"));
    }

    [Fact]
    public void Remove_StopsTracking()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);

        _sm.Remove("server-1");

        Assert.Null(_sm.GetStateData("server-1"));
        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("server-1"));
    }

    [Fact]
    public void StateChanged_EventFires_OnValidTransition()
    {
        string? receivedServerId = null;
        ConnectionState? receivedPrevious = null;
        ConnectionState? receivedNew = null;

        _sm.StateChanged += change =>
        {
            receivedServerId = change.ServerId;
            receivedPrevious = change.PreviousState;
            receivedNew = change.NewState;
        };

        _sm.TryTransition("server-1", ConnectionState.Initializing);

        Assert.Equal("server-1", receivedServerId);
        Assert.Equal(ConnectionState.Disconnected, receivedPrevious);
        Assert.Equal(ConnectionState.Initializing, receivedNew);
    }

    [Fact]
    public void StateChanged_EventFires_OnSetError()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);

        string? receivedError = null;
        _sm.StateChanged += change => { receivedError = change.ErrorMessage; };

        _sm.SetError("server-1", "auth failed");

        Assert.Equal("auth failed", receivedError);
    }

    [Fact]
    public void StateChanged_DoesNotFire_OnInvalidTransition()
    {
        bool fired = false;
        _sm.StateChanged += _ => { fired = true; };

        _sm.TryTransition("server-1", ConnectionState.Connected);

        Assert.False(fired);
    }

    [Fact]
    public void StateChanged_RevisionsIncreaseForEveryAppliedTransition()
    {
        List<long> revisions = [];
        _sm.StateChanged += change => revisions.Add(change.Revision);

        Assert.True(_sm.TryTransition("server-1", ConnectionState.Initializing));
        Assert.True(_sm.TryTransition("server-1", ConnectionState.ValidatingConfig));
        Assert.True(_sm.SetError("server-1", "validation failed"));
        _sm.Reset("server-1");

        Assert.Equal([1L, 2L, 3L, 4L], revisions);
        Assert.Equal(4, Assert.IsType<ConnectionStateData>(_sm.GetStateData("server-1")).Revision);
    }

    [Fact]
    public void Teardown_PublishesOrderedDisconnectThenRemovesEntry()
    {
        List<ConnectionStateChange> teardownChanges = [];
        bool entryPresentDuringFinalNotification = false;
        bool captureTeardown = false;
        _sm.StateChanged += change =>
        {
            if (!captureTeardown)
            {
                return;
            }

            teardownChanges.Add(change);
            if (change.NewState == ConnectionState.Disconnected)
            {
                entryPresentDuringFinalNotification = _sm.GetStateData(change.ServerId) is not null;
            }
        };
        TransitionSshToConnected(_sm, "server-1");
        captureTeardown = true;

        _sm.Teardown("server-1");

        Assert.Collection(
            teardownChanges,
            change =>
            {
                Assert.Equal(ConnectionState.Connected, change.PreviousState);
                Assert.Equal(ConnectionState.Disconnecting, change.NewState);
                Assert.Equal(5, change.Revision);
            },
            change =>
            {
                Assert.Equal(ConnectionState.Disconnecting, change.PreviousState);
                Assert.Equal(ConnectionState.Disconnected, change.NewState);
                Assert.Equal(6, change.Revision);
            });
        Assert.True(entryPresentDuringFinalNotification);
        Assert.Null(_sm.GetStateData("server-1"));
        Assert.Empty(_sm.GetActiveConnections());
    }

    [Fact]
    public void Teardown_RepeatedDistinctSessionCyclesKeepTrackingBounded()
    {
        const int CycleCount = 500;

        for (int index = 0; index < CycleCount; index++)
        {
            string sessionId = $"inventory_{index:x8}";
            Assert.True(_sm.TryTransition(sessionId, ConnectionState.Initializing));

            _sm.Teardown(sessionId);

            Assert.Null(_sm.GetStateData(sessionId));
        }

        Assert.Empty(_sm.GetActiveConnections());
        Assert.Empty(_sm.GetServersByState(ConnectionState.Disconnected));
    }

    [Fact]
    public void Teardown_RemovesEntryEvenWhenSubscriberThrows()
    {
        Assert.True(_sm.TryTransition("server-1", ConnectionState.Initializing));
        _sm.StateChanged += change =>
        {
            if (change.NewState == ConnectionState.Disconnected)
            {
                throw new InvalidOperationException("subscriber failed");
            }
        };

        Assert.Throws<InvalidOperationException>(() => _sm.Teardown("server-1"));

        Assert.Null(_sm.GetStateData("server-1"));
    }

    [Fact]
    public void ConcurrentTransitions_PublishOutsideLockInRevisionOrderAndTeardownCleanly()
    {
        const string SessionId = "shared-session";
        System.Collections.Concurrent.ConcurrentQueue<long> revisions = new();
        System.Collections.Concurrent.ConcurrentQueue<Exception> workerFailures = new();
        int probeStarted = 0;
        bool crossThreadReadCompleted = false;
        _sm.StateChanged += change =>
        {
            revisions.Enqueue(change.Revision);
            if (Interlocked.CompareExchange(ref probeStarted, 1, 0) == 0)
            {
                Task readTask = Task.Run(() => _sm.GetStateData(change.ServerId));
                crossThreadReadCompleted = readTask.Wait(CrossThreadReadBackstop);
            }
        };

        // Dedicated threads, not pool tasks. The state machine invokes StateChanged
        // synchronously on the caller's stack, so the handler's bounded wait above runs
        // on whichever thread drives a transition. On pool tasks that meant blocking a
        // pool worker while waiting for a pool work item, with eight more workers held
        // by this test.
        Thread[] workers = Enumerable.Range(0, 8)
            .Select(worker => new Thread(() =>
            {
                try
                {
                    for (int iteration = 0; iteration < 100; iteration++)
                    {
                        switch ((worker + iteration) % 4)
                        {
                            case 0:
                                _sm.TryTransition(SessionId, ConnectionState.Initializing);
                                break;
                            case 1:
                                _sm.TryTransition(SessionId, ConnectionState.ValidatingConfig);
                                break;
                            case 2:
                                _sm.SetError(SessionId, "concurrent failure");
                                break;
                            default:
                                _sm.Reset(SessionId);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A thread surfaces nothing on its own: uncaught this would tear the
                    // process down, swallowed it would lose the failure. Collect and
                    // rethrow after the join to keep what Task.WhenAll reported.
                    workerFailures.Enqueue(ex);
                }
            })
            {
                IsBackground = true,
                Name = $"state-machine-worker-{worker}",
            })
            .ToArray();

        foreach (Thread thread in workers)
        {
            thread.Start();
        }

        // One budget for all eight joins. Joining each thread against the full backstop
        // would allow eight times the intended bound.
        System.Diagnostics.Stopwatch joinClock = System.Diagnostics.Stopwatch.StartNew();
        foreach (Thread thread in workers)
        {
            TimeSpan remaining = WorkerCompletionBackstop - joinClock.Elapsed;
            if (remaining <= TimeSpan.Zero || !thread.Join(remaining))
            {
                throw new TimeoutException(
                    $"Concurrent transition workers did not all finish within "
                    + $"{WorkerCompletionBackstop.TotalSeconds:0.##}s; {thread.Name} is "
                    + $"still running.");
            }
        }

        if (!workerFailures.IsEmpty)
        {
            throw new AggregateException(
                "One or more concurrent transition workers failed.",
                workerFailures);
        }

        _sm.Teardown(SessionId);

        long[] publishedRevisions = revisions.ToArray();
        Assert.True(
            crossThreadReadCompleted,
            $"The cross-thread state read did not complete within "
            + $"{CrossThreadReadBackstop.TotalSeconds:0.##}s, so the probe stalled "
            + $"instead of proving the publication is observable off the publishing "
            + $"thread.");
        Assert.NotEmpty(publishedRevisions);
        Assert.True(publishedRevisions.Zip(
            publishedRevisions.Skip(1),
            (previous, next) => previous < next).All(inOrder => inOrder));
        Assert.Null(_sm.GetStateData(SessionId));
    }

    [Fact]
    public void SetTunnelInfo_StoresPortAndPid()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.SetTunnelInfo("server-1", 55000, 1234);

        ConnectionStateData? data = _sm.GetStateData("server-1");
        Assert.NotNull(data);
        Assert.Equal(55000, data.TunnelLocalPort);
        Assert.Equal(1234, data.TunnelProcessId);
    }

    [Fact]
    public void ConnectedAt_SetWhenReachingConnected()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.TryTransition("server-1", ConnectionState.ValidatingConfig);
        _sm.TryTransition("server-1", ConnectionState.LaunchingRdp);
        _sm.TryTransition("server-1", ConnectionState.Connected);

        ConnectionStateData? data = _sm.GetStateData("server-1");
        Assert.NotNull(data);
        Assert.NotNull(data.ConnectedAtUtc);
    }

    [Fact]
    public void ConnectedAt_ClearedOnDisconnected()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.TryTransition("server-1", ConnectionState.ValidatingConfig);
        _sm.TryTransition("server-1", ConnectionState.LaunchingRdp);
        _sm.TryTransition("server-1", ConnectionState.Connected);
        _sm.TryTransition("server-1", ConnectionState.Disconnecting);
        _sm.TryTransition("server-1", ConnectionState.Disconnected);

        ConnectionStateData? data = _sm.GetStateData("server-1");
        Assert.NotNull(data);
        Assert.Null(data.ConnectedAtUtc);
    }

    [Fact]
    public void GetActiveConnections_ExcludesDisconnectedAndError()
    {
        _sm.TryTransition("active-1", ConnectionState.Initializing);
        _sm.TryTransition("active-1", ConnectionState.ValidatingConfig);
        _sm.TryTransition("active-1", ConnectionState.LaunchingRdp);
        _sm.TryTransition("active-1", ConnectionState.Connected);

        _sm.TryTransition("disconnected-1", ConnectionState.Initializing);
        _sm.TryTransition("disconnected-1", ConnectionState.Disconnected);

        _sm.SetError("error-1", "failed");

        IReadOnlyDictionary<string, ConnectionStateData> active = _sm.GetActiveConnections();

        Assert.Single(active);
        Assert.True(active.ContainsKey("active-1"));
    }

    [Fact]
    public void GetServersByState_ReturnsCorrectServers()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);
        _sm.TryTransition("server-2", ConnectionState.Initializing);
        _sm.TryTransition("server-3", ConnectionState.Initializing);
        _sm.TryTransition("server-3", ConnectionState.ValidatingConfig);

        List<string> initializing = _sm.GetServersByState(ConnectionState.Initializing).ToList();

        Assert.Equal(2, initializing.Count);
        Assert.Contains("server-1", initializing);
        Assert.Contains("server-2", initializing);
    }

    [Fact]
    public void GetStateData_ReturnsSnapshot_NotLiveReference()
    {
        _sm.TryTransition("server-1", ConnectionState.Initializing);

        ConnectionStateData? snapshot = _sm.GetStateData("server-1");
        Assert.NotNull(snapshot);

        _sm.TryTransition("server-1", ConnectionState.ValidatingConfig);

        // Snapshot should still show Initializing
        Assert.Equal(ConnectionState.Initializing, snapshot.CurrentState);
        // Live state should be ValidatingConfig
        Assert.Equal(ConnectionState.ValidatingConfig, _sm.GetState("server-1"));
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Initializing, true)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Error, true)]
    [InlineData(ConnectionState.Initializing, ConnectionState.ValidatingConfig, true)]
    [InlineData(ConnectionState.Initializing, ConnectionState.Error, true)]
    [InlineData(ConnectionState.Initializing, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Initializing, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.EstablishingTunnel, true)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.LaunchingRdp, true)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.LaunchingSsh, true)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.LaunchingSftp, true)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.Error, true)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.EstablishingTunnel, ConnectionState.TunnelEstablished, true)]
    [InlineData(ConnectionState.EstablishingTunnel, ConnectionState.Error, true)]
    [InlineData(ConnectionState.EstablishingTunnel, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.EstablishingTunnel, ConnectionState.LaunchingRdp, false)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.LaunchingRdp, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.LaunchingSsh, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.LaunchingSftp, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.LaunchingLocal, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.Error, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingRdp, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingRdp, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingRdp, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingRdp, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.LaunchingSsh, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingSsh, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingSsh, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingSsh, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingSftp, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingSftp, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingSftp, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingSftp, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Error, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Initializing, false)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Error, true)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Error, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Error, ConnectionState.Initializing, true)]
    [InlineData(ConnectionState.Error, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Error, ConnectionState.Error, false)]
    public void IsValidTransition_MatchesTable(ConnectionState from, ConnectionState to, bool expected)
    {
        Assert.Equal(expected, ConnectionStateMachine.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, "StatusReady", "LogDisconnected", false, true, false)]
    [InlineData(ConnectionState.Initializing, "StatusConnectingProgress", "LogInitializing", false, false, true)]
    [InlineData(ConnectionState.ValidatingConfig, "StatusConnectingProgress", "LogValidating", false, false, true)]
    [InlineData(ConnectionState.EstablishingTunnel, "StatusEstablishingTunnel", "LogTunnelCreating", false, false, true)]
    [InlineData(ConnectionState.TunnelEstablished, "StatusTunnelEstablished", "LogTunnelCreated", false, true, false)]
    [InlineData(ConnectionState.LaunchingRdp, "StatusConnecting", "LogRdpLaunching", false, false, true)]
    [InlineData(ConnectionState.LaunchingSsh, "StatusLaunchingSsh", "LogSshLaunching", false, false, true)]
    [InlineData(ConnectionState.LaunchingSftp, "StatusLaunchingSftp", "LogSftpLaunching", false, false, true)]
    [InlineData(ConnectionState.LaunchingWinRm, "StatusLaunchingWinRm", "LogWinRmLaunching", false, false, true)]
    [InlineData(ConnectionState.Connected, "StatusConnected", "LogRdpConnection", false, true, false)]
    [InlineData(ConnectionState.RemoteSessionHandedOff, "StatusRemoteSessionHandedOff", "LogRemoteSessionHandedOff", false, true, false)]
    [InlineData(ConnectionState.Disconnecting, "StatusDisconnecting", "LogDisconnecting", false, false, true)]
    [InlineData(ConnectionState.Error, "StatusError", "LogError", false, true, false)]
    public void GetMetadata_ReturnsCorrectValues(
        ConnectionState state,
        string expectedDisplayKey,
        string expectedLogKey,
        bool expectedIsTerminal,
        bool expectedAllowsUserAction,
        bool expectedIsProgress)
    {
        StateMetadata metadata = ConnectionStateMachine.GetMetadata(state);

        Assert.Equal(expectedDisplayKey, metadata.DisplayKey);
        Assert.Equal(expectedLogKey, metadata.LogKey);
        Assert.Equal(expectedIsTerminal, metadata.IsTerminal);
        Assert.Equal(expectedAllowsUserAction, metadata.AllowsUserAction);
        Assert.Equal(expectedIsProgress, metadata.IsProgress);
    }

    [Fact]
    public void FullRdpTunnelWorkflow_Succeeds()
    {
        Assert.True(_sm.TryTransition("srv", ConnectionState.Initializing));
        Assert.True(_sm.TryTransition("srv", ConnectionState.ValidatingConfig));
        Assert.True(_sm.TryTransition("srv", ConnectionState.EstablishingTunnel));
        _sm.SetTunnelInfo("srv", 55000, 4321);
        Assert.True(_sm.TryTransition("srv", ConnectionState.TunnelEstablished));
        Assert.True(_sm.TryTransition("srv", ConnectionState.LaunchingRdp));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Connected));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnecting));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnected));

        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("srv"));
    }

    [Fact]
    public void CanTransition_LaunchingRdp_ToLaunchedExternalClient()
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        Assert.True(sm.TryTransition("server-1", ConnectionState.Initializing));
        Assert.True(sm.TryTransition("server-1", ConnectionState.ValidatingConfig));
        Assert.True(sm.TryTransition("server-1", ConnectionState.LaunchingRdp));

        bool transitioned = sm.TryTransition("server-1", ConnectionState.LaunchedExternalClient);

        Assert.True(transitioned);
        Assert.Equal(ConnectionState.LaunchedExternalClient, sm.GetState("server-1"));
    }

    [Fact]
    public void LaunchedExternalClient_CanTransitionToDisconnected()
    {
        ConnectionStateMachine sm = ArrangeAtLaunchedExternalClient("server-1");

        Assert.True(sm.TryTransition("server-1", ConnectionState.Disconnected));
        Assert.Equal(ConnectionState.Disconnected, sm.GetState("server-1"));
    }

    [Fact]
    public void LaunchedExternalClient_CanTransitionToError()
    {
        ConnectionStateMachine sm = ArrangeAtLaunchedExternalClient("server-1");

        Assert.True(sm.TryTransition("server-1", ConnectionState.Error));
        Assert.Equal(ConnectionState.Error, sm.GetState("server-1"));
    }

    [Fact]
    public void LaunchedExternalClient_CannotTransitionBackToConnected()
    {
        ConnectionStateMachine sm = ArrangeAtLaunchedExternalClient("server-1");

        Assert.False(sm.TryTransition("server-1", ConnectionState.Connected));
        Assert.Equal(ConnectionState.LaunchedExternalClient, sm.GetState("server-1"));
    }

    [Fact]
    public void LaunchedExternalClient_Metadata_UsesDedicatedStatusKey()
    {
        StateMetadata metadata = ConnectionStateMachine.GetMetadata(ConnectionState.LaunchedExternalClient);

        Assert.Equal("StatusLaunchedExternalClient", metadata.DisplayKey);
        Assert.Equal("LogLaunchedExternalClient", metadata.LogKey);
        Assert.False(metadata.IsTerminal);
        Assert.True(metadata.AllowsUserAction);
        Assert.False(metadata.IsProgress);
    }

    [Fact]
    public void CanTransition_LaunchingLocal_ToRemoteSessionHandedOff()
    {
        ConnectionStateMachine sm = ArrangeAtLaunchingLocal("server-1");

        bool transitioned = sm.TryTransition("server-1", ConnectionState.RemoteSessionHandedOff);

        Assert.True(transitioned);
        Assert.Equal(ConnectionState.RemoteSessionHandedOff, sm.GetState("server-1"));
    }

    [Fact]
    public void RemoteSessionHandedOff_CanTransitionToDisconnectingDisconnectedAndError()
    {
        ConnectionStateMachine disconnectingSm = ArrangeAtRemoteSessionHandedOff("disconnecting");
        Assert.True(disconnectingSm.TryTransition("disconnecting", ConnectionState.Disconnecting));

        ConnectionStateMachine disconnectedSm = ArrangeAtRemoteSessionHandedOff("disconnected");
        Assert.True(disconnectedSm.TryTransition("disconnected", ConnectionState.Disconnected));

        ConnectionStateMachine errorSm = ArrangeAtRemoteSessionHandedOff("error");
        Assert.True(errorSm.SetError("error", "terminal exited"));
    }

    [Fact]
    public void RemoteSessionHandedOff_CannotTransitionBackToConnected()
    {
        ConnectionStateMachine sm = ArrangeAtRemoteSessionHandedOff("server-1");

        Assert.False(sm.TryTransition("server-1", ConnectionState.Connected));
        Assert.Equal(ConnectionState.RemoteSessionHandedOff, sm.GetState("server-1"));
    }

    [Fact]
    public void RemoteSessionHandedOff_SetsConnectedAtAndCountsAsActive()
    {
        ConnectionStateMachine sm = ArrangeAtRemoteSessionHandedOff("server-1");

        ConnectionStateData? data = sm.GetStateData("server-1");
        IReadOnlyDictionary<string, ConnectionStateData> active = sm.GetActiveConnections();

        Assert.NotNull(data);
        Assert.NotNull(data.ConnectedAtUtc);
        Assert.True(active.ContainsKey("server-1"));
    }

    [Fact]
    public void RemoteSessionHandedOff_Metadata_UsesDedicatedStatusKey()
    {
        StateMetadata metadata = ConnectionStateMachine.GetMetadata(ConnectionState.RemoteSessionHandedOff);

        Assert.Equal("StatusRemoteSessionHandedOff", metadata.DisplayKey);
        Assert.Equal("LogRemoteSessionHandedOff", metadata.LogKey);
        Assert.False(metadata.IsTerminal);
        Assert.True(metadata.AllowsUserAction);
        Assert.False(metadata.IsProgress);
    }

    [Fact]
    public void FullDirectSshWorkflow_Succeeds()
    {
        Assert.True(_sm.TryTransition("srv", ConnectionState.Initializing));
        Assert.True(_sm.TryTransition("srv", ConnectionState.ValidatingConfig));
        Assert.True(_sm.TryTransition("srv", ConnectionState.LaunchingSsh));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Connected));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnecting));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnected));
    }

    [Fact]
    public void ErrorRecoveryWorkflow_Succeeds()
    {
        _sm.TryTransition("srv", ConnectionState.Initializing);
        _sm.SetError("srv", "timeout");

        Assert.Equal(ConnectionState.Error, _sm.GetState("srv"));

        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnected));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Initializing));
    }

    // ── LaunchingLocal state transitions ────────────────────────────────

    [Fact]
    public void ValidatingConfig_CanTransitionToLaunchingLocal()
    {
        Assert.True(_sm.TryTransition("srv", ConnectionState.Initializing));
        Assert.True(_sm.TryTransition("srv", ConnectionState.ValidatingConfig));
        Assert.True(_sm.TryTransition("srv", ConnectionState.LaunchingLocal));

        Assert.Equal(ConnectionState.LaunchingLocal, _sm.GetState("srv"));
    }

    [Fact]
    public void LaunchingLocal_CanTransitionToConnected()
    {
        _sm.TryTransition("srv", ConnectionState.Initializing);
        _sm.TryTransition("srv", ConnectionState.ValidatingConfig);
        _sm.TryTransition("srv", ConnectionState.LaunchingLocal);

        Assert.True(_sm.TryTransition("srv", ConnectionState.Connected));
    }

    [Fact]
    public void LaunchingLocal_CanTransitionToError()
    {
        _sm.TryTransition("srv", ConnectionState.Initializing);
        _sm.TryTransition("srv", ConnectionState.ValidatingConfig);
        _sm.TryTransition("srv", ConnectionState.LaunchingLocal);

        Assert.True(_sm.SetError("srv", "Local shell failed"));
        Assert.Equal(ConnectionState.Error, _sm.GetState("srv"));
    }

    [Fact]
    public void LaunchingLocal_CanTransitionToDisconnecting()
    {
        _sm.TryTransition("srv", ConnectionState.Initializing);
        _sm.TryTransition("srv", ConnectionState.ValidatingConfig);
        _sm.TryTransition("srv", ConnectionState.LaunchingLocal);

        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnecting));
    }

    [Fact]
    public void LaunchingLocal_CannotTransitionToDisconnectedDirectly()
    {
        _sm.TryTransition("srv", ConnectionState.Initializing);
        _sm.TryTransition("srv", ConnectionState.ValidatingConfig);
        _sm.TryTransition("srv", ConnectionState.LaunchingLocal);

        Assert.False(_sm.TryTransition("srv", ConnectionState.Disconnected));
    }

    [Fact]
    public void FullLocalShellWorkflow_Succeeds()
    {
        Assert.True(_sm.TryTransition("srv", ConnectionState.Initializing));
        Assert.True(_sm.TryTransition("srv", ConnectionState.ValidatingConfig));
        Assert.True(_sm.TryTransition("srv", ConnectionState.LaunchingLocal));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Connected));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnecting));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnected));
    }

    [Theory]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.LaunchingLocal, true)]
    [InlineData(ConnectionState.LaunchingLocal, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingLocal, ConnectionState.RemoteSessionHandedOff, true)]
    [InlineData(ConnectionState.LaunchingLocal, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingLocal, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingLocal, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingLocal, ConnectionState.Initializing, false)]
    [InlineData(ConnectionState.RemoteSessionHandedOff, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.RemoteSessionHandedOff, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.RemoteSessionHandedOff, ConnectionState.Error, true)]
    [InlineData(ConnectionState.RemoteSessionHandedOff, ConnectionState.Connected, false)]
    public void LaunchingLocal_TransitionTable(ConnectionState from, ConnectionState to, bool expected)
    {
        Assert.Equal(expected, ConnectionStateMachine.IsValidTransition(from, to));
    }

    [Fact]
    public void LaunchingLocal_Metadata_IsProgressState()
    {
        StateMetadata metadata = ConnectionStateMachine.GetMetadata(ConnectionState.LaunchingLocal);

        Assert.Equal("StatusLaunchingLocal", metadata.DisplayKey);
        Assert.Equal("LogLocalShellLaunching", metadata.LogKey);
        Assert.False(metadata.IsTerminal);
        Assert.False(metadata.AllowsUserAction);
        Assert.True(metadata.IsProgress);
    }

    // ── LaunchingWinRm: WinRM's explicit state (WinRmHandler.cs transition) ──
    // WinRM hands the REPL off to the local PowerShell host, so its row mirrors
    // LaunchingLocal including the RemoteSessionHandedOff target.

    [Theory]
    [InlineData(ConnectionState.ValidatingConfig, ConnectionState.LaunchingWinRm, true)]
    [InlineData(ConnectionState.TunnelEstablished, ConnectionState.LaunchingWinRm, true)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.RemoteSessionHandedOff, true)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.LaunchingSsh, false)]
    [InlineData(ConnectionState.LaunchingWinRm, ConnectionState.Initializing, false)]
    public void LaunchingWinRm_TransitionTable(ConnectionState from, ConnectionState to, bool expected)
    {
        Assert.Equal(expected, ConnectionStateMachine.IsValidTransition(from, to));
    }

    [Fact]
    public void LaunchingWinRm_Metadata_IsProgressState()
    {
        StateMetadata metadata = ConnectionStateMachine.GetMetadata(ConnectionState.LaunchingWinRm);

        Assert.Equal("StatusLaunchingWinRm", metadata.DisplayKey);
        Assert.Equal("LogWinRmLaunching", metadata.LogKey);
        Assert.False(metadata.IsTerminal);
        Assert.False(metadata.AllowsUserAction);
        Assert.True(metadata.IsProgress);
    }

    [Fact]
    public void FullWinRmWorkflow_HandsOffToLocalHost()
    {
        Assert.True(_sm.TryTransition("srv", ConnectionState.Initializing));
        Assert.True(_sm.TryTransition("srv", ConnectionState.ValidatingConfig));
        Assert.True(_sm.TryTransition("srv", ConnectionState.LaunchingWinRm));
        Assert.True(_sm.TryTransition("srv", ConnectionState.RemoteSessionHandedOff));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnecting));
        Assert.True(_sm.TryTransition("srv", ConnectionState.Disconnected));

        Assert.Equal(ConnectionState.Disconnected, _sm.GetState("srv"));
    }

    // ── All protocol launch states: full transition coverage ────────────

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    [InlineData(ConnectionState.LaunchingRdp)]
    [InlineData(ConnectionState.LaunchingSsh)]
    [InlineData(ConnectionState.LaunchingSftp)]
    [InlineData(ConnectionState.LaunchingLocal)]
    [InlineData(ConnectionState.LaunchingWinRm)]
    public void LaunchState_ToConnected_IsValid(ConnectionState launchState)
    {
        Assert.True(ConnectionStateMachine.IsValidTransition(launchState, ConnectionState.Connected));
    }

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    [InlineData(ConnectionState.LaunchingRdp)]
    [InlineData(ConnectionState.LaunchingSsh)]
    [InlineData(ConnectionState.LaunchingSftp)]
    [InlineData(ConnectionState.LaunchingLocal)]
    [InlineData(ConnectionState.LaunchingWinRm)]
    public void LaunchState_ToError_IsValid(ConnectionState launchState)
    {
        Assert.True(ConnectionStateMachine.IsValidTransition(launchState, ConnectionState.Error));
    }

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    [InlineData(ConnectionState.LaunchingRdp)]
    [InlineData(ConnectionState.LaunchingSsh)]
    [InlineData(ConnectionState.LaunchingSftp)]
    [InlineData(ConnectionState.LaunchingLocal)]
    [InlineData(ConnectionState.LaunchingWinRm)]
    public void LaunchState_ToDisconnecting_IsValid(ConnectionState launchState)
    {
        Assert.True(ConnectionStateMachine.IsValidTransition(launchState, ConnectionState.Disconnecting));
    }

    // ── TunnelEstablished → each protocol launch transition ─────────────

    [Theory]
    [InlineData(ConnectionState.LaunchingRdp)]
    [InlineData(ConnectionState.LaunchingSsh)]
    [InlineData(ConnectionState.LaunchingSftp)]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    [InlineData(ConnectionState.LaunchingLocal)]
    [InlineData(ConnectionState.LaunchingWinRm)]
    public void TunnelEstablished_ToLaunchState_IsValid(ConnectionState launchState)
    {
        Assert.True(ConnectionStateMachine.IsValidTransition(ConnectionState.TunnelEstablished, launchState));
    }

    // ── ValidatingConfig → each protocol launch (direct, no tunnel) ─────

    [Theory]
    [InlineData(ConnectionState.LaunchingRdp)]
    [InlineData(ConnectionState.LaunchingSsh)]
    [InlineData(ConnectionState.LaunchingSftp)]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    [InlineData(ConnectionState.LaunchingLocal)]
    [InlineData(ConnectionState.LaunchingWinRm)]
    public void ValidatingConfig_ToLaunchState_IsValid(ConnectionState launchState)
    {
        Assert.True(ConnectionStateMachine.IsValidTransition(ConnectionState.ValidatingConfig, launchState));
    }

    // ── Full workflow for each protocol via tunnel ───────────────────────

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    public void FullTunnelWorkflow_Succeeds_ForAllProtocols(ConnectionState launchState)
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        string id = $"srv-{launchState}";

        Assert.True(sm.TryTransition(id, ConnectionState.Initializing));
        Assert.True(sm.TryTransition(id, ConnectionState.ValidatingConfig));
        Assert.True(sm.TryTransition(id, ConnectionState.EstablishingTunnel));
        Assert.True(sm.TryTransition(id, ConnectionState.TunnelEstablished));
        Assert.True(sm.TryTransition(id, launchState));
        Assert.True(sm.TryTransition(id, ConnectionState.Connected));
        Assert.True(sm.TryTransition(id, ConnectionState.Disconnecting));
        Assert.True(sm.TryTransition(id, ConnectionState.Disconnected));

        Assert.Equal(ConnectionState.Disconnected, sm.GetState(id));
    }

    // ── Full direct workflow for each protocol (no tunnel) ──────────────

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc)]
    [InlineData(ConnectionState.LaunchingFtp)]
    [InlineData(ConnectionState.LaunchingTelnet)]
    [InlineData(ConnectionState.LaunchingCitrix)]
    public void FullDirectWorkflow_Succeeds_ForAllProtocols(ConnectionState launchState)
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        string id = $"srv-{launchState}";

        Assert.True(sm.TryTransition(id, ConnectionState.Initializing));
        Assert.True(sm.TryTransition(id, ConnectionState.ValidatingConfig));
        Assert.True(sm.TryTransition(id, launchState));
        Assert.True(sm.TryTransition(id, ConnectionState.Connected));
        Assert.True(sm.TryTransition(id, ConnectionState.Disconnecting));
        Assert.True(sm.TryTransition(id, ConnectionState.Disconnected));
    }

    // ── Concurrent servers in different states ──────────────────────────

    [Fact]
    public void ConcurrentServers_ThreeServersInDifferentStates()
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();

        // Server 1: Connected
        sm.TryTransition("srv-1", ConnectionState.Initializing);
        sm.TryTransition("srv-1", ConnectionState.ValidatingConfig);
        sm.TryTransition("srv-1", ConnectionState.LaunchingSsh);
        sm.TryTransition("srv-1", ConnectionState.Connected);

        // Server 2: Establishing tunnel
        sm.TryTransition("srv-2", ConnectionState.Initializing);
        sm.TryTransition("srv-2", ConnectionState.ValidatingConfig);
        sm.TryTransition("srv-2", ConnectionState.EstablishingTunnel);

        // Server 3: Error state
        sm.TryTransition("srv-3", ConnectionState.Initializing);
        sm.SetError("srv-3", "Auth failed");

        Assert.Equal(ConnectionState.Connected, sm.GetState("srv-1"));
        Assert.Equal(ConnectionState.EstablishingTunnel, sm.GetState("srv-2"));
        Assert.Equal(ConnectionState.Error, sm.GetState("srv-3"));

        // Verify active connections excludes Error and Disconnected
        IReadOnlyDictionary<string, ConnectionStateData> active = sm.GetActiveConnections();
        Assert.Equal(2, active.Count);
        Assert.True(active.ContainsKey("srv-1"));
        Assert.True(active.ContainsKey("srv-2"));
        Assert.False(active.ContainsKey("srv-3"));
    }

    // ── Reset after Error state ─────────────────────────────────────────

    [Fact]
    public void ResetAfterError_TransitionsToDisconnected()
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        sm.TryTransition("srv", ConnectionState.Initializing);
        sm.TryTransition("srv", ConnectionState.ValidatingConfig);
        sm.TryTransition("srv", ConnectionState.LaunchingVnc);
        sm.SetError("srv", "Connection refused");

        Assert.Equal(ConnectionState.Error, sm.GetState("srv"));

        sm.Reset("srv");

        Assert.Equal(ConnectionState.Disconnected, sm.GetState("srv"));
        ConnectionStateData? data = sm.GetStateData("srv");
        Assert.NotNull(data);
        Assert.Null(data.ErrorMessage);
    }

    [Fact]
    public void ResetAfterError_CanReconnect()
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        sm.TryTransition("srv", ConnectionState.Initializing);
        sm.SetError("srv", "timeout");
        sm.Reset("srv");

        Assert.True(sm.TryTransition("srv", ConnectionState.Initializing));
        Assert.Equal(ConnectionState.Initializing, sm.GetState("srv"));
    }

    // ── Metadata for all launch states ──────────────────────────────────

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc, "StatusVncConnecting", "LogVncLaunching")]
    [InlineData(ConnectionState.LaunchingFtp, "StatusFtpConnecting", "LogFtpLaunching")]
    [InlineData(ConnectionState.LaunchingTelnet, "StatusTelnetConnecting", "LogTelnetLaunching")]
    [InlineData(ConnectionState.LaunchingCitrix, "StatusCitrixConnecting", "LogCitrixLaunching")]
    public void LaunchState_Metadata_IsProgressState(ConnectionState state, string displayKey, string logKey)
    {
        StateMetadata metadata = ConnectionStateMachine.GetMetadata(state);

        Assert.Equal(displayKey, metadata.DisplayKey);
        Assert.Equal(logKey, metadata.LogKey);
        Assert.False(metadata.IsTerminal);
        Assert.False(metadata.AllowsUserAction);
        Assert.True(metadata.IsProgress);
    }

    // ── Transition table coverage for Vnc/Ftp/Telnet/Citrix ─────────────

    [Theory]
    [InlineData(ConnectionState.LaunchingVnc, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingVnc, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingVnc, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingVnc, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingVnc, ConnectionState.Initializing, false)]
    [InlineData(ConnectionState.LaunchingFtp, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingFtp, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingFtp, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingFtp, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingTelnet, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingTelnet, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingTelnet, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingTelnet, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.LaunchingCitrix, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.LaunchingCitrix, ConnectionState.Error, true)]
    [InlineData(ConnectionState.LaunchingCitrix, ConnectionState.Disconnecting, true)]
    [InlineData(ConnectionState.LaunchingCitrix, ConnectionState.Disconnected, false)]
    public void NewLaunchStates_TransitionTable(ConnectionState from, ConnectionState to, bool expected)
    {
        Assert.Equal(expected, ConnectionStateMachine.IsValidTransition(from, to));
    }

    // ── WinRM gateway launches through the local terminal state ──────────

    [Fact]
    public void TunnelEstablished_CanTransitionToLaunchingLocal()
    {
        Assert.True(ConnectionStateMachine.IsValidTransition(
            ConnectionState.TunnelEstablished, ConnectionState.LaunchingLocal));
    }

    private static ConnectionStateMachine ArrangeAtLaunchedExternalClient(string serverId)
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        Assert.True(sm.TryTransition(serverId, ConnectionState.Initializing));
        Assert.True(sm.TryTransition(serverId, ConnectionState.ValidatingConfig));
        Assert.True(sm.TryTransition(serverId, ConnectionState.LaunchingRdp));
        Assert.True(sm.TryTransition(serverId, ConnectionState.LaunchedExternalClient));
        return sm;
    }

    private static ConnectionStateMachine ArrangeAtLaunchingLocal(string serverId)
    {
        ConnectionStateMachine sm = new ConnectionStateMachine();
        Assert.True(sm.TryTransition(serverId, ConnectionState.Initializing));
        Assert.True(sm.TryTransition(serverId, ConnectionState.ValidatingConfig));
        Assert.True(sm.TryTransition(serverId, ConnectionState.LaunchingLocal));
        return sm;
    }

    private static ConnectionStateMachine ArrangeAtRemoteSessionHandedOff(string serverId)
    {
        ConnectionStateMachine sm = ArrangeAtLaunchingLocal(serverId);
        Assert.True(sm.TryTransition(serverId, ConnectionState.RemoteSessionHandedOff));
        return sm;
    }

    private static void TransitionSshToConnected(ConnectionStateMachine sm, string serverId)
    {
        Assert.True(sm.TryTransition(serverId, ConnectionState.Initializing));
        Assert.True(sm.TryTransition(serverId, ConnectionState.ValidatingConfig));
        Assert.True(sm.TryTransition(serverId, ConnectionState.LaunchingSsh));
        Assert.True(sm.TryTransition(serverId, ConnectionState.Connected));
    }
}

public class ApplicationStatusMachineTests
{
    private readonly ApplicationStatusMachine _sm = new();

    [Fact]
    public void InitialStatus_IsInitializing()
    {
        Assert.Equal(ApplicationStatus.Initializing, _sm.CurrentStatus);
    }

    [Fact]
    public void TransitionToReady_Succeeds()
    {
        bool result = _sm.TryTransition(ApplicationStatus.Ready);

        Assert.True(result);
        Assert.Equal(ApplicationStatus.Ready, _sm.CurrentStatus);
    }

    [Fact]
    public void InvalidTransition_ReturnsFalse()
    {
        // Initializing -> Busy is not valid
        bool result = _sm.TryTransition(ApplicationStatus.Busy);

        Assert.False(result);
        Assert.Equal(ApplicationStatus.Initializing, _sm.CurrentStatus);
    }

    [Fact]
    public void SameStateTransition_IsNoOp_ReturnsTrue()
    {
        bool result = _sm.TryTransition(ApplicationStatus.Initializing);

        Assert.True(result);
        Assert.Equal(ApplicationStatus.Initializing, _sm.CurrentStatus);
    }

    [Fact]
    public void TransitionToBusy_StoresReason()
    {
        _sm.TryTransition(ApplicationStatus.Ready);

        _sm.TryTransition(ApplicationStatus.Busy, "Loading servers");

        Assert.Equal("Loading servers", _sm.BusyReason);
    }

    [Fact]
    public void TransitionToError_StoresMessage()
    {
        _sm.TryTransition(ApplicationStatus.Ready);

        _sm.TryTransition(ApplicationStatus.Error, "Config corrupted");

        Assert.Equal("Config corrupted", _sm.ErrorMessage);
    }

    [Fact]
    public void TransitionToReady_ClearsBusyAndError()
    {
        _sm.TryTransition(ApplicationStatus.Ready);
        _sm.TryTransition(ApplicationStatus.Busy, "working");
        _sm.TryTransition(ApplicationStatus.Ready);

        Assert.Null(_sm.BusyReason);
        Assert.Null(_sm.ErrorMessage);
    }

    [Fact]
    public void BeginOperation_TransitionsToBusy()
    {
        _sm.TryTransition(ApplicationStatus.Ready);

        using IDisposable op = _sm.BeginOperation("test operation");

        Assert.Equal(ApplicationStatus.Busy, _sm.CurrentStatus);
    }

    [Fact]
    public void DisposeOperation_ReturnsToReady()
    {
        _sm.TryTransition(ApplicationStatus.Ready);

        IDisposable op = _sm.BeginOperation("test");
        op.Dispose();

        Assert.Equal(ApplicationStatus.Ready, _sm.CurrentStatus);
    }

    [Fact]
    public void DisposeOperation_Idempotent()
    {
        _sm.TryTransition(ApplicationStatus.Ready);

        IDisposable op = _sm.BeginOperation("test");
        op.Dispose();
        op.Dispose(); // Should not throw or corrupt state

        Assert.Equal(ApplicationStatus.Ready, _sm.CurrentStatus);
    }

    [Fact]
    public void MultipleOperations_StaysBusy_UntilAllComplete()
    {
        _sm.TryTransition(ApplicationStatus.Ready);

        IDisposable op1 = _sm.BeginOperation("op1");
        IDisposable op2 = _sm.BeginOperation("op2");

        op1.Dispose();
        Assert.Equal(ApplicationStatus.Busy, _sm.CurrentStatus);

        op2.Dispose();
        Assert.Equal(ApplicationStatus.Ready, _sm.CurrentStatus);
    }

    [Fact]
    public void BeginOperation_Succeeds_WhenInitializing()
    {
        // Status is Initializing — allowed since startup needs tracked operations
        using IDisposable op = _sm.BeginOperation();
        Assert.NotNull(op);
    }

    [Fact]
    public void BeginOperation_Throws_WhenShutdown()
    {
        _sm.TryTransition(ApplicationStatus.Ready);
        _sm.TryTransition(ApplicationStatus.Shutdown);

        Assert.Throws<InvalidOperationException>(() => _sm.BeginOperation());
    }

    [Fact]
    public void AllowsUserAction_OnlyWhenReady()
    {
        Assert.False(_sm.AllowsUserAction); // Initializing

        _sm.TryTransition(ApplicationStatus.Ready);
        Assert.True(_sm.AllowsUserAction);

        _sm.TryTransition(ApplicationStatus.Busy);
        Assert.False(_sm.AllowsUserAction);
    }

    [Fact]
    public void StatusChanged_EventFires()
    {
        ApplicationStatus? receivedPrev = null;
        ApplicationStatus? receivedNew = null;

        _sm.StatusChanged += (prev, next) =>
        {
            receivedPrev = prev;
            receivedNew = next;
        };

        _sm.TryTransition(ApplicationStatus.Ready);

        Assert.Equal(ApplicationStatus.Initializing, receivedPrev);
        Assert.Equal(ApplicationStatus.Ready, receivedNew);
    }

    [Fact]
    public void StatusChanged_DoesNotFire_OnSameState()
    {
        bool fired = false;
        _sm.StatusChanged += (_, _) => { fired = true; };

        _sm.TryTransition(ApplicationStatus.Initializing);

        Assert.False(fired);
    }

    [Fact]
    public void StatusChanged_DoesNotFire_OnInvalidTransition()
    {
        bool fired = false;
        _sm.StatusChanged += (_, _) => { fired = true; };

        _sm.TryTransition(ApplicationStatus.Shutdown); // Invalid from Initializing

        Assert.False(fired);
    }

    [Fact]
    public void PreviousStatus_TracksLastTransition()
    {
        _sm.TryTransition(ApplicationStatus.Ready);
        _sm.TryTransition(ApplicationStatus.Busy);

        Assert.Equal(ApplicationStatus.Ready, _sm.PreviousStatus);
    }

    [Fact]
    public void Shutdown_IsTerminal()
    {
        _sm.TryTransition(ApplicationStatus.Ready);
        _sm.TryTransition(ApplicationStatus.Shutdown);

        Assert.Equal(ApplicationStatus.Shutdown, _sm.CurrentStatus);

        // No transitions allowed from Shutdown
        Assert.False(_sm.TryTransition(ApplicationStatus.Ready));
        Assert.False(_sm.TryTransition(ApplicationStatus.Error));
    }

    [Fact]
    public void ErrorRecovery_TransitionsToReady()
    {
        _sm.TryTransition(ApplicationStatus.Ready);
        _sm.TryTransition(ApplicationStatus.Error, "disk full");

        Assert.True(_sm.AllowsUserAction); // Error allows user action

        _sm.TryTransition(ApplicationStatus.Ready);
        Assert.Equal(ApplicationStatus.Ready, _sm.CurrentStatus);
    }

    [Theory]
    [InlineData(ApplicationStatus.Initializing, ApplicationStatus.Ready, true)]
    [InlineData(ApplicationStatus.Initializing, ApplicationStatus.Error, true)]
    [InlineData(ApplicationStatus.Initializing, ApplicationStatus.Busy, false)]
    [InlineData(ApplicationStatus.Initializing, ApplicationStatus.Shutdown, false)]
    [InlineData(ApplicationStatus.Ready, ApplicationStatus.Busy, true)]
    [InlineData(ApplicationStatus.Ready, ApplicationStatus.Shutdown, true)]
    [InlineData(ApplicationStatus.Ready, ApplicationStatus.Error, true)]
    [InlineData(ApplicationStatus.Ready, ApplicationStatus.Initializing, false)]
    [InlineData(ApplicationStatus.Busy, ApplicationStatus.Ready, true)]
    [InlineData(ApplicationStatus.Busy, ApplicationStatus.Error, true)]
    [InlineData(ApplicationStatus.Busy, ApplicationStatus.Shutdown, false)]
    [InlineData(ApplicationStatus.Error, ApplicationStatus.Ready, true)]
    [InlineData(ApplicationStatus.Error, ApplicationStatus.Shutdown, true)]
    [InlineData(ApplicationStatus.Error, ApplicationStatus.Busy, false)]
    [InlineData(ApplicationStatus.Shutdown, ApplicationStatus.Ready, false)]
    [InlineData(ApplicationStatus.Shutdown, ApplicationStatus.Error, false)]
    public void IsValidTransition_MatchesTable(ApplicationStatus from, ApplicationStatus to, bool expected)
    {
        Assert.Equal(expected, ApplicationStatusMachine.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(ApplicationStatus.Initializing, "AppStatusInitializing", false, false)]
    [InlineData(ApplicationStatus.Ready, "StatusReady", true, false)]
    [InlineData(ApplicationStatus.Busy, "AppStatusBusy", false, false)]
    [InlineData(ApplicationStatus.Error, "AppStatusError", true, false)]
    [InlineData(ApplicationStatus.Shutdown, "AppStatusShutdown", false, true)]
    public void GetMetadata_ReturnsCorrectValues(
        ApplicationStatus status,
        string expectedDisplayKey,
        bool expectedAllowsUserAction,
        bool expectedIsTerminal)
    {
        ApplicationStatusMetadata metadata = ApplicationStatusMachine.GetMetadata(status);

        Assert.Equal(expectedDisplayKey, metadata.DisplayKey);
        Assert.Equal(expectedAllowsUserAction, metadata.AllowsUserAction);
        Assert.Equal(expectedIsTerminal, metadata.IsTerminal);
    }
}
