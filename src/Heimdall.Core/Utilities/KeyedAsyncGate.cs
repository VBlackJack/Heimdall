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

namespace Heimdall.Core.Utilities;

/// <summary>
/// Lets one caller at a time through for a given key, while callers holding different keys
/// run side by side.
/// </summary>
/// <remarks>
/// An entry lives only while someone holds or awaits its key, so a gate that sees many
/// distinct keys over a long run does not grow with them.
/// </remarks>
public sealed class KeyedAsyncGate
{
    private readonly Dictionary<string, Entry> _entries;
    private readonly object _lock = new();

    /// <summary>Initialises a gate that compares keys with <paramref name="comparer"/>.</summary>
    public KeyedAsyncGate(IEqualityComparer<string>? comparer = null)
    {
        _entries = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);
    }

    /// <summary>The number of keys currently held or awaited.</summary>
    public int ActiveKeyCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits until no other caller holds <paramref name="key"/>, then holds it until the
    /// returned handle is disposed.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the key was obtained; the
    /// key is then not held.
    /// </exception>
    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Entry? existing))
            {
                existing = new Entry();
                _entries[key] = existing;
            }

            existing.Users++;
            entry = existing;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Holder(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (_lock)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Holder(KeyedAsyncGate gate, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            entry.Semaphore.Release();
            gate.Leave(key, entry);
        }
    }
}
