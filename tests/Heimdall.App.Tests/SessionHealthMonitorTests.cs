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

        var updates = new List<(string Id, HealthState State)>();
        fixture.Monitor.StatusChanged += (id, state) => updates.Add((id, state));

        await fixture.RunCycleAsync();

        Assert.Single(updates);
        Assert.Equal("srv-gw", updates[0].Id);
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
        fixture.Monitor.StatusChanged += (_, state) => updates.Add(state.Status);

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

    // ── Test doubles ─────────────────────────────────────────────────

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
