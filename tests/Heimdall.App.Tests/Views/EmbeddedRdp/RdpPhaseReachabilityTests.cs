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
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that every connection phase the view can describe is a phase the view can enter.
/// </summary>
/// <remarks>
/// <para><c>Preparing</c> was declared, given a locale key, a lit stepper segment, a cancel-button
/// visibility rule and a watchdog rule - and never entered. The view went None to Connecting, so
/// the four-segment stepper's first visible value was "2 of 4", and the certificate probe ran in
/// phase None: no lit segment, no Cancel button, no watchdog, while the status line already said
/// the session was connecting. Four pure-function tests covered the Preparing branches and could
/// not fail, because nothing produced that input.</para>
/// <para>A phase that no longer has a producer must be removed rather than left declared.</para>
/// </remarks>
public sealed class RdpPhaseReachabilityTests
{
    [Theory]
    [MemberData(nameof(AllPhases))]
    public void EveryDeclaredPhaseIsEnteredSomewhere(string phase)
    {
        string source = ViewSource.Code();

        Assert.True(
            Regex.IsMatch(source, @"TransitionPhase\(RdpConnectionPhase\." + phase + @"\)"),
            $"No code path enters RdpConnectionPhase.{phase}, so every policy branch and every "
                + "test written for it describes a state the product never reaches.");
    }

    [Fact]
    public void TheCertificateCheckRunsInThePreparingPhase()
    {
        string body = ViewSource.HandlerBody("private async Task StartVerifiedConnectAsync()");

        Assert.Contains(
            "TransitionPhase(RdpConnectionPhase.Preparing)",
            body,
            StringComparison.Ordinal);
    }

    // Preparing has to keep meaning "a connection is being prepared", or moving the certificate
    // check into it would light the stepper and arm the watchdog for something else entirely.
    [Fact]
    public void PreparingStillReadsAsAConnectionInProgress()
    {
        Assert.Equal(1, RdpConnectionPhasePolicy.GetLitSegmentCount(RdpConnectionPhase.Preparing));
        Assert.True(RdpConnectWatchdogPolicy.ShouldArm(RdpConnectionPhase.Preparing));

        (bool cancelConnectVisible, bool _) =
            RdpConnectionPhasePolicy.ResolveVisibility(RdpConnectionPhase.Preparing);
        Assert.True(
            cancelConnectVisible,
            "The certificate probe can block a connect for its full timeout, so the user needs the "
                + "Cancel button during it.");
    }

    // Cancelling during the certificate check has to stop the check, not just the connect that
    // would follow it.
    [Fact]
    public void CancellingAConnectCancelsTheCertificateCheckToo()
    {
        string handler = ViewSource.HandlerBody("private void OnCancelConnectClick");

        Assert.Contains("_certificateVerificationCts", handler, StringComparison.Ordinal);
    }

    public static TheoryData<string> AllPhases()
    {
        var data = new TheoryData<string>();
        foreach (RdpConnectionPhase phase in Enum.GetValues<RdpConnectionPhase>())
        {
            if (phase != RdpConnectionPhase.None)
            {
                data.Add(phase.ToString());
            }
        }

        return data;
    }
}
