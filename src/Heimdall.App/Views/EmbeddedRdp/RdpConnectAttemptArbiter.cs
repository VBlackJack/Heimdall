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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>What may be done once the certificate check standing in front of a connect settles.</summary>
internal enum RdpVerifiedConnectAdmission
{
    /// <summary>Nobody abandoned the attempt while the check ran, so the connect may start.</summary>
    Proceed,

    /// <summary>The attempt is over, so nothing may start a connection on its behalf.</summary>
    Refuse,
}

/// <summary>The connect attempt, as the arbiter needs to drive it.</summary>
/// <remarks>
/// Two effects, because the arbiter decides two things that end in an action on the control:
/// running an attempt it has authorized, and hard-disconnecting a session that came up behind an
/// attempt nobody is waiting for any more. Everything the effects need from the view - the ActiveX
/// host, the profile, the settings - stays on the view; what reaches the arbiter is the attempt
/// token and nothing else.
/// </remarks>
internal interface IRdpConnectAttemptRunner
{
    /// <summary>Configures the control and calls Connect for <paramref name="attempt"/>.</summary>
    /// <param name="attempt">The attempt being run, carried into any retry it schedules.</param>
    void RunAttempt(int attempt);

    /// <summary>Hard-disconnects a connection that completed behind an abandoned attempt.</summary>
    void DropAbandonedConnection();
}

/// <summary>
/// Owns the lifecycle of a connect attempt: which attempt is in flight, whether it was
/// abandoned, and therefore whether a retry may connect and whether a connect that arrives may be
/// promoted.
/// </summary>
/// <remarks>
/// <para>The reason this is a type and not four statements in the view: the RDP surface is often
/// not laid out when the connect starts, so the view schedules itself again about a render pass
/// later. That retry used to re-enter the entry point of a connect the user asked for, and that
/// entry point begins by clearing the abandonment latches. Press Cancel inside the retry window
/// and the sequence was: the latch is set and the control is asked to disconnect, then the retry
/// clears the latch and calls Connect() again. The session the user stopped came up live, and the
/// late-connect refusal could not catch it either, because the state it reads had been wiped by
/// the same statement.</para>
/// <para>Both halves of that sequence are decisions over (current attempt, abandonment latches,
/// view disposed), so both are taken here rather than in the code-behind. That is what lets the
/// whole scenario be played in a test - open an attempt, cancel it, deliver the retry, deliver the
/// late connect - against a recording runner, with no <c>Window</c> anywhere.
/// <see cref="RdpConnectAttemptGate"/> holds the state, this decides what follows from it, and the
/// view is left with one delegating statement per event.</para>
/// <para>All members are called from the UI thread, like the control they drive.</para>
/// </remarks>
internal sealed class RdpConnectAttemptArbiter
{
    private readonly IRdpConnectAttemptRunner _runner;
    private readonly RdpConnectAttemptGate _gate = new();

    /// <summary>Initializes a new instance of the <see cref="RdpConnectAttemptArbiter"/> class.</summary>
    /// <param name="runner">The connect attempt to drive.</param>
    public RdpConnectAttemptArbiter(IRdpConnectAttemptRunner runner)
        => _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>Whether the user cancelled the attempt currently in flight.</summary>
    public bool AbandonedByUser => _gate.AbandonedByUser;

    /// <summary>Whether the connect watchdog aborted the attempt currently in flight.</summary>
    public bool AbandonedByWatchdog => _gate.AbandonedByWatchdog;

    /// <summary>The attempt a retry must present to be allowed to continue.</summary>
    public int CurrentAttempt => _gate.CurrentAttempt;

    /// <summary>
    /// Opens a fresh attempt because the user asked for a connect, and runs it.
    /// </summary>
    /// <remarks>
    /// Opening is what clears any prior abandonment, and it is the only thing that does: a new
    /// attempt is a new decision by the user, while a retry is the same attempt finishing what it
    /// started.
    /// </remarks>
    public void UserRequestedConnect() => _runner.RunAttempt(_gate.OpenAttempt());

    /// <summary>
    /// Decides what becomes of a retry that has waited out its render pass, and runs it when it
    /// is still allowed to connect.
    /// </summary>
    /// <param name="attempt">The attempt the retry was scheduled for.</param>
    /// <param name="viewDisposed">Whether the view was torn down while the retry waited.</param>
    /// <returns>What was decided, so the caller can report a refusal.</returns>
    public RdpConnectRetryAdmission RetryArrived(int attempt, bool viewDisposed)
    {
        RdpConnectRetryAdmission admission = viewDisposed
            ? RdpConnectRetryAdmission.Refuse
            : _gate.AdmitRetry(attempt);

        if (admission == RdpConnectRetryAdmission.Admit)
        {
            _runner.RunAttempt(attempt);
        }

        return admission;
    }

    /// <summary>
    /// Decides whether the connect waiting behind a certificate check may still be started.
    /// </summary>
    /// <param name="viewDisposed">Whether the view was torn down while the check ran.</param>
    /// <returns>What was decided, so the caller can stop before starting the connect.</returns>
    /// <remarks>
    /// <para>The certificate probe and any trust question stand in front of the connect, and the
    /// Cancel button is on screen for the whole of that wait: the phase is Preparing, which is what
    /// shows the button. So this is a fourth place a Cancel has to be stopped at, and the one that
    /// costs the most when it is missed, because what stands behind it is the entry point of a
    /// user-requested connect - which opens a fresh attempt and, in doing so, clears the very
    /// latches the later refusals read. Let a cancelled attempt through here and the late-connect
    /// refusal cannot catch it either.</para>
    /// <para>The question asked is the one the late connect asks, so it is asked through the same
    /// policy rather than restated here: two surfaces that have to agree on "this attempt was
    /// abandoned" share the predicate instead of keeping a copy each.</para>
    /// <para>The watchdog's abort counts here too, which it did not while this lived in the view.
    /// The Preparing phase arms the watchdog and the check only suspends it once a check is known
    /// to be owed, so the budget can expire over the part of the probe that runs before the
    /// suspension - and that abort has already put the connect-timeout error on the screen.</para>
    /// </remarks>
    public RdpVerifiedConnectAdmission CertificateCheckSettled(bool viewDisposed)
    {
        RdpLateConnectDecision abandonment = RdpLateConnectPolicy.Resolve(
            _gate.AbandonedByWatchdog,
            _gate.AbandonedByUser);

        return viewDisposed || abandonment == RdpLateConnectDecision.Refuse
            ? RdpVerifiedConnectAdmission.Refuse
            : RdpVerifiedConnectAdmission.Proceed;
    }

    /// <summary>Records that the user asked for the attempt in flight to stop.</summary>
    public void UserCancelled() => _gate.AbandonByUser();

    /// <summary>Records that the connect watchdog aborted the attempt in flight.</summary>
    public void WatchdogAborted() => _gate.AbandonByWatchdog();

    /// <summary>
    /// Decides whether a connect that has just completed may be promoted, and drops the
    /// connection when it may not.
    /// </summary>
    /// <returns>What was decided, so the caller can stop before promoting the session.</returns>
    /// <remarks>
    /// The disconnect belongs here rather than at the call site: refusing the promotion while
    /// leaving the control connected is the same defect one step further down, a live session
    /// behind a screen that says the connection was stopped.
    /// </remarks>
    public RdpLateConnectDecision ConnectArrived()
    {
        RdpLateConnectDecision decision = RdpLateConnectPolicy.Resolve(
            _gate.AbandonedByWatchdog,
            _gate.AbandonedByUser);

        if (decision == RdpLateConnectDecision.Refuse)
        {
            _runner.DropAbandonedConnection();
        }

        return decision;
    }
}
