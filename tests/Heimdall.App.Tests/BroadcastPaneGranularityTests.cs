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

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that a Command Library broadcast counts terminal <b>panes</b>, not tabs.
/// </summary>
/// <remarks>
/// <para>
/// <b>It counted tabs, and that was wrong.</b> A split tab holds two terminals, and they are two
/// separate machines as often as not. Enumerating tabs and writing through
/// <c>TrySendCommandToFirstSink</c> reached the first of them, skipped the second, and reported
/// the tab as delivered - so the operator ticked one row, saw one success, and had run the command
/// on half of what they were looking at.
/// </para>
/// <para>
/// The terminal broadcast mode has resolved per pane through <c>BroadcastTargetResolver</c> since
/// it shipped. This puts the two on the same footing.
/// </para>
/// </remarks>
public sealed class BroadcastPaneGranularityTests
{
    // -- What counts as a target ------------------------------------

    [Fact]
    public void ASplitTabWithTwoTerminalsOffersTwoTargets()
    {
        var panes = PanesOf(Tab("web-01", Split(Terminal(), Terminal())));

        Assert.Equal(2, panes.Count);
    }

    /// <summary>
    /// Control for the test above: the unsplit case must still offer exactly one, or "two panes"
    /// could be satisfied by anything that double-counts.
    /// </summary>
    [Fact]
    public void AnUnsplitTabOffersOne()
    {
        var panes = PanesOf(Tab("web-01", Terminal()));

        Assert.Single(panes);
    }

    /// <summary>
    /// A terminal beside a file browser is one target, not two: what disqualifies a pane is having
    /// nothing to write to, and the local shell ships exactly this shape.
    /// </summary>
    [Fact]
    public void APaneWithNothingToWriteToIsNotATarget()
    {
        var panes = PanesOf(Tab("web-01", Split(Terminal(), Leaf(new object()))));

        Assert.Single(panes);
    }

    [Fact]
    public void ATabWithNoTerminalAtAllOffersNothing()
    {
        var panes = PanesOf(Tab("rdp-01", Leaf(new object())));

        Assert.Empty(panes);
    }

    [Fact]
    public void PanesComeBackInTabOrderThenSplitOrder()
    {
        var firstOfA = new FakeSink();
        var secondOfA = new FakeSink();
        var onlyOfB = new FakeSink();

        var panes = PanesOf(
            Tab("web-01", Split(Leaf(firstOfA), Leaf(secondOfA))),
            Tab("web-02", Leaf(onlyOfB)));

        Assert.Equal(
            [firstOfA, secondOfA, onlyOfB],
            panes.Select(candidate => candidate.Pane.HostControl));
    }

    // -- How a pane is named ----------------------------------------

    /// <summary>
    /// Two rows both reading "web-01" are worse than one row that dropped a pane: the operator
    /// would believe they had chosen between them.
    /// </summary>
    [Fact]
    public void ASplitPaneCarriesItsOwnTitleAfterTheTabs()
    {
        var session = Tab("web-01", Terminal());
        var pane = new SessionPaneModel { Title = "shell 2" };

        string described = EmbeddedSessionManager.DescribeBroadcastPane(
            session, pane, sessionIsSplit: true);

        Assert.Equal("web-01 - shell 2", described);
    }

    [Fact]
    public void AnUnsplitPaneIsJustTheTabsName()
    {
        var session = Tab("web-01", Terminal());
        var pane = new SessionPaneModel { Title = "shell 1" };

        string described = EmbeddedSessionManager.DescribeBroadcastPane(
            session, pane, sessionIsSplit: false);

        Assert.Equal("web-01", described);
    }

    /// <summary>
    /// A pane whose title already is the tab's name would otherwise read "web-01 - web-01".
    /// </summary>
    [Fact]
    public void ARepeatedTitleIsNotSaidTwice()
    {
        var session = Tab("web-01", Terminal());
        var pane = new SessionPaneModel { Title = "web-01" };

        string described = EmbeddedSessionManager.DescribeBroadcastPane(
            session, pane, sessionIsSplit: true);

        Assert.Equal("web-01", described);
    }

    [Fact]
    public void AnUntitledPaneFallsBackToTheTabsName()
    {
        var session = Tab("web-01", Terminal());
        var pane = new SessionPaneModel { Title = "   " };

        string described = EmbeddedSessionManager.DescribeBroadcastPane(
            session, pane, sessionIsSplit: true);

        Assert.Equal("web-01", described);
    }

    // -- Identity ---------------------------------------------------

    /// <summary>
    /// The id has to separate the two halves of one split, which is the whole point of counting
    /// panes. <c>ServerId</c> could not: both halves of a split on one server share it.
    /// </summary>
    [Fact]
    public void EveryTargetCarriesItsOwnId()
    {
        var panes = PanesOf(
            Tab("web-01", Split(Terminal(), Terminal())),
            Tab("web-02", Terminal()));

        var ids = panes
            .Select(candidate => candidate.Pane.PaneId)
            .ToList();

        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    // -- Harness ----------------------------------------------------

    private sealed class FakeSink : ITerminalCommandSink
    {
        public List<string> Received { get; } = [];

        public void WriteCommand(string command) => Received.Add(command);
    }

    private static SessionPaneModel Leaf(object? hostControl) => new() { HostControl = hostControl };

    private static SessionPaneModel Terminal() => Leaf(new FakeSink());

    private static SplitContainerModel Split(ISplitContent first, ISplitContent second) => new()
    {
        First = first,
        Second = second,
        Orientation = SplitOrientation.Vertical
    };

    private static SessionTabViewModel Tab(string title, ISplitContent root)
    {
        var tab = new SessionTabViewModel { RootContent = root };
        tab.Title = title;
        return tab;
    }

    /// <summary>
    /// The pure helper the manager's own <c>BroadcastCandidates</c> delegates to, so these run
    /// without standing up a manager and its ten dependencies.
    /// </summary>
    private static IReadOnlyList<(SessionTabViewModel Session, SessionPaneModel Pane)> PanesOf(
        params SessionTabViewModel[] sessions)
        => EmbeddedSessionManager.EnumerateBroadcastPanes(sessions);
}
