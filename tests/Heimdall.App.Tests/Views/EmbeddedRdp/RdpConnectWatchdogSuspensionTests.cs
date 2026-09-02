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
/// Freezes what the connect watchdog does while a server-certificate question is queued.
/// </summary>
/// <remarks>
/// <para>The owner keeps about ten RDP sessions open. Reconnecting them all put ten views into
/// <see cref="RdpConnectionPhase.Preparing"/> at once, each arming its own 45 s connect budget,
/// while the trust prompts are serialized and shown one at a time. At ten to fifteen seconds an
/// answer, the fourth session in the queue spent its whole budget before its question was even
/// displayed. Serializing the prompts had turned "a dialog hides behind another window" into
/// "the back of the queue times out".</para>
/// <para>Time is modelled here, not slept: the double below knows the budget it was armed on and
/// reports whether an elapsed span would have fired it. A wall-clock wait would measure the
/// scheduler instead.</para>
/// </remarks>
public sealed class RdpConnectWatchdogSuspensionTests
{
    /// <summary>The budget the view resolves for a default installation.</summary>
    private static readonly int BudgetMs =
        RdpConnectWatchdogPolicy.ResolveTimeoutMs(RdpConnectWatchdogPolicy.DefaultTimeoutMs);

    /// <summary>How long a queue of ten serialized certificate questions can run.</summary>
    private const int QueuedQuestionsMs = 10 * 15_000;

    // Requirement 1. A certificate check that outlasts the budget does not fire the watchdog.
    [Fact]
    public void CertificateCheckLongerThanTheBudget_DoesNotFireTheWatchdog()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);

        // Positive control: entering Preparing really does arm the watchdog, so the assertion
        // below measures a suspension and not an arming that never happened.
        Assert.True(
            timer.IsArmed,
            "Preparing no longer arms the connect watchdog, so this test proves nothing about "
                + "suspending it.");
        Assert.True(QueuedQuestionsMs > BudgetMs);

        arbiter.CertificateCheckStarted();

        Assert.False(
            timer.AdvanceAndReportExpiry(QueuedQuestionsMs),
            "The connect watchdog expired while this session's certificate question was still "
                + "queued behind other sessions' questions, tearing down a session whose only "
                + "fault was being at the back of the queue.");
        Assert.False(timer.IsArmed);
    }

    // Requirement 2. A connection that hangs after the check still fires it, on the same budget.
    [Fact]
    public void ConnectionHangingAfterTheCheck_FiresTheWatchdogOnTheUnchangedBudget()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);
        arbiter.CertificateCheckStarted();
        Assert.False(timer.AdvanceAndReportExpiry(QueuedQuestionsMs));

        arbiter.CertificateCheckCompleted(RdpConnectionPhase.Preparing, viewDisposed: false);

        Assert.True(timer.IsArmed, "The watchdog was not resumed once the check had answered.");
        Assert.False(
            timer.AdvanceAndReportExpiry(BudgetMs - 1),
            "The watchdog fired before its own budget had elapsed, so the time the human spent "
                + "answering was still being charged to the connection.");
        Assert.True(
            timer.AdvanceAndReportExpiry(1),
            "A connection hanging after the certificate check no longer trips the watchdog.");
    }

    // Requirement 2, at the other arming phase: the view moves to Connecting once BeginConnect
    // runs, and a stall there is exactly what the watchdog exists for.
    [Fact]
    public void ConnectingPhase_ArmsAndFiresOnTheSameBudget()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Connecting);

        Assert.True(timer.IsArmed);
        Assert.False(timer.AdvanceAndReportExpiry(BudgetMs - 1));
        Assert.True(timer.AdvanceAndReportExpiry(1));
    }

    // Requirement 3. Cancelling during the check cancels; the resume must not resurrect it.
    [Fact]
    public void CancelDuringTheCheck_StopsTheWatchdogAndTheResumeDoesNotRearmIt()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);
        arbiter.CertificateCheckStarted();

        // What OnCancelConnectClick does first, before it cancels the verification token.
        arbiter.PhaseChanged(RdpConnectionPhase.None);

        Assert.Equal(
            new[]
            {
                nameof(IRdpConnectWatchdogTimer.Arm),
                nameof(IRdpConnectWatchdogTimer.Suspend),
                nameof(IRdpConnectWatchdogTimer.Cancel),
            },
            timer.Calls);

        arbiter.CertificateCheckCompleted(RdpConnectionPhase.None, viewDisposed: false);

        Assert.False(
            timer.IsArmed,
            "The watchdog was re-armed after the user cancelled, so its expiry would raise a "
                + "reconnect overlay on a session the user asked to abandon.");
        Assert.False(timer.AdvanceAndReportExpiry(QueuedQuestionsMs));
    }

    // Requirement 4. A teardown during the check must not leave a timer running.
    [Fact]
    public void TeardownDuringTheCheck_LeavesNoTimerRunning()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);
        arbiter.CertificateCheckStarted();

        arbiter.CertificateCheckCompleted(RdpConnectionPhase.Preparing, viewDisposed: true);

        Assert.False(
            timer.IsArmed,
            "The certificate check resumed the watchdog of a view that had already been torn "
                + "down, leaving a timer running on a disposed session.");
        Assert.False(timer.AdvanceAndReportExpiry(QueuedQuestionsMs));
        Assert.Equal(nameof(IRdpConnectWatchdogTimer.Cancel), timer.Calls[^1]);
    }

    // The positive control for the test above: on a live view that same sequence does resume.
    [Fact]
    public void SameSequenceOnALiveView_DoesResumeTheWatchdog()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);
        arbiter.CertificateCheckStarted();

        arbiter.CertificateCheckCompleted(RdpConnectionPhase.Preparing, viewDisposed: false);

        Assert.True(timer.IsArmed);
        Assert.True(timer.AdvanceAndReportExpiry(BudgetMs));
    }

    // Requirement 5. A profile that owes no certificate check is untouched: one arming, and the
    // budget is not restarted by the resume the caller runs unconditionally.
    [Fact]
    public void ProfileWithoutACertificateCheck_KeepsOneArmingAndTheOriginalBudget()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);

        Assert.False(timer.AdvanceAndReportExpiry(BudgetMs - 1));

        // The caller runs this in a finally whether or not a check was owed.
        arbiter.CertificateCheckCompleted(RdpConnectionPhase.Preparing, viewDisposed: false);

        Assert.Equal(new[] { nameof(IRdpConnectWatchdogTimer.Arm) }, timer.Calls);
        Assert.True(
            timer.AdvanceAndReportExpiry(1),
            "The watchdog of a profile that owes no certificate check was re-armed, restarting "
                + "its budget from zero and silently doubling the time a hung connect takes to "
                + "be caught.");
    }

    // Requirement 5, at the decision itself: with no check outstanding every phase resolves to
    // exactly what the view did before any of this existed.
    [Theory]
    [InlineData((int)RdpConnectionPhase.None, (int)RdpConnectWatchdogAction.Cancel)]
    [InlineData((int)RdpConnectionPhase.Preparing, (int)RdpConnectWatchdogAction.Arm)]
    [InlineData((int)RdpConnectionPhase.Connecting, (int)RdpConnectWatchdogAction.Arm)]
    [InlineData((int)RdpConnectionPhase.Loading, (int)RdpConnectWatchdogAction.Arm)]
    [InlineData((int)RdpConnectionPhase.Connected, (int)RdpConnectWatchdogAction.Cancel)]
    public void ResolveTransitionAction_WithoutACheck_MatchesTheOriginalArming(
        int phaseValue,
        int expectedAction)
    {
        RdpConnectWatchdogAction actual = RdpConnectWatchdogPolicy.ResolveTransitionAction(
            (RdpConnectionPhase)phaseValue,
            certificateCheckInProgress: false);

        Assert.Equal((RdpConnectWatchdogAction)expectedAction, actual);
    }

    // A phase that ends the attempt still wins over an outstanding question.
    [Theory]
    [InlineData((int)RdpConnectionPhase.None, (int)RdpConnectWatchdogAction.Cancel)]
    [InlineData((int)RdpConnectionPhase.Preparing, (int)RdpConnectWatchdogAction.Suspend)]
    [InlineData((int)RdpConnectionPhase.Connecting, (int)RdpConnectWatchdogAction.Suspend)]
    [InlineData((int)RdpConnectionPhase.Loading, (int)RdpConnectWatchdogAction.Suspend)]
    [InlineData((int)RdpConnectionPhase.Connected, (int)RdpConnectWatchdogAction.Cancel)]
    public void ResolveTransitionAction_DuringACheck_SuspendsUnlessTheAttemptIsOver(
        int phaseValue,
        int expectedAction)
    {
        RdpConnectWatchdogAction actual = RdpConnectWatchdogPolicy.ResolveTransitionAction(
            (RdpConnectionPhase)phaseValue,
            certificateCheckInProgress: true);

        Assert.Equal((RdpConnectWatchdogAction)expectedAction, actual);
    }

    // A second start must not stack, and a completion without a suspension must not touch the
    // timer at all - that second half is what requirement 5 rests on.
    [Fact]
    public void UnbalancedCalls_DoNotStackOrTouchAnUnsuspendedTimer()
    {
        FakeConnectWatchdogTimer timer = new(BudgetMs);
        RdpConnectWatchdogArbiter arbiter = new(timer);

        arbiter.CertificateCheckCompleted(RdpConnectionPhase.Preparing, viewDisposed: false);
        Assert.Empty(timer.Calls);

        arbiter.PhaseChanged(RdpConnectionPhase.Preparing);
        arbiter.CertificateCheckStarted();
        arbiter.CertificateCheckStarted();

        Assert.Equal(
            new[]
            {
                nameof(IRdpConnectWatchdogTimer.Arm),
                nameof(IRdpConnectWatchdogTimer.Suspend),
            },
            timer.Calls);
        Assert.True(arbiter.CertificateCheckInProgress);

        arbiter.CertificateCheckCompleted(RdpConnectionPhase.Preparing, viewDisposed: false);
        Assert.False(arbiter.CertificateCheckInProgress);
    }

    /// <summary>
    /// A connect watchdog whose clock the test advances, instead of waiting on one.
    /// </summary>
    /// <remarks>
    /// It models the one property the real <c>DispatcherTimer</c> contributes: it expires when
    /// the budget has elapsed since it was armed, and never while it is stopped.
    /// </remarks>
    private sealed class FakeConnectWatchdogTimer(int budgetMs) : IRdpConnectWatchdogTimer
    {
        private int _nowMs;
        private int? _armedAtMs;

        /// <summary>Every verb the arbiter invoked, in order.</summary>
        public List<string> Calls { get; } = [];

        public bool IsArmed => _armedAtMs is not null;

        public void Arm()
        {
            _armedAtMs = _nowMs;
            Calls.Add(nameof(Arm));
        }

        public void Cancel()
        {
            _armedAtMs = null;
            Calls.Add(nameof(Cancel));
        }

        public void Suspend()
        {
            _armedAtMs = null;
            Calls.Add(nameof(Suspend));
        }

        /// <summary>Moves the clock on and says whether the watchdog has expired.</summary>
        /// <param name="elapsedMs">How much time passes.</param>
        public bool AdvanceAndReportExpiry(int elapsedMs)
        {
            _nowMs += elapsedMs;
            return _armedAtMs is int armedAtMs && _nowMs - armedAtMs >= budgetMs;
        }
    }
}
