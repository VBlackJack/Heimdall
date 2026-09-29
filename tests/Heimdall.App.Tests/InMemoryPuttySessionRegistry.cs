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
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Tests;

/// <summary>
/// A PuTTY session store that never touches the real HKCU hive, with every write and deletion
/// recorded and each operation able to fail on demand.
/// </summary>
internal sealed class InMemoryPuttySessionRegistry : IPuttySessionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<PuttyRegistryValue>> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> Deleted { get; } = [];

    public List<string> Written { get; } = [];

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? EnumerateFailure { get; set; }

    public void Seed(string sessionName, params PuttyRegistryValue[] values)
    {
        lock (_sync)
        {
            _sessions[sessionName] = [.. values];
        }
    }

    public bool Contains(string sessionName)
    {
        lock (_sync)
        {
            return _sessions.ContainsKey(sessionName);
        }
    }

    public IReadOnlyList<string> GetSessionNames()
    {
        if (EnumerateFailure is not null)
        {
            throw EnumerateFailure;
        }

        lock (_sync)
        {
            return [.. _sessions.Keys];
        }
    }

    public IReadOnlyList<PuttyRegistryValue>? ReadSession(string sessionName)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        lock (_sync)
        {
            return _sessions.TryGetValue(sessionName, out List<PuttyRegistryValue>? values) ? [.. values] : null;
        }
    }

    public void WriteSession(string sessionName, IReadOnlyList<PuttyRegistryValue> values)
    {
        lock (_sync)
        {
            // What a real write that fails halfway leaves behind: the key exists, the values do not.
            if (!_sessions.TryGetValue(sessionName, out List<PuttyRegistryValue>? existing))
            {
                existing = [];
                _sessions[sessionName] = existing;
            }

            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            existing.AddRange(values);
            Written.Add(sessionName);
        }
    }

    public void DeleteSession(string sessionName)
    {
        if (!PlinkSizeSessionNaming.IsHeimdallSession(sessionName))
        {
            throw new ArgumentException(null, nameof(sessionName));
        }

        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        lock (_sync)
        {
            _sessions.Remove(sessionName);
            Deleted.Add(sessionName);
        }
    }
}
