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

/// <summary>The connect watchdog timer, as the arbiter needs to drive it.</summary>
/// <remarks>
/// Three verbs rather than start/stop, because the two ways of stopping differ: a
/// cancellation ends the attempt and drops the credential-wait promotion with it, while a
/// suspension keeps every other watchdog state and expects a later <see cref="Arm"/>.
/// </remarks>
internal interface IRdpConnectWatchdogTimer
{
    /// <summary>Arms, or re-arms, the watchdog on the budget its owner resolves.</summary>
    void Arm();

    /// <summary>Stops the watchdog and forgets any credential-wait promotion.</summary>
    void Cancel();

    /// <summary>Stops the watchdog, keeping the rest of its state, for a human wait.</summary>
    void Suspend();
}

/// <summary>
/// Owns whether the connect watchdog is running, so that a queued certificate question
/// does not spend the connection's budget.
/// </summary>
/// <remarks>
/// <para>The reason this is a type and not two statements in the view: with about ten RDP
/// sessions reconnecting at once, every view enters <see cref="RdpConnectionPhase.Preparing"/>
/// immediately and arms its own connect budget, while the trust prompts are serialized and
/// shown one at a time. The sessions at the back of the queue exceeded the budget before
/// their question was ever displayed. Serializing the prompts had turned "a dialog hides
/// behind another window" into "the back of the queue times out".</para>
/// <para>The whole certificate check is suspended, not just the question. The probe bounds
/// itself - five seconds by default - so the watchdog is not what protects it, and the only
/// unbounded wait inside the check is the human answer, which is precisely the wait that must
/// not expire.</para>
/// <para>All members are called from the UI thread, like the timer they drive.</para>
/// </remarks>
internal sealed class RdpConnectWatchdogArbiter
{
    private readonly IRdpConnectWatchdogTimer _timer;

    /// <summary>Initializes a new instance of the <see cref="RdpConnectWatchdogArbiter"/> class.</summary>
    /// <param name="timer">The watchdog timer to drive.</param>
    public RdpConnectWatchdogArbiter(IRdpConnectWatchdogTimer timer)
        => _timer = timer ?? throw new ArgumentNullException(nameof(timer));

    /// <summary>Whether a server-certificate check is outstanding for this view.</summary>
    public bool CertificateCheckInProgress { get; private set; }

    /// <summary>Applies the watchdog decision that entering <paramref name="phase"/> implies.</summary>
    /// <param name="phase">The phase the view is entering.</param>
    public void PhaseChanged(RdpConnectionPhase phase)
        => Apply(RdpConnectWatchdogPolicy.ResolveTransitionAction(
            phase,
            CertificateCheckInProgress));

    /// <summary>
    /// Suspends the watchdog because a server-certificate check has begun.
    /// </summary>
    /// <remarks>
    /// Called only where a check is genuinely owed. A profile that owes none never reaches
    /// here, so its watchdog is armed by the phase transition and stays on exactly the
    /// budget it had before any of this existed.
    /// </remarks>
    public void CertificateCheckStarted()
    {
        if (CertificateCheckInProgress)
        {
            return;
        }

        CertificateCheckInProgress = true;
        Apply(RdpConnectWatchdogAction.Suspend);
    }

    /// <summary>
    /// Ends the suspension started by <see cref="CertificateCheckStarted"/> and resumes the
    /// watchdog when there is still a connection to watch.
    /// </summary>
    /// <param name="phase">The phase the view is in now the check has returned.</param>
    /// <param name="viewDisposed">Whether the view was torn down while the check ran.</param>
    /// <remarks>
    /// A no-op when no suspension is outstanding: resuming a watchdog nobody suspended would
    /// restart the budget of a connection already being watched.
    /// </remarks>
    public void CertificateCheckCompleted(RdpConnectionPhase phase, bool viewDisposed)
    {
        if (!CertificateCheckInProgress)
        {
            return;
        }

        CertificateCheckInProgress = false;
        Apply(RdpConnectWatchdogPolicy.ResolveCertificateCheckCompletedAction(phase, viewDisposed));
    }

    private void Apply(RdpConnectWatchdogAction action)
    {
        switch (action)
        {
            case RdpConnectWatchdogAction.Arm:
                _timer.Arm();
                break;

            case RdpConnectWatchdogAction.Cancel:
                _timer.Cancel();
                break;

            case RdpConnectWatchdogAction.Suspend:
                _timer.Suspend();
                break;

            default:
                break;
        }
    }
}
