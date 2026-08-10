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

    [Theory]
    [InlineData("SSH")]
    [InlineData("RDP")]
    public async Task SessionTabContextMenu_AdHocDuplicate_UsesSnapshotAndKeepsSource(string protocol)
    {
        TestHarness? harness = null;
        ControlledProtocolHandler? protocolHandler = null;
        ServerProfileDto? snapshot = null;
        SessionTabViewModel? source = null;
        string expectedSnapshotId = $"adhoc-{protocol.ToLowerInvariant()}-demo.example.com";
        TaskCompletionSource<SessionTabViewModel> duplicateAdded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            RunOnStaThread(() =>
            {
                harness = TestHarness.Create();
                protocolHandler = harness.GetHandler(protocol);
                snapshot = harness.CreateServer(protocol);
                snapshot.Id = expectedSnapshotId;
                source = harness.Main.Connection.AddSession(
                    snapshot.Id,
                    snapshot.DisplayName,
                    snapshot.ConnectionType);
                source.MarkAsAdHoc(snapshot);
                harness.Main.Connection.ActiveSessions.CollectionChanged += (_, args) =>
                {
                    if (args.NewItems is null)
                    {
                        return;
                    }

                    foreach (object item in args.NewItems)
                    {
                        if (item is SessionTabViewModel added && !ReferenceEquals(added, source))
                        {
                            added.PropertyChanged += (_, changeArgs) =>
                            {
                                if (string.Equals(
                                        changeArgs.PropertyName,
                                        nameof(SessionTabViewModel.AdHocProfileSnapshot),
                                        StringComparison.Ordinal)
                                    && added.IsAdHoc)
                                {
                                    duplicateAdded.TrySetResult(added);
                                }
                            };
                        }
                    }
                };

                ContextMenu menu = CreateSessionTabMenu(harness.Main, source);
                MenuItem duplicateItem = AssertMenuItem(
                    menu,
                    harness.Main.Localize("SessionDuplicateTab"));

                duplicateItem.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            });

            Assert.NotNull(harness);
            Assert.NotNull(protocolHandler);
            Assert.NotNull(snapshot);
            Assert.NotNull(source);

            CancellationToken token = await protocolHandler.Started.Task.WaitAsync(TestTimeout);
            Assert.False(token.IsCancellationRequested);
            Assert.Contains(source, harness.Main.Connection.ActiveSessions);
            Assert.Equal(expectedSnapshotId, snapshot.Id);

            protocolHandler.Result.SetResult(SuccessWithTerminalSession());
            SessionTabViewModel duplicate = await duplicateAdded.Task.WaitAsync(TestTimeout);

            Assert.Contains(source, harness.Main.Connection.ActiveSessions);
            Assert.True(duplicate.IsAdHoc);
            Assert.Same(snapshot, duplicate.AdHocProfileSnapshot);
            Assert.NotEqual(source.ServerId, duplicate.ServerId);
            Assert.Equal(expectedSnapshotId, snapshot.Id);
        }
        finally
        {
            harness?.Dispose();
        }
    }

    [Fact]
    public async Task SessionTabContextMenu_PersistedDuplicate_StillUsesInventoryConnection()
    {
        TestHarness? harness = null;
        ControlledProtocolHandler? sshHandler = null;
        ServerProfileDto? server = null;
        SessionTabViewModel? source = null;
        TaskCompletionSource<SessionTabViewModel> duplicateAdded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            RunOnStaThread(() =>
            {
                harness = TestHarness.Create();
                sshHandler = harness.GetHandler("SSH");
                server = harness.CreateServer("SSH");
            });

            Assert.NotNull(harness);
            Assert.NotNull(sshHandler);
            Assert.NotNull(server);
            await harness.PersistServerAsync(server);

            RunOnStaThread(() =>
            {
                source = harness.Main.Connection.AddSession(
                    "existing-runtime-session",
                    server.DisplayName,
                    server.ConnectionType);
                source.OriginalServerId = server.Id;
                harness.Main.Connection.ActiveSessions.CollectionChanged += (_, args) =>
                {
                    if (args.NewItems is null)
                    {
                        return;
                    }

                    foreach (object item in args.NewItems)
                    {
                        if (item is SessionTabViewModel added && !ReferenceEquals(added, source))
                        {
                            duplicateAdded.TrySetResult(added);
                        }
                    }
                };

                ContextMenu menu = CreateSessionTabMenu(harness.Main, source);
                MenuItem duplicateItem = AssertMenuItem(
                    menu,
                    harness.Main.Localize("SessionDuplicateTab"));

                duplicateItem.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            });

            Assert.NotNull(source);

            CancellationToken token = await sshHandler.Started.Task.WaitAsync(TestTimeout);
            Assert.False(token.IsCancellationRequested);
            SessionTabViewModel duplicate = await duplicateAdded.Task.WaitAsync(TestTimeout);

            Assert.Contains(source, harness.Main.Connection.ActiveSessions);
            Assert.False(duplicate.IsAdHoc);
            Assert.Equal(server.Id, duplicate.OriginalServerId);

            sshHandler.Result.SetResult(SuccessWithTerminalSession());
            await WaitUntilAsync(() => harness.EmbeddedSessionManager.AttachSshSessionCalls == 1);
        }
        finally
        {
            harness?.Dispose();
        }
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
