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

    private readonly Func<T> _factory;
    private readonly int _capacity;
    private readonly Stack<T> _idle = new();
    private readonly Action<string>? _log;
    private bool _disposed;

    public ReusableHostPool(Func<T> factory, int capacity = DefaultCapacity, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _factory = factory;
        _capacity = capacity;
        _log = log;
    }

    /// <summary>Idle hosts currently held. Exposed so the pool's behaviour can be asserted.</summary>
    public int IdleCount => _idle.Count;

    /// <summary>Hosts handed out since construction that came from the pool rather than the factory.</summary>
    public int ReuseCount { get; private set; }

    /// <summary>Hosts built by the factory since construction.</summary>
    public int CreationCount { get; private set; }

    /// <summary>
    /// Hands out an idle host if there is one, and builds a new one otherwise.
    /// </summary>
    public T Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_idle.Count > 0)
        {
            T candidate = _idle.Pop();

            // A host can go bad while it sits idle, so its state is checked on the way out
            // as well as on the way in.
            if (candidate.IsReusable)
            {
                ReuseCount++;
                _log?.Invoke($"ReusableHostPool: reusing an idle host, idle={_idle.Count} reused={ReuseCount}");
                return candidate;
            }

            DisposeQuietly(candidate, "an idle host was no longer reusable");
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

        if (_idle.Count >= _capacity)
        {
            DisposeQuietly(host, "the pool is full");
            return;
        }

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

        _idle.Push(host);
        _log?.Invoke($"ReusableHostPool: host returned to the pool, idle={_idle.Count}");
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
            DisposeQuietly(_idle.Pop(), "the pool is shutting down");
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
}
