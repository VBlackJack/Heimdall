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

/// <summary>What a certificate check concluded, and what it means for the connection.</summary>
/// <param name="Decision">Whether the session may be opened.</param>
/// <param name="Outcome">
/// What the verifier concluded, or null when no check ran or the check threw.
/// </param>
/// <remarks>
/// <b>The decision alone is not enough, and this record exists because it was being asked to
/// be.</b> Two outcomes stop the connection - a person pressing "Do not connect", and an unknown
/// certificate whose question reached nobody - and they produce the same one bit. A caller that
/// has only the bit has to guess which happened, and the guess made was "refused": the pane wrote
/// "you did not approve the certificate this server presented" in front of a user who was asked
/// nothing. Carrying the outcome out with the decision is what removes the guess.
/// </remarks>
public readonly record struct RdpCertificateCheckResult(
    RdpConnectionDecision Decision,
    RdpVerificationOutcome? Outcome);

/// <summary>What the caller does with the connection it was about to open.</summary>
public enum RdpConnectionDecision
{
    /// <summary>Open it.</summary>
    Proceed,

    /// <summary>Do not open it. The user said no.</summary>
    Abandon,
}

/// <summary>
/// Decides when the certificate check runs, and what its answer means for the connection.
/// </summary>
/// <remarks>
/// The two decisions that make the rest of this feature reach a user, kept pure and out of
/// the view so they can be pinned by tests. Building a WPF window in a test seals
/// application-level styles onto the shared dispatcher and takes unrelated tests down with
/// it, so nothing decidable is allowed to live in one.
/// </remarks>
public static class RdpCertificateGate
{
    /// <summary>
    /// The <c>AuthenticationLevel</c> value that imposes no server-authentication
    /// requirement at all.
    /// </summary>
    public const int NoServerAuthenticationRequired = 0;

    /// <summary>
    /// Whether Heimdall should verify the server certificate itself before connecting.
    /// </summary>
    /// <param name="authenticationLevel">
    /// The level about to be applied: 0 requires nothing of the server, 1 requires
    /// server authentication, 2 attempts it and warns on failure.
    /// </param>
    /// <remarks>
    /// <b>Only where nothing else checks.</b> At level 1 and level 2 Windows performs its
    /// own server-authentication step and shows its own warning, so a second question here
    /// would be one prompt too many for the same fact - and a prompt the user learns to
    /// click through is worse than no prompt. Level 0 is the case where today nobody
    /// checks anything, which is the whole reason this feature exists.
    /// <para>
    /// This is a narrower rule than "always verify", chosen deliberately: it adds a probe
    /// to exactly the connections that currently have no verification, and none to those
    /// that do.
    /// </para>
    /// </remarks>
    public static bool VerificationRequired(int authenticationLevel)
        => authenticationLevel == NoServerAuthenticationRequired;

    /// <summary>Turns a verification outcome into a decision about the connection.</summary>
    /// <param name="outcome">What the verifier concluded.</param>
    /// <remarks>
    /// <b>Only an unapproved certificate stops the connection.</b> The two outcomes that mean
    /// "verified nothing" - an unreachable probe and an endpoint that offers no certificate -
    /// proceed. That is the same rule the verifier itself holds, seen from the other side:
    /// Heimdall may relax the Windows check only where it performed an equivalent one, and by
    /// symmetry it may not *tighten* the connection on the strength of a check that did not
    /// happen either. A probe failure that blocked the session would turn a verification step
    /// into a new way to fail, on a code path that worked before this feature existed.
    /// <para>
    /// <b>Two outcomes stop it, and they are not the same event.</b>
    /// <see cref="RdpVerificationOutcome.RefusedByUser"/> is a person pressing "Do not
    /// connect"; <see cref="RdpVerificationOutcome.QuestionNotAsked"/> is a certificate this
    /// profile has never trusted whose question reached nobody. Both must stop the session,
    /// because in neither case did anyone approve it - but the caller has a sentence to choose
    /// and the two sentences are not interchangeable, which is why the distinction survives
    /// this far rather than being flattened here.
    /// </para>
    /// </remarks>
    public static RdpConnectionDecision Decide(RdpVerificationOutcome outcome)
        => outcome is RdpVerificationOutcome.RefusedByUser
            or RdpVerificationOutcome.QuestionNotAsked
            ? RdpConnectionDecision.Abandon
            : RdpConnectionDecision.Proceed;

    /// <summary>
    /// Runs the check when one is owed, and says what to do with the connection.
    /// </summary>
    /// <param name="authenticationLevel">The level about to be applied.</param>
    /// <param name="verifyAsync">Performs the check. Not called when none is owed.</param>
    /// <param name="onVerificationFailed">
    /// Told when the check could not run at all. Reporting only - it cannot change the
    /// decision.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <remarks>
    /// The whole orchestration, kept out of the view on purpose. A connection path that
    /// only the running application exercises is the shape that leaves a guard attached
    /// to nothing: two green suites either side of a junction neither of them crosses.
    /// <para>
    /// <b>A check that threw proceeds.</b> It verified nothing, so it may neither relax
    /// nor tighten the connection; turning a probe failure into a refused session would
    /// make this feature a new way to fail on a path that worked without it.
    /// </para>
    /// </remarks>
    public static async Task<RdpConnectionDecision> DecideConnectionAsync(
        int authenticationLevel,
        Func<CancellationToken, Task<RdpVerificationOutcome>> verifyAsync,
        Action<Exception>? onVerificationFailed,
        CancellationToken cancellationToken)
        => (await CheckConnectionAsync(
            authenticationLevel,
            verifyAsync,
            onVerificationFailed,
            cancellationToken)).Decision;

    /// <summary>
    /// Runs the check when one is owed, and says both what to do and what was concluded.
    /// </summary>
    /// <param name="authenticationLevel">The level about to be applied.</param>
    /// <param name="verifyAsync">Performs the check. Not called when none is owed.</param>
    /// <param name="onVerificationFailed">
    /// Told when the check could not run at all. Reporting only - it cannot change the
    /// decision.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <remarks>
    /// <para><b>Why the outcome comes out of here rather than being captured at the call
    /// site.</b> The caller has a sentence to choose and two different outcomes stop the
    /// connection, so it needs the outcome as well as the decision. Reaching into the check's
    /// own lambda to keep a copy would put that decision inside a WPF view, one brace deeper
    /// than anything can read, which is where a decision goes to stop being testable.</para>
    /// <para><b>A check that threw reports no outcome, and connects.</b> It verified nothing, so
    /// it may neither relax nor tighten the connection, and it concluded nothing worth naming.
    /// </para>
    /// </remarks>
    public static async Task<RdpCertificateCheckResult> CheckConnectionAsync(
        int authenticationLevel,
        Func<CancellationToken, Task<RdpVerificationOutcome>> verifyAsync,
        Action<Exception>? onVerificationFailed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifyAsync);

        if (!VerificationRequired(authenticationLevel))
        {
            return new RdpCertificateCheckResult(RdpConnectionDecision.Proceed, null);
        }

        try
        {
            RdpVerificationOutcome outcome = await verifyAsync(cancellationToken);
            return new RdpCertificateCheckResult(Decide(outcome), outcome);
        }
        catch (Exception ex)
        {
            onVerificationFailed?.Invoke(ex);
            return new RdpCertificateCheckResult(RdpConnectionDecision.Proceed, null);
        }
    }
}
