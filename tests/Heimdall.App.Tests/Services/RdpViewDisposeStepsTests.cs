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

namespace Heimdall.App.Tests.Services;

/// <summary>
/// Whichever release of the embedded RDP view's dispose throws, every other release still
/// runs - above all the sleep prevention release - and so does the COM teardown.
/// </summary>
public sealed class RdpViewDisposeStepsTests
{
    private const string Teardown = "Teardown";

    private static readonly string[] ExpectedOrder =
    [
        nameof(IRdpViewDisposeTarget.EmitTeardownDisconnectEvent),
        nameof(IRdpViewDisposeTarget.MarkDisposed),
        nameof(IRdpViewDisposeTarget.SettleCertificatePrompt),
        nameof(IRdpViewDisposeTarget.UnregisterEscapeHook),
        nameof(IRdpViewDisposeTarget.UnregisterDpiChangedHandler),
        nameof(IRdpViewDisposeTarget.DetachConnectionStateMachine),
        nameof(IRdpViewDisposeTarget.DetachLayoutHandlers),
        nameof(IRdpViewDisposeTarget.StopAutofillFilledTimer),
        nameof(IRdpViewDisposeTarget.StopTransientToastTimer),
        nameof(IRdpViewDisposeTarget.HideLetterboxHint),
        nameof(IRdpViewDisposeTarget.StopStabilization),
        nameof(IRdpViewDisposeTarget.StopReconnectElapsedTracking),
        nameof(IRdpViewDisposeTarget.StopAntiIdleTimer),
        nameof(IRdpViewDisposeTarget.StopConnectWatchdog),
        nameof(IRdpViewDisposeTarget.ReleaseSleepPrevention),
        nameof(IRdpViewDisposeTarget.CancelAutofill),
        nameof(IRdpViewDisposeTarget.ResetSessionIndicators),
    ];

    public static TheoryData<string> EveryStep()
    {
        TheoryData<string> data = [];
        foreach (string step in ExpectedOrder)
        {
            data.Add(step);
        }

        return data;
    }

    [Fact]
    public void Releases_NothingThrows_RunsEveryStepInOrderThenTheTeardown()
    {
        RecordingTarget target = new(throwingStep: null);

        RunDispose(target, out List<Exception> reported);

        Assert.Equal([.. ExpectedOrder, Teardown], target.Calls);
        Assert.Empty(reported);
    }

    [Theory]
    [MemberData(nameof(EveryStep))]
    public void Releases_OneStepThrows_EveryOtherStepAndTheTeardownStillRun(string throwingStep)
    {
        RecordingTarget target = new(throwingStep);

        RunDispose(target, out List<Exception> reported);

        Assert.Equal([.. ExpectedOrder.Where(step => step != throwingStep), Teardown], target.Calls);
        Exception failure = Assert.Single(reported);
        Assert.Equal(throwingStep, failure.Message);
    }

    [Fact]
    public void Releases_TheFirstStepThrows_StillReleasesSleepPreventionAndStopsTheWatchdog()
    {
        RecordingTarget target = new(nameof(IRdpViewDisposeTarget.EmitTeardownDisconnectEvent));

        RunDispose(target, out _);

        Assert.Contains(nameof(IRdpViewDisposeTarget.ReleaseSleepPrevention), target.Calls);
        Assert.Contains(nameof(IRdpViewDisposeTarget.StopConnectWatchdog), target.Calls);
        Assert.Equal(Teardown, target.Calls[^1]);
    }

    [Fact]
    public void Run_TheReportItselfThrows_TheRemainingReleasesAndTheTeardownStillRun()
    {
        RecordingTarget target = new(nameof(IRdpViewDisposeTarget.SettleCertificatePrompt));

        DisposeSequence.Run(
            RdpViewDisposeSteps.Releases(target, DisconnectReason.TabClose),
            () => target.Calls.Add(Teardown),
            _ => throw new InvalidOperationException("logger gone"));

        Assert.Contains(nameof(IRdpViewDisposeTarget.ReleaseSleepPrevention), target.Calls);
        Assert.Equal(Teardown, target.Calls[^1]);
    }

    [Fact]
    public void Releases_CarryTheTeardownReasonToTheStepsThatTakeIt()
    {
        RecordingTarget target = new(throwingStep: null);

        DisposeSequence.Run(
            RdpViewDisposeSteps.Releases(target, DisconnectReason.FailedSession),
            () => { },
            _ => { });

        Assert.Equal([DisconnectReason.FailedSession, DisconnectReason.FailedSession], target.Reasons);
    }

    private static void RunDispose(RecordingTarget target, out List<Exception> reported)
    {
        List<Exception> failures = [];
        DisposeSequence.Run(
            RdpViewDisposeSteps.Releases(target, DisconnectReason.TabClose),
            () => target.Calls.Add(Teardown),
            failures.Add);
        reported = failures;
    }

    private sealed class RecordingTarget(string? throwingStep) : IRdpViewDisposeTarget
    {
        public List<string> Calls { get; } = [];

        public List<DisconnectReason> Reasons { get; } = [];

        public void EmitTeardownDisconnectEvent(DisconnectReason reason)
        {
            Reasons.Add(reason);
            Step(nameof(EmitTeardownDisconnectEvent));
        }

        public void MarkDisposed(DisconnectReason reason)
        {
            Reasons.Add(reason);
            Step(nameof(MarkDisposed));
        }

        public void SettleCertificatePrompt() => Step(nameof(SettleCertificatePrompt));

        public void UnregisterEscapeHook() => Step(nameof(UnregisterEscapeHook));

        public void UnregisterDpiChangedHandler() => Step(nameof(UnregisterDpiChangedHandler));

        public void DetachConnectionStateMachine() => Step(nameof(DetachConnectionStateMachine));

        public void DetachLayoutHandlers() => Step(nameof(DetachLayoutHandlers));

        public void StopAutofillFilledTimer() => Step(nameof(StopAutofillFilledTimer));

        public void StopTransientToastTimer() => Step(nameof(StopTransientToastTimer));

        public void HideLetterboxHint() => Step(nameof(HideLetterboxHint));

        public void StopStabilization() => Step(nameof(StopStabilization));

        public void StopReconnectElapsedTracking() => Step(nameof(StopReconnectElapsedTracking));

        public void StopAntiIdleTimer() => Step(nameof(StopAntiIdleTimer));

        public void StopConnectWatchdog() => Step(nameof(StopConnectWatchdog));

        public void ReleaseSleepPrevention() => Step(nameof(ReleaseSleepPrevention));

        public void CancelAutofill() => Step(nameof(CancelAutofill));

        public void ResetSessionIndicators() => Step(nameof(ResetSessionIndicators));

        private void Step(string name)
        {
            if (name == throwingStep)
            {
                throw new InvalidOperationException(name);
            }

            Calls.Add(name);
        }
    }
}
