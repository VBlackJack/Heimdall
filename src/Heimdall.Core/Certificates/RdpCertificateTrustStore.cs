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

namespace Heimdall.Core.Certificates;

/// <summary>
/// The RDP certificates each profile trusts - a SET per profile, never one.
/// </summary>
/// <remarks>
/// <b>The cardinality is the feature.</b> Windows keeps exactly one thumbprint per host
/// name, so when several machines answer to the same name - the measured case is a pool of
/// domain controllers, each with its own self-signed certificate - every acceptance
/// overwrites the previous one and the warning returns forever. Heimdall holds a set, so
/// the question converges: it is asked once per machine, then never again for that machine.
/// <para>
/// The SSH <c>HostKeyStore</c> is the right model for the INTERACTION - trust on first use,
/// a dialog showing the fingerprint, a settings screen, session-only trust as a distinct
/// outcome - and the WRONG model for the storage, because its <c>GetEntry</c> returns one
/// entry per host. Reuse its vocabulary, never its arity.
/// </para>
/// <para>
/// Durable trust carries an <see cref="RdpCertificateEntry"/>; session trust carries only a
/// thumbprint, because nothing outlives the run to describe.
/// </para>
/// </remarks>
public sealed class RdpCertificateTrustStore
{
    private readonly Dictionary<string, Dictionary<string, RdpCertificateEntry>> _approved =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, HashSet<string>> _session = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="RdpCertificateTrustStore"/> class.</summary>
    /// <param name="timeProvider">Clock used to stamp approvals; the system clock by default.</param>
    /// <remarks>
    /// Injected so a test can assert the stamp instead of asserting that a stamp merely
    /// exists, which is the version that passes whatever the code does.
    /// </remarks>
    public RdpCertificateTrustStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Raised when the durable set of a profile changes, so it can be persisted.</summary>
    public event Action<string, IReadOnlyCollection<RdpCertificateEntry>>? TrustChanged;

    /// <summary>Loads the durable sets read from configuration at startup.</summary>
    /// <param name="entries">One entry per profile that has any trusted certificate.</param>
    /// <remarks>
    /// Replaces the durable state wholesale, as a load should. Session trust is untouched:
    /// it belongs to the run, not to the file - dropping it here would re-ask about a
    /// machine the user accepted minutes earlier.
    /// </remarks>
    public void LoadFromConfig(
        IEnumerable<(string ProfileId, IEnumerable<RdpCertificateEntry> Entries)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _approved.Clear();
            foreach ((string profileId, IEnumerable<RdpCertificateEntry> profileEntries) in entries)
            {
                if (string.IsNullOrWhiteSpace(profileId))
                {
                    continue;
                }

                _approved[profileId] = Normalize(profileEntries);
            }
        }
    }

    /// <summary>Decides what a profile makes of the certificate it was just shown.</summary>
    /// <param name="profileId">The profile the connection belongs to.</param>
    /// <param name="presented">The observed thumbprint.</param>
    public RdpCertificateTrustDecision Evaluate(string profileId, string presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        lock (_gate)
        {
            return RdpCertificateTrust.Decide(
                presented,
                ApprovedThumbprints(profileId),
                SessionThumbprints(profileId));
        }
    }

    /// <summary>Remembers a thumbprint for this profile, across restarts.</summary>
    /// <param name="profileId">The profile the connection belongs to.</param>
    /// <param name="thumbprint">The thumbprint the user approved.</param>
    public void Trust(string profileId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        Trust(profileId, new RdpCertificateEntry(thumbprint.Trim(), _timeProvider.GetUtcNow()));
    }

    /// <summary>Remembers a certificate for this profile, across restarts.</summary>
    /// <param name="profileId">The profile the connection belongs to.</param>
    /// <param name="entry">What was approved, and what was known about it.</param>
    /// <remarks>
    /// <b>Adds. Never replaces.</b> Replacing here is exactly the Windows behaviour this
    /// store exists to escape, and it would look like it works - the connection just
    /// accepted would succeed, and only the NEXT one landing on another machine would ask
    /// again, forever.
    /// <para>
    /// Re-approving a thumbprint already held keeps the ORIGINAL entry. The stamp answers
    /// "since when has this been trusted", so refreshing it on every reconnection would
    /// erase the only fact it carries.
    /// </para>
    /// </remarks>
    public void Trust(string profileId, RdpCertificateEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Thumbprint);

        IReadOnlyCollection<RdpCertificateEntry> updated;
        lock (_gate)
        {
            if (!_approved.TryGetValue(profileId, out Dictionary<string, RdpCertificateEntry>? set))
            {
                set = new Dictionary<string, RdpCertificateEntry>(StringComparer.OrdinalIgnoreCase);
                _approved[profileId] = set;
            }

            string thumbprint = entry.Thumbprint.Trim();
            if (set.ContainsKey(thumbprint))
            {
                return;
            }

            set[thumbprint] = entry with { Thumbprint = thumbprint };
            updated = [.. set.Values];
        }

        TrustChanged?.Invoke(profileId, updated);
    }

    /// <summary>Remembers a thumbprint for this run only.</summary>
    /// <param name="profileId">The profile the connection belongs to.</param>
    /// <param name="thumbprint">The thumbprint the user approved once.</param>
    /// <remarks>
    /// Deliberately does NOT raise <see cref="TrustChanged"/>: nothing about this decision
    /// may reach the configuration file, or "just this once" would silently become forever.
    /// </remarks>
    public void TrustForSession(string profileId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        lock (_gate)
        {
            if (!_session.TryGetValue(profileId, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _session[profileId] = set;
            }

            set.Add(thumbprint.Trim());
        }
    }

    /// <summary>Forgets one durable certificate, as the settings screen must be able to.</summary>
    /// <param name="profileId">The profile the certificate belongs to.</param>
    /// <param name="thumbprint">The thumbprint to forget.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    public bool Remove(string profileId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        IReadOnlyCollection<RdpCertificateEntry> updated;
        lock (_gate)
        {
            if (!_approved.TryGetValue(profileId, out Dictionary<string, RdpCertificateEntry>? set)
                || !set.Remove(thumbprint.Trim()))
            {
                return false;
            }

            updated = [.. set.Values];
        }

        TrustChanged?.Invoke(profileId, updated);
        return true;
    }

    /// <summary>The durable certificates a profile trusts, in no particular order.</summary>
    /// <param name="profileId">The profile to read.</param>
    public IReadOnlyCollection<RdpCertificateEntry> GetApproved(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        lock (_gate)
        {
            return _approved.TryGetValue(profileId, out Dictionary<string, RdpCertificateEntry>? set)
                ? [.. set.Values]
                : [];
        }
    }

    /// <summary>Every profile trusting at least one certificate, for the settings screen.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<RdpCertificateEntry>> GetAllApproved()
    {
        lock (_gate)
        {
            Dictionary<string, IReadOnlyCollection<RdpCertificateEntry>> all =
                new(StringComparer.Ordinal);

            foreach ((string profileId, Dictionary<string, RdpCertificateEntry> set) in _approved)
            {
                if (set.Count > 0)
                {
                    all[profileId] = [.. set.Values];
                }
            }

            return all;
        }
    }

    private IReadOnlyCollection<string> ApprovedThumbprints(string profileId)
        => _approved.TryGetValue(profileId, out Dictionary<string, RdpCertificateEntry>? set)
            ? [.. set.Keys]
            : [];

    private IReadOnlyCollection<string> SessionThumbprints(string profileId)
        => _session.TryGetValue(profileId, out HashSet<string>? set) ? [.. set] : [];

    private static Dictionary<string, RdpCertificateEntry> Normalize(
        IEnumerable<RdpCertificateEntry> entries)
    {
        Dictionary<string, RdpCertificateEntry> set =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (RdpCertificateEntry entry in entries ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Thumbprint))
            {
                continue;
            }

            string thumbprint = entry.Thumbprint.Trim();
            set.TryAdd(thumbprint, entry with { Thumbprint = thumbprint });
        }

        return set;
    }
}
