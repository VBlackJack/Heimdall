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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// The exit-path decisions, as shared predicates. The main window already returned
/// early on a shutting-down application; the floating windows, the unhandled
/// exception handler and the session census each kept a rule of their own.
/// </summary>
public sealed class ShutdownDecisionsTests
{
    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    public void FloatingWindowShouldPollGuards_OnlyForALiveGuardedUngrantedWindow(
        bool isShuttingDown,
        bool closeGranted,
        bool reattached,
        bool hostIsGuard,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShutdownDecisions.FloatingWindowShouldPollGuards(isShuttingDown, closeGranted, reattached, hostIsGuard));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void FloatingWindowShouldCloseSessionInteractively_NeverDuringShutdown(
        bool isShuttingDown,
        bool reattached,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShutdownDecisions.FloatingWindowShouldCloseSessionInteractively(isShuttingDown, reattached));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldShowUnhandledExceptionDialog_NeverDuringShutdown(bool isShuttingDown, bool expected)
    {
        Assert.Equal(expected, ShutdownDecisions.ShouldShowUnhandledExceptionDialog(isShuttingDown));
    }

    /// <remarks>
    /// Detaching removes a session from the main collection, so three detached RDP
    /// sessions and nothing in the main window produced a count of zero and no question.
    /// </remarks>
    [Fact]
    public void CountConnectedSessions_CountsDetachedSessionsToo()
    {
        SessionTabViewModel[] attached = [];
        SessionTabViewModel[] detached = [Connected(), Connected(), Connected()];

        Assert.Equal(3, ShutdownDecisions.CountConnectedSessions(attached, detached));
    }

    [Fact]
    public void CountConnectedSessions_CountsOnlyConnectedPanesOnBothSides()
    {
        SessionTabViewModel[] attached = [Connected(), Disconnected()];
        SessionTabViewModel[] detached = [Connected(), Disconnected()];

        Assert.Equal(2, ShutdownDecisions.CountConnectedSessions(attached, detached));
    }

    [Fact]
    public void CountConnectedSessions_NothingOpen_IsZero()
    {
        Assert.Equal(0, ShutdownDecisions.CountConnectedSessions([], []));
    }

    private static SessionTabViewModel Connected() => new() { Status = "Connected" };

    private static SessionTabViewModel Disconnected() => new() { Status = "Disconnected" };
}
