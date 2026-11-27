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

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// Keeps a small number of idle hosts alive so successive sessions can share them.
/// </summary>
/// <remarks>
/// <para>
/// A host that has ever connected costs a measured 66 kernel handles that the operating
/// system never gets back, against roughly 3 for reusing one. The leak is inside the RDP
/// ActiveX control and cannot be repaired from here; not creating a new control is the
/// entire remedy, and this is what makes that possible.
/// </para>
/// <para>
/// <b>What an idle host costs, and why it expires.</b> Each one holds the resources of a
/// control that has connected, about 300 MB of private commit measured on 2026-08-24, so a
/// Heimdall that has been used sat 600 MB above one just started, for as long as it ran.
/// The capacity and the idle expiry are read live from delegates, so a settings change
/// applies without a restart; <see cref="Trim"/> is what disposes what has expired, and it
/// is the caller's timer that decides when to ask.
/// </para>
/// <para>
/// <b>UI thread only.</b> The pooled hosts are Windows Forms controls, which belong to the
/// thread that created them. No locking is used here, deliberately: a lock would make this
/// class look thread-safe while the objects it hands out would still not be.
/// </para>
/// </remarks>
public sealed class ReusableHostPool<T> : IDisposable
    where T : class, IReusableHost
{
    /// <summary>
    /// Idle hosts kept by default. Small on purpose: each one holds the resources of a
    /// control that has connected, so a large pool trades the leak for a standing cost.
    /// </summary>
    public const int DefaultCapacity = 2;

    /// <summary>
    /// How long an idle host is kept before <see cref="Trim"/> disposes it, when the owner
    /// does not say otherwise.
    /// </summary>
    public static readonly TimeSpan DefaultIdleExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a released host may wait for its disconnect to settle. The asynchronous mstscax
    /// disconnect normally completes well within this; a control that has not disconnected by
    /// then is disposed exactly as it was before the wait existed, so the bound only limits
    /// how long a connected control lingers.
    /// </summary>
    public static readonly TimeSpan DefaultDisconnectSettleTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<T> _factory;
    private readonly Func<int> _capacity;
    private readonly Func<TimeSpan> _idleExpiry;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;
    private readonly TimeSpan _settleTimeout;

    /// <summary>Idle hosts, oldest release first. Acquire takes from the end.</summary>
    private readonly List<IdleHost> _idle = new();

    /// <summary>Released hosts still disconnecting, oldest first. Never handed out.</summary>
    private readonly List<SettlingHost> _settling = new();
    private bool _disposed;

    /// <summary>Creates a pool of fixed capacity whose idle hosts never expire.</summary>
    /// <remarks>
    /// The shape pooling shipped with. Kept so nothing that relied on "an idle host is kept
    /// until the pool is disposed" changes under it; the expiring shape is the other constructor.
    /// </remarks>
    public ReusableHostPool(Func<T> factory, int capacity = DefaultCapacity, Action<string>? log = null)
        : this(factory, () => capacity, static () => TimeSpan.Zero, TimeProvider.System, log)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
    }

    /// <summary>Creates a pool whose capacity and idle expiry are read live.</summary>
    /// <param name="factory">Builds a host when none is idle.</param>
    /// <param name="capacity">Idle hosts to keep; read at every release and trim. Negative reads as zero.</param>
    /// <param name="idleExpiry">
    /// How long an idle host is kept; read at every trim. Zero or less means it is kept until
    /// the pool is disposed.
    /// </param>
    /// <param name="timeProvider">The clock idle time is measured by; the system clock by default.</param>
    /// <param name="log">Where the pool says what it did.</param>
    /// <param name="disconnectSettleTimeout">
    /// How long a released host may wait for its disconnect to settle;
    /// <see cref="DefaultDisconnectSettleTimeout"/> when omitted.
    /// </param>
    public ReusableHostPool(
        Func<T> factory,
        Func<int> capacity,
        Func<TimeSpan> idleExpiry,
        TimeProvider? timeProvider = null,
        Action<string>? log = null,
        TimeSpan? disconnectSettleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(idleExpiry);

        _factory = factory;
        _capacity = capacity;
        _idleExpiry = idleExpiry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
        _settleTimeout = disconnectSettleTimeout ?? DefaultDisconnectSettleTimeout;
    }

    /// <summary>Idle hosts currently held. Exposed so the pool's behaviour can be asserted.</summary>
    public int IdleCount => _idle.Count;

    /// <summary>Released hosts still waiting for their disconnect to settle.</summary>
    public int SettleCount => _settling.Count;

    /// <summary>Hosts handed out since construction that came from the pool rather than the factory.</summary>
    public int ReuseCount { get; private set; }

    /// <summary>Hosts built by the factory since construction.</summary>
    public int CreationCount { get; private set; }

    /// <summary>The number of idle hosts the pool keeps, as its owner says right now.</summary>
    public int Capacity => Math.Max(0, _capacity());

    /// <summary>How long an idle host is kept, as its owner says right now. Zero or less: for ever.</summary>
    public TimeSpan IdleExpiry => _idleExpiry();

    /// <summary>
    /// Hands out an idle host if there is one, and builds a new one otherwise.
    /// </summary>
    /// <remarks>
    /// The most recently released host goes first: it is the one whose expiry is furthest
    /// away, so the oldest keeps ageing towards <see cref="Trim"/> instead of being revived.
    /// </remarks>
    public T Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_idle.Count > 0)
        {
            IdleHost candidate = _idle[^1];
            _idle.RemoveAt(_idle.Count - 1);

            // A host can go bad while it sits idle, so its state is checked on the way out
            // as well as on the way in.
            if (candidate.Host.IsReusable)
            {
                ReuseCount++;
                _log?.Invoke($"ReusableHostPool: reusing an idle host, idle={_idle.Count} reused={ReuseCount}");
                return candidate.Host;
            }

            DisposeQuietly(candidate.Host, "an idle host was no longer reusable");
        }

        CreationCount++;
        _log?.Invoke($"ReusableHostPool: no idle host available, creating one, created={CreationCount}");
        return _factory();
    }

    /// <summary>
    /// Takes a host back. It is pooled only if it can be returned to a neutral state and
    /// there is room; otherwise it is disposed.
    /// </summary>
    public void Release(T host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_disposed)
        {
            DisposeQuietly(host, "the pool is disposed");
            return;
        }

        if (!host.IsReusable)
        {
            DisposeQuietly(host, "the host reported itself unusable");
            return;
        }

        if (_idle.Count >= Capacity)
        {
            DisposeQuietly(host, "the pool is full");
            return;
        }

        if (!SafeIsDisconnectSettled(host))
        {
            _settling.Add(new SettlingHost(host, _timeProvider.GetUtcNow()));
            _log?.Invoke($"ReusableHostPool: host waiting for its disconnect to settle, settling={_settling.Count}");
            return;
        }

        ResetAndPool(host);
    }

    /// <summary>
    /// Settles the hosts released while their disconnect was still in flight: a host that has
    /// settled goes through the normal release path, one that became unusable or has waited
    /// past the timeout is disposed, and the rest keep waiting.
    /// </summary>
    /// <returns>The number of hosts that left the settling list, pooled or disposed.</returns>
    /// <remarks>Oldest first. The caller's timer decides when to ask.</remarks>
    public int ProcessSettling()
    {
        if (_disposed || _settling.Count == 0)
        {
            return 0;
        }

        int resolved = 0;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int index = 0;
        while (index < _settling.Count)
        {
            SettlingHost entry = _settling[index];
            if (!entry.Host.IsReusable)
            {
                _settling.RemoveAt(index);
                DisposeQuietly(entry.Host, "it became unusable while its disconnect settled");
                resolved++;
            }
            else if (SafeIsDisconnectSettled(entry.Host))
            {
                _settling.RemoveAt(index);
                _log?.Invoke($"ReusableHostPool: host disconnect settled after {(now - entry.ReleasedAt).TotalMilliseconds:0} ms");
                CompleteRelease(entry.Host);
                resolved++;
            }
            else if (now - entry.ReleasedAt >= _settleTimeout)
            {
                _settling.RemoveAt(index);
                DisposeQuietly(entry.Host, $"its disconnect did not settle within {_settleTimeout.TotalSeconds:0} s");
                resolved++;
            }
            else
            {
                index++;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The release path from the capacity check onwards, shared by an immediate release and
    /// by a host whose disconnect has settled since.
    /// </summary>
    private void CompleteRelease(T host)
    {
        if (_idle.Count >= Capacity)
        {
            DisposeQuietly(host, "the pool is full");
            return;
        }

        ResetAndPool(host);
    }

    /// <summary>A host that throws while reporting its state has not settled.</summary>
    private bool SafeIsDisconnectSettled(T host)
    {
        try
        {
            return host.IsDisconnectSettled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ResetAndPool(T host)
    {
        bool reset;
        try
        {
            reset = host.ResetForReuse();
        }
        catch (Exception ex)
        {
            DisposeQuietly(host, $"resetting it threw: {ex.Message}");
            return;
        }

        if (!reset)
        {
            DisposeQuietly(host, "it could not be reset");
            return;
        }

        _idle.Add(new IdleHost(host, _timeProvider.GetUtcNow()));
        _log?.Invoke($"ReusableHostPool: host returned to the pool, idle={_idle.Count}");
    }

    /// <summary>
    /// Disposes the idle hosts that have outlived the expiry, and the oldest ones beyond the
    /// capacity when it has been lowered since they were pooled.
    /// </summary>
    /// <returns>The number of hosts disposed.</returns>
    /// <remarks>
    /// Oldest first in both cases: the host released longest ago is the one least likely to
    /// be asked for again, and the one that has held its memory longest.
    /// </remarks>
    public int Trim()
    {
        if (_disposed)
        {
            return 0;
        }

        int disposed = 0;
        TimeSpan expiry = IdleExpiry;
        if (expiry > TimeSpan.Zero)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            while (_idle.Count > 0 && now - _idle[0].ReleasedAt >= expiry)
            {
                IdleHost expired = _idle[0];
                _idle.RemoveAt(0);
                DisposeQuietly(
                    expired.Host,
                    $"it sat idle for {(now - expired.ReleasedAt).TotalSeconds:0} s, past the {expiry.TotalSeconds:0} s expiry");
                disposed++;
            }
        }

        int capacity = Capacity;
        while (_idle.Count > capacity)
        {
            IdleHost surplus = _idle[0];
            _idle.RemoveAt(0);
            DisposeQuietly(surplus.Host, $"the pool is over its capacity of {capacity}");
            disposed++;
        }

        return disposed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_idle.Count > 0)
        {
            IdleHost last = _idle[^1];
            _idle.RemoveAt(_idle.Count - 1);
            DisposeQuietly(last.Host, "the pool is shutting down");
        }

        while (_settling.Count > 0)
        {
            SettlingHost last = _settling[^1];
            _settling.RemoveAt(_settling.Count - 1);
            DisposeQuietly(last.Host, "the pool is shutting down");
        }
    }

    private void DisposeQuietly(T host, string because)
    {
        _log?.Invoke($"ReusableHostPool: disposing a host because {because}");
        try
        {
            host.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"ReusableHostPool: disposing a host failed: {ex.Message}");
        }
    }

    /// <summary>One idle host and the instant it was released.</summary>
    private readonly record struct IdleHost(T Host, DateTimeOffset ReleasedAt);

    /// <summary>One released host still disconnecting and the instant it was released.</summary>
    private readonly record struct SettlingHost(T Host, DateTimeOffset ReleasedAt);
}
