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

using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.Core.Terminal;

/// <summary>
/// Routes session output to subscribers and keeps what arrives before the first one.
/// A device or child process that prints its banner and prompt once (network equipment, a
/// shell) produces that output while the start is still returning, before the terminal view
/// can subscribe; without a buffer the screen stays blank until the user presses Enter.
/// </summary>
/// <remarks>
/// Lock order is always the delivery lock, then the state lock. The delivery lock
/// serializes the replay to the first subscriber with live deliveries from the read
/// loop, so order is preserved. Subscriber callbacks run under the delivery lock only,
/// never under the state lock, so a callback may add or remove handlers on its own thread.
/// </remarks>
internal sealed class OutputReplayGate
{
    /// <summary>
    /// Default cap on bytes held for the first subscriber; one bound for every session type.
    /// </summary>
    internal const int DefaultBootstrapCapBytes = AppConstants.MaxConPtyBootstrapBufferBytes;

    private readonly object _deliveryLock = new();
    private readonly object _stateLock = new();
    private readonly int _capBytes;
    private readonly string _owner;
    private readonly List<(Action<ReadOnlyMemory<byte>> Original, Action<byte[]> Adapter)> _memoryHandlers = new();
    private Action<byte[]>? _handlers;
    private List<byte[]>? _bootstrap = new();
    private int _bootstrapBytes;
    private bool _firstSubscriberAttached;
    private bool _bufferClosed;
    private int _subscriberFailureLogged;

    /// <param name="owner">Session type name, prefixed to the log lines.</param>
    /// <param name="capBytes">Bytes held for the first subscriber.</param>
    public OutputReplayGate(string owner, int capBytes = DefaultBootstrapCapBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capBytes);
        _capBytes = capBytes;
        _owner = owner;
    }

    /// <summary>Adds a handler. The first one ever added receives the buffered output, in order.</summary>
    public void Add(Action<byte[]>? handler) => Add(handler, null);

    // bookkeeping runs under the state lock in the same step as the subscription.
    private void Add(Action<byte[]>? handler, Action? bookkeeping)
    {
        if (handler is null)
        {
            return;
        }

        lock (_deliveryLock)
        {
            byte[][] replay = [];
            Action<byte[]>? target;
            bool first;
            lock (_stateLock)
            {
                _handlers += handler;
                bookkeeping?.Invoke();
                target = _handlers;
                first = !_firstSubscriberAttached;
                if (first)
                {
                    _firstSubscriberAttached = true;
                    if (_bootstrap is { Count: > 0 })
                    {
                        replay = _bootstrap.ToArray();
                    }

                    _bootstrap = null;
                    _bootstrapBytes = 0;
                }
            }

            if (first)
            {
                foreach (byte[] chunk in replay)
                {
                    Invoke(target, chunk);
                }
            }
        }
    }

    /// <summary>Adds a handler of a memory-shaped event; same contract as <see cref="Add"/>.</summary>
    public void AddMemory(Action<ReadOnlyMemory<byte>>? handler)
    {
        if (handler is null)
        {
            return;
        }

        Action<byte[]> adapter = chunk => handler(chunk);
        Add(adapter, () => _memoryHandlers.Add((handler, adapter)));
    }

    /// <summary>Removes a handler added with <see cref="AddMemory"/>; the last matching add is undone, as for a delegate.</summary>
    public void RemoveMemory(Action<ReadOnlyMemory<byte>>? handler)
    {
        if (handler is null)
        {
            return;
        }

        // The lookup and the removal are one step under the state lock, so a concurrent add of the
        // same handler cannot leave an adapter that nothing can remove.
        lock (_stateLock)
        {
            for (int i = _memoryHandlers.Count - 1; i >= 0; i--)
            {
                if (_memoryHandlers[i].Original.Equals(handler))
                {
                    _handlers -= _memoryHandlers[i].Adapter;
                    _memoryHandlers.RemoveAt(i);
                    break;
                }
            }
        }
    }

    /// <summary>Removes a handler. Output is not buffered again afterwards.</summary>
    public void Remove(Action<byte[]>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_stateLock)
        {
            _handlers -= handler;
        }
    }

    /// <summary>Delivers a chunk to the subscribers, or keeps it until the first one attaches.</summary>
    public void Deliver(byte[] chunk)
    {
        lock (_deliveryLock)
        {
            Action<byte[]>? target;
            lock (_stateLock)
            {
                if (!_firstSubscriberAttached)
                {
                    Buffer(chunk);
                    return;
                }

                target = _handlers;
            }

            Invoke(target, chunk);
        }
    }

    /// <summary>Drops any output still waiting for a first subscriber, and stops keeping more.</summary>
    public void Release()
    {
        lock (_stateLock)
        {
            _bootstrap = null;
            _bootstrapBytes = 0;
        }
    }

    // The caller holds the state lock. The first chunk that would cross the cap closes the
    // buffer for good, so the replay is always an unbroken prefix of the stream: a later,
    // smaller chunk appended after a hole could start in the middle of an escape sequence.
    private void Buffer(byte[] chunk)
    {
        if (_bootstrap is null || _bufferClosed)
        {
            return;
        }

        if (_bootstrapBytes + chunk.Length > _capBytes)
        {
            _bufferClosed = true;
            FileLogger.Warn(
                $"[{_owner}] Output exceeded {_capBytes} bytes before a subscriber "
                + "attached; keeping the bytes received so far and dropping everything after.");
            return;
        }

        _bootstrap.Add(chunk);
        _bootstrapBytes += chunk.Length;
    }

    // One failing handler must neither end the read loop nor skip the handlers after it.
    // Only the first failure is logged: a handler that throws on every chunk would flood the log.
    private void Invoke(Action<byte[]>? handler, byte[] chunk)
    {
        if (handler is null)
        {
            return;
        }

        foreach (Delegate single in handler.GetInvocationList())
        {
            try
            {
                ((Action<byte[]>)single)(chunk);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _subscriberFailureLogged, 1) == 0)
                {
                    FileLogger.Error($"[{_owner}] A DataReceived subscriber threw; the session continues", ex);
                }
            }
        }
    }
}
