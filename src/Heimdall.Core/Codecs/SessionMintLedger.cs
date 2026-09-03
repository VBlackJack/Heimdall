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

namespace Heimdall.Core.Codecs;

/// <summary>
/// Remembers which inventory profile each minted session identifier was minted for, keeping the
/// most recent <c>capacity</c> mints and forgetting the oldest.
/// </summary>
/// <remarks>
/// <para><b>Its own type, with its own capacity, so the forgetting rule is measured on an
/// instance nothing else shares.</b> The ledger the application runs on is a static held by
/// <see cref="SessionIdCodec"/>; filling that one to prove what happens at the boundary would
/// evict the mints of every test running beside it, and those tests resolve identifiers. The
/// rule under test is the eviction order, which does not care how large the bound is - so it is
/// tested at a capacity of two on a private instance instead.</para>
/// <para><b>Bounded because the application is long-lived and nothing removes an entry.</b> A
/// session identifier is minted per connection and stays reachable for as long as the pane does,
/// and there is no single moment where every pane's death is observed - the mint happens at four
/// call sites and the copies made from it outlive their originals. A bound costs a fixed
/// ceiling; wiring a removal to each of those lifetimes costs a silent leak wherever one was
/// forgotten.</para>
/// <para><b>Forgetting is the safe direction, which is why a bound is affordable at all.</b> An
/// identifier the ledger no longer knows is reported as its own, so an approval given for it is
/// filed under a key that dies with the pane and the certificate is asked about again next
/// time. The unsafe direction - naming some OTHER profile - is unreachable by forgetting,
/// because the ledger never invents an origin it was not told.</para>
/// </remarks>
internal sealed class SessionMintLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly int _capacity;

    /// <param name="capacity">How many mints to remember before the oldest is forgotten.</param>
    public SessionMintLedger(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
    }

    /// <summary>Records that <paramref name="sessionId"/> was minted for
    /// <paramref name="inventoryId"/>.</summary>
    public void Record(string sessionId, string inventoryId)
    {
        lock (_gate)
        {
            if (!_origins.TryAdd(sessionId, inventoryId))
            {
                return;
            }

            _order.Enqueue(sessionId);

            while (_order.Count > _capacity)
            {
                _origins.Remove(_order.Dequeue());
            }
        }
    }

    /// <summary>
    /// The inventory profile <paramref name="runtimeId"/> was minted for, or
    /// <paramref name="runtimeId"/> itself when this ledger did not mint it.
    /// </summary>
    public string ResolveOrigin(string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return runtimeId;
        }

        lock (_gate)
        {
            return _origins.TryGetValue(runtimeId, out string? origin) ? origin : runtimeId;
        }
    }
}
