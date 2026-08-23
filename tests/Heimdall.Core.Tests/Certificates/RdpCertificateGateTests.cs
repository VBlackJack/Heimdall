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

using Heimdall.Core.Certificates;

namespace Heimdall.Core.Tests.Certificates;

/// <summary>
/// When the certificate check runs, and what its answer does to the connection.
/// </summary>
public sealed class RdpCertificateGateTests
{
    [Fact]
    public void VerificationRequired_NothingElseChecks_SoHeimdallDoes()
    {
        // Level 0 imposes no server-authentication requirement. Nobody checks anything
        // today on this path, which is the entire reason the feature exists.
        Assert.True(RdpCertificateGate.VerificationRequired(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void VerificationRequired_WindowsAlreadyChecks_SoHeimdallDoesNotAskAgain(int level)
    {
        // At level 1 Windows requires server authentication and at level 2 it attempts it
        // and warns. A second question about the same fact is one prompt too many, and a
        // prompt users learn to click through is worse than no prompt.
        Assert.False(RdpCertificateGate.VerificationRequired(level));
    }

    [Fact]
    public void Decide_UserRefused_StopsTheConnection()
        => Assert.Equal(
            RdpConnectionDecision.Abandon,
            RdpCertificateGate.Decide(RdpVerificationOutcome.RefusedByUser));

    [Fact]
    public void Decide_UserTrusted_LetsItThrough()
        => Assert.Equal(
            RdpConnectionDecision.Proceed,
            RdpCertificateGate.Decide(RdpVerificationOutcome.TrustedByUser));

    [Theory]
    [InlineData(RdpVerificationOutcome.CouldNotVerify)]
    [InlineData(RdpVerificationOutcome.NoCertificateOffered)]
    public void Decide_NothingWasVerified_ConnectsExactlyAsBefore(RdpVerificationOutcome outcome)
    {
        // The rule the verifier holds, seen from the other side. Heimdall may relax the
        // Windows check only where it performed an equivalent one - and by symmetry it may
        // not TIGHTEN the connection on the strength of a check that did not happen.
        // Blocking here would turn a verification step into a new way to fail, on a path
        // that worked before this feature existed.
        Assert.Equal(RdpConnectionDecision.Proceed, RdpCertificateGate.Decide(outcome));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DecideConnection_WindowsAlreadyChecks_DoesNotProbeAtAll(int level)
    {
        int probes = 0;

        RdpConnectionDecision decision = await RdpCertificateGate.DecideConnectionAsync(
            level,
            _ =>
            {
                probes++;
                return Task.FromResult(RdpVerificationOutcome.RefusedByUser);
            },
            onVerificationFailed: null,
            CancellationToken.None);

        // Not merely 'proceeds anyway': the endpoint is never contacted. A probe on a
        // path Windows already checks would add a network round trip to every session
        // for a question nobody asked.
        Assert.Equal(0, probes);
        Assert.Equal(RdpConnectionDecision.Proceed, decision);
    }

    [Fact]
    public async Task DecideConnection_UserRefused_AbandonsTheConnection()
        => Assert.Equal(
            RdpConnectionDecision.Abandon,
            await RdpCertificateGate.DecideConnectionAsync(
                0,
                _ => Task.FromResult(RdpVerificationOutcome.RefusedByUser),
                onVerificationFailed: null,
                CancellationToken.None));

    [Fact]
    public async Task DecideConnection_UserTrusted_LetsItThrough()
        => Assert.Equal(
            RdpConnectionDecision.Proceed,
            await RdpCertificateGate.DecideConnectionAsync(
                0,
                _ => Task.FromResult(RdpVerificationOutcome.TrustedByUser),
                onVerificationFailed: null,
                CancellationToken.None));

    [Fact]
    public async Task DecideConnection_CheckThrew_ConnectsAndReportsIt()
    {
        Exception? reported = null;

        RdpConnectionDecision decision = await RdpCertificateGate.DecideConnectionAsync(
            0,
            _ => throw new InvalidOperationException("probe exploded"),
            ex => reported = ex,
            CancellationToken.None);

        // A check that threw verified nothing, so it may neither relax nor tighten the
        // connection. Refusing here would make this feature a new way to fail on a path
        // that worked without it - and the operator would never learn why.
        Assert.Equal(RdpConnectionDecision.Proceed, decision);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public async Task DecideConnection_CheckThrewAndNobodyIsListening_StillConnects()
        => Assert.Equal(
            RdpConnectionDecision.Proceed,
            await RdpCertificateGate.DecideConnectionAsync(
                0,
                _ => throw new InvalidOperationException("probe exploded"),
                onVerificationFailed: null,
                CancellationToken.None));

    [Fact]
    public async Task DecideConnection_TokenIsHandedToTheCheck()
    {
        using CancellationTokenSource cts = new();
        CancellationToken seen = default;

        await RdpCertificateGate.DecideConnectionAsync(
            0,
            ct =>
            {
                seen = ct;
                return Task.FromResult(RdpVerificationOutcome.TrustedByUser);
            },
            onVerificationFailed: null,
            cts.Token);

        // Without this, closing the tab leaves the dialog open with nothing to cancel it.
        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public void Decide_EveryOutcome_IsAccountedFor()
    {
        // Reflected over the enum so an outcome added later cannot silently fall into the
        // proceed branch by default. Exactly one of them stops the connection.
        RdpVerificationOutcome[] all = Enum.GetValues<RdpVerificationOutcome>();

        Assert.Equal(4, all.Length);
        Assert.Single(all, outcome =>
            RdpCertificateGate.Decide(outcome) == RdpConnectionDecision.Abandon);
    }
}
