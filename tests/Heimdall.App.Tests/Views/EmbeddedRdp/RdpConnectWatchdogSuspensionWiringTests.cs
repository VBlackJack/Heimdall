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

using System.Text.RegularExpressions;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Proves the suspension arbiter is reached from the view, and in the right order.
/// </summary>
/// <remarks>
/// <para><see cref="RdpConnectWatchdogSuspensionTests"/> pins what the arbiter decides. Nothing
/// there fails if the view stops calling it: that is the shape this repository has already been
/// bitten by, a guard delivered complete and left attached to no host, with green suites either
/// side of a junction neither of them crosses.</para>
/// <para>Only statement ORDER is read from the source here. Every decision is asserted
/// behaviourally in the sibling file, against the arbiter itself.</para>
/// </remarks>
public sealed class RdpConnectWatchdogSuspensionWiringTests
{
    private const string TransitionPhase = "private void TransitionPhase(RdpConnectionPhase newPhase)";
    private const string StartVerifiedConnect = "private async Task StartVerifiedConnectAsync()";
    private const string VerifyCertificate =
        "private async Task<RdpConnectionDecision> VerifyServerCertificateAsync()";
    private const string SuspendMember = "private void SuspendConnectWatchdog()";
    private const string CancelMember = "private void CancelConnectWatchdog()";

    // The extraction itself, so nothing below can pass by finding an empty body.
    [Fact]
    public void EveryMemberThisFileMeasuresStillExists()
    {
        string source = ViewSource.Code();

        foreach (string member in new[]
        {
            TransitionPhase, StartVerifiedConnect, VerifyCertificate, SuspendMember, CancelMember,
        })
        {
            Assert.Contains(member, source, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, ViewSource.HandlerBody(member).Trim());
        }
    }

    // The phase transition no longer drives the timer itself: if it did, Preparing would arm a
    // connect budget over a certificate question however the arbiter had decided.
    [Fact]
    public void TransitionPhaseDelegatesTheWatchdogDecisionToTheArbiter()
    {
        string body = ViewSource.HandlerBody(TransitionPhase);

        Assert.Contains("_connectWatchdogArbiter.PhaseChanged(newPhase)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StartConnectWatchdog()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StopConnectWatchdog()", body, StringComparison.Ordinal);
    }

    // The suspension is taken where a check is genuinely owed - after every early return that
    // means "this profile owes none" - and before the call that can ask a human.
    [Fact]
    public void TheCheckIsSuspendedOnlyOnTheBranchThatActuallyRunsAProbe()
    {
        string body = ViewSource.HandlerBody(VerifyCertificate);

        int suspend = body.IndexOf(
            "_connectWatchdogArbiter.CertificateCheckStarted()", StringComparison.Ordinal);
        Assert.True(suspend >= 0, "The certificate check no longer suspends the connect watchdog.");

        int decide = body.IndexOf(
            "RdpCertificateGate.DecideConnectionAsync", StringComparison.Ordinal);
        Assert.True(decide >= 0, "The verification no longer runs through the gate.");
        Assert.True(
            suspend < decide,
            "The watchdog is suspended after the check has already been started, so the question "
                + "is still charged to the connect budget.");

        int required = body.IndexOf(
            "RdpCertificateGate.VerificationRequired", StringComparison.Ordinal);
        Assert.True(required >= 0, "The profile no longer decides whether a check is owed.");
        Assert.True(
            required < suspend,
            "A profile that owes no certificate check would suspend its watchdog anyway, which "
                + "is the regression that changes the behaviour of every other profile.");
    }

    // Resumed in a finally, before the two abandonment checks and before BeginConnect: a
    // refusal, a cancellation and a teardown must all leave the watchdog in a chosen state.
    [Fact]
    public void TheWatchdogIsResumedInAFinallyBeforeTheAbandonmentChecksAndBeforeBeginConnect()
    {
        string body = ViewSource.HandlerBody(StartVerifiedConnect);

        Match resume = Regex.Match(
            body,
            @"(?ms)^\s*finally\s*$.*?_connectWatchdogArbiter\.CertificateCheckCompleted\(");
        Assert.True(
            resume.Success,
            "The connect watchdog is not resumed from a finally, so a throw from the certificate "
                + "check leaves the session in Preparing with no watchdog at all.");

        int completed = body.IndexOf(
            "_connectWatchdogArbiter.CertificateCheckCompleted(", StringComparison.Ordinal);
        int abandoned = body.IndexOf("_connectAbandonedByUser", StringComparison.Ordinal);
        int beginConnect = body.IndexOf("BeginConnect", StringComparison.Ordinal);

        Assert.True(abandoned >= 0, "Cancelling during the check is no longer detected.");
        Assert.True(beginConnect >= 0, "The connect is no longer started from here.");
        Assert.True(
            completed < abandoned && abandoned < beginConnect,
            "The watchdog must be resumed before the abandonment checks, and the connect must "
                + "start after both.");
    }

    // The two ways of stopping the timer are not interchangeable. Suspending must keep the
    // credential-wait promotion: it is a pause on the same attempt, not its end.
    [Fact]
    public void SuspendingKeepsTheCredentialWaitPromotionAndCancellingDropsIt()
    {
        Assert.DoesNotContain(
            "_watchdogCredentialWaitActive",
            ViewSource.HandlerBody(SuspendMember),
            StringComparison.Ordinal);

        Assert.Contains(
            "_watchdogCredentialWaitActive = false",
            ViewSource.HandlerBody(CancelMember),
            StringComparison.Ordinal);
    }
}
