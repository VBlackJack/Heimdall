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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The arbiter is the whole of the synchronous/asynchronous split: the close primitives stay
/// synchronous because it resolves the asynchronous decision outside them and hands them a settled
/// verdict. These tests pin that protocol on its own, before any close path consumes it.
/// </summary>
/// <remarks>
/// Hosts are plain objects on purpose - a pane's content is typed <c>object?</c>, so the contract
/// is genuinely neutral with respect to tools, protocols and WPF, and none of this needs a UI.
/// </remarks>
public sealed class PaneCloseArbiterTests
{
    [Fact]
    public void Poll_SilentRequest_SamplesNoGuardAtAll()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Deny };

        CloseDecision decision = arbiter.Poll(SilentRequest(), [guard]);

        Assert.Equal(CloseVerdict.Allow, decision.Verdict);
        Assert.Equal(0, guard.SampleCount);
        Assert.Equal(0, guard.PollCount);
    }

    [Fact]
    public void Poll_HostThatIsNotAGuard_IsIgnoredRatherThanRefused()
    {
        PaneCloseArbiter arbiter = new();

        CloseDecision decision = arbiter.Poll(InteractiveRequest(), [new object(), null]);

        Assert.Equal(CloseVerdict.Allow, decision.Verdict);
    }

    [Fact]
    public void Poll_GuardNotBusy_AllowsWithoutPollingIt()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = false, PollVerdict = CloseVerdict.Deny };

        CloseDecision decision = arbiter.Poll(InteractiveRequest(), [guard]);

        // An idle guard has nothing to protect, so it is not consulted at all.
        Assert.Equal(CloseVerdict.Allow, decision.Verdict);
        Assert.Equal(0, guard.PollCount);
    }

    [Fact]
    public void Poll_BusyGuardThatDefers_ReportsTheDeferAndItsReason()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer };

        CloseDecision decision = arbiter.Poll(InteractiveRequest(), [guard]);

        Assert.Equal(CloseVerdict.Defer, decision.Verdict);
        Assert.Equal(FakeCloseGuard.ReasonKey, decision.ReasonKey);
    }

    [Fact]
    public void Poll_GuardDenies_StopsBeforeTheGuardsBehindIt()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard denying = new() { IsBusy = true, PollVerdict = CloseVerdict.Deny };
        FakeCloseGuard behind = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer };

        CloseDecision decision = arbiter.Poll(InteractiveRequest(), [denying, behind]);

        Assert.Equal(CloseVerdict.Deny, decision.Verdict);
        Assert.Equal(0, behind.PollCount);
    }

    [Fact]
    public async Task ResolveAsync_ConsentingGuard_LetsTheRetryThrough()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseRequest request = InteractiveRequest();

        Assert.Equal(CloseVerdict.Defer, arbiter.Poll(request, [guard]).Verdict);
        Assert.True(await arbiter.ResolveAsync(request, [guard]));

        // The grant lives in the arbiter, so the retry succeeds without the guard being asked to
        // answer differently the second time - it still reports itself busy and still defers.
        Assert.Equal(CloseVerdict.Allow, arbiter.Poll(request, [guard]).Verdict);
        Assert.Equal(1, guard.ResolveCount);
    }

    [Fact]
    public async Task ResolveAsync_RefusingGuard_KeepsTheCloseBlocked()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = false };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [guard]);

        Assert.False(await arbiter.ResolveAsync(request, [guard]));
        Assert.Equal(CloseVerdict.Defer, arbiter.Poll(request, [guard]).Verdict);
    }

    [Fact]
    public async Task ResolveAsync_GuardThrows_IsTreatedAsARefusal()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new()
        {
            IsBusy = true,
            PollVerdict = CloseVerdict.Defer,
            ResolveThrows = true
        };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [guard]);

        // Fail closed: a guard that breaks must not be read as consent.
        Assert.False(await arbiter.ResolveAsync(request, [guard]));
    }

    [Fact]
    public async Task ResolveAsync_WorkFinishedWhileConfirming_StillCloses()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [guard]);
        Assert.True(await arbiter.ResolveAsync(request, [guard]));

        // The transfer completed, which necessarily moved the epoch. That must read as "nothing
        // left to protect", not as "the consent is stale".
        guard.IsBusy = false;
        guard.Epoch += 1;

        Assert.Equal(CloseVerdict.Allow, arbiter.Poll(request, [guard]).Verdict);
    }

    [Fact]
    public async Task ResolveAsync_NewWorkStartedWhileConfirming_RefusesRatherThanHonourStaleConsent()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [guard]);
        Assert.True(await arbiter.ResolveAsync(request, [guard]));

        // Still busy, but it is no longer the same work the user consented to abandoning.
        guard.Epoch += 1;

        CloseDecision decision = arbiter.Poll(request, [guard]);

        Assert.Equal(CloseVerdict.Deny, decision.Verdict);
        Assert.Equal(CloseGuardLocaleKeys.BlockedStale, decision.ReasonKey);
    }

    [Fact]
    public async Task Poll_WhileAResolutionIsInFlight_DefersWithoutAskingTheGuardAgain()
    {
        PaneCloseArbiter arbiter = new();
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeCloseGuard guard = new()
        {
            IsBusy = true,
            PollVerdict = CloseVerdict.Defer,
            ResolveGate = gate
        };
        CloseRequest first = InteractiveRequest();
        arbiter.Poll(first, [guard]);
        Task<bool> resolving = arbiter.ResolveAsync(first, [guard]);

        // A second click on the close button mints a NEW request, which is why the in-flight
        // bookkeeping is keyed by guard: a request-keyed defence would miss this entirely.
        CloseRequest second = InteractiveRequest();
        CloseDecision decision = arbiter.Poll(second, [guard]);

        Assert.Equal(CloseVerdict.Defer, decision.Verdict);
        Assert.Equal(1, guard.PollCount);

        // The second gesture joins the resolution already running rather than raising its own
        // prompt. It has to be started BEFORE the gate opens: once the first resolution completes
        // the guard leaves the in-flight set, and a later gesture then legitimately asks again.
        Task<bool> joining = arbiter.ResolveAsync(second, [guard]);
        Assert.Equal(1, guard.ResolveCount);

        gate.SetResult(true);
        Assert.True(await resolving);
        Assert.True(await joining);
        Assert.Equal(1, guard.ResolveCount);
    }

    [Fact]
    public async Task Release_DropsTheGrant_SoALaterGestureAsksAgain()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [guard]);
        Assert.True(await arbiter.ResolveAsync(request, [guard]));
        Assert.Equal(CloseVerdict.Allow, arbiter.Poll(request, [guard]).Verdict);

        arbiter.Release(request);

        Assert.Equal(CloseVerdict.Defer, arbiter.Poll(request, [guard]).Verdict);
    }

    [Fact]
    public async Task ResolveAsync_GuardThatAllowed_IsNeverHandedItsAsyncContinuation()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard idle = new() { IsBusy = false };
        FakeCloseGuard busy = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [idle, busy]);
        Assert.True(await arbiter.ResolveAsync(request, [idle, busy]));

        Assert.Equal(0, idle.ResolveCount);
        Assert.Equal(1, busy.ResolveCount);
    }

    [Fact]
    public async Task ResolveAsync_TwoDeferringGuards_AreResolvedOneAfterTheOther()
    {
        PaneCloseArbiter arbiter = new();
        List<string> order = [];
        FakeCloseGuard first = new()
        {
            IsBusy = true,
            PollVerdict = CloseVerdict.Defer,
            Consent = true,
            OnResolve = () => order.Add("first")
        };
        FakeCloseGuard second = new()
        {
            IsBusy = true,
            PollVerdict = CloseVerdict.Defer,
            Consent = true,
            OnResolve = () => order.Add("second")
        };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [first, second]);
        Assert.True(await arbiter.ResolveAsync(request, [first, second]));

        // Sequential, so closing N panes never raises N prompts at once.
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task ResolveAsync_FirstGuardRefuses_LeavesTheSecondUnasked()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard refusing = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = false };
        FakeCloseGuard behind = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseRequest request = InteractiveRequest();

        arbiter.Poll(request, [refusing, behind]);

        Assert.False(await arbiter.ResolveAsync(request, [refusing, behind]));
        Assert.Equal(0, behind.ResolveCount);
    }

    [Fact]
    public async Task ResolveAsync_SilentRequest_ConsentsWithoutTouchingAGuard()
    {
        PaneCloseArbiter arbiter = new();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = false };
        CloseRequest request = SilentRequest();

        arbiter.Poll(request, [guard]);

        Assert.True(await arbiter.ResolveAsync(request, [guard]));
        Assert.Equal(0, guard.ResolveCount);
    }

    [Fact]
    public async Task ResolveAsync_GuardThatReEntersWhileDeciding_IsNotAskedASecondTime()
    {
        PaneCloseArbiter arbiter = new();
        CloseRequest request = InteractiveRequest();
        FakeCloseGuard guard = new() { IsBusy = true, PollVerdict = CloseVerdict.Defer, Consent = true };
        CloseDecision? reentrant = null;

        // This is the shape a confirmation dialog really has: ShowConfirmAsync runs a BLOCKING
        // modal with its own nested message pump and only then hands back an already-completed
        // task, so the whole decision executes inside the synchronous prefix of the guard's
        // continuation - and that nested pump can deliver another close gesture. Registering the
        // resolution only after the guard returned would leave the in-flight set empty for exactly
        // that window, and the user would get a second dialog stacked on the first.
        guard.OnResolve = () => reentrant = arbiter.Poll(InteractiveRequest(), [guard]);

        arbiter.Poll(request, [guard]);
        Assert.True(await arbiter.ResolveAsync(request, [guard]));

        Assert.NotNull(reentrant);
        Assert.Equal(CloseVerdict.Defer, reentrant!.Value.Verdict);
        Assert.Equal(1, guard.PollCount);
        Assert.Equal(1, guard.ResolveCount);
    }

    private static CloseRequest InteractiveRequest() => CloseRequest.Interactive(DisconnectReason.TabClose);

    private static CloseRequest SilentRequest() => CloseRequest.Silent(DisconnectReason.TabClose);

    private sealed class FakeCloseGuard : ICloseGuard
    {
        internal const string ReasonKey = "FakeGuardBusy";

        public bool IsBusy { get; set; }

        public long Epoch { get; set; } = 1;

        public CloseVerdict PollVerdict { get; set; } = CloseVerdict.Allow;

        public bool Consent { get; set; }

        public bool ResolveThrows { get; set; }

        public TaskCompletionSource<bool>? ResolveGate { get; set; }

        public Action? OnResolve { get; set; }

        public int SampleCount { get; private set; }

        public int PollCount { get; private set; }

        public int ResolveCount { get; private set; }

        public CloseGuardState SampleCloseGuardState()
        {
            SampleCount++;
            return new CloseGuardState(IsBusy, Epoch);
        }

        public CloseDecision PollClose(CloseRequest request)
        {
            PollCount++;
            return PollVerdict switch
            {
                CloseVerdict.Defer => CloseDecision.Defer(ReasonKey, Epoch),
                CloseVerdict.Deny => CloseDecision.Deny(ReasonKey, Epoch),
                _ => CloseDecision.Allow(Epoch)
            };
        }

        public async Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
        {
            ResolveCount++;
            OnResolve?.Invoke();

            if (ResolveThrows)
            {
                throw new InvalidOperationException("guard failed");
            }

            if (ResolveGate is not null)
            {
                return await ResolveGate.Task;
            }

            return Consent;
        }
    }
}
