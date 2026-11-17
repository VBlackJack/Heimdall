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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// An asynchronous reader-writer gate with no thread affinity, closing the window between sealing
/// a secret with the vault key and the write that stores it.
/// </summary>
/// <remarks>
/// <para>A writer that seals a secret holds the shared side from the seal until its write has
/// landed. Disabling the vault takes the exclusive side only around withdrawing the in-memory key,
/// which waits for every writer in flight, so a value sealed before the key was withdrawn is on
/// disk before the check that follows looks for it, and a writer that starts later fails closed
/// because the key is gone.</para>
/// <para>A waiting exclusive request holds new shared entries back, so a steady stream of writers
/// cannot starve it. A flow that already holds a scope passes through a nested entry without
/// waiting when the gate was free at its first entry (tracked with an <see cref="AsyncLocal{T}"/>
/// that only counts while the outer scope is open); no writer in the application enters twice.
/// There is no synchronous entry: nothing on the UI thread blocks on this gate. No scope may be held
/// across a network connect or a user prompt.</para>
/// </remarks>
public sealed class VaultSealGate
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _exclusiveTurn = new(1, 1);
    private readonly AsyncLocal<Scope?> _current = new();
    private int _writers;
    private bool _exclusiveWaiting;
    private bool _exclusiveHeld;
    private TaskCompletionSource _writersDrained = NewSignal();
    private TaskCompletionSource _exclusiveDone = NewSignal();

    /// <summary>The gate shared by every writer in the process.</summary>
    public static VaultSealGate Shared { get; } = new();

    /// <summary>Enters the shared side, waiting while the exclusive side is requested or held.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <remarks>
    /// Not an async method on purpose: when the gate is free the scope is registered in the caller's
    /// own execution context, which is what lets a nested entry by the same flow pass through. When it
    /// has to wait, the entry is not registered; no writer in the application enters twice (they are
    /// four, each sealing and storing in one scope), so that case is not met.
    /// </remarks>
    public ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        if (_current.Value is { IsOpen: true })
        {
            // Nested entry by the same flow: it already holds the gate, waiting would deadlock.
            return new ValueTask<IDisposable>(new NestedScope());
        }

        lock (_sync)
        {
            if (!_exclusiveWaiting && !_exclusiveHeld)
            {
                _writers++;
                var scope = new Scope(this, exclusive: false);
                _current.Value = scope;
                return new ValueTask<IDisposable>(scope);
            }
        }

        return new ValueTask<IDisposable>(EnterSharedSlowAsync(cancellationToken));
    }

    private async Task<IDisposable> EnterSharedSlowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (!_exclusiveWaiting && !_exclusiveHeld)
                {
                    _writers++;
                    return new Scope(this, exclusive: false);
                }

                wait = _exclusiveDone.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enters the exclusive side once every writer in flight has finished, holding new ones back
    /// meanwhile.
    /// </summary>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">When the writers in flight did not finish in time.</exception>
    public async Task<IDisposable> EnterExclusiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await EnterExclusiveCoreAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The writers in flight did not finish in time.");
        }
    }

    /// <summary>Enters the exclusive side without a time limit.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public Task<IDisposable> EnterExclusiveAsync(CancellationToken cancellationToken = default) =>
        EnterExclusiveCoreAsync(cancellationToken);

    private async Task<IDisposable> EnterExclusiveCoreAsync(CancellationToken cancellationToken)
    {
        await _exclusiveTurn.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _exclusiveWaiting = true;
        }

        try
        {
            while (true)
            {
                Task drained;
                lock (_sync)
                {
                    if (_writers == 0)
                    {
                        _exclusiveWaiting = false;
                        _exclusiveHeld = true;
                        var scope = new Scope(this, exclusive: true);
                        _current.Value = scope;
                        return scope;
                    }

                    drained = _writersDrained.Task;
                }

                await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                _exclusiveWaiting = false;
                _exclusiveDone.TrySetResult();
                _exclusiveDone = NewSignal();
            }

            _exclusiveTurn.Release();
            throw;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Leave(bool exclusive)
    {
        lock (_sync)
        {
            if (exclusive)
            {
                _exclusiveHeld = false;
                _exclusiveDone.TrySetResult();
                _exclusiveDone = NewSignal();
            }
            else
            {
                _writers--;
                if (_writers == 0)
                {
                    _writersDrained.TrySetResult();
                    _writersDrained = NewSignal();
                }
            }
        }

        if (exclusive)
        {
            _exclusiveTurn.Release();
        }
    }

    private sealed class NestedScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly VaultSealGate _gate;
        private readonly bool _exclusive;
        private int _disposed;

        public Scope(VaultSealGate gate, bool exclusive)
        {
            _gate = gate;
            _exclusive = exclusive;
        }

        public bool IsOpen => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Leave(_exclusive);
            }
        }
    }
}
