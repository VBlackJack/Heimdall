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

using System.Collections.Concurrent;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Services;

/// <summary>
/// Background reachability monitor. Loads the server inventory from
/// <see cref="IConfigManager"/> on every cycle, runs a throttled batch of
/// <see cref="IHealthProbe"/> calls, and exposes the result both as a queryable
/// dictionary and as a per-server <see cref="StatusChanged"/> event the UI can
/// subscribe to.
/// </summary>
/// <remarks>
/// Gateway-fronted servers (<see cref="ServerProfileDto.SshGatewayId"/> set)
/// and servers whose protocol exposes no probe port (Citrix, Local Shell) are
/// recorded as <see cref="HealthStatus.Unknown"/> without consuming a probe
/// slot - the MVP scope chose direct TCP only.
/// </remarks>
public sealed class SessionHealthMonitor : IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly IHealthProbe _probe;
    private readonly ConcurrentDictionary<string, HealthState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stateGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _stateGates = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();

    private PeriodicTimer? _periodicTimer;
    private Task? _schedulerTask;
    private CancellationTokenSource? _cycleCts;
    private AppSettings? _currentSettings;
    private long _nextGeneration;
    private long _lifecycleVersion;
    private bool _disposed;

    public event Action<HealthStateChange>? StatusChanged;

    public SessionHealthMonitor(IConfigManager configManager, IHealthProbe probe)
    {
        _configManager = configManager;
        _probe = probe;
        _configManager.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Current per-server state snapshot. Safe to enumerate concurrently.</summary>
    public IReadOnlyDictionary<string, HealthState> States => _states;

    /// <summary>Returns the last known state for a server, or <see cref="HealthState.Initial"/> when never probed.</summary>
    public HealthState GetState(string serverId)
        => _states.TryGetValue(serverId, out var state) ? state : HealthState.Initial;

    internal long LifecycleVersion => Volatile.Read(ref _lifecycleVersion);

    /// <summary>
    /// Boots the monitor with the given settings snapshot. Safe to call repeatedly;
    /// each call cancels the in-flight cycle and re-arms the sequential scheduler
    /// with the new interval. When <see cref="AppSettings.SessionHealthMonitorEnabled"/>
    /// is false, the scheduler stays stopped and existing state is cleared.
    /// </summary>
    public void Start(AppSettings settings) => Start(settings, armTimer: true);

    /// <summary>
    /// Internal seam used by unit tests to set up the throttle and settings without
    /// arming the background scheduler (which would race with manual
    /// <see cref="RunCycleAsync"/> calls).
    /// </summary>
    internal void Start(AppSettings settings, bool armTimer)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;

            _currentSettings = settings;
            StopUnsafe();
            long lifecycleVersion = Interlocked.Increment(ref _lifecycleVersion);

            if (!settings.SessionHealthMonitorEnabled)
            {
                _states.Clear();
                return;
            }

            var intervalSeconds = Math.Max(SettingRanges.Of(nameof(AppSettings.SessionHealthCheckIntervalSeconds)).Min, settings.SessionHealthCheckIntervalSeconds);

            _cycleCts = new CancellationTokenSource();
            if (armTimer)
            {
                _periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
                PeriodicTimer timer = _periodicTimer;
                CancellationToken token = _cycleCts.Token;
                _schedulerTask = Task.Run(
                    () => RunSequentialLoopAsync(
                        ct => timer.WaitForNextTickAsync(ct).AsTask(),
                        lifecycleVersion,
                        token),
                    CancellationToken.None);
            }
        }
    }

    /// <summary>Stops the timer and cancels any in-flight cycle.</summary>
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            StopUnsafe();
        }
    }

    private void StopUnsafe()
    {
        _periodicTimer?.Dispose();
        _periodicTimer = null;
        _schedulerTask = null;

        // Cancel the in-flight cycle, but defer disposing the source: a running
        // cycle still holds this token and must observe cancellation (not an
        // ObjectDisposedException) before the source is reclaimed.
        var cts = _cycleCts;
        _cycleCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* Already disposed. */ }

            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(
                _ => cts.Dispose(),
                TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _configManager.SettingsChanged -= OnSettingsChanged;
            StopUnsafe();
        }
    }

    private void OnSettingsChanged(AppSettings settings) => Start(settings);

    /// <summary>
    /// Executes a single probe cycle. Exposed as internal so unit tests can drive
    /// the scheduler deterministically without relying on the Timer.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        long lifecycleVersion = Volatile.Read(ref _lifecycleVersion);
        await RunCycleAsync(lifecycleVersion, ct).ConfigureAwait(false);
    }

    private async Task RunCycleAsync(long lifecycleVersion, CancellationToken ct)
    {
        var settings = _currentSettings;
        if (settings is null) return;

        long generation = Interlocked.Increment(ref _nextGeneration);
        var maxConcurrent = Math.Max(SettingRanges.Of(nameof(AppSettings.SessionHealthMaxConcurrent)).Min, settings.SessionHealthMaxConcurrent);
        var profiles = await _configManager.LoadServersAsync().ConfigureAwait(false);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // The throttle is scoped to this cycle: created and disposed here, so a
        // concurrent Stop()/Start() can never dispose a semaphore an in-flight
        // probe still holds.
        using var throttle = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var tasks = new List<Task>(profiles.Count);
        foreach (var dto in profiles)
        {
            if (string.IsNullOrEmpty(dto.Id)) continue;
            seenIds.Add(dto.Id);
            tasks.Add(ProbeOneAsync(
                dto,
                settings.SessionHealthProbeTimeoutMs,
                throttle,
                generation,
                lifecycleVersion,
                ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Drop state for servers removed from the inventory between cycles.
        foreach (var key in _states.Keys.ToList())
        {
            if (!seenIds.Contains(key))
            {
                PruneState(key, generation, lifecycleVersion);
            }
        }
    }

    internal async Task RunSequentialLoopAsync(
        Func<CancellationToken, Task<bool>> waitForNextTickAsync,
        CancellationToken ct)
    {
        long lifecycleVersion = Volatile.Read(ref _lifecycleVersion);
        await RunSequentialLoopAsync(
            waitForNextTickAsync,
            lifecycleVersion,
            ct).ConfigureAwait(false);
    }

    private async Task RunSequentialLoopAsync(
        Func<CancellationToken, Task<bool>> waitForNextTickAsync,
        long lifecycleVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(waitForNextTickAsync);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(lifecycleVersion, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"SessionHealthMonitor cycle failed: {ex.Message}");
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (!await waitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"SessionHealthMonitor scheduler failed: {ex.Message}");
                return;
            }
        }
    }

    private async Task ProbeOneAsync(
        ServerProfileDto dto,
        int timeoutMs,
        SemaphoreSlim throttle,
        long generation,
        long lifecycleVersion,
        CancellationToken ct)
    {
        // Gateway-fronted servers and protocols without a probe port short-circuit
        // before they queue against the throttle, leaving slots free for probes
        // that will actually hit the network.
        if (!string.IsNullOrEmpty(dto.SshGatewayId))
        {
            PublishState(
                dto.Id,
                new HealthState(HealthStatus.Unknown, DateTime.UtcNow, null, "behind-gateway"),
                generation,
                lifecycleVersion);
            return;
        }

        var port = ResolveProbePort(dto);
        if (!port.HasValue || port.Value <= 0)
        {
            PublishState(
                dto.Id,
                new HealthState(HealthStatus.Unknown, DateTime.UtcNow, null, "no-port"),
                generation,
                lifecycleVersion);
            return;
        }

        if (string.IsNullOrWhiteSpace(dto.RemoteServer))
        {
            PublishState(
                dto.Id,
                new HealthState(HealthStatus.Unknown, DateTime.UtcNow, null, "no-host"),
                generation,
                lifecycleVersion);
            return;
        }

        await throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested) return;

            PublishState(
                dto.Id,
                new HealthState(HealthStatus.Probing, DateTime.UtcNow, null, null),
                generation,
                lifecycleVersion);
            var result = await _probe.ProbeAsync(dto.RemoteServer, port.Value, timeoutMs, ct).ConfigureAwait(false);
            PublishState(dto.Id, result, generation, lifecycleVersion);
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>
    /// Maps a server profile to its probe port. SSH and SFTP share the SSH port;
    /// Citrix (StoreFront HTTP/HTTPS in this codebase) and Local Shell are
    /// intentionally non-probable in the MVP.
    /// </summary>
    internal static int? ResolveProbePort(ServerProfileDto dto)
    {
        return (dto.ConnectionType ?? string.Empty).ToUpperInvariant() switch
        {
            "RDP" => dto.RemotePort,
            "SSH" or "SFTP" => dto.SshPort,
            "VNC" => dto.VncPort,
            "FTP" => dto.FtpPort,
            "TELNET" => dto.TelnetPort,
            _ => null
        };
    }

    internal bool PublishState(string serverId, HealthState state, long generation)
    {
        return PublishState(
            serverId,
            state,
            generation,
            Volatile.Read(ref _lifecycleVersion));
    }

    internal bool PublishState(
        string serverId,
        HealthState state,
        long generation,
        long lifecycleVersion)
    {
        if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
        {
            return false;
        }

        object stateGate = _stateGates.GetOrAdd(serverId, static _ => new object());
        lock (stateGate)
        {
            if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
            {
                return false;
            }

            if (_stateGenerations.TryGetValue(serverId, out long currentGeneration)
                && generation < currentGeneration)
            {
                return false;
            }

            _stateGenerations[serverId] = generation;
            _states[serverId] = state;
        }

        if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
        {
            return false;
        }

        StatusChanged?.Invoke(new HealthStateChange(serverId, state, generation));
        return true;
    }

    private void PruneState(
        string serverId,
        long generation,
        long lifecycleVersion)
    {
        if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
        {
            return;
        }

        object stateGate = _stateGates.GetOrAdd(serverId, static _ => new object());
        lock (stateGate)
        {
            if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
            {
                return;
            }

            if (_stateGenerations.TryGetValue(serverId, out long currentGeneration)
                && currentGeneration > generation)
            {
                return;
            }

            _stateGenerations[serverId] = generation;
            // Keep the generation as a tombstone after removing the visible state.
            // A lingering older cycle must not be able to recreate a removed server.
            _states.TryRemove(serverId, out _);
        }
    }
}

public readonly record struct HealthStateChange(
    string ServerId,
    HealthState State,
    long Generation);
