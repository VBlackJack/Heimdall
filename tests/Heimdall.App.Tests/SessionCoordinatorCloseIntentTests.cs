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
using Heimdall.App.Services.CloseGuard;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The intent each close producer actually emits, read from the observed request.
/// </summary>
/// <remarks>
/// These were source scans asserting that a method body contained or lacked the text
/// <c>CloseIntent.Silent</c>. A scan cannot tell a real argument from a comment, cannot follow the
/// value through a helper, and says nothing about which branch runs. The intent is now read off
/// the request the arbiter was handed, through the callbacks the coordinator itself wires.
/// <para>
/// No total is asserted anywhere. Swapping two producers' intents leaves every count unchanged,
/// which is precisely how a user gesture reached the silent path to begin with.
/// </para>
/// </remarks>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task SessionStartFailure_TearsDownSilently()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto server = harness.CreateServer("SSH");
        await harness.PersistServerAsync(server);
        const string sessionId = "failing-session";
        harness.CloseArbiter.Reset();

        // The real pipeline, and the placeholder tab created by the real OnSessionStarting. Adding
        // the tab by hand sent OnSessionStartingCore down its duplicate-ignored branch, so the test
        // still reached OnSessionStartFailed but never through the materialisation cycle it claims
        // to exercise.
        Task<BulkConnectOutcome> pipeline = harness.RunPipelineAsync(server, sessionId);
        ControlledProtocolHandler handler = harness.GetHandler("SSH");
        await handler.Started.Task.WaitAsync(TestTimeout);

        await WaitUntilAsync(() => harness.Main.Connection.ActiveSessions
            .Any(session => string.Equals(session.ServerId, sessionId, StringComparison.Ordinal)));

        handler.Result.SetResult(new ConnectionResult(false, "materialisation failed", null));
        await pipeline.WaitAsync(TestTimeout);

        CloseRequest request = await TakeSingleRequestAsync(harness);

        // Nobody is there to answer: the session never came up, so a prompt would block teardown
        // on a question about work that does not exist.
        Assert.Equal(DisconnectReason.FailedSession, request.Reason);
        Assert.Equal(CloseIntent.Silent, request.Intent);
    }

    [Fact]
    public async Task AdHocReconnect_TearsTheOldTabDownSilently()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto snapshot = harness.CreateServer("SSH");
        SessionTabViewModel tab = harness.Main.Connection.AddSession(
            "adhoc-runtime",
            snapshot.DisplayName,
            "SSH");
        tab.MarkAsAdHoc(snapshot);
        harness.CloseArbiter.Reset();

        harness.Main.Session.ReconnectSession(tab);

        CloseRequest request = await TakeSingleRequestAsync(harness);

        // A reconnect replaces the tab it came from. Asking permission to close the thing the user
        // just asked to reconnect would be a question with only one sensible answer.
        Assert.Equal(DisconnectReason.ReconnectInitiated, request.Reason);
        Assert.Equal(CloseIntent.Silent, request.Intent);

        // Finish the controlled handler so no reconnect task outlives the test.
        ControlledProtocolHandler handler = harness.GetHandler("SSH");
        await handler.Started.Task.WaitAsync(TestTimeout);
        handler.Result.SetResult(new ConnectionResult(false, "reconnect declined", null));
        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
    }

    [Fact]
    public async Task ClosingFromTheOverlay_KeepsTheGuard()
    {
        using TestHarness harness = TestHarness.Create();
        SessionTabViewModel tab = harness.Main.Connection.AddSession("close-runtime", "Demo", "SSH");
        harness.CloseArbiter.Reset();

        Assert.NotNull(harness.EmbeddedSessionManager.CloseRequestedCallback);
        harness.EmbeddedSessionManager.CloseRequestedCallback!(tab);

        CloseRequest request = await TakeSingleRequestAsync(harness);

        // A user pressed close. Whatever a guard wants to say about unsaved work, it must be
        // allowed to say it.
        Assert.Equal(DisconnectReason.UserAction, request.Reason);
        Assert.Equal(CloseIntent.Interactive, request.Intent);
    }

    [Fact]
    public async Task DisconnectingAnUnsplitSession_KeepsTheGuardAndTheReason()
    {
        using TestHarness harness = TestHarness.Create();
        SessionTabViewModel tab = harness.Main.Connection.AddSession("plain-runtime", "Demo", "SSH");
        SessionPaneModel pane = Assert.IsType<SessionPaneModel>(tab.RootContent);
        harness.CloseArbiter.Reset();

        Assert.NotNull(harness.EmbeddedSessionManager.DisconnectRequestedCallback);
        harness.EmbeddedSessionManager.DisconnectRequestedCallback!(
            tab,
            pane,
            DisconnectReason.TabClose);

        CloseRequest request = await TakeSingleRequestAsync(harness);

        // The reason travels: a guard that reacts to why the session is going away needs it.
        Assert.Equal(DisconnectReason.TabClose, request.Reason);
        Assert.Equal(CloseIntent.Interactive, request.Intent);
    }

    [Fact]
    public async Task DisconnectingOneSideOfASplit_KeepsTheGuardAndTheReason()
    {
        using TestHarness harness = TestHarness.Create();
        SessionTabViewModel tab = harness.Main.Connection.AddSession("split-runtime", "Demo", "SSH");
        SessionPaneModel primary = Assert.IsType<SessionPaneModel>(tab.RootContent);
        SessionPaneModel secondary = new() { PaneId = "secondary", Title = "Secondary" };
        tab.RootContent = new SplitContainerModel { First = primary, Second = secondary };
        harness.CloseArbiter.Reset();

        Assert.NotNull(harness.EmbeddedSessionManager.DisconnectRequestedCallback);
        harness.EmbeddedSessionManager.DisconnectRequestedCallback!(
            tab,
            secondary,
            DisconnectReason.TabClose);

        CloseRequest request = await TakeSingleRequestAsync(harness);

        // The split branch runs MainViewModel.ClosePaneAsync instead of closing the whole session.
        // Covering only the unsplit case would let a mutation of this branch alone survive, and it
        // is the branch that decides whether a user loses one pane or is asked first.
        Assert.Equal(DisconnectReason.TabClose, request.Reason);
        Assert.Equal(CloseIntent.Interactive, request.Intent);
    }

    /// <summary>
    /// Waits for exactly one close request to reach the arbiter and returns it.
    /// </summary>
    /// <remarks>
    /// Polls for the observation rather than sleeping: several of these producers hand off through
    /// <c>SafeFireAndForget</c>, so the request arrives on another turn of the loop.
    /// </remarks>
    private static async Task<CloseRequest> TakeSingleRequestAsync(TestHarness harness)
    {
        await WaitUntilAsync(() => harness.CloseArbiter.Polled.Count > 0);
        return Assert.Single(harness.CloseArbiter.Polled);
    }
}
