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

using Heimdall.Core.Logging;

namespace Heimdall.Core.Terminal;

/// <summary>
/// Remembers that a session ended and with what argument, so a handler that subscribes after
/// the end (a terminal view that attaches once a short-lived session has already closed) is told
/// at once instead of waiting for a notification that already fired.
/// </summary>
/// <remarks>
/// One lock covers adding a handler and firing, so a handler is invoked exactly once: either by
/// the fire (it was present) or immediately on add (it came later), never both. Handlers run
/// outside the lock. A handler removed and added again after the fire is invoked again.
/// </remarks>
/// <typeparam name="T">The argument of the end-of-session notification.</typeparam>
internal sealed class ExitLatch<T>
{
    private readonly object _lock = new();
    private readonly string _owner;
    private Action<T>? _handlers;
    private bool _fired;
    private T? _argument;

    public ExitLatch(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        _owner = owner;
    }

    /// <summary>Adds a handler; when the notification already fired it is invoked at once.</summary>
    public void Add(Action<T>? handler)
    {
        if (handler is null)
        {
            return;
        }

        T? latched;
        bool alreadyFired;
        lock (_lock)
        {
            alreadyFired = _fired;
            latched = _argument;
            if (!alreadyFired)
            {
                _handlers += handler;
            }
        }

        if (alreadyFired)
        {
            Invoke(handler, latched!);
        }
    }

    /// <summary>Removes a handler.</summary>
    public void Remove(Action<T>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_lock)
        {
            _handlers -= handler;
        }
    }

    /// <summary>Records the notification and invokes the present handlers; only the first call has an effect.</summary>
    public void Fire(T argument)
    {
        Action<T>? target;
        lock (_lock)
        {
            if (_fired)
            {
                return;
            }

            _fired = true;
            _argument = argument;
            target = _handlers;
            _handlers = null;
        }

        if (target is null)
        {
            return;
        }

        foreach (Delegate single in target.GetInvocationList())
        {
            Invoke((Action<T>)single, argument);
        }
    }

    // One failing handler must not skip the handlers after it.
    private void Invoke(Action<T> handler, T argument)
    {
        try
        {
            handler(argument);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[{_owner}] An exit subscriber threw", ex);
        }
    }
}
