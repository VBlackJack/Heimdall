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

using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Pure-helper tests for the "Close others" / "Close to the right" target
/// selection. No WPF, no harness.
/// </summary>
public class SessionTabCloseTargetTests
{
    private static SessionTabViewModel Tab(string title) =>
        new SessionTabViewModel { Title = title };

    [Fact]
    public void SessionsToCloseOthers_Middle_ReturnsEveryOtherInOrder()
    {
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");

        var others = SessionTabContextMenuFactory.SessionsToCloseOthers([a, b, c], b);

        Assert.Equal(new[] { a, c }, others);
    }

    [Fact]
    public void SessionsToCloseOthers_Single_ReturnsEmpty()
    {
        var a = Tab("a");

        var others = SessionTabContextMenuFactory.SessionsToCloseOthers([a], a);

        Assert.Empty(others);
    }

    [Fact]
    public void SessionsToCloseOthers_CurrentNotInList_ReturnsAll()
    {
        var a = Tab("a");
        var b = Tab("b");
        var stranger = Tab("stranger");

        var others = SessionTabContextMenuFactory.SessionsToCloseOthers([a, b], stranger);

        Assert.Equal(new[] { a, b }, others);
    }

    [Fact]
    public void SessionsToCloseToRight_Middle_ReturnsSessionsAfter()
    {
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        var d = Tab("d");

        var right = SessionTabContextMenuFactory.SessionsToCloseToRight([a, b, c, d], b);

        Assert.Equal(new[] { c, d }, right);
    }

    [Fact]
    public void SessionsToCloseToRight_First_ReturnsAllButFirst()
    {
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");

        var right = SessionTabContextMenuFactory.SessionsToCloseToRight([a, b, c], a);

        Assert.Equal(new[] { b, c }, right);
    }

    [Fact]
    public void SessionsToCloseToRight_Last_ReturnsEmpty()
    {
        var a = Tab("a");
        var b = Tab("b");

        var right = SessionTabContextMenuFactory.SessionsToCloseToRight([a, b], b);

        Assert.Empty(right);
    }

    [Fact]
    public void SessionsToCloseToRight_CurrentNotInList_ReturnsEmpty()
    {
        var a = Tab("a");
        var b = Tab("b");
        var stranger = Tab("stranger");

        var right = SessionTabContextMenuFactory.SessionsToCloseToRight([a, b], stranger);

        Assert.Empty(right);
    }

    [Fact]
    public void SessionsToCloseOthers_ExcludesPinnedTabs()
    {
        var a = Tab("a");
        var b = Tab("b");
        b.IsPinned = true;
        var c = Tab("c");

        var others = SessionTabContextMenuFactory.SessionsToCloseOthers([a, b, c], a);

        Assert.Equal(new[] { c }, others);
    }

    [Fact]
    public void SessionsToCloseToRight_ExcludesPinnedTabs()
    {
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        c.IsPinned = true;
        var d = Tab("d");

        var right = SessionTabContextMenuFactory.SessionsToCloseToRight([a, b, c, d], a);

        Assert.Equal(new[] { b, d }, right);
    }
}

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void SessionTabContextMenu_SingleSession_DisablesCloseOthers()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel only = CreateSession("s0", "TOOL:PING");
            harness.Main.Connection.ActiveSessions.Add(only);

            ContextMenu menu = CreateSessionTabMenu(harness.Main, only);

            MenuItem closeOthers = AssertMenuItem(menu, harness.Main.Localize("SessionCloseOthers"));
            Assert.False(closeOthers.IsEnabled);
        });
    }

    [Fact]
    public void SessionTabContextMenu_MiddleSession_EnablesCloseOthersAndCloseToRight()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel s0 = CreateSession("s0", "TOOL:PING");
            SessionTabViewModel s1 = CreateSession("s1", "TOOL:PING");
            SessionTabViewModel s2 = CreateSession("s2", "TOOL:PING");
            harness.Main.Connection.ActiveSessions.Add(s0);
            harness.Main.Connection.ActiveSessions.Add(s1);
            harness.Main.Connection.ActiveSessions.Add(s2);

            ContextMenu menu = CreateSessionTabMenu(harness.Main, s1);

            Assert.True(AssertMenuItem(menu, harness.Main.Localize("SessionCloseOthers")).IsEnabled);
            Assert.True(AssertMenuItem(menu, harness.Main.Localize("SessionCloseToRight")).IsEnabled);
        });
    }

    [Fact]
    public void SessionTabContextMenu_LastSession_DisablesCloseToRight()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel s0 = CreateSession("s0", "TOOL:PING");
            SessionTabViewModel s1 = CreateSession("s1", "TOOL:PING");
            harness.Main.Connection.ActiveSessions.Add(s0);
            harness.Main.Connection.ActiveSessions.Add(s1);

            ContextMenu menu = CreateSessionTabMenu(harness.Main, s1);

            Assert.False(AssertMenuItem(menu, harness.Main.Localize("SessionCloseToRight")).IsEnabled);
        });
    }

    [Fact]
    public void SessionTabContextMenu_ResolvedProfile_AddsRevealInTree()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            SessionTabViewModel session = CreateSession(server.Id, "SSH");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("SessionRevealInTree")));
        });
    }

    [Fact]
    public void SessionTabContextMenu_ToolTab_DoesNotAddRevealInTree()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("any", "TOOL:PING");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            Assert.Null(FindMenuItem(menu, harness.Main.Localize("SessionRevealInTree")));
        });
    }

    [Fact]
    public void SessionTabContextMenu_RevealInTreeClick_InvokesCallbackWithServerId()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            SessionTabViewModel session = CreateSession(server.Id, "SSH");

            NullSessionTabContextCallbacks callbacks = new NullSessionTabContextCallbacks();
            SessionTabContextMenuFactory factory = new SessionTabContextMenuFactory();
            ContextMenu menu = factory.CreateMenu(session, harness.Main, callbacks);

            MenuItem reveal = AssertMenuItem(menu, harness.Main.Localize("SessionRevealInTree"));
            reveal.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(server.Id, callbacks.RevealedServerId);
        });
    }

    [Fact]
    public void SessionTabContextMenu_AlwaysHasRenameTab()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("any", "TOOL:PING");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("SessionRenameTab")));
        });
    }

    [Fact]
    public void SessionTabContextMenu_ResetTitle_PresentOnlyWhenCustomTitleSet()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("any", "TOOL:PING");

            ContextMenu withoutCustom = CreateSessionTabMenu(harness.Main, session);
            Assert.Null(FindMenuItem(withoutCustom, harness.Main.Localize("SessionResetTitle")));

            session.CustomTitle = "Renamed";
            ContextMenu withCustom = CreateSessionTabMenu(harness.Main, session);
            Assert.NotNull(FindMenuItem(withCustom, harness.Main.Localize("SessionResetTitle")));
        });
    }

    [Fact]
    public void SessionTabContextMenu_PinLabel_ReflectsIsPinned()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("any", "TOOL:PING");

            ContextMenu unpinned = CreateSessionTabMenu(harness.Main, session);
            Assert.NotNull(FindMenuItem(unpinned, harness.Main.Localize("SessionPinTab")));
            Assert.Null(FindMenuItem(unpinned, harness.Main.Localize("SessionUnpinTab")));

            session.IsPinned = true;
            ContextMenu pinned = CreateSessionTabMenu(harness.Main, session);
            Assert.NotNull(FindMenuItem(pinned, harness.Main.Localize("SessionUnpinTab")));
            Assert.Null(FindMenuItem(pinned, harness.Main.Localize("SessionPinTab")));
        });
    }
}
