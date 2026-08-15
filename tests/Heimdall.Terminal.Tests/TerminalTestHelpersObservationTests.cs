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

namespace Heimdall.Terminal.Tests;

/// <summary>
/// Proves the wrappers the tests actually call reach the observation.
/// </summary>
/// <remarks>
/// The guard forces every backstop-bounded wait through these wrappers, and separate oracles prove
/// the observation publishes what it should. Neither says the wrappers are wired to it: a wrapper
/// that quietly awaited the task itself would satisfy the guard, keep the suite green, and publish
/// nothing at all. These close that seam.
/// </remarks>
public sealed class TerminalTestHelpersObservationTests
{
    private static readonly TimeSpan OverBound =
        TerminalWaitObservation.LegacyBound + TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AwaitProcessEventAsync_PublishesThroughTheObservation()
    {
        List<string> published = [];

        int result = await TerminalTestHelpers.AwaitProcessEventAsync(
            Task.FromResult(9),
            "ProcessExited",
            "PublishingCaller",
            published.Add,
            () => OverBound);

        Assert.Equal(9, result);
        string line = Assert.Single(published);
        Assert.Contains("caller=PublishingCaller", line, StringComparison.Ordinal);
        Assert.Contains("awaited=ProcessExited", line, StringComparison.Ordinal);
        Assert.Contains("outcome=completed", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AwaitProcessEventAsync_WithADiagnosticContext_PublishesThroughTheObservation()
    {
        List<string> published = [];
        TerminalTimeoutContext context = CreateContext("SessionStopped");

        int result = await TerminalTestHelpers.AwaitProcessEventAsync(
            Task.FromResult(4),
            context,
            "PublishingCaller",
            published.Add,
            () => OverBound);

        // The overload that carries the rich failure snapshot must publish too: it is the shape the
        // three tests named in the flake family use.
        Assert.Equal(4, result);
        Assert.Contains("awaited=SessionStopped", Assert.Single(published), StringComparison.Ordinal);
    }

    [Fact]
    public void SpinUntilProcessEvent_PublishesThroughTheObservation()
    {
        List<string> published = [];

        Assert.True(TerminalTestHelpers.SpinUntilProcessEvent(
            () => true,
            "BootstrapSignalFile",
            "PublishingCaller",
            published.Add,
            () => OverBound));

        Assert.Contains("awaited=BootstrapSignalFile", Assert.Single(published), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollUntilProcessEventAsync_PublishesThroughTheObservation()
    {
        List<string> published = [];

        Assert.True(await TerminalTestHelpers.PollUntilProcessEventAsync(
            () => true,
            "SessionStopped",
            "PublishingCaller",
            published.Add,
            () => OverBound));

        Assert.Contains("outcome=completed", Assert.Single(published), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFastWaitThroughTheRealClock_PublishesNothing()
    {
        List<string> published = [];

        // No elapsed override, so this runs on the real stopwatch the wrappers use in anger. It is
        // the assurance that instrumenting every wait did not turn the CI log into noise.
        int result = await TerminalTestHelpers.AwaitProcessEventAsync(
            Task.FromResult(1),
            "ProcessExited",
            "PublishingCaller",
            published.Add);

        Assert.Equal(1, result);
        Assert.Empty(published);
    }

    private static TerminalTimeoutContext CreateContext(string awaitedEvent)
    {
        return new TerminalTimeoutContext(
            awaitedEvent,
            () => null,
            () => false,
            () => 0,
            () => 0L,
            () => 0,
            () => null);
    }
}
