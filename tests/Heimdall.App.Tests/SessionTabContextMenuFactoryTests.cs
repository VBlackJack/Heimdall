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

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void SessionTabContextMenu_ResolvedProfile_AddsProfileActions()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));
            SessionTabViewModel session = CreateSession(server.Id, "SSH");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            MenuItem editItem = AssertMenuItem(menu, harness.Main.Localize("TreeCtxEdit"));
            Assert.Same(harness.Main.ServerList.EditServerCommand, editItem.Command);
            Assert.Same(serverVm, editItem.CommandParameter);
            Assert.Equal("Ctrl+E", editItem.InputGestureText);

            MenuItem copyHostnameItem = AssertMenuItem(
                menu,
                harness.Main.Localize("TreeCtxCopyHostname"));
            Assert.Same(harness.Main.ServerList.CopyHostnameCommand, copyHostnameItem.Command);
            Assert.Same(serverVm, copyHostnameItem.CommandParameter);

            MenuItem copyUsernameItem = AssertMenuItem(
                menu,
                harness.Main.Localize("TreeCtxCopyUsername"));
            Assert.Same(harness.Main.ServerList.CopyUsernameCommand, copyUsernameItem.Command);
            Assert.Same(serverVm, copyUsernameItem.CommandParameter);
            Assert.True(copyUsernameItem.IsEnabled);
        });
    }

    [Fact]
    public void SessionTabContextMenu_UnresolvedProfile_DoesNotAddProfileActions()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("missing-profile", "SSH");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxEdit")));
            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopyHostname")));
            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopyUsername")));
        });
    }

    [Fact]
    public void SessionTabContextMenu_ToolTab_DoesNotAddProfileActions()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            SessionTabViewModel session = CreateSession(server.Id, "TOOL:PING");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxEdit")));
            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopyHostname")));
            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopyUsername")));
        });
    }

    [Fact]
    public void SessionTabContextMenu_BlankUsername_DisablesCopyUsername()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            server.SshUsername = "";
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            SessionTabViewModel session = CreateSession(server.Id, "SSH");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            MenuItem copyUsernameItem = AssertMenuItem(
                menu,
                harness.Main.Localize("TreeCtxCopyUsername"));
            Assert.False(copyUsernameItem.IsEnabled);
        });
    }

    [Fact]
    public void SessionTabContextMenu_HostlessUnsplitSession_DisablesDetach()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("external-rdp", "RDP");

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            MenuItem detachItem = AssertMenuItem(
                menu,
                harness.Main.Localize("SessionCtxDetach"));
            Assert.False(detachItem.IsEnabled);
        });
    }

    [Fact]
    public void SessionTabContextMenu_HostedUnsplitSession_EnablesDetach()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("embedded-rdp", "RDP");
            session.HostControl = new Border();

            ContextMenu menu = CreateSessionTabMenu(harness.Main, session);

            MenuItem detachItem = AssertMenuItem(
                menu,
                harness.Main.Localize("SessionCtxDetach"));
            Assert.True(detachItem.IsEnabled);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static SessionTabViewModel CreateSession(string serverId, string connectionType)
    {
        return new SessionTabViewModel
        {
            Title = "Demo session",
            ServerId = serverId,
            OriginalServerId = serverId,
            ConnectionType = connectionType
        };
    }

    private static ContextMenu CreateSessionTabMenu(MainViewModel vm, SessionTabViewModel session)
    {
        SessionTabContextMenuFactory factory = new SessionTabContextMenuFactory();
        return factory.CreateMenu(session, vm, new NullSessionTabContextCallbacks());
    }

    private static MenuItem AssertMenuItem(ContextMenu menu, string header)
    {
        MenuItem? item = FindMenuItem(menu, header);
        Assert.NotNull(item);
        return item!;
    }

    private static MenuItem? FindMenuItem(ContextMenu menu, string header)
    {
        foreach (object rawItem in menu.Items)
        {
            if (rawItem is MenuItem menuItem
                && menuItem.Header is string itemHeader
                && string.Equals(itemHeader, header, StringComparison.Ordinal))
            {
                return menuItem;
            }
        }

        return null;
    }

    private sealed class NullSessionTabContextCallbacks : ISessionTabContextCallbacks
    {
        public string? RevealedServerId { get; private set; }

        public void OnResolutionChanged(SessionPaneModel pane, ResolutionChoice choice)
        {
        }

        public void ToggleFullscreen()
        {
        }

        public void DetachSessionToFloatingWindow(SessionTabViewModel session)
        {
        }

        public void DetachSecondaryToFloatingWindow(SessionTabViewModel session)
        {
        }

        public void RequestSplitSession(SessionTabViewModel session, SplitOrientation orientation)
        {
        }

        public void UnsplitSession(SessionTabViewModel session)
        {
        }

        public void RevealServerInTree(string serverId)
        {
            RevealedServerId = serverId;
        }
    }
}
