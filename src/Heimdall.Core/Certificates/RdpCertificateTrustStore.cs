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
/// </remarks>
public sealed class RdpCertificateTrustStore
{
    private readonly Dictionary<string, HashSet<string>> _approved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _session = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Raised when the durable set of a profile changes, so it can be persisted.</summary>
    public event Action<string, IReadOnlyCollection<string>>? TrustChanged;

    /// <summary>Loads the durable sets read from configuration at startup.</summary>
    /// <param name="entries">One entry per profile that has any trusted certificate.</param>
    /// <remarks>
    /// Replaces the durable state wholesale, as a load should. Session trust is untouched:
    /// it belongs to the run, not to the file.
    /// </remarks>
    public void LoadFromConfig(
        IEnumerable<(string ProfileId, IEnumerable<string> Thumbprints)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _approved.Clear();
            foreach ((string profileId, IEnumerable<string> thumbprints) in entries)
            {
                if (string.IsNullOrWhiteSpace(profileId))
                {
                    continue;
                }

                _approved[profileId] = Normalize(thumbprints);
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
                Snapshot(_approved, profileId),
                Snapshot(_session, profileId));
        }
    }

    /// <summary>Remembers a thumbprint for this profile, across restarts.</summary>
    /// <param name="profileId">The profile the connection belongs to.</param>
    /// <param name="thumbprint">The thumbprint the user approved.</param>
    /// <remarks>
    /// <b>Adds. Never replaces.</b> Replacing here is exactly the Windows behaviour this
    /// store exists to escape, and it would look like it works - the connection just
    /// accepted would succeed, and only the NEXT one to land on another machine would ask
    /// again, forever.
    /// </remarks>
    public void Trust(string profileId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        IReadOnlyCollection<string> updated;
        lock (_gate)
        {
            if (!_approved.TryGetValue(profileId, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _approved[profileId] = set;
            }

            if (!set.Add(thumbprint.Trim()))
            {
                return;
            }

            updated = [.. set];
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

    /// <summary>Forgets one durable thumbprint, as the settings screen must be able to.</summary>
    /// <param name="profileId">The profile the certificate belongs to.</param>
    /// <param name="thumbprint">The thumbprint to forget.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    public bool Remove(string profileId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        IReadOnlyCollection<string> updated;
        lock (_gate)
        {
            if (!_approved.TryGetValue(profileId, out HashSet<string>? set)
                || !set.Remove(thumbprint.Trim()))
            {
                return false;
            }

            updated = [.. set];
        }

        TrustChanged?.Invoke(profileId, updated);
        return true;
    }

    /// <summary>The durable thumbprints a profile trusts, in no particular order.</summary>
    /// <param name="profileId">The profile to read.</param>
    public IReadOnlyCollection<string> GetApproved(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        lock (_gate)
        {
            return Snapshot(_approved, profileId);
        }
    }

    /// <summary>Every profile trusting at least one certificate, for the settings screen.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetAllApproved()
    {
        lock (_gate)
        {
            Dictionary<string, IReadOnlyCollection<string>> all = new(StringComparer.Ordinal);
            foreach ((string profileId, HashSet<string> set) in _approved)
            {
                if (set.Count > 0)
                {
                    all[profileId] = [.. set];
                }
            }

            return all;
        }
    }

    private static IReadOnlyCollection<string> Snapshot(
        Dictionary<string, HashSet<string>> source,
        string profileId)
        => source.TryGetValue(profileId, out HashSet<string>? set) ? [.. set] : [];

    private static HashSet<string> Normalize(IEnumerable<string> thumbprints)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach (string thumbprint in thumbprints ?? [])
        {
            if (!string.IsNullOrWhiteSpace(thumbprint))
            {
                set.Add(thumbprint.Trim());
            }
        }

        return set;
    }
}
