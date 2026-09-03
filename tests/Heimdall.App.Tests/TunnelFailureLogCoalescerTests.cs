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
using Heimdall.Ssh;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// Identical repeats fold into the first report; anything genuinely different
/// is always reported.
/// </summary>
public sealed class TunnelFailureLogCoalescerTests
{
    private const string GatewayA = "v1:sha256:gateway-a";
    private const string GatewayB = "v1:sha256:gateway-b";

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private static TunnelFailureLogCoalescer Create(FakeTimeProvider clock) =>
        new TunnelFailureLogCoalescer(clock, Window);

    [Fact]
    public void TenIdenticalFailuresOnOneGateway_ReportOnce()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        List<TunnelFailureReportDecision> decisions = new();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            decisions.Add(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected));
            clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(1, decisions.Count(decision => decision.ShouldReport));
        Assert.True(decisions[0].ShouldReport);
        Assert.Equal(9, decisions[^1].SuppressedRepeats);
    }

    [Fact]
    public void ASecondGatewayFailingTheSameWay_IsStillReported()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);
        Assert.True(coalescer.Evaluate(GatewayB, SshFailureCode.AuthRejected).ShouldReport);
    }

    [Fact]
    public void TheSameGatewayFailingADifferentWay_IsStillReported()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);
        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.NetworkRefused).ShouldReport);
        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.HostKeyMismatch).ShouldReport);
    }

    [Fact]
    public void AfterTheWindowCloses_TheFailureIsReportedAgainWithItsSuppressedCount()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);
        Assert.False(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);
        Assert.False(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);

        clock.Advance(Window + TimeSpan.FromSeconds(1));

        TunnelFailureReportDecision reopened = coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected);
        Assert.True(reopened.ShouldReport);
        Assert.Equal(2, reopened.SuppressedRepeats);
    }

    [Fact]
    public void SuppressionIsBounded_ASteadyDripDoesNotExtendTheSilenceIndefinitely()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);

        // Every repeat lands inside the window measured from the previous
        // repeat. If the window were reset by each repeat, the failure would
        // never be reported again over these four minutes.
        int laterReports = 0;
        for (int step = 0; step < 30; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(8));
            if (coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport)
            {
                laterReports++;
            }
        }

        // One report every 32 s across 240 s: at 32, 64, 96, 128, 160, 192, 224.
        Assert.Equal(7, laterReports);
    }

    [Fact]
    public void ANullGatewayChainKey_IsTrackedLikeAnyOther()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer.Evaluate(null, SshFailureCode.AuthRejected).ShouldReport);
        Assert.False(coalescer.Evaluate(null, SshFailureCode.AuthRejected).ShouldReport);
        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected).ShouldReport);
    }

    // Two profiles behind one gateway can fail under one coarse code for
    // genuinely different reasons - a local bind collision on one, a forward to
    // a different target refused on the other. Reporting the second as a repeat
    // of the first names only the first one's problem.
    [Fact]
    public void TheSameCodeCarryingADifferentMessage_IsStillReported()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer
            .Evaluate(GatewayA, SshFailureCode.Unknown, "Local bind 127.0.0.2:13389 was refused by the OS.")
            .ShouldReport);
        Assert.True(coalescer
            .Evaluate(GatewayA, SshFailureCode.Unknown, "Remote forwarding to 10.0.0.9:3389 was refused.")
            .ShouldReport);
    }

    [Fact]
    public void TheSameCodeCarryingTheSameMessage_IsStillFoldedIntoOneReport()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);
        const string Message = "Permission denied (password). No agent key was loaded.";

        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected, Message).ShouldReport);

        for (int repeat = 0; repeat < 9; repeat++)
        {
            Assert.False(
                coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected, Message).ShouldReport,
                $"Repeat {repeat + 1} of a byte-identical failure must still fold.");
        }
    }

    // The message returning to a previously seen one must fold again rather than
    // reopen: a diagnosis that flips back and forth would otherwise report every
    // attempt in a burst.
    [Fact]
    public void AMessageReturningToAnEarlierOne_FoldsBackIntoItsOwnWindow()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected, "first").ShouldReport);
        Assert.True(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected, "second").ShouldReport);
        Assert.False(coalescer.Evaluate(GatewayA, SshFailureCode.AuthRejected, "first").ShouldReport);
    }

    [Fact]
    public void ManyDistinctGateways_StayReportedRatherThanEvictedIntoSilence()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        TunnelFailureLogCoalescer coalescer = Create(clock);

        for (int gateway = 0; gateway < 200; gateway++)
        {
            Assert.True(
                coalescer.Evaluate($"v1:sha256:gateway-{gateway}", SshFailureCode.AuthRejected).ShouldReport,
                $"Gateway {gateway} failing for the first time must be reported.");
        }
    }
}
