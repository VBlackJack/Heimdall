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

/// <summary>What the connect watchdog timer must do after a decision point.</summary>
internal enum RdpConnectWatchdogAction
{
    /// <summary>Leave the timer exactly as it is.</summary>
    Leave,

    /// <summary>Arm, or re-arm, the timer on the resolved budget.</summary>
    Arm,

    /// <summary>
    /// Stop the timer and forget any credential-wait promotion: the attempt this
    /// watchdog was watching is over.
    /// </summary>
    Cancel,

    /// <summary>
    /// Stop the timer but keep every other watchdog state, because the view is
    /// waiting on a human answer rather than on the network.
    /// </summary>
    Suspend,
}

internal static class RdpConnectWatchdogPolicy
{
    public const int DisabledTimeoutMs = 0;
    public const int DefaultTimeoutMs = 45_000;
    public const int MinTimeoutMs = 5_000;
    public const int MaxTimeoutMs = 600_000;

    /// <summary>
    /// Extra time added on top of the credential pipeline budget so the connect
    /// watchdog strictly outlives the autofill's own timeout. This guarantees the
    /// autofill graceful TimedOut/Failed retry path runs instead of a hard
    /// watchdog teardown.
    /// </summary>
    public const int CredentialWaitGraceMs = 15_000;

    public static bool ShouldArm(RdpConnectionPhase phase)
        => phase is RdpConnectionPhase.Preparing
            or RdpConnectionPhase.Connecting
            or RdpConnectionPhase.Loading;

    public static bool ShouldCancel(RdpConnectionPhase phase)
        => phase is RdpConnectionPhase.None
            or RdpConnectionPhase.Connected;

    /// <summary>
    /// What the watchdog must do when the view enters <paramref name="phase"/>.
    /// </summary>
    /// <param name="phase">The phase the view is entering.</param>
    /// <param name="certificateCheckInProgress">
    /// Whether a server-certificate check is outstanding for this view.
    /// </param>
    /// <remarks>
    /// <b>The connect watchdog exists to catch a connection that hangs, not a human who has
    /// not answered yet.</b> The wait inside a certificate check is a human reading a
    /// fingerprint, and there is no budget at which that becomes a fault: a session torn down
    /// because its owner was still reading is a session destroyed by its own safety check.
    /// <para>
    /// <b>The wait can now be indefinite, and that is deliberate.</b> The question used to be
    /// an application-modal window, which forced an answer by making the rest of the
    /// application unusable until one arrived. It is now displayed inside the session pane
    /// that asked it, so it blocks that one connection and nothing else: a user can leave it
    /// unanswered for as long as they like while they work in every other tab and window. What
    /// that costs is one pane sitting in <see cref="RdpConnectionPhase.Preparing"/>, showing an
    /// explicit question, with two ways out on screen. What it buys is that no session is torn
    /// down for a delay that belongs to the person, not to the network. The pane is not silent
    /// meanwhile: its status line says it is waiting on the answer.
    /// </para>
    /// <para>
    /// The phase itself is unaffected and still lights the stepper's first segment, and the
    /// Cancel button it shows is genuinely usable now - in this pane and in every other one.
    /// That was NOT true while the question was a <c>Window.ShowDialog</c>: application modality
    /// disabled every other window at the Win32 level for as long as any question was on
    /// screen, so the button reported itself enabled and could not be clicked. So there are
    /// three ways out of the wait rather than one: answer, refuse, or cancel the connection.
    /// </para>
    /// <para>
    /// A phase that ends the attempt still wins over a suspension, so cancelling or tearing
    /// down while a question is outstanding stops the watchdog rather than leaving it in
    /// limbo.
    /// </para>
    /// </remarks>
    public static RdpConnectWatchdogAction ResolveTransitionAction(
        RdpConnectionPhase phase,
        bool certificateCheckInProgress)
    {
        if (ShouldCancel(phase))
        {
            return RdpConnectWatchdogAction.Cancel;
        }

        if (certificateCheckInProgress)
        {
            return RdpConnectWatchdogAction.Suspend;
        }

        return ShouldArm(phase)
            ? RdpConnectWatchdogAction.Arm
            : RdpConnectWatchdogAction.Leave;
    }

    /// <summary>
    /// What the watchdog must do once the certificate check has ended, whatever its answer.
    /// </summary>
    /// <param name="phase">The phase the view is in when the check returns.</param>
    /// <param name="viewDisposed">Whether the view was torn down while the check ran.</param>
    /// <remarks>
    /// Resuming is not unconditional. A view torn down during the check, or one the user
    /// cancelled - which leaves the phase in <see cref="RdpConnectionPhase.None"/> - must
    /// end with a stopped timer, never with a fresh budget armed over a session nobody is
    /// waiting for.
    /// </remarks>
    public static RdpConnectWatchdogAction ResolveCertificateCheckCompletedAction(
        RdpConnectionPhase phase,
        bool viewDisposed)
        => !viewDisposed && ShouldArm(phase)
            ? RdpConnectWatchdogAction.Arm
            : RdpConnectWatchdogAction.Cancel;

    public static int ResolveTimeoutMs(int configured)
        => configured <= DisabledTimeoutMs
            ? DisabledTimeoutMs
            : Math.Clamp(configured, MinTimeoutMs, MaxTimeoutMs);

    /// <summary>
    /// Resolves the Stage 2 watchdog budget applied once the credential-autofill
    /// watcher proves the RDP stack is reachable and is blocked waiting on the
    /// remote NLA credential prompt. A disabled watchdog stays disabled. Otherwise
    /// the budget is the larger of the configured watchdog and the autofill timeout
    /// plus <see cref="CredentialWaitGraceMs"/>, clamped to
    /// [<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>]. Total and
    /// overflow-safe over the full <see cref="int"/> range.
    /// </summary>
    public static int ResolveStageTwoTimeoutMs(int configuredWatchdogMs, int autofillTimeoutMs)
    {
        if (configuredWatchdogMs <= DisabledTimeoutMs)
        {
            return DisabledTimeoutMs;
        }

        int baseTimeoutMs = Math.Max(configuredWatchdogMs, autofillTimeoutMs);

        // Promote to long before adding the grace window to guard against int overflow.
        long extendedTimeoutMs = (long)baseTimeoutMs + CredentialWaitGraceMs;

        return (int)Math.Clamp(extendedTimeoutMs, MinTimeoutMs, MaxTimeoutMs);
    }
}
