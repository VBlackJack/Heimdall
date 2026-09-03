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

using System.Reflection;
using Heimdall.App.Views;
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that a connect retry can finish an attempt but can never re-authorize one.
/// </summary>
/// <remarks>
/// <para>The RDP surface is often not laid out when the connect starts, so the view schedules
/// itself again about a render pass later. That retry re-entered the same entry point as a connect
/// the user had just asked for, and that entry point begins by clearing the abandonment latches.
/// Press Cancel inside the retry window and the sequence was: the latch is set and the control is
/// asked to disconnect, then the retry clears the latch and calls Connect() again. The session the
/// user stopped came up live, and the late-connect refusal could not catch it either, because the
/// state it reads had been wiped by the same statement.</para>
/// <para>What the tests below distinguish is "the retry ran" from "the retry was allowed to
/// present itself as a new user-authorized attempt". A retry is admitted when nothing happened to
/// its attempt - that is the positive control, without which refusing everything would pass - and
/// refused as soon as the attempt is over, whether because the user cancelled it, the watchdog
/// aborted it, or a newer attempt has replaced it.</para>
/// <para>Scope, stated rather than implied: these measure the gate, which is the state alone.
/// What is decided from that state, and what the control is then asked to do about it, is measured
/// in <see cref="RdpConnectAttemptArbiterTests"/>, which plays the whole sequence against a
/// recording runner. The only thing pinned about the view here is that it keeps no second copy of
/// this state to drift from, which the last test reads off the compiled type; the statements that
/// join the view to the arbiter are read from its source in
/// <see cref="RdpLateConnectPolicyTests"/>.</para>
/// </remarks>
public sealed class RdpConnectAttemptGateTests
{
    /// <summary>The defect itself: Cancel during the retry window, retry afterwards.</summary>
    [Fact]
    public void ARetryOfAnAttemptTheUserCancelledIsRefused_AndTheCancelSurvivesIt()
    {
        var gate = new RdpConnectAttemptGate();
        int attempt = gate.OpenAttempt();

        gate.AbandonByUser();

        Assert.Equal(RdpConnectRetryAdmission.Refuse, gate.AdmitRetry(attempt));

        // The second half is the one that was actually broken. Refusing to connect while
        // quietly clearing the latch would leave the late OnConnected free to promote the
        // session anyway, which is the same defect one step further down.
        Assert.True(gate.AbandonedByUser);
    }

    /// <summary>
    /// The control: a retry of an attempt nobody touched still connects.
    /// </summary>
    /// <remarks>
    /// Without this, a gate that refused every retry would satisfy the test above and would break
    /// the case the retry exists for - a surface that simply needed another render pass.
    /// </remarks>
    [Fact]
    public void ARetryOfAnAttemptNobodyAbandonedIsAdmitted()
    {
        var gate = new RdpConnectAttemptGate();
        int attempt = gate.OpenAttempt();

        Assert.Equal(RdpConnectRetryAdmission.Admit, gate.AdmitRetry(attempt));
        Assert.False(gate.AbandonedByUser);
        Assert.False(gate.AbandonedByWatchdog);
    }

    /// <summary>A watchdog abort is abandoned in the same way, and for the same reason.</summary>
    /// <remarks>
    /// The abort tears the session down and puts the connect-timeout error on the screen. A retry
    /// that cleared that latch would start a fresh connection behind an error overlay describing
    /// the connection it just replaced.
    /// </remarks>
    [Fact]
    public void ARetryOfAnAttemptTheWatchdogAbortedIsRefused_AndTheAbortSurvivesIt()
    {
        var gate = new RdpConnectAttemptGate();
        int attempt = gate.OpenAttempt();

        gate.AbandonByWatchdog();

        Assert.Equal(RdpConnectRetryAdmission.Refuse, gate.AdmitRetry(attempt));
        Assert.True(gate.AbandonedByWatchdog);
    }

    /// <summary>
    /// A retry left over from an earlier attempt cannot act on the current one.
    /// </summary>
    /// <remarks>
    /// This is why the retry carries a token rather than asking "is anything abandoned?" on
    /// arrival. Cancel, then connect again: the new attempt legitimately clears the latches, so a
    /// question asked at that moment answers "nothing is abandoned" and the stale retry would
    /// connect a second time, on a session nobody is waiting for.
    /// </remarks>
    [Fact]
    public void ARetryScheduledUnderAnEarlierAttemptIsRefusedByTheAttemptThatReplacedIt()
    {
        var gate = new RdpConnectAttemptGate();
        int cancelled = gate.OpenAttempt();
        gate.AbandonByUser();

        int current = gate.OpenAttempt();

        Assert.NotEqual(cancelled, current);
        Assert.Equal(RdpConnectRetryAdmission.Refuse, gate.AdmitRetry(cancelled));
        Assert.Equal(RdpConnectRetryAdmission.Admit, gate.AdmitRetry(current));
    }

    /// <summary>
    /// A connect the user asked for is the one thing that clears the latches.
    /// </summary>
    /// <remarks>
    /// The clearing is not an accident to be removed: without it, one cancelled attempt would
    /// refuse every later connect on the same view for the life of the session.
    /// </remarks>
    [Fact]
    public void OpeningAFreshAttemptClearsBothLatches()
    {
        var gate = new RdpConnectAttemptGate();
        gate.OpenAttempt();
        gate.AbandonByUser();
        gate.AbandonByWatchdog();

        gate.OpenAttempt();

        Assert.False(gate.AbandonedByUser);
        Assert.False(gate.AbandonedByWatchdog);
    }

    /// <summary>Nothing is a retry of an attempt that was never opened.</summary>
    [Fact]
    public void ARetryCarryingNoAttemptIsRefused()
    {
        var gate = new RdpConnectAttemptGate();

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            gate.AdmitRetry(RdpConnectAttemptGate.NoAttempt));

        gate.OpenAttempt();

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            gate.AdmitRetry(RdpConnectAttemptGate.NoAttempt));
    }

    /// <summary>
    /// Every instance bool the view is allowed to carry, each reviewed as not this state.
    /// </summary>
    /// <remarks>
    /// <para>The list is what makes the test below a measurement. Filtering the census by name -
    /// looking for "abandon" - would catch only a latch re-added under one of the two names the
    /// defect used, and the shape that has to be caught is a latch re-added under any name at
    /// all: <c>_connectStoppedByUser</c>, <c>_userCancelled</c>, <c>_connectAborted</c>. So the
    /// census takes every instance bool on the type and measures it against this list, and adding
    /// a field to the view fails here until someone writes down why it is not a second copy of the
    /// abandonment state.</para>
    /// <para><c>bool?</c> counts, and had to be asked for by name: <c>typeof(bool)</c> and
    /// <c>typeof(bool?)</c> are different types, so an earlier census walked straight past
    /// <c>private bool? _connectStoppedByUser;</c> - which is the natural declaration for a
    /// tri-state "not asked yet / cancelled / not cancelled" latch, and reintroduces the defect
    /// exactly as the two plain fields did.</para>
    /// <para><c>_userInitiatedDisconnect</c> is the entry to read twice: it records that a session
    /// which came up was torn down by the user, which is a different fact from an attempt being
    /// abandoned before it ever connected, and it is cleared on the disconnect path rather than on
    /// a retry. <c>_contentLoaded</c> is generated by the XAML compiler.
    /// <c>&lt;SessionLoggingOverride&gt;k__BackingField</c> is the tri-state profile override for
    /// session transcripts - inherit, on, off - written by whoever opens the tab and read once
    /// when logging starts; it is never touched by a connect, a cancel or a retry.</para>
    /// </remarks>
    private static readonly string[] AllowedViewBoolFields =
    [
        "_contentLoaded",
        "<SessionLoggingOverride>k__BackingField",
        "_redirectionExpandedOverride",
        "_watchdogCredentialWaitActive",
        "_initialized",
        "_connectStarted",
        "_disposed",
        "_allowResolutionUpdates",
        "_sleepPreventionActive",
        "_comDrivenStatusActive",
        "_escapeHookRegistered",
        "_isFullscreen",
        "_disconnectConfirmInFlight",
        "_resolutionReconnectConfirmInFlight",
        "_autofillAttemptInFlight",
        "_dpiChangeDroppedDuringLockout",
        "_eventConnectEmitted",
        "_eventDisconnectEmitted",
        "_userInitiatedDisconnect",
    ];

    /// <summary>
    /// The view keeps no second copy of the abandonment state.
    /// </summary>
    /// <remarks>
    /// <para>Read off the compiled type, not the source text, and it claims only what it measures:
    /// it does not prove the retry path carries the token - that needs the ActiveX control - it
    /// proves the view carries no boolean copy of this state. The defect above existed because the
    /// two latches were plain fields of the view, assigned from five places including the top of
    /// every connect; a re-added field would restore exactly that, and it would not have to be
    /// called anything in particular to do it.</para>
    /// <para>Hence a census of every instance bool and <c>bool?</c> rather than a search for a
    /// word, and hence the two assertions before it: an absence is worth nothing until the query
    /// that reports it is known to return anything at all.</para>
    /// <para><b>The bound on it.</b> The census is over boolean fields, so the same latch carried
    /// as an <c>int</c>, an enum or a tuple still escapes it. It is not "there is nowhere else on
    /// the view for this state to live"; it is "the shape the defect actually had cannot come back
    /// unreviewed". Auto-properties are inside the census rather than outside it, but they arrive
    /// under the compiler's own name for their backing field, which is how
    /// <c>&lt;SessionLoggingOverride&gt;k__BackingField</c> is spelled in the list.</para>
    /// </remarks>
    [Fact]
    public void TheViewHoldsThisStateOnlyInTheArbiter()
    {
        string[] flags = typeof(EmbeddedRdpView)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(bool) || field.FieldType == typeof(bool?))
            .Select(field => field.Name)
            .ToArray();

        // The control on the census itself. Without it a reflection query that matched nothing -
        // a renamed type, a changed binding flag - would report the view clean for ever.
        Assert.Contains("_watchdogCredentialWaitActive", flags);
        Assert.Contains("_redirectionExpandedOverride", flags);

        string[] unexpected = flags.Except(AllowedViewBoolFields, StringComparer.Ordinal).ToArray();
        Assert.True(
            unexpected.Length == 0,
            "The RDP view carries a bool nobody has reviewed against this defect: "
                + string.Join(", ", unexpected)
                + ". If it is not a second copy of the abandonment state, add it to "
                + nameof(AllowedViewBoolFields) + " and say so there. A second copy of that state, "
                + "under any name, is what let a retry clear the user's Cancel.");

        // And the one place it is allowed to live is still on the view, holding the gate.
        Assert.Contains(
            typeof(EmbeddedRdpView).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(RdpConnectAttemptArbiter));
        Assert.Contains(
            typeof(RdpConnectAttemptArbiter).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(RdpConnectAttemptGate));
    }
}
