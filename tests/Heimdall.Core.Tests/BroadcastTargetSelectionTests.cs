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

using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

/// <summary>
/// Producer: BroadcastTargetSelection
/// (src/Heimdall.Core/Models/BroadcastTargetSelection.cs). Pins the per-session
/// tab-strip toggle semantics consumed by SessionCoordinator.ToggleSessionBroadcastTarget
/// / RefreshBroadcastTabMarkers / CountBroadcastTargets.
/// </summary>
public class BroadcastTargetSelectionTests
{
    private sealed class TerminalHost
    {
        public string Name { get; init; } = "";
    }

    private sealed class NonTerminalHost
    {
        public string Name { get; init; } = "";
    }

    private static readonly Func<SessionPaneModel, bool> IsTerminal =
        pane => pane.HostControl is TerminalHost;

    private static SessionPaneModel Pane(object? host, bool marked = false)
        => new() { HostControl = host, IsBroadcastTarget = marked };

    private static SplitContainerModel Split(ISplitContent first, ISplitContent second)
        => new() { First = first, Second = second, Orientation = SplitOrientation.Vertical, SplitRatio = 0.5 };

    [Fact]
    public void ToggleSession_NoneMarked_MarksAllTerminalPanes()
    {
        var t1 = Pane(new TerminalHost { Name = "t1" });
        var t2 = Pane(new TerminalHost { Name = "t2" });
        var root = Split(t1, t2);

        bool? result = BroadcastTargetSelection.ToggleSession(root, IsTerminal);

        Assert.True(result);
        Assert.True(t1.IsBroadcastTarget);
        Assert.True(t2.IsBroadcastTarget);
    }

    [Fact]
    public void ToggleSession_AllMarked_UnmarksAll()
    {
        var t1 = Pane(new TerminalHost { Name = "t1" }, marked: true);
        var t2 = Pane(new TerminalHost { Name = "t2" }, marked: true);
        var root = Split(t1, t2);

        bool? result = BroadcastTargetSelection.ToggleSession(root, IsTerminal);

        Assert.False(result);
        Assert.False(t1.IsBroadcastTarget);
        Assert.False(t2.IsBroadcastTarget);
    }

    [Fact]
    public void ToggleSession_PartiallyMarked_MarksAll()
    {
        var t1 = Pane(new TerminalHost { Name = "t1" }, marked: true);
        var t2 = Pane(new TerminalHost { Name = "t2" }); // unmarked
        var root = Split(t1, t2);

        bool? result = BroadcastTargetSelection.ToggleSession(root, IsTerminal);

        Assert.True(result);
        Assert.True(t1.IsBroadcastTarget);
        Assert.True(t2.IsBroadcastTarget);
    }

    [Fact]
    public void ToggleSession_NonTerminalSession_ReturnsNull_AndDoesNotMutate()
    {
        var n1 = Pane(new NonTerminalHost { Name = "rdp" });
        var n2 = Pane(new NonTerminalHost { Name = "vnc" });
        var root = Split(n1, n2);

        bool? result = BroadcastTargetSelection.ToggleSession(root, IsTerminal);

        Assert.Null(result);
        Assert.False(n1.IsBroadcastTarget);
        Assert.False(n2.IsBroadcastTarget);
    }

    [Fact]
    public void ToggleSession_MixedSession_FlipsOnlyTerminalPanes()
    {
        var terminal = Pane(new TerminalHost { Name = "ssh" });
        var fileBrowser = Pane(new NonTerminalHost { Name = "files" });
        var root = Split(terminal, fileBrowser);

        bool? result = BroadcastTargetSelection.ToggleSession(root, IsTerminal);

        Assert.True(result);
        Assert.True(terminal.IsBroadcastTarget);
        Assert.False(fileBrowser.IsBroadcastTarget); // non-terminal untouched
    }

    [Fact]
    public void SessionHasTerminal_ReflectsPresenceOfTerminalPane()
    {
        Assert.True(BroadcastTargetSelection.SessionHasTerminal(
            Split(Pane(new TerminalHost()), Pane(new NonTerminalHost())), IsTerminal));
        Assert.False(BroadcastTargetSelection.SessionHasTerminal(
            Split(Pane(new NonTerminalHost()), Pane(new NonTerminalHost())), IsTerminal));
        Assert.False(BroadcastTargetSelection.SessionHasTerminal(null, IsTerminal));
    }

    [Fact]
    public void IsSessionTargeted_TrueOnlyWhenAllTerminalPanesMarked()
    {
        Assert.True(BroadcastTargetSelection.IsSessionTargeted(
            Split(Pane(new TerminalHost(), marked: true), Pane(new TerminalHost(), marked: true)), IsTerminal));

        // Partially marked -> not targeted.
        Assert.False(BroadcastTargetSelection.IsSessionTargeted(
            Split(Pane(new TerminalHost(), marked: true), Pane(new TerminalHost())), IsTerminal));

        // No terminal pane -> not targeted even if a non-terminal pane is flagged.
        Assert.False(BroadcastTargetSelection.IsSessionTargeted(
            Split(Pane(new NonTerminalHost(), marked: true), Pane(new NonTerminalHost())), IsTerminal));
    }

    [Fact]
    public void CountTargets_CountsMarkedTerminalPanesAcrossSessions_IgnoringOthers()
    {
        var session1 = Split(Pane(new TerminalHost(), marked: true), Pane(new NonTerminalHost(), marked: true));
        var session2 = Split(Pane(new TerminalHost(), marked: true), Pane(new TerminalHost())); // 1 marked, 1 not
        var session3 = (ISplitContent?)Pane(new TerminalHost()); // unmarked terminal

        int count = BroadcastTargetSelection.CountTargets(
            new List<ISplitContent?> { session1, session2, session3 }, IsTerminal);

        // session1: 1 (the non-terminal marked pane does not count), session2: 1, session3: 0.
        Assert.Equal(2, count);
    }
}
