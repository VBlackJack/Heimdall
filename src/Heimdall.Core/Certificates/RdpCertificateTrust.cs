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

using System.Security.Cryptography;
using System.Text;

namespace Heimdall.Core.Certificates;

/// <summary>What a profile already knows about the certificate it was just shown.</summary>
public enum RdpCertificateTrustVerdict
{
    /// <summary>Approved before and remembered across restarts.</summary>
    Trusted,

    /// <summary>Approved for this run only; it will be asked again next time.</summary>
    TrustedForSession,

    /// <summary>Never approved for this profile. The user has to be asked.</summary>
    Unknown,
}

/// <summary>
/// The verdict for one presented certificate, plus what the question should say.
/// </summary>
/// <param name="Verdict">Whether this exact thumbprint was approved before.</param>
/// <param name="AlreadyTrustedCount">
/// How many distinct certificates this profile already trusts, session ones included.
/// </param>
/// <remarks>
/// <paramref name="AlreadyTrustedCount"/> exists because of the arbitration of 2026-08-23,
/// and it is the whole difference between a usable feature and an alarm that cries wolf.
/// One name can front several machines - the measured case is a pool of domain controllers
/// each carrying its own self-signed certificate - so a second, third and fourth unknown
/// thumbprint is the NORMAL situation, not a sign that something changed. Carrying the
/// count lets the question read "another machine behind this name, you already trust N"
/// instead of the identical alarm every time.
/// </remarks>
public readonly record struct RdpCertificateTrustDecision(
    RdpCertificateTrustVerdict Verdict,
    int AlreadyTrustedCount);

/// <summary>
/// Decides what a profile makes of the certificate presented to it.
/// </summary>
/// <remarks>
/// Separate from the store, and pure, because this is the part worth pinning: the store
/// holds bytes, this holds the rule.
/// </remarks>
public static class RdpCertificateTrust
{
    /// <summary>Decides whether a presented thumbprint is already trusted by a profile.</summary>
    /// <param name="presented">The thumbprint just observed, as produced by
    /// <see cref="CertificateFingerprint.ComputeSha256"/>.</param>
    /// <param name="approved">Thumbprints this profile trusts across restarts.</param>
    /// <param name="approvedForSession">Thumbprints trusted for this run only.</param>
    /// <remarks>
    /// <b>Both collections are SETS, and that is the design point this whole item exists
    /// for.</b> Windows keeps exactly ONE thumbprint per host name, so on a pool of servers
    /// sharing a name each acceptance overwrites the previous one and the next connection
    /// mismatches again - a loop that never settles. Holding a set is the one thing Heimdall
    /// can do that Windows structurally cannot. A single-valued store here would move that
    /// bug from Windows into Heimdall rather than fix it.
    /// <para>
    /// Persisted trust is checked before session trust so the verdict names the durable
    /// state when both hold, which is what a settings screen has to display.
    /// </para>
    /// </remarks>
    public static RdpCertificateTrustDecision Decide(
        string presented,
        IReadOnlyCollection<string> approved,
        IReadOnlyCollection<string> approvedForSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presented);
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(approvedForSession);

        int alreadyTrusted = CountDistinct(approved, approvedForSession);

        if (ContainsThumbprint(approved, presented))
        {
            return new RdpCertificateTrustDecision(
                RdpCertificateTrustVerdict.Trusted,
                alreadyTrusted);
        }

        if (ContainsThumbprint(approvedForSession, presented))
        {
            return new RdpCertificateTrustDecision(
                RdpCertificateTrustVerdict.TrustedForSession,
                alreadyTrusted);
        }

        return new RdpCertificateTrustDecision(
            RdpCertificateTrustVerdict.Unknown,
            alreadyTrusted);
    }

    /// <summary>Whether a set holds a thumbprint, compared without leaking timing.</summary>
    /// <param name="thumbprints">The set to search.</param>
    /// <param name="candidate">The thumbprint to look for.</param>
    /// <remarks>
    /// Every member is compared even after a match, so the time taken says how many
    /// certificates the profile trusts and nothing about which one matched.
    /// </remarks>
    internal static bool ContainsThumbprint(
        IReadOnlyCollection<string> thumbprints,
        string candidate)
    {
        string normalizedCandidate = Normalize(candidate);
        bool found = false;
        foreach (string stored in thumbprints)
        {
            found |= ConstantTimeEquals(Normalize(stored), normalizedCandidate);
        }

        return found;
    }

    /// <summary>Puts a thumbprint in the one form every side of the store compares.</summary>
    /// <param name="thumbprint">The thumbprint as it was given, from a file or a probe.</param>
    /// <remarks>
    /// <b>One rule about case, stated once.</b> The lookup below is a byte-exact fixed-time
    /// comparison, so it is case-SENSITIVE; the store keys its sets by thumbprint. When
    /// those two disagreed - the sets deduping case-insensitively while the lookup did not -
    /// a thumbprint stored in the other case was invisible to <see cref="Decide"/> and at the
    /// same time blocked the correctly-cased one from ever being added, so the question could
    /// not be answered at all. Every value crossing this class is normalized here first, and
    /// upper case is the form <see cref="CertificateFingerprint.ComputeSha256"/> emits.
    /// </remarks>
    public static string Normalize(string? thumbprint)
        => thumbprint is null ? string.Empty : thumbprint.Trim().ToUpperInvariant();

    /// <summary>
    /// Fixed-time comparison of two equal-length ASCII thumbprints.
    /// </summary>
    /// <remarks>
    /// A length mismatch returns early, which is safe here: these are fixed-format SHA-256
    /// thumbprints, so a different length means a malformed value rather than a near miss.
    /// <b>Do not copy this to compare variable-length secrets</b> - the same caveat the SSH
    /// host key store carries, for the same reason.
    /// </remarks>
    internal static bool ConstantTimeEquals(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a),
            Encoding.ASCII.GetBytes(b));
    }

    private static int CountDistinct(
        IReadOnlyCollection<string> approved,
        IReadOnlyCollection<string> approvedForSession)
    {
        HashSet<string> distinct = new(approved.Select(Normalize), StringComparer.Ordinal);
        distinct.UnionWith(approvedForSession.Select(Normalize));
        return distinct.Count;
    }
}
