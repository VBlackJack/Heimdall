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

/// <summary>What may be done with a connect retry once it has waited out a render pass.</summary>
internal enum RdpConnectRetryAdmission
{
    /// <summary>The retry continues the current attempt, which nobody has abandoned.</summary>
    Admit,

    /// <summary>The retry is stale or its attempt was abandoned, so it must not connect.</summary>
    Refuse,
}

/// <summary>
/// Owns the abandonment state of the connect attempt in flight, and decides whether a retry may
/// still act on it.
/// </summary>
/// <remarks>
/// <para>The RDP surface is not always laid out when the connect starts, so the view schedules
/// itself again after a render pass. That retry used to re-enter the same entry point as a genuine
/// new connect, which begins by clearing the abandonment latches: a Cancel pressed inside the
/// retry window set the latch, the retry cleared it about a hundred milliseconds later, and the
/// session the user had just stopped came up connected. The late-connect refusal could not save
/// it either, because the state it reads had been wiped by the same statement.</para>
/// <para>So the two are separated here. Only <see cref="OpenAttempt"/> - a connect the user asked
/// for - clears the latches, and it stamps the attempt with a token. A retry has to present that
/// token, and is refused both when its attempt was abandoned and when the token has moved on,
/// which is what stops a retry left over from a previous attempt from connecting a session
/// nobody is waiting for.</para>
/// <para>Both latches are kept here, not just the user's. The watchdog's abort has the same
/// shape - it tears the attempt down and puts the connect-timeout error on screen - and it was
/// cleared by the same statement, so a retry could restart a connection behind that error.</para>
/// </remarks>
internal sealed class RdpConnectAttemptGate
{
    /// <summary>The token of an attempt that was never opened.</summary>
    internal const int NoAttempt = 0;

    private int _attemptToken = NoAttempt;

    /// <summary>Whether the user cancelled the attempt currently in flight.</summary>
    internal bool AbandonedByUser { get; private set; }

    /// <summary>Whether the connect watchdog aborted the attempt currently in flight.</summary>
    internal bool AbandonedByWatchdog { get; private set; }

    /// <summary>The attempt a retry must present to be allowed to continue.</summary>
    internal int CurrentAttempt => _attemptToken;

    /// <summary>Opens a fresh, user-authorized attempt.</summary>
    /// <returns>The token identifying it, to be carried by any retry of this attempt.</returns>
    /// <remarks>
    /// This is the only place the latches are cleared, and that is the whole point: a new attempt
    /// is a new decision by the user, while a retry is the same attempt finishing what it started.
    /// Opening also invalidates every retry still pending against the previous attempt.
    /// </remarks>
    internal int OpenAttempt()
    {
        _attemptToken++;
        AbandonedByUser = false;
        AbandonedByWatchdog = false;
        return _attemptToken;
    }

    /// <summary>Records that the user asked for the attempt in flight to stop.</summary>
    internal void AbandonByUser() => AbandonedByUser = true;

    /// <summary>Records that the connect watchdog aborted the attempt in flight.</summary>
    internal void AbandonByWatchdog() => AbandonedByWatchdog = true;

    /// <summary>Decides whether a retry carrying <paramref name="attemptToken"/> may connect.</summary>
    /// <param name="attemptToken">The token the retry was scheduled with.</param>
    internal RdpConnectRetryAdmission AdmitRetry(int attemptToken)
        => attemptToken != NoAttempt
            && attemptToken == _attemptToken
            && !AbandonedByUser
            && !AbandonedByWatchdog
                ? RdpConnectRetryAdmission.Admit
                : RdpConnectRetryAdmission.Refuse;
}
