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

using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Plays the connect attempt sequence the user drives, and measures what the control is asked to
/// do at each step.
/// </summary>
/// <remarks>
/// <para>This is the branch's headline behaviour, run rather than read. The defect was a race
/// between two entry points: the RDP surface is often not laid out when the connect starts, so the
/// view schedules itself again about a render pass later, and that retry re-entered the entry
/// point of a connect the user had asked for - which begins by clearing the abandonment latches.
/// Press Cancel inside the retry window and the sequence was: the latch is set and the control is
/// asked to disconnect, then the retry clears the latch and calls Connect() again. The session the
/// user stopped came up live, the late-connect refusal could not catch it because the state it
/// reads had just been wiped, and the user-disconnect flag stayed raised, so the next genuine drop
/// of that live session was read as a user disconnect - no overlay, no diagnostic, a session dying
/// in silence.</para>
/// <para>Both halves of that sequence are decisions over the attempt in flight, so both live in
/// <see cref="RdpConnectAttemptArbiter"/> and both are exercised here through a recording runner:
/// no <c>Application</c>, no ActiveX control, no <c>Window</c>. What the view still owns is one
/// delegating statement per event, and that - only that - is read from its source in
/// <see cref="RdpLateConnectPolicyTests"/>.</para>
/// <para>Every refusal here carries its positive control, because an arbiter that refused
/// everything would satisfy all of them while breaking the case the retry exists for: a surface
/// that simply needed another render pass.</para>
/// </remarks>
public sealed class RdpConnectAttemptArbiterTests
{
    /// <summary>The defect itself, from Cancel to the connect that arrives after it.</summary>
    /// <remarks>
    /// One test for both halves on purpose: refusing the retry while letting the late connect
    /// promote, and refusing the promotion while leaving the control connected, are each the same
    /// defect one step further down. The user pressed Cancel; nothing may end with a live session.
    /// </remarks>
    [Fact]
    public void ACancelInsideTheRetryWindowStopsBothTheRetryAndTheConnectThatArrivesAfterIt()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        arbiter.UserRequestedConnect();
        int scheduled = Assert.Single(runner.RunAttempts);

        arbiter.UserCancelled();

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            arbiter.RetryArrived(scheduled, viewDisposed: false));
        Assert.Equal(new[] { scheduled }, runner.RunAttempts);

        // The half that was actually broken. Refusing the retry while quietly clearing the cancel
        // would leave the handshake already in flight free to promote the session anyway.
        Assert.True(arbiter.AbandonedByUser);
        Assert.Equal(RdpLateConnectDecision.Refuse, arbiter.ConnectArrived());
        Assert.Equal(1, runner.DroppedConnections);
    }

    /// <summary>
    /// The control: nobody abandoned this attempt, so its retry connects and the connect that
    /// follows is promoted.
    /// </summary>
    [Fact]
    public void ARetryOfAnAttemptNobodyAbandonedConnectsAndTheConnectIsPromoted()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        arbiter.UserRequestedConnect();
        int scheduled = Assert.Single(runner.RunAttempts);

        Assert.Equal(
            RdpConnectRetryAdmission.Admit,
            arbiter.RetryArrived(scheduled, viewDisposed: false));
        Assert.Equal(new[] { scheduled, scheduled }, runner.RunAttempts);

        Assert.Equal(RdpLateConnectDecision.Promote, arbiter.ConnectArrived());
        Assert.Equal(0, runner.DroppedConnections);
    }

    /// <summary>A watchdog abort ends the attempt in exactly the same way.</summary>
    /// <remarks>
    /// The abort tears the session down and puts the connect-timeout error on the screen. A retry
    /// that connected behind it would start a fresh connection under an error describing the
    /// connection it just replaced, and a promoted late connect would be a live session nobody can
    /// see.
    /// </remarks>
    [Fact]
    public void AWatchdogAbortStopsBothTheRetryAndTheConnectThatArrivesAfterIt()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        arbiter.UserRequestedConnect();
        int scheduled = Assert.Single(runner.RunAttempts);

        arbiter.WatchdogAborted();

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            arbiter.RetryArrived(scheduled, viewDisposed: false));
        Assert.True(arbiter.AbandonedByWatchdog);
        Assert.Equal(RdpLateConnectDecision.Refuse, arbiter.ConnectArrived());
        Assert.Equal(1, runner.DroppedConnections);
        Assert.Equal(new[] { scheduled }, runner.RunAttempts);
    }

    /// <summary>
    /// A connect the user asks for is the one thing that reopens the attempt, and a retry left
    /// over from the attempt before it cannot act on the new one.
    /// </summary>
    /// <remarks>
    /// This is why the retry carries a token instead of asking "is anything abandoned?" when it
    /// arrives. Cancel, then connect again: the new attempt legitimately clears the latches, so a
    /// question asked at that moment answers "nothing is abandoned" and the stale retry would
    /// connect a second time, on a session nobody is waiting for.
    /// </remarks>
    [Fact]
    public void AFreshConnectClearsTheAbandonmentAndStrandsTheRetryOfTheAttemptItReplaced()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        arbiter.UserRequestedConnect();
        int cancelled = Assert.Single(runner.RunAttempts);
        arbiter.UserCancelled();

        arbiter.UserRequestedConnect();
        Assert.Equal(2, runner.RunAttempts.Count);
        int current = runner.RunAttempts[1];

        Assert.NotEqual(cancelled, current);
        Assert.False(arbiter.AbandonedByUser);
        Assert.Equal(current, arbiter.CurrentAttempt);

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            arbiter.RetryArrived(cancelled, viewDisposed: false));
        Assert.Equal(2, runner.RunAttempts.Count);

        // The control on the refusal above: the current attempt's own retry still connects.
        Assert.Equal(
            RdpConnectRetryAdmission.Admit,
            arbiter.RetryArrived(current, viewDisposed: false));
        Assert.Equal(new[] { cancelled, current, current }, runner.RunAttempts);
    }

    /// <summary>A retry that arrives on a torn-down view touches nothing.</summary>
    /// <remarks>
    /// The retry waits out a render pass on the dispatcher, so the tab it belongs to can be closed
    /// while it waits. Connecting the control of a disposed view would leave a session running
    /// with no surface to show it and nothing left to tear it down.
    /// </remarks>
    [Fact]
    public void ARetryThatArrivesAfterTheViewWasTornDownIsRefused()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        arbiter.UserRequestedConnect();
        int scheduled = Assert.Single(runner.RunAttempts);

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            arbiter.RetryArrived(scheduled, viewDisposed: true));
        Assert.Equal(new[] { scheduled }, runner.RunAttempts);

        // The control: the same retry on a live view is admitted, so what refused it above is the
        // teardown and not the token it carries.
        Assert.Equal(
            RdpConnectRetryAdmission.Admit,
            arbiter.RetryArrived(scheduled, viewDisposed: false));
    }

    /// <summary>Nothing is a retry of an attempt that was never opened.</summary>
    [Fact]
    public void ARetryCarryingNoAttemptIsRefused()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            arbiter.RetryArrived(RdpConnectAttemptGate.NoAttempt, viewDisposed: false));

        arbiter.UserRequestedConnect();

        Assert.Equal(
            RdpConnectRetryAdmission.Refuse,
            arbiter.RetryArrived(RdpConnectAttemptGate.NoAttempt, viewDisposed: false));
        Assert.Single(runner.RunAttempts);
    }

    /// <summary>
    /// A Cancel taken while the certificate check is in flight stops the connect that was waiting
    /// behind it.
    /// </summary>
    /// <remarks>
    /// <para>The check runs before the connect does, so at this point no attempt has been opened
    /// yet: the Cancel lands on an arbiter whose token is still <see cref="RdpConnectAttemptGate.NoAttempt"/>.
    /// That is the real order of the first connect of a session, and it is why this decision is
    /// taken over the latches alone rather than over a token.</para>
    /// <para>What the refusal is worth is asserted below it, because refusing is only half the
    /// claim: the entry point behind this door opens a fresh attempt, and opening an attempt is
    /// what clears the latches. A connect let through here would therefore disarm the
    /// late-connect refusal on its way past, which is the whole reason a cancelled session used
    /// to come up live.</para>
    /// </remarks>
    [Fact]
    public void ACancelDuringTheCertificateCheckStopsTheConnectThatWasWaitingBehindIt()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        // The control, taken first: with nothing abandoned, the check settles into a connect.
        Assert.Equal(
            RdpVerifiedConnectAdmission.Proceed,
            arbiter.CertificateCheckSettled(viewDisposed: false));

        arbiter.UserCancelled();

        Assert.Equal(
            RdpVerifiedConnectAdmission.Refuse,
            arbiter.CertificateCheckSettled(viewDisposed: false));
        Assert.Empty(runner.RunAttempts);

        // The cancel is still standing, so everything downstream of it still refuses.
        Assert.True(arbiter.AbandonedByUser);
        Assert.Equal(RdpLateConnectDecision.Refuse, arbiter.ConnectArrived());
        Assert.Equal(1, runner.DroppedConnections);

        // And the defect itself, shown rather than described: this is what starting the connect
        // does to the state every later refusal reads.
        arbiter.UserRequestedConnect();
        Assert.False(arbiter.AbandonedByUser);
        Assert.Equal(RdpLateConnectDecision.Promote, arbiter.ConnectArrived());
    }

    /// <summary>A watchdog abort taken during the check ends the connect in the same way.</summary>
    /// <remarks>
    /// The Preparing phase arms the connect watchdog and the check suspends it only once a check
    /// is known to be owed, so the budget can expire over the part of the probe that runs before
    /// the suspension. The abort has already put the connect-timeout error on the screen; a
    /// connect starting behind it would run under an error describing it.
    /// </remarks>
    [Fact]
    public void AWatchdogAbortDuringTheCertificateCheckStopsTheConnectToo()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        arbiter.WatchdogAborted();

        Assert.Equal(
            RdpVerifiedConnectAdmission.Refuse,
            arbiter.CertificateCheckSettled(viewDisposed: false));
        Assert.Empty(runner.RunAttempts);
    }

    /// <summary>A check that settles on a torn-down view starts nothing.</summary>
    /// <remarks>
    /// The probe and the trust question are awaited, and the question is serialized across every
    /// session, so the answer can arrive minutes later, on a tab that has been closed since.
    /// Connecting there would leave a session running with no surface to show it.
    /// </remarks>
    [Fact]
    public void ACertificateCheckThatSettlesOnATornDownViewStartsNothing()
    {
        var runner = new RecordingRunner();
        var arbiter = new RdpConnectAttemptArbiter(runner);

        Assert.Equal(
            RdpVerifiedConnectAdmission.Refuse,
            arbiter.CertificateCheckSettled(viewDisposed: true));

        // The control: the same arbiter on a live view proceeds, so what refused above is the
        // teardown and not some state the arbiter was already carrying.
        Assert.Equal(
            RdpVerifiedConnectAdmission.Proceed,
            arbiter.CertificateCheckSettled(viewDisposed: false));
    }

    /// <summary>Records what the arbiter asked the control to do, in order.</summary>
    private sealed class RecordingRunner : IRdpConnectAttemptRunner
    {
        internal List<int> RunAttempts { get; } = new();

        internal int DroppedConnections { get; private set; }

        public void RunAttempt(int attempt) => RunAttempts.Add(attempt);

        public void DropAbandonedConnection() => DroppedConnections++;
    }
}
