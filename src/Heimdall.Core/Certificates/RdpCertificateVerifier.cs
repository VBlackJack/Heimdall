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

    /// <summary>
    /// An unknown certificate was found and the question about it never reached a person.
    /// The connection must not be opened, and the user must not be told they refused it.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="RefusedByUser"/>, and the difference is a sentence the user reads.</b>
    /// The pane that could not ask - torn down between the probe and the question, holding no
    /// surface to draw on - used to report the same outcome as a person pressing "Do not
    /// connect", so the status line said "you did not approve the certificate" about a question
    /// that was never put to anyone.
    /// <para>
    /// <b>Not <see cref="CouldNotVerify"/> either.</b> That one means the probe found nothing to
    /// judge, and the connection proceeds exactly as it would have without this feature. Here
    /// the probe found a certificate this profile has never trusted, so proceeding would open a
    /// session on an approval nobody gave.
    /// </para>
    /// </remarks>
    QuestionNotAsked,
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
    int AlreadyTrustedCount)
{
    /// <summary>
    /// Identifies the owner whose trust set the answer will be written to.
    /// </summary>
    /// <remarks>
    /// Carried so a presenter can keep questions for different owners apart. Trust is per
    /// owner, so one dialog naming owner A must never supply the answer for owner B - that
    /// would grant durable trust from a question the user was never shown. The whole key,
    /// scope included: a saved profile and a typed destination can share an identity string,
    /// and a presenter keying on the string alone would merge their two questions into one.
    /// </remarks>
    public RdpTrustKey? TrustKey { get; init; }

    /// <summary>
    /// Identifies the surface that must display this question, so it is put where the user is
    /// looking rather than at whichever window the application calls its main one.
    /// </summary>
    /// <remarks>
    /// An opaque token minted by the presentation layer and carried through untouched: this
    /// assembly neither reads it nor knows what it addresses. It exists because a question has
    /// to name a machine, and <see cref="Host"/> cannot always do that - a session tunnelled
    /// over SSH verifies the local end of the tunnel, so its address is 127.0.0.1 for every
    /// such profile. Two tunnelled profiles both named "Production" therefore produced two
    /// identical questions, both owned by the main window, and either answer could be given
    /// to the wrong machine.
    /// </remarks>
    public string? PromptScopeId { get; init; }
}

/// <summary>What the user answered.</summary>
public enum RdpTrustAnswer
{
    /// <summary>Remember this certificate for this profile, across restarts.</summary>
    TrustPermanently,

    /// <summary>Accept it for this run only.</summary>
    TrustForSession,

    /// <summary>Do not connect.</summary>
    Refuse,

    /// <summary>
    /// Nobody was asked, so nobody answered.
    /// </summary>
    /// <remarks>
    /// <para><b>Every way of not answering resolves here, and none of them is approval.</b> The
    /// surface torn down, the pane closed, the question withdrawn because another pane answered
    /// it first: nobody decided anything in that place. What the separate value buys is that the
    /// caller can tell those apart from <see cref="Refuse"/> - an answer a person gave - and so
    /// can say which of the two happened instead of attributing to the user a decision they never
    /// made.</para>
    /// <para><b>What it does NOT mean is "the connection stops".</b> It means that here, where
    /// this value reaches <see cref="RdpCertificateVerifier"/>, because no approval was given and
    /// the check refuses without one. It does not mean it wherever the value is produced. A
    /// presentation layer may share one answer between several sessions asking about the same
    /// certificate, and a session that produced this value by being withdrawn is then handed the
    /// answer that withdrew it - an approval included - before anything reaches this assembly.
    /// That sharing is the presentation layer's decision and is documented where it is taken;
    /// what belongs here is only that this value is not an approval. Every sentence written about
    /// a session that produced it must say what happened in that session and stop there.</para>
    /// </remarks>
    NotAsked,
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
/// <param name="Key">The owner whose trust set applies: a saved profile, or a typed destination.</param>
/// <param name="ProfileName">How the user named it.</param>
/// <param name="Host">Address to probe.</param>
/// <param name="Port">Port to probe.</param>
public sealed record RdpCertificateVerificationRequest(
    RdpTrustKey Key,
    string ProfileName,
    string Host,
    int Port)
{
    /// <summary>Identifies the surface that must display any question this check raises.</summary>
    /// <remarks>
    /// Opaque here; <see cref="RdpCertificatePromptContext.PromptScopeId"/> says what it buys.
    /// A request carrying none asks nobody: the presenter has no surface to put the question
    /// on, and a question that cannot be asked is refused rather than assumed.
    /// </remarks>
    public string? PromptScopeId { get; init; }
}

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
            _store.Evaluate(request.Key, probed.Thumbprint);

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
                decision.AlreadyTrustedCount)
            {
                TrustKey = request.Key,
                PromptScopeId = request.PromptScopeId,
            },
            cancellationToken);

        return Remember(request.Key, probed, answer);
    }

    private RdpVerificationOutcome Remember(
        RdpTrustKey key,
        RdpProbeResult probed,
        RdpTrustAnswer answer)
    {
        switch (answer)
        {
            case RdpTrustAnswer.TrustPermanently:
                _store.Trust(
                    key,
                    new RdpCertificateEntry(probed.Thumbprint!, DateTimeOffset.UtcNow)
                    {
                        Subject = probed.Subject,
                        Issuer = probed.Issuer,
                    });
                return RdpVerificationOutcome.TrustedByUser;

            case RdpTrustAnswer.TrustForSession:
                _store.TrustForSession(key, probed.Thumbprint!);
                return RdpVerificationOutcome.TrustedByUser;

            case RdpTrustAnswer.NotAsked:
                // Nothing is written and nothing is opened, but the caller is told which of the
                // two "do not connect" outcomes this was, because it has a sentence to choose.
                return RdpVerificationOutcome.QuestionNotAsked;

            default:
                return RdpVerificationOutcome.RefusedByUser;
        }
    }
}
