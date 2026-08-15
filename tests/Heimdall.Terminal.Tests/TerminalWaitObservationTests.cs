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

using System.Text;

namespace Heimdall.Terminal.Tests;

/// <summary>
/// Oracles for the wait observation. None of them waits: the elapsed time is injected, so the
/// over-bound behaviour is proven in milliseconds instead of being demonstrated by a test that
/// genuinely blocks for longer than the bound it is testing.
/// </summary>
public sealed class TerminalWaitObservationTests
{
    private static readonly TimeSpan OverBound =
        TerminalWaitObservation.LegacyBound + TimeSpan.FromSeconds(2.5);

    private static readonly TimeSpan UnderBound =
        TerminalWaitObservation.LegacyBound - TimeSpan.FromMilliseconds(1);

    [Fact]
    public void AWaitThatStaysUnderTheLegacyBound_PublishesNothing()
    {
        List<string> published = [];

        TerminalWaitObservation.Publish(
            "SomeTest",
            "ProcessExited",
            UnderBound,
            TerminalWaitOutcome.Completed,
            published.Add);

        // The point is the signal-to-noise ratio: if every wait published, the CI log would carry
        // thousands of lines and the handful that matter would be unfindable.
        Assert.Empty(published);
    }

    [Fact]
    public void AWaitThatTakesExactlyTheLegacyBound_PublishesNothing()
    {
        // Strictly greater, deliberately. A wait that took exactly ten seconds would not have
        // failed under the old bound either, so reporting it would report a non-event.
        Assert.Null(TerminalWaitObservation.Describe(
            "SomeTest",
            "ProcessExited",
            TerminalWaitObservation.LegacyBound,
            TerminalWaitOutcome.Completed));

        Assert.NotNull(TerminalWaitObservation.Describe(
            "SomeTest",
            "ProcessExited",
            TerminalWaitObservation.LegacyBound + TimeSpan.FromTicks(1),
            TerminalWaitOutcome.Completed));
    }

    [Fact]
    public void ASucceedingWaitThatOutlivesTheLegacyBound_IsPublished()
    {
        List<string> published = [];

        TerminalWaitObservation.Publish(
            "Write_InputReachesProcessStdin",
            "ProcessExited",
            OverBound,
            TerminalWaitOutcome.Completed,
            published.Add);

        // This is the whole lot in one assertion. Under the widened backstop such a wait passes,
        // so before this instrumentation it left no trace whatsoever, and a green run could not be
        // told apart from a run where the stall had actually gone away.
        string line = Assert.Single(published);
        Assert.StartsWith(TerminalWaitObservation.Marker, line, StringComparison.Ordinal);
        Assert.Contains("caller=Write_InputReachesProcessStdin", line, StringComparison.Ordinal);
        Assert.Contains("awaited=ProcessExited", line, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=12500.000", line, StringComparison.Ordinal);
        Assert.Contains("legacyBoundMs=10000.000", line, StringComparison.Ordinal);
        Assert.Contains("outcome=completed", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AWaitThatNeverDelivered_IsPublishedAsUnfinished()
    {
        List<string> published = [];

        TerminalWaitObservation.Publish(
            "SomeTest",
            "SessionStopped",
            OverBound,
            TerminalWaitOutcome.Unfinished,
            published.Add);

        string line = Assert.Single(published);
        Assert.Contains("outcome=unfinished", line, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome=completed", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveAsync_ReturnsTheResult_AndPublishesTheSlowSuccess()
    {
        List<string> published = [];

        int result = await TerminalWaitObservation.ObserveAsync(
            "SomeTest",
            "ProcessExited",
            () => Task.FromResult(17),
            published.Add,
            () => OverBound);

        Assert.Equal(17, result);
        string line = Assert.Single(published);
        Assert.Contains("outcome=completed", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveAsync_AFailingWait_StillPublishes_AndRethrows()
    {
        List<string> published = [];

        await Assert.ThrowsAsync<TimeoutException>(() => TerminalWaitObservation.ObserveAsync<int>(
            "SomeTest",
            "ProcessExited",
            () => Task.FromException<int>(new TimeoutException("bound expired")),
            published.Add,
            () => OverBound));

        // Reported from a finally, and the outcome starts at Unfinished: the wait that failed is
        // the one that must not go unreported, and it is the one an early return would lose.
        string line = Assert.Single(published);
        Assert.Contains("outcome=unfinished", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveAsync_AFastWait_PublishesNothing()
    {
        List<string> published = [];

        int result = await TerminalWaitObservation.ObserveAsync(
            "SomeTest",
            "ProcessExited",
            () => Task.FromResult(3),
            published.Add,
            () => UnderBound);

        Assert.Equal(3, result);
        Assert.Empty(published);
    }

    [Fact]
    public void ObserveSpin_ReportsTheReturnValueAsTheOutcome()
    {
        List<string> satisfied = [];
        List<string> expired = [];

        Assert.True(TerminalWaitObservation.ObserveSpin(
            "SomeTest", "BootstrapSignalFile", () => true, satisfied.Add, () => OverBound));
        Assert.False(TerminalWaitObservation.ObserveSpin(
            "SomeTest", "BootstrapSignalFile", () => false, expired.Add, () => OverBound));

        // A spin reports failure by returning false, not by throwing, so an outcome derived from
        // "did it throw" would label every expired spin a success.
        Assert.Contains("outcome=completed", Assert.Single(satisfied), StringComparison.Ordinal);
        Assert.Contains("outcome=unfinished", Assert.Single(expired), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservePollAsync_ReportsTheReturnValueAsTheOutcome()
    {
        List<string> expired = [];

        Assert.False(await TerminalWaitObservation.ObservePollAsync(
            "SomeTest", "SessionStopped", () => Task.FromResult(false), expired.Add, () => OverBound));

        Assert.Contains("outcome=unfinished", Assert.Single(expired), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoSink_ThePublishedLineReachesStandardOutput()
    {
        // Standard output is the delivery channel: it was measured on this repository that console
        // output from a PASSING test appears in the `dotnet test --verbosity normal` log, which is
        // what makes these lines accumulate in CI without a collector. Redirecting Console.Out is
        // global for the duration; nothing else in this assembly writes to it, and the assertion is
        // a containment check, so a stray concurrent line would not turn this green or red.
        TextWriter original = Console.Out;
        StringBuilder captured = new();

        try
        {
            using StringWriter writer = new(captured);
            Console.SetOut(writer);

            TerminalWaitObservation.Publish(
                "SomeTest",
                "ProcessExited",
                OverBound,
                TerminalWaitOutcome.Completed);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains(TerminalWaitObservation.Marker, captured.ToString(), StringComparison.Ordinal);
    }
}
