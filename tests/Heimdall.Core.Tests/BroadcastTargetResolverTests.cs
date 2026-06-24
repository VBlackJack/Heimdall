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
/// Producer: BroadcastTargetResolver.ResolveTargets
/// (src/Heimdall.Core/Models/BroadcastTargetResolver.cs). These tests pin the
/// pure scope/sender/predicate semantics consumed by
/// SessionCoordinator.ResolveBroadcastTargets and reused by Lot B.
/// </summary>
public class BroadcastTargetResolverTests
{
    // Stand-in host controls. The production predicate keys on EmbeddedSshView
    // (a WPF view); tests substitute marker types so the resolver can run without
    // any UI dependency.
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

    private static SessionPaneModel Pane(object? host)
        => new() { HostControl = host };

    private static SplitContainerModel Split(ISplitContent first, ISplitContent second)
        => new() { First = first, Second = second, Orientation = SplitOrientation.Vertical, SplitRatio = 0.5 };

    [Fact]
    public void CurrentTab_ReturnsOnlyPanesOfActiveTab()
    {
        var activeTerminal = new TerminalHost { Name = "active" };
        var otherTerminal = new TerminalHost { Name = "other" };

        var activeRoot = Pane(activeTerminal);
        var otherRoot = Pane(otherTerminal);
        var roots = new List<ISplitContent?> { activeRoot, otherRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.CurrentTab, sender: null, IsTerminal);

        Assert.Single(targets);
        Assert.Same(activeRoot, targets[0]);
    }

    [Fact]
    public void AllTabs_ReturnsPanesAcrossEveryTab()
    {
        var t1 = Pane(new TerminalHost { Name = "t1" });
        var t2 = Pane(new TerminalHost { Name = "t2" });
        var t3 = Pane(new TerminalHost { Name = "t3" });
        var roots = new List<ISplitContent?> { t1, t2, t3 };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, t1, BroadcastScope.AllTabs, sender: null, IsTerminal);

        Assert.Equal(new[] { t1, t2, t3 }, targets);
    }

    [Fact]
    public void Sender_IsAlwaysExcluded()
    {
        var senderHost = new TerminalHost { Name = "sender" };
        var peerHost = new TerminalHost { Name = "peer" };

        var senderPane = Pane(senderHost);
        var peerPane = Pane(peerHost);
        var activeRoot = Split(senderPane, peerPane);
        var roots = new List<ISplitContent?> { activeRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.CurrentTab, sender: senderHost, IsTerminal);

        Assert.Single(targets);
        Assert.Same(peerPane, targets[0]);
    }

    [Fact]
    public void NonTerminalSurfaces_AreNeverTargeted()
    {
        // One terminal pane plus four non-terminal surfaces (RDP/VNC/SFTP/Citrix
        // stand-ins). Only the terminal pane is a valid broadcast target.
        var terminalPane = Pane(new TerminalHost { Name = "ssh" });
        var rdpPane = Pane(new NonTerminalHost { Name = "rdp" });
        var vncPane = Pane(new NonTerminalHost { Name = "vnc" });
        var sftpPane = Pane(new NonTerminalHost { Name = "sftp" });
        var citrixPane = Pane(new NonTerminalHost { Name = "citrix" });

        var activeRoot = Split(
            Split(terminalPane, rdpPane),
            Split(Split(vncPane, sftpPane), citrixPane));
        var roots = new List<ISplitContent?> { activeRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.AllTabs, sender: null, IsTerminal);

        Assert.Single(targets);
        Assert.Same(terminalPane, targets[0]);
    }

    [Fact]
    public void EmptySessions_ReturnsEmpty()
    {
        var targets = BroadcastTargetResolver.ResolveTargets(
            new List<ISplitContent?>(), activeSessionRoot: null, BroadcastScope.AllTabs, sender: null, IsTerminal);

        Assert.Empty(targets);
    }

    [Fact]
    public void CurrentTab_WithNoActiveTab_ReturnsEmpty()
    {
        var orphan = Pane(new TerminalHost { Name = "orphan" });
        var roots = new List<ISplitContent?> { orphan };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeSessionRoot: null, BroadcastScope.CurrentTab, sender: null, IsTerminal);

        Assert.Empty(targets);
    }

    [Fact]
    public void PreservesDepthFirstPaneOrder()
    {
        var a = Pane(new TerminalHost { Name = "a" });
        var b = Pane(new TerminalHost { Name = "b" });
        var c = Pane(new TerminalHost { Name = "c" });

        // Tree: (a | (b | c)) -> depth-first order is a, b, c.
        var activeRoot = Split(a, Split(b, c));
        var roots = new List<ISplitContent?> { activeRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.CurrentTab, sender: null, IsTerminal);

        Assert.Equal(new[] { a, b, c }, targets);
    }

    // ── SelectedPanes (Lot B) ───────────────────────────────────────────
    // Producer: BroadcastTargetResolver.ResolveTargets (the AllTabs/SelectedPanes
    // span-every-tab branch) combined with a predicate that, like
    // SessionCoordinator.ResolveBroadcastTargets, requires
    // SessionPaneModel.IsBroadcastTarget (src/Heimdall.Core/Models/SessionPaneModel.cs).

    private static readonly Func<SessionPaneModel, bool> IsSelectedTerminal =
        pane => pane.HostControl is TerminalHost && pane.IsBroadcastTarget;

    private static SessionPaneModel MarkedTerminal(string name)
        => new() { HostControl = new TerminalHost { Name = name }, IsBroadcastTarget = true };

    [Fact]
    public void SelectedPanes_TargetsOnlyMarkedTerminalPanes()
    {
        var marked1 = MarkedTerminal("m1");
        var unmarked = Pane(new TerminalHost { Name = "u" }); // terminal but not selected
        var marked2 = MarkedTerminal("m2");

        var activeRoot = Split(marked1, Split(unmarked, marked2));
        var roots = new List<ISplitContent?> { activeRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.SelectedPanes, sender: null, IsSelectedTerminal);

        Assert.Equal(new[] { marked1, marked2 }, targets);
    }

    [Fact]
    public void SelectedPanes_SpansMultipleTabs()
    {
        var tab1Marked = MarkedTerminal("t1");
        var tab2Unmarked = Pane(new TerminalHost { Name = "t2" });
        var tab3Marked = MarkedTerminal("t3");
        var roots = new List<ISplitContent?> { tab1Marked, tab2Unmarked, tab3Marked };

        // Active tab is tab2 (unmarked); SelectedPanes must still reach marked panes
        // in the other tabs.
        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, tab2Unmarked, BroadcastScope.SelectedPanes, sender: null, IsSelectedTerminal);

        Assert.Equal(new[] { tab1Marked, tab3Marked }, targets);
    }

    [Fact]
    public void SelectedPanes_ExcludesSender_EvenWhenMarked()
    {
        var senderHost = new TerminalHost { Name = "sender" };
        var senderPane = new SessionPaneModel { HostControl = senderHost, IsBroadcastTarget = true };
        var peerPane = MarkedTerminal("peer");

        var activeRoot = Split(senderPane, peerPane);
        var roots = new List<ISplitContent?> { activeRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.SelectedPanes, sender: senderHost, IsSelectedTerminal);

        Assert.Single(targets);
        Assert.Same(peerPane, targets[0]);
    }

    [Fact]
    public void SelectedPanes_NeverTargetsNonTerminal_EvenIfFlagged()
    {
        // A non-terminal pane with the flag set must still be rejected by the
        // terminal-type half of the predicate.
        var flaggedNonTerminal = new SessionPaneModel
        {
            HostControl = new NonTerminalHost { Name = "rdp" },
            IsBroadcastTarget = true,
        };
        var markedTerminal = MarkedTerminal("ssh");

        var activeRoot = Split(flaggedNonTerminal, markedTerminal);
        var roots = new List<ISplitContent?> { activeRoot };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, activeRoot, BroadcastScope.SelectedPanes, sender: null, IsSelectedTerminal);

        Assert.Single(targets);
        Assert.Same(markedTerminal, targets[0]);
    }

    [Fact]
    public void SelectedPanes_EmptySelection_ReturnsEmpty_NoFallback()
    {
        // Terminal panes exist but none are marked: SelectedPanes must resolve to an
        // empty target set, NOT fall back to CurrentTab/AllTabs.
        var t1 = Pane(new TerminalHost { Name = "t1" });
        var t2 = Pane(new TerminalHost { Name = "t2" });
        var roots = new List<ISplitContent?> { t1, t2 };

        var targets = BroadcastTargetResolver.ResolveTargets(
            roots, t1, BroadcastScope.SelectedPanes, sender: null, IsSelectedTerminal);

        Assert.Empty(targets);
    }

    [Fact]
    public void SessionPaneModel_IsBroadcastTarget_DefaultsFalseAndRaisesOnChange()
    {
        // Producer seam: SessionPaneModel.IsBroadcastTarget
        // (src/Heimdall.Core/Models/SessionPaneModel.cs) is an ObservableProperty.
        var pane = new SessionPaneModel();
        Assert.False(pane.IsBroadcastTarget);

        var raised = new List<string?>();
        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.IsBroadcastTarget = true;

        Assert.True(pane.IsBroadcastTarget);
        Assert.Contains(nameof(SessionPaneModel.IsBroadcastTarget), raised);
    }
}
