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

/// <summary>What the caller may do with the connection it was about to open.</summary>
public enum RdpVerificationOutcome
{
    /// <summary>
    /// The certificate is one this profile trusts. Heimdall has performed the check
    /// Windows would have, so relaxing the Windows check is now honest.
    /// </summary>
    TrustedByUser,

    /// <summary>The user declined this certificate. The connection must not be opened.</summary>
    RefusedByUser,

    /// <summary>
    /// The endpoint keeps standard RDP security and presents no certificate. There is
    /// nothing to verify, and nothing may be relaxed on the strength of it.
    /// </summary>
    NoCertificateOffered,

    /// <summary>
    /// The certificate could not be read. Connect exactly as before - Heimdall has
    /// verified nothing and must not act as though it had.
    /// </summary>
    CouldNotVerify,
}

/// <summary>What the user is shown when an unknown certificate turns up.</summary>
/// <param name="ProfileName">The profile being connected, as the user named it.</param>
/// <param name="Host">The address that answered.</param>
/// <param name="Thumbprint">The SHA-256 thumbprint just observed.</param>
/// <param name="Subject">Certificate subject, when the probe read one.</param>
/// <param name="AlreadyTrustedCount">
/// How many certificates this profile already trusts.
/// </param>
/// <remarks>
/// <paramref name="AlreadyTrustedCount"/> is the arbitration of 2026-08-23 made visible.
/// A second or third unknown thumbprint on a pool is the NORMAL situation, so the question
/// has to read "another machine behind this name, you already trust N" rather than
/// repeating one identical alarm until the user stops reading it.
/// </remarks>
public sealed record RdpCertificatePromptContext(
    string ProfileName,
    string Host,
    string Thumbprint,
    string? Subject,
    int AlreadyTrustedCount);

/// <summary>What the user answered.</summary>
public enum RdpTrustAnswer
{
    /// <summary>Remember this certificate for this profile, across restarts.</summary>
    TrustPermanently,

    /// <summary>Accept it for this run only.</summary>
    TrustForSession,

    /// <summary>Do not connect.</summary>
    Refuse,
}

/// <summary>Asks the user about a certificate their profile has never seen.</summary>
public interface IRdpCertificateTrustPrompt
{
    /// <summary>Puts the question and waits for the answer.</summary>
    /// <param name="context">What the user needs in order to answer.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<RdpTrustAnswer> AskAsync(
        RdpCertificatePromptContext context,
        CancellationToken cancellationToken);
}

/// <summary>What is being connected.</summary>
/// <param name="ProfileId">Identifies the profile whose trust set applies.</param>
/// <param name="ProfileName">How the user named it.</param>
/// <param name="Host">Address to probe.</param>
/// <param name="Port">Port to probe.</param>
public sealed record RdpCertificateVerificationRequest(
    string ProfileId,
    string ProfileName,
    string Host,
    int Port);

/// <summary>
/// Runs the certificate check that must happen before an RDP session is opened.
/// </summary>
/// <remarks>
/// <b>The safety argument of this whole feature rests on one rule: Heimdall may relax the
/// Windows check only where it has performed an equivalent one itself.</b> Every outcome
/// other than <see cref="RdpVerificationOutcome.TrustedByUser"/> therefore means "change
/// nothing" - a probe that could not run, or an endpoint with no certificate at all, must
/// leave the connection exactly as it would have been without this feature. Treating an
/// unverifiable endpoint as verified would be strictly worse than never having built any
/// of this.
/// </remarks>
public sealed class RdpCertificateVerifier(
    IRdpCertificateProbe probe,
    RdpCertificateTrustStore store,
    IRdpCertificateTrustPrompt prompt)
{
    private readonly IRdpCertificateProbe _probe =
        probe ?? throw new ArgumentNullException(nameof(probe));

    private readonly RdpCertificateTrustStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    private readonly IRdpCertificateTrustPrompt _prompt =
        prompt ?? throw new ArgumentNullException(nameof(prompt));

    /// <summary>Probes the endpoint and decides what the caller may do.</summary>
    /// <param name="request">What is being connected.</param>
    /// <param name="cancellationToken">Cancels the probe and the question.</param>
    public async Task<RdpVerificationOutcome> VerifyAsync(
        RdpCertificateVerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RdpProbeResult probed = await _probe.ProbeAsync(
            request.Host,
            request.Port,
            cancellationToken);

        if (probed.Outcome == RdpProbeOutcome.TlsNotOffered)
        {
            return RdpVerificationOutcome.NoCertificateOffered;
        }

        if (probed.Outcome != RdpProbeOutcome.CertificateObtained
            || string.IsNullOrWhiteSpace(probed.Thumbprint))
        {
            return RdpVerificationOutcome.CouldNotVerify;
        }

        RdpCertificateTrustDecision decision =
            _store.Evaluate(request.ProfileId, probed.Thumbprint);

        if (decision.Verdict != RdpCertificateTrustVerdict.Unknown)
        {
            return RdpVerificationOutcome.TrustedByUser;
        }

        RdpTrustAnswer answer = await _prompt.AskAsync(
            new RdpCertificatePromptContext(
                request.ProfileName,
                request.Host,
                probed.Thumbprint,
                probed.Subject,
                decision.AlreadyTrustedCount),
            cancellationToken);

        return Remember(request.ProfileId, probed, answer);
    }

    private RdpVerificationOutcome Remember(
        string profileId,
        RdpProbeResult probed,
        RdpTrustAnswer answer)
    {
        switch (answer)
        {
            case RdpTrustAnswer.TrustPermanently:
                _store.Trust(
                    profileId,
                    new RdpCertificateEntry(probed.Thumbprint!, DateTimeOffset.UtcNow)
                    {
                        Subject = probed.Subject,
                        Issuer = probed.Issuer,
                    });
                return RdpVerificationOutcome.TrustedByUser;

            case RdpTrustAnswer.TrustForSession:
                _store.TrustForSession(profileId, probed.Thumbprint!);
                return RdpVerificationOutcome.TrustedByUser;

            default:
                return RdpVerificationOutcome.RefusedByUser;
        }
    }
}
