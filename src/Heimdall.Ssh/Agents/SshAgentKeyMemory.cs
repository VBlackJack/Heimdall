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
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Agents;

/// <summary>
/// Bounded, session-lifetime in-memory cache of the SSH agent key fingerprint that last
/// succeeded for a given <c>(host, port)</c>. Prioritizing that key on subsequent connections
/// avoids burning <c>MaxAuthTries</c> when many keys are loaded in an agent.
/// </summary>
public sealed class SshAgentKeyMemory
{
    internal const int DefaultMaxEntries = 256;

    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, string> _fingerprints = new(StringComparer.Ordinal);

    /// <summary>Process-wide shared cache used by <see cref="SshConnectionFactory"/>.</summary>
    public static SshAgentKeyMemory Default { get; } = new();

    public SshAgentKeyMemory(int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Maximum entries must be at least 1.");
        }

        _maxEntries = maxEntries;
    }

    public int Count => _fingerprints.Count;

    public void Remember(string host, int port, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fingerprint))
        {
            return;
        }

        var key = MakeKey(host, port);
        if (!_fingerprints.ContainsKey(key) && _fingerprints.Count >= _maxEntries)
        {
            foreach (var existingKey in _fingerprints.Keys)
            {
                if (_fingerprints.TryRemove(existingKey, out _))
                {
                    break;
                }
            }
        }

        _fingerprints[key] = fingerprint;
    }

    public string? GetPreferredFingerprint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        return _fingerprints.TryGetValue(MakeKey(host, port), out var fingerprint)
            ? fingerprint
            : null;
    }

    public void Forget(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        _fingerprints.TryRemove(MakeKey(host, port), out _);
    }

    public void Clear() => _fingerprints.Clear();

    /// <summary>
    /// Returns <paramref name="keys"/> with the last-working key for <c>(host, port)</c> moved
    /// to index 0, preserving the relative order of all remaining keys.
    /// </summary>
    public IReadOnlyList<ISshAgentKey> Prioritize(
        string host,
        int port,
        IReadOnlyList<ISshAgentKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count <= 1)
        {
            return keys;
        }

        var preferred = GetPreferredFingerprint(host, port);
        if (string.IsNullOrWhiteSpace(preferred))
        {
            return keys;
        }

        var matchIndex = -1;
        for (var i = 0; i < keys.Count; i++)
        {
            var fp = SshAgentKeyFingerprints.Compute(keys[i].PublicKeyBlob);
            if (string.Equals(fp, preferred, StringComparison.Ordinal))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex <= 0)
        {
            return keys;
        }

        var reordered = new List<ISshAgentKey>(keys.Count) { keys[matchIndex] };
        for (var i = 0; i < keys.Count; i++)
        {
            if (i != matchIndex)
            {
                reordered.Add(keys[i]);
            }
        }

        return reordered;
    }

    private static string MakeKey(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{port}";
}
