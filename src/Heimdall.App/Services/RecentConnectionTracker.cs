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

namespace Heimdall.App.Services;

/// <summary>
/// In-memory record of recent successful connections, used to bias the
/// command palette towards the protocol the user actually picked last for a
/// given host (RDP-DISC-04) and to surface a "Recents" suggestion list when
/// the palette query is empty (RDP-DISC-05).
///
/// Process-scoped only - no persistence in this iteration. Adding disk
/// persistence later means adding load/save around <see cref="_entries"/>
/// without changing the public API.
/// </summary>
public interface IRecentConnectionTracker
{
    /// <summary>
    /// Records that the user successfully reached <paramref name="host"/>
    /// with the given <paramref name="protocol"/>. The host is normalized to
    /// lower-case for case-insensitive lookups.
    /// </summary>
    void Record(string? host, string? protocol);

    /// <summary>
    /// Returns the protocol most recently used for <paramref name="host"/>,
    /// or <c>null</c> if no record exists.
    /// </summary>
    string? GetLastProtocol(string? host);

    /// <summary>
    /// Returns the most recent connection records, newest first, up to
    /// <paramref name="max"/>.
    /// </summary>
    IReadOnlyList<RecentConnectionEntry> GetRecents(int max);
}

/// <summary>One entry in the recent-connections log.</summary>
public sealed record RecentConnectionEntry(
    string Host,
    string Protocol,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Default <see cref="IRecentConnectionTracker"/> implementation. Stores the
/// last <see cref="MaxEntries"/> connections in process memory, deduped by
/// (host, protocol) so a single host with two protocols is tracked separately.
/// </summary>
public sealed class RecentConnectionTracker : IRecentConnectionTracker
{
    private const int MaxEntries = 50;
    private readonly object _lock = new();
    private readonly LinkedList<RecentConnectionEntry> _entries = new();

    public void Record(string? host, string? protocol)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(protocol))
        {
            return;
        }

        var normalizedHost = host.Trim().ToLowerInvariant();
        var normalizedProtocol = protocol.Trim().ToUpperInvariant();
        var entry = new RecentConnectionEntry(
            normalizedHost,
            normalizedProtocol,
            DateTimeOffset.UtcNow);

        lock (_lock)
        {
            // Drop any existing entry for the same (host, protocol) - keeps the list deduped.
            var node = _entries.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Host, normalizedHost, StringComparison.Ordinal)
                    && string.Equals(node.Value.Protocol, normalizedProtocol, StringComparison.Ordinal))
                {
                    _entries.Remove(node);
                }
                node = next;
            }

            _entries.AddFirst(entry);

            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveLast();
            }
        }
    }

    public string? GetLastProtocol(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalized = host.Trim().ToLowerInvariant();

        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Host, normalized, StringComparison.Ordinal))
                {
                    return entry.Protocol;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<RecentConnectionEntry> GetRecents(int max)
    {
        if (max <= 0)
        {
            return Array.Empty<RecentConnectionEntry>();
        }

        lock (_lock)
        {
            return _entries.Take(max).ToList();
        }
    }
}
