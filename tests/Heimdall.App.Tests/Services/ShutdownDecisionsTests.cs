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
using Heimdall.Core.Models;

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
    [InlineData(false, false, true, nameof(UnhandledExceptionRoute.ShowInline))]
    [InlineData(false, false, false, nameof(UnhandledExceptionRoute.ShowOnDispatcher))]
    [InlineData(true, false, false, nameof(UnhandledExceptionRoute.LogOnly))]
    [InlineData(true, false, true, nameof(UnhandledExceptionRoute.LogOnly))]
    [InlineData(false, true, false, nameof(UnhandledExceptionRoute.LogOnly))]
    public void RouteAppDomainUnhandledException_NeverShowsOrWaitsDuringShutdown(
        bool isShuttingDown,
        bool dispatcherShutdownStarted,
        bool onDispatcherThread,
        string expected)
    {
        // A worker that crashed during teardown showed a modal box from the UI thread and
        // waited on it, so the exit - and the update relauncher - waited for a click.
        Assert.Equal(
            Enum.Parse<UnhandledExceptionRoute>(expected),
            ShutdownDecisions.RouteAppDomainUnhandledException(isShuttingDown, dispatcherShutdownStarted, onDispatcherThread));
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

    /// <remarks>
    /// An embedded WinRM session carries this status for its whole life and is ended by quitting,
    /// so quitting with only WinRM tabs open must ask, attached or detached.
    /// </remarks>
    [Fact]
    public void CountConnectedSessions_CountsEmbeddedRemoteSessionsAttachedAndDetached()
    {
        SessionTabViewModel[] attached = [WithStatus(SessionStatusTokens.RemoteSessionHandedOff)];
        SessionTabViewModel[] detached = [WithStatus(SessionStatusTokens.RemoteSessionHandedOff)];

        Assert.Equal(1, ShutdownDecisions.CountConnectedSessions(attached, []));
        Assert.Equal(1, ShutdownDecisions.CountConnectedSessions([], detached));
        Assert.Equal(2, ShutdownDecisions.CountConnectedSessions(attached, detached));
    }

    /// <remarks>
    /// An external client (mstsc, PuTTY, a browser) is a separate process that survives the exit,
    /// and for some of them Heimdall has no signal that they ended: counting them would ask a
    /// false question at every quit. This differs on purpose from ConnectionStateSets.Connected.
    /// </remarks>
    [Fact]
    public void CountConnectedSessions_DoesNotCountExternalClientsAttachedOrDetached()
    {
        SessionTabViewModel[] attached = [WithStatus(SessionStatusTokens.LaunchedExternalClient)];
        SessionTabViewModel[] detached = [WithStatus(SessionStatusTokens.LaunchedExternalClient)];

        Assert.Equal(0, ShutdownDecisions.CountConnectedSessions(attached, detached));
    }

    [Theory]
    [InlineData(SessionStatusTokens.Connecting)]
    [InlineData(SessionStatusTokens.Reconnecting)]
    [InlineData(SessionStatusTokens.Disconnecting)]
    [InlineData(SessionStatusTokens.Error)]
    [InlineData(SessionStatusTokens.Disconnected)]
    [InlineData("The remote host refused the connection")]
    [InlineData("")]
    public void CountConnectedSessions_DoesNotCountOtherStatuses(string status)
    {
        SessionTabViewModel[] attached = [WithStatus(status)];
        SessionTabViewModel[] detached = [WithStatus(status)];

        Assert.Equal(0, ShutdownDecisions.CountConnectedSessions(attached, detached));
    }

    [Fact]
    public void CountConnectedSessions_MixedSet_CountsOnlySessionsQuittingEnds()
    {
        SessionTabViewModel[] attached =
        [
            WithStatus(SessionStatusTokens.Connected),
            WithStatus(SessionStatusTokens.RemoteSessionHandedOff),
            WithStatus(SessionStatusTokens.LaunchedExternalClient),
            WithStatus(SessionStatusTokens.Error),
        ];
        SessionTabViewModel[] detached =
        [
            WithStatus(SessionStatusTokens.RemoteSessionHandedOff),
            WithStatus(SessionStatusTokens.LaunchedExternalClient),
            WithStatus(SessionStatusTokens.Disconnected),
        ];

        Assert.Equal(3, ShutdownDecisions.CountConnectedSessions(attached, detached));
    }

    /// <remarks>Pins the ordinal match: the tokens are exact, not parsed like an enum name.</remarks>
    [Fact]
    public void CountConnectedSessions_DoesNotCountAWrongCaseStatus()
    {
        SessionTabViewModel[] attached =
        [
            WithStatus(SessionStatusTokens.RemoteSessionHandedOff.ToLowerInvariant()),
            WithStatus(SessionStatusTokens.Connected.ToUpperInvariant()),
        ];

        Assert.Equal(0, ShutdownDecisions.CountConnectedSessions(attached, []));
    }

    [Fact]
    public void CountConnectedSessions_CountsATabWhoseOnlyWinRmPaneIsSecondary()
    {
        SessionTabViewModel split = new()
        {
            RootContent = new SplitContainerModel
            {
                First = new SessionPaneModel { Status = SessionStatusTokens.Disconnected },
                Second = new SessionPaneModel { Status = SessionStatusTokens.RemoteSessionHandedOff },
            },
        };

        Assert.Equal(1, ShutdownDecisions.CountConnectedSessions([split], []));
    }

    [Fact]
    public void CountConnectedSessions_NullStatus_IsNotCounted()
    {
        SessionTabViewModel[] attached = [WithStatus(null!)];

        Assert.Equal(0, ShutdownDecisions.CountConnectedSessions(attached, []));
    }

    private static SessionTabViewModel Connected() => WithStatus(SessionStatusTokens.Connected);

    private static SessionTabViewModel Disconnected() => WithStatus(SessionStatusTokens.Disconnected);

    private static SessionTabViewModel WithStatus(string status) => new() { Status = status };
}
