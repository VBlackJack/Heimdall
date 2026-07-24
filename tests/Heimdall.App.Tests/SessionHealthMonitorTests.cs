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
using Heimdall.Core.Configuration;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="SessionHealthMonitor"/>. Drives the monitor via
/// the internal <c>RunCycleAsync</c> seam so no real timer fires and no real
/// socket is opened — <see cref="FakeHealthProbe"/> replays canned states.
/// </summary>
public class SessionHealthMonitorTests
{
    [Theory]
    [InlineData("RDP", 3389)]
    [InlineData("rdp", 3389)]
    [InlineData("SSH", 22)]
    [InlineData("SFTP", 22)]
    [InlineData("VNC", 5900)]
    [InlineData("FTP", 21)]
    [InlineData("TELNET", 23)]
    public void ResolveProbePort_MapsProtocolToDeclaredPort(string protocol, int expectedPort)
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = protocol,
            RemotePort = 3389,
            SshPort = 22,
            VncPort = 5900,
            FtpPort = 21,
            TelnetPort = 23
        };

        Assert.Equal(expectedPort, SessionHealthMonitor.ResolveProbePort(dto));
    }

    [Theory]
    [InlineData("CITRIX")]
    [InlineData("LOCAL")]
    [InlineData("")]
    [InlineData("UNKNOWN_PROTOCOL")]
    public void ResolveProbePort_ReturnsNull_ForNonProbableProtocols(string protocol)
    {
        var dto = new ServerProfileDto { ConnectionType = protocol };

        Assert.Null(SessionHealthMonitor.ResolveProbePort(dto));
    }

    [Fact]
    public async Task GatewayFrontedServer_IsMarkedUnknown_WithoutHittingProbe()
    {
        var probe = new FakeHealthProbe();
        await using var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-1",
            RemoteServer = "10.0.0.1",
            ConnectionType = "SSH",
            SshPort = 22,
            SshGatewayId = "gw-42"
        });

        await fixture.RunCycleAsync();

        Assert.Equal(0, probe.CallCount);
        var state = fixture.Monitor.GetState("srv-1");
        Assert.Equal(HealthStatus.Unknown, state.Status);
        Assert.Equal("behind-gateway", state.Reason);
    }

    [Fact]
    public async Task EmptyHost_IsMarkedUnknown_WithoutHittingProbe()
    {
        var probe = new FakeHealthProbe();
        await using var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-2",
            RemoteServer = "   ",
            ConnectionType = "SSH",
            SshPort = 22
        });

        await fixture.RunCycleAsync();

        Assert.Equal(0, probe.CallCount);
        Assert.Equal("no-host", fixture.Monitor.GetState("srv-2").Reason);
    }

    [Fact]
    public async Task NonProbableProtocol_IsMarkedUnknown_WithoutHittingProbe()
    {
        var probe = new FakeHealthProbe();
        await using var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-3",
            RemoteServer = "storefront.example.com",
            ConnectionType = "CITRIX"
        });

        await fixture.RunCycleAsync();

        Assert.Equal(0, probe.CallCount);
        Assert.Equal("no-port", fixture.Monitor.GetState("srv-3").Reason);
    }

    [Fact]
    public async Task SuccessfulProbe_PublishesUpState_WithLatency()
    {
        var probe = new FakeHealthProbe((_, _, _, _) =>
            new HealthState(HealthStatus.Up, DateTime.UtcNow, 42, null));
        await using var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-up",
            RemoteServer = "ssh.example.com",
            ConnectionType = "SSH",
            SshPort = 22
        });

        await fixture.RunCycleAsync();

        var state = fixture.Monitor.GetState("srv-up");
        Assert.Equal(HealthStatus.Up, state.Status);
        Assert.Equal(42, state.LatencyMs);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task FailedProbe_PublishesDownState_WithReason()
    {
        var probe = new FakeHealthProbe((_, _, _, _) =>
            new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "refused"));
        await using var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-down",
            RemoteServer = "vnc.example.com",
            ConnectionType = "VNC",
            VncPort = 5900
        });

        await fixture.RunCycleAsync();

        var state = fixture.Monitor.GetState("srv-down");
        Assert.Equal(HealthStatus.Down, state.Status);
        Assert.Equal("refused", state.Reason);
    }

    [Fact]
    public async Task StatusChangedEvent_FiresOnceForGatewayShortCircuit()
    {
        var probe = new FakeHealthProbe();
        var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-gw",
            RemoteServer = "host",
            ConnectionType = "SSH",
            SshPort = 22,
            SshGatewayId = "gw"
        });
        await using var _ = fixture;

        var updates = new List<HealthStateChange>();
        fixture.Monitor.StatusChanged += updates.Add;

        await fixture.RunCycleAsync();

        Assert.Single(updates);
        Assert.Equal("srv-gw", updates[0].ServerId);
        Assert.Equal(HealthStatus.Unknown, updates[0].State.Status);
    }

    [Fact]
    public async Task StatusChangedEvent_FiresTwiceForRealProbe_ProbingThenResult()
    {
        var probe = new FakeHealthProbe((_, _, _, _) =>
            new HealthState(HealthStatus.Up, DateTime.UtcNow, 5, null));
        var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-x",
            RemoteServer = "host",
            ConnectionType = "RDP",
            RemotePort = 3389
        });
        await using var _ = fixture;

        var updates = new List<HealthStatus>();
        fixture.Monitor.StatusChanged += change => updates.Add(change.State.Status);

        await fixture.RunCycleAsync();

        Assert.Equal(new[] { HealthStatus.Probing, HealthStatus.Up }, updates);
    }

    [Fact]
    public async Task RemovedServer_HasItsStateEvicted_OnNextCycle()
    {
        var probe = new FakeHealthProbe((_, _, _, _) =>
            new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null));
        var fakeConfig = new FakeConfigManager(new ServerProfileDto
        {
            Id = "srv-keep",
            RemoteServer = "host",
            ConnectionType = "SSH",
            SshPort = 22
        }, new ServerProfileDto
        {
            Id = "srv-drop",
            RemoteServer = "host",
            ConnectionType = "SSH",
            SshPort = 22
        });

        await using var fixture = new MonitorFixture(fakeConfig, probe);
        await fixture.RunCycleAsync();

        Assert.True(fixture.Monitor.States.ContainsKey("srv-keep"));
        Assert.True(fixture.Monitor.States.ContainsKey("srv-drop"));

        fakeConfig.RemoveServer("srv-drop");
        await fixture.RunCycleAsync();

        Assert.True(fixture.Monitor.States.ContainsKey("srv-keep"));
        Assert.False(fixture.Monitor.States.ContainsKey("srv-drop"));
    }

    [Fact]
    public async Task DisabledMonitor_DoesNothing_AndClearsState()
    {
        var probe = new FakeHealthProbe((_, _, _, _) =>
            new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null));
        var fakeConfig = new FakeConfigManager(new ServerProfileDto
        {
            Id = "srv-1",
            RemoteServer = "host",
            ConnectionType = "SSH",
            SshPort = 22
        });

        await using var fixture = new MonitorFixture(fakeConfig, probe);
        await fixture.RunCycleAsync();
        Assert.True(fixture.Monitor.States.ContainsKey("srv-1"));

        fixture.Monitor.Start(new AppSettings { SessionHealthMonitorEnabled = false });
        Assert.Empty(fixture.Monitor.States);
    }

    [Fact]
    public async Task StopDuringInFlightCycle_DoesNotDisposeProbeThrottle()
    {
        var probe = new BlockingHealthProbe();
        await using var fixture = new MonitorFixture(probe, new ServerProfileDto
        {
            Id = "srv-slow",
            RemoteServer = "slow.example.com",
            ConnectionType = "SSH",
            SshPort = 22
        });

        var cycleTask = fixture.Monitor.RunCycleAsync(CancellationToken.None);
        await probe.WaitUntilEnteredAsync();

        fixture.Monitor.Stop();
        probe.Complete();

        await cycleTask.WaitAsync(TimeSpan.FromSeconds(5));

        var state = fixture.Monitor.GetState("srv-slow");
        Assert.Equal(HealthStatus.Up, state.Status);
    }

    [Fact]
    public async Task PublishState_OlderGenerationCannotOverwriteNewerState()
    {
        await using var fixture = new MonitorFixture(new FakeHealthProbe());
        var updates = new List<HealthStateChange>();
        fixture.Monitor.StatusChanged += updates.Add;

        bool newerApplied = fixture.Monitor.PublishState(
            "srv-versioned",
            new HealthState(HealthStatus.Up, DateTime.UtcNow, 4, null),
            generation: 20);
        bool olderApplied = fixture.Monitor.PublishState(
            "srv-versioned",
            new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "late"),
            generation: 19);

        Assert.True(newerApplied);
        Assert.False(olderApplied);
        Assert.Equal(HealthStatus.Up, fixture.Monitor.GetState("srv-versioned").Status);
        HealthStateChange update = Assert.Single(updates);
        Assert.Equal(20, update.Generation);
    }

    [Fact]
    public async Task RunCycleAsync_AssignsIncreasingGenerationPerCycle()
    {
        await using var fixture = new MonitorFixture(
            new FakeHealthProbe(),
            new ServerProfileDto
            {
                Id = "srv-generation",
                RemoteServer = "host",
                ConnectionType = "SSH",
                SshPort = 22
            });
        var updates = new List<HealthStateChange>();
        fixture.Monitor.StatusChanged += updates.Add;

        await fixture.RunCycleAsync();
        long firstGeneration = Assert.Single(
            updates.Select(update => update.Generation).Distinct());

        updates.Clear();
        await fixture.RunCycleAsync();
        long secondGeneration = Assert.Single(
            updates.Select(update => update.Generation).Distinct());

        Assert.True(secondGeneration > firstGeneration);
    }

    [Fact]
    public async Task SequentialLoop_WaitsForCurrentCycleBeforeStartingNext()
    {
        var probe = new SequentialBlockingProbe();
        await using var fixture = new MonitorFixture(
            probe,
            new ServerProfileDto
            {
                Id = "srv-sequential",
                RemoteServer = "host",
                ConnectionType = "SSH",
                SshPort = 22
            });
        using var loopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nextTick = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waitCalls = 0;

        Task loop = fixture.Monitor.RunSequentialLoopAsync(
            ct =>
            {
                Interlocked.Increment(ref waitCalls);
                return nextTick.Task.WaitAsync(ct);
            },
            loopCts.Token);

        await probe.WaitUntilEnteredAsync(callNumber: 1);
        Assert.Equal(0, Volatile.Read(ref waitCalls));
        Assert.Equal(1, probe.MaxConcurrentCalls);

        probe.Complete(callNumber: 1);
        await WaitUntilAsync(() => Volatile.Read(ref waitCalls) == 1);
        Assert.Equal(1, probe.CallCount);

        nextTick.SetResult(true);
        await probe.WaitUntilEnteredAsync(callNumber: 2);
        Assert.Equal(2, probe.CallCount);
        Assert.Equal(1, probe.MaxConcurrentCalls);

        loopCts.Cancel();
        probe.Complete(callNumber: 2);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledOlderCycle_CannotClobberNewerVerdict()
    {
        var probe = new OutOfOrderHealthProbe();
        await using var fixture = new MonitorFixture(
            probe,
            new ServerProfileDto
            {
                Id = "srv-late",
                RemoteServer = "host",
                ConnectionType = "SSH",
                SshPort = 22
            });
        using var olderCycleCts = new CancellationTokenSource();

        Task olderCycle = fixture.Monitor.RunCycleAsync(olderCycleCts.Token);
        await probe.WaitUntilFirstEnteredAsync();
        olderCycleCts.Cancel();

        await fixture.Monitor.RunCycleAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Down, fixture.Monitor.GetState("srv-late").Status);

        probe.CompleteFirst();
        await olderCycle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthStatus.Down, fixture.Monitor.GetState("srv-late").Status);
    }

    [Fact]
    public async Task Start_RearmRejectsLateResultFromPreviousGeneration()
    {
        await using var fixture = new MonitorFixture(
            new FakeHealthProbe(),
            new ServerProfileDto
            {
                Id = "srv-rearm",
                RemoteServer = "host",
                ConnectionType = "SSH",
                SshPort = 22
            });
        var updates = new List<HealthStateChange>();
        fixture.Monitor.StatusChanged += updates.Add;
        await fixture.RunCycleAsync();
        long previousGeneration = updates.Max(update => update.Generation);
        long previousLifecycleVersion = fixture.Monitor.LifecycleVersion;

        fixture.Monitor.Start(
            new AppSettings
            {
                SessionHealthMonitorEnabled = true,
                SessionHealthCheckIntervalSeconds = 30
            },
            armTimer: false);

        bool lateApplied = fixture.Monitor.PublishState(
            "srv-rearm",
            new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "old-scheduler"),
            previousGeneration,
            previousLifecycleVersion);

        Assert.False(lateApplied);
        Assert.Equal(HealthStatus.Up, fixture.Monitor.GetState("srv-rearm").Status);
    }

    // ── Test doubles ─────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeHealthProbe : IHealthProbe
    {
        private readonly Func<string, int, int, CancellationToken, HealthState> _responder;
        public int CallCount { get; private set; }

        public FakeHealthProbe(Func<string, int, int, CancellationToken, HealthState>? responder = null)
        {
            _responder = responder ?? ((_, _, _, _) =>
                new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null));
        }

        public Task<HealthState> ProbeAsync(string host, int port, int timeoutMs, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_responder(host, port, timeoutMs, ct));
        }
    }

    private sealed class FakeConfigManager : IConfigManager
    {
        private readonly List<ServerProfileDto> _profiles;
        private readonly AppSettings _settings = new() { SessionHealthMonitorEnabled = true };

        public event Action<AppSettings>? SettingsChanged;

        public FakeConfigManager(params ServerProfileDto[] profiles)
        {
            _profiles = profiles.ToList();
        }

        public void RemoveServer(string id) => _profiles.RemoveAll(p => p.Id == id);

        // raise compiler-unused warning suppressor — the event is part of the contract,
        // tests don't invoke it but the interface requires it to compile.
        private void TouchEvent() => SettingsChanged?.Invoke(_settings);

        public Task<List<ServerProfileDto>> LoadServersAsync()
            => Task.FromResult(_profiles.ToList());

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(_settings);

        // ── Unused interface members (test fixture only uses the two methods above) ──
        public string ConfigPath => string.Empty;
        public string SettingsPath => string.Empty;
        public string ServersPath => string.Empty;
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate(_profiles.ToList()));
        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;
        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => Task.FromResult(false);
        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) => Task.FromResult(0);
        public Task MergeSettingAsync(Action<AppSettings> mutate) => Task.CompletedTask;
    }

    private sealed class BlockingHealthProbe : IHealthProbe
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HealthState> ProbeAsync(string host, int port, int timeoutMs, CancellationToken ct)
        {
            _entered.SetResult();
            await _complete.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null);
        }

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Complete() => _complete.SetResult();
    }

    private sealed class SequentialBlockingProbe : IHealthProbe
    {
        private readonly TaskCompletionSource[] _entered =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];
        private readonly TaskCompletionSource[] _complete =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];
        private int _callCount;
        private int _activeCalls;
        private int _maxConcurrentCalls;

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public async Task<HealthState> ProbeAsync(
            string host,
            int port,
            int timeoutMs,
            CancellationToken ct)
        {
            int callNumber = Interlocked.Increment(ref _callCount);
            int activeCalls = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(ref _maxConcurrentCalls, activeCalls);
            _entered[callNumber - 1].TrySetResult();

            try
            {
                await _complete[callNumber - 1].Task.ConfigureAwait(false);
                return new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task WaitUntilEnteredAsync(int callNumber)
        {
            return _entered[callNumber - 1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Complete(int callNumber)
        {
            _complete[callNumber - 1].TrySetResult();
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            int current = Volatile.Read(ref target);
            while (candidate > current)
            {
                int observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class OutOfOrderHealthProbe : IHealthProbe
    {
        private readonly TaskCompletionSource _firstEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeFirst = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public async Task<HealthState> ProbeAsync(
            string host,
            int port,
            int timeoutMs,
            CancellationToken ct)
        {
            int callNumber = Interlocked.Increment(ref _callCount);
            if (callNumber == 1)
            {
                _firstEntered.TrySetResult();
                await _completeFirst.Task.ConfigureAwait(false);
                return new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null);
            }

            return new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "newer");
        }

        public Task WaitUntilFirstEnteredAsync()
        {
            return _firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void CompleteFirst()
        {
            _completeFirst.TrySetResult();
        }
    }

    private sealed class MonitorFixture : IAsyncDisposable
    {
        public SessionHealthMonitor Monitor { get; }
        private readonly FakeConfigManager _configManager;

        public MonitorFixture(IHealthProbe probe, params ServerProfileDto[] profiles)
            : this(new FakeConfigManager(profiles), probe) { }

        public MonitorFixture(FakeConfigManager configManager, IHealthProbe probe)
        {
            _configManager = configManager;
            Monitor = new SessionHealthMonitor(configManager, probe);
            Monitor.Start(new AppSettings { SessionHealthMonitorEnabled = true }, armTimer: false);
        }

        public Task RunCycleAsync() => Monitor.RunCycleAsync(CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            Monitor.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
