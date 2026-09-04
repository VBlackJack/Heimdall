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
/// The RDP certificates each owner trusts - a SET per owner, never one.
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
/// <b>An owner is an <see cref="RdpTrustKey"/>, not a string.</b> A saved profile and a
/// destination typed by hand can carry the same identifier string, and a store keyed by the
/// string alone let an approval given for one silence the question for the other. Both the
/// durable sets and the session sets are keyed by scope and identity together; there is no
/// per-scope store to fall through to, so nothing here can serve one owner's set to the other.
/// </para>
/// <para>
/// Durable trust carries an <see cref="RdpCertificateEntry"/>; session trust carries only a
/// thumbprint, because nothing outlives the run to describe.
/// </para>
/// <para>
/// <b>Every thumbprint entering this store passes through
/// <see cref="RdpCertificateTrust.Normalize"/> first, and the sets are keyed
/// <see cref="StringComparer.Ordinal"/> on the result.</b> The lookup is a byte-exact
/// fixed-time comparison, so the sets may not dedupe by a looser rule than the one the
/// lookup applies: when they did, an entry stored in another case was invisible to
/// <see cref="Evaluate"/> and simultaneously made <see cref="Trust(RdpTrustKey, string)"/> a
/// no-op for the correctly-cased one, and the question could never be answered.
/// </para>
/// </remarks>
public sealed class RdpCertificateTrustStore
{
    private readonly Dictionary<RdpTrustKey, Dictionary<string, RdpCertificateEntry>> _approved = [];
    private readonly Dictionary<RdpTrustKey, HashSet<string>> _session = [];
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

    /// <summary>Raised when the durable set of an owner changes, so it can be persisted.</summary>
    public event Action<RdpTrustKey, IReadOnlyCollection<RdpCertificateEntry>>? TrustChanged;

    /// <summary>Loads the durable sets read from configuration at startup.</summary>
    /// <param name="entries">One entry per owner that has any trusted certificate, both scopes together.</param>
    /// <remarks>
    /// <para>Replaces the durable state wholesale, as a load should. Session trust is untouched:
    /// it belongs to the run, not to the file - dropping it here would re-ask about a
    /// machine the user accepted minutes earlier.</para>
    /// <para><b>One call carries both scopes.</b> A load per scope would wipe the scope loaded
    /// before it, and it would do so invisibly, since each call on its own looks like the
    /// load it replaced.</para>
    /// </remarks>
    public void LoadFromConfig(
        IEnumerable<(RdpTrustKey Key, IEnumerable<RdpCertificateEntry> Entries)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _approved.Clear();
            foreach ((RdpTrustKey key, IEnumerable<RdpCertificateEntry> ownerEntries) in entries)
            {
                if (string.IsNullOrWhiteSpace(key.Identity))
                {
                    continue;
                }

                _approved[key] = Normalize(ownerEntries);
            }
        }
    }

    /// <summary>Decides what an owner makes of the certificate it was just shown.</summary>
    /// <param name="key">The owner the connection belongs to.</param>
    /// <param name="presented">The observed thumbprint.</param>
    public RdpCertificateTrustDecision Evaluate(RdpTrustKey key, string presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Identity);

        lock (_gate)
        {
            return RdpCertificateTrust.Decide(
                presented,
                ApprovedThumbprints(key),
                SessionThumbprints(key));
        }
    }

    /// <summary>Remembers a thumbprint for this owner, across restarts.</summary>
    /// <param name="key">The owner the connection belongs to.</param>
    /// <param name="thumbprint">The thumbprint the user approved.</param>
    public void Trust(RdpTrustKey key, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        Trust(
            key,
            new RdpCertificateEntry(
                RdpCertificateTrust.Normalize(thumbprint),
                _timeProvider.GetUtcNow()));
    }

    /// <summary>Remembers a certificate for this owner, across restarts.</summary>
    /// <param name="key">The owner the connection belongs to.</param>
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
    public void Trust(RdpTrustKey key, RdpCertificateEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Identity);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Thumbprint);

        IReadOnlyCollection<RdpCertificateEntry> updated;
        lock (_gate)
        {
            if (!_approved.TryGetValue(key, out Dictionary<string, RdpCertificateEntry>? set))
            {
                set = new Dictionary<string, RdpCertificateEntry>(StringComparer.Ordinal);
                _approved[key] = set;
            }

            string thumbprint = RdpCertificateTrust.Normalize(entry.Thumbprint);
            if (set.ContainsKey(thumbprint))
            {
                return;
            }

            set[thumbprint] = entry with { Thumbprint = thumbprint };
            updated = [.. set.Values];
        }

        TrustChanged?.Invoke(key, updated);
    }

    /// <summary>Remembers a thumbprint for this run only.</summary>
    /// <param name="key">The owner the connection belongs to.</param>
    /// <param name="thumbprint">The thumbprint the user approved once.</param>
    /// <remarks>
    /// Deliberately does NOT raise <see cref="TrustChanged"/>: nothing about this decision
    /// may reach the configuration file, or "just this once" would silently become forever.
    /// </remarks>
    public void TrustForSession(RdpTrustKey key, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        lock (_gate)
        {
            if (!_session.TryGetValue(key, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _session[key] = set;
            }

            set.Add(RdpCertificateTrust.Normalize(thumbprint));
        }
    }

    /// <summary>Forgets one durable certificate.</summary>
    /// <param name="key">The owner the certificate belongs to.</param>
    /// <param name="thumbprint">The thumbprint to forget.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    /// <remarks>
    /// <b>The caller is the settings screen this method was written for.</b>
    /// <c>TrustedRdpCertificatesSettingsViewModel</c> lists every durable decision and
    /// revokes one behind a confirmation; before it existed the only way back out of an
    /// approval was to hand-edit <c>trustedRdpCertificates</c> in settings.json.
    /// <para>
    /// The removal is persisted by the <see cref="TrustChanged"/> subscriber, which writes
    /// the set back exactly as a new approval does. Nothing here writes anything: a caller
    /// that removes without that subscription in place forgets only until the next launch.
    /// </para>
    /// </remarks>
    public bool Remove(RdpTrustKey key, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        IReadOnlyCollection<RdpCertificateEntry> updated;
        lock (_gate)
        {
            if (!_approved.TryGetValue(key, out Dictionary<string, RdpCertificateEntry>? set)
                || !set.Remove(RdpCertificateTrust.Normalize(thumbprint)))
            {
                return false;
            }

            updated = [.. set.Values];
        }

        TrustChanged?.Invoke(key, updated);
        return true;
    }

    /// <summary>The durable certificates an owner trusts, in no particular order.</summary>
    /// <param name="key">The owner to read.</param>
    public IReadOnlyCollection<RdpCertificateEntry> GetApproved(RdpTrustKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Identity);

        lock (_gate)
        {
            return _approved.TryGetValue(key, out Dictionary<string, RdpCertificateEntry>? set)
                ? [.. set.Values]
                : [];
        }
    }

    /// <summary>Every owner trusting at least one certificate.</summary>
    /// <remarks>
    /// Shaped for, and read by, <c>TrustedRdpCertificatesSettingsViewModel</c>: one row per
    /// certificate, grouped under the owner that approved it. An owner holding an empty
    /// set is omitted rather than returned empty, so the screen never draws a server with
    /// nothing under it.
    /// </remarks>
    public IReadOnlyDictionary<RdpTrustKey, IReadOnlyCollection<RdpCertificateEntry>> GetAllApproved()
    {
        lock (_gate)
        {
            Dictionary<RdpTrustKey, IReadOnlyCollection<RdpCertificateEntry>> all = [];
            foreach ((RdpTrustKey key, Dictionary<string, RdpCertificateEntry> set) in _approved)
            {
                if (set.Count > 0)
                {
                    all[key] = [.. set.Values];
                }
            }

            return all;
        }
    }

    private IReadOnlyCollection<string> ApprovedThumbprints(RdpTrustKey key)
        => _approved.TryGetValue(key, out Dictionary<string, RdpCertificateEntry>? set)
            ? [.. set.Keys]
            : [];

    private IReadOnlyCollection<string> SessionThumbprints(RdpTrustKey key)
        => _session.TryGetValue(key, out HashSet<string>? set) ? [.. set] : [];

    private static Dictionary<string, RdpCertificateEntry> Normalize(
        IEnumerable<RdpCertificateEntry> entries)
    {
        Dictionary<string, RdpCertificateEntry> set =
            new(StringComparer.Ordinal);
        foreach (RdpCertificateEntry entry in entries ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Thumbprint))
            {
                continue;
            }

            string thumbprint = RdpCertificateTrust.Normalize(entry.Thumbprint);
            set.TryAdd(thumbprint, entry with { Thumbprint = thumbprint });
        }

        return set;
    }
}
