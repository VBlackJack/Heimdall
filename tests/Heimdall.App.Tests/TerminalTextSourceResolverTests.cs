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
using static Heimdall.App.Tests.MaskedCopyTestSupport;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers <c>TerminalTextSourceResolver.Resolve</c>: which pane of a tab the masked copy reads.
/// </summary>
public sealed class TerminalTextSourceResolverTests
{
    private static bool NoFocus(object host) => false;

    [Fact]
    public void AReadyTerminal_IsFound()
    {
        var terminal = new FakeTerminal();

        var found = TerminalTextSourceResolver.Resolve(Tab(Leaf(terminal)), hasKeyboardFocus: NoFocus);

        Assert.Same(terminal, found);
    }

    [Fact]
    public void AHostControlThatIsNotATerminal_YieldsNone()
    {
        var found = TerminalTextSourceResolver.Resolve(Tab(Leaf(new object())), hasKeyboardFocus: NoFocus);

        Assert.Null(found);
    }

    [Fact]
    public void ATerminalThatIsNotReady_YieldsNone()
    {
        var found = TerminalTextSourceResolver.Resolve(
            Tab(Leaf(new FakeTerminal { CanReadText = false })),
            hasKeyboardFocus: NoFocus);

        Assert.Null(found);
    }

    [Fact]
    public void ANullTab_YieldsNone()
    {
        Assert.Null(TerminalTextSourceResolver.Resolve(null));
    }

    [Fact]
    public void ASplitWithAnRdpPrimaryAndATerminalSecondary_FindsTheSecondary()
    {
        var terminal = new FakeTerminal();
        var tab = Tab(Split(Leaf(new object()), Leaf(terminal)));

        var found = TerminalTextSourceResolver.Resolve(tab, hasKeyboardFocus: NoFocus);

        Assert.Same(terminal, found);
    }

    [Fact]
    public void ASplitWithTwoTerminals_FindsTheFocusedOne()
    {
        var first = new FakeTerminal();
        var second = new FakeTerminal();
        var tab = Tab(Split(Leaf(first), Leaf(second)));

        var found = TerminalTextSourceResolver.Resolve(tab, hasKeyboardFocus: host => ReferenceEquals(host, second));

        Assert.Same(second, found);
    }

    /// <summary>
    /// Control for the focus test: with no pointer and no focus two terminals have no single
    /// answer, so a resolver that simply took the first would pass the test above for the wrong reason.
    /// </summary>
    [Fact]
    public void ASplitWithTwoTerminalsAndNoFocus_YieldsNone()
    {
        var tab = Tab(Split(Leaf(new FakeTerminal()), Leaf(new FakeTerminal())));

        Assert.Null(TerminalTextSourceResolver.Resolve(tab, hasKeyboardFocus: NoFocus));
    }

    [Fact]
    public void TheRightClickedPane_WinsOverTheFocusedOne()
    {
        var focused = new FakeTerminal();
        var clicked = new FakeTerminal();
        var clickedPane = Leaf(clicked);
        var tab = Tab(Split(Leaf(focused), clickedPane));

        var found = TerminalTextSourceResolver.Resolve(
            tab,
            clickedPane,
            hasKeyboardFocus: host => ReferenceEquals(host, focused));

        Assert.Same(clicked, found);
    }

    [Fact]
    public void ARightClickedPaneThatIsNotATerminal_YieldsNone_NotTheOtherPane()
    {
        var clickedPane = Leaf(new object());
        var tab = Tab(Split(clickedPane, Leaf(new FakeTerminal())));

        Assert.Null(TerminalTextSourceResolver.Resolve(tab, clickedPane, hasKeyboardFocus: NoFocus));
    }
}
