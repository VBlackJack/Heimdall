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

using System.IO;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void ServerContextMenu_SshServer_HasAddressSshCommandAndReachability()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));

            ContextMenu menu = CreateServerMenu(harness.Main, serverVm);

            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopyAddress")));
            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopySshCommand")));
            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("TreeCtxTestReachability")));
        });
    }

    [Fact]
    public void ServerContextMenu_RdpServer_OmitsCopySshCommand()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("RDP");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));

            ContextMenu menu = CreateServerMenu(harness.Main, serverVm);

            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopyAddress")));
            Assert.Null(FindMenuItem(menu, harness.Main.Localize("TreeCtxCopySshCommand")));
            Assert.NotNull(FindMenuItem(menu, harness.Main.Localize("TreeCtxTestReachability")));
        });
    }

    [Fact]
    public void ServerContextMenu_WithActiveSession_HasEnabledOpenInSplitWithChildren()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));

            harness.Main.Connection.ActiveSession = new SessionTabViewModel { Title = "Active" };

            ContextMenu menu = CreateServerMenu(harness.Main, serverVm);

            MenuItem split = AssertMenuItem(menu, harness.Main.Localize("TreeCtxOpenInSplit"));
            Assert.True(split.IsEnabled);

            MenuItem? horizontal = FindChildMenuItem(split, harness.Main.Localize("OrientationHorizontal"));
            MenuItem? vertical = FindChildMenuItem(split, harness.Main.Localize("OrientationVertical"));
            Assert.NotNull(horizontal);
            Assert.NotNull(vertical);
            Assert.True(horizontal!.IsEnabled);
            Assert.True(vertical!.IsEnabled);
        });
    }

    [Fact]
    public void ServerContextMenu_NoActiveSession_OpenInSplitDisabled()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));

            Assert.Null(harness.Main.Connection.ActiveSession);

            ContextMenu menu = CreateServerMenu(harness.Main, serverVm);

            MenuItem split = AssertMenuItem(menu, harness.Main.Localize("TreeCtxOpenInSplit"));
            Assert.False(split.IsEnabled);

            MenuItem? horizontal = FindChildMenuItem(split, harness.Main.Localize("OrientationHorizontal"));
            Assert.NotNull(horizontal);
            Assert.False(horizontal!.IsEnabled);
        });
    }

    [Fact]
    public void ServerContextMenu_ConnectAs_SshServer_ListsOtherFourProtocols()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("SSH");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));

            ContextMenu menu = CreateServerMenu(harness.Main, serverVm);

            MenuItem connectAs = AssertMenuItem(menu, harness.Main.Localize("TreeCtxConnectAs"));

            Assert.Equal(4, connectAs.Items.Count);
            Assert.Null(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeSsh")));
            Assert.NotNull(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeRdp")));
            Assert.NotNull(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeSftp")));
            Assert.NotNull(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeVnc")));
            Assert.NotNull(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeTelnet")));
        });
    }

    [Fact]
    public void ServerContextMenu_ConnectAs_RdpServer_OmitsRdp()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("RDP");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                (ServerItemViewModel item) => string.Equals(item.Id, server.Id, StringComparison.Ordinal));

            ContextMenu menu = CreateServerMenu(harness.Main, serverVm);

            MenuItem connectAs = AssertMenuItem(menu, harness.Main.Localize("TreeCtxConnectAs"));

            Assert.Equal(4, connectAs.Items.Count);
            Assert.Null(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeRdp")));
            Assert.NotNull(FindChildMenuItem(connectAs, harness.Main.Localize("ConnectionTypeSsh")));
        });
    }

    [Fact]
    public void ServerContextMenu_MultiSelectBulkEdit_ContainsSetGateway()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto ssh = harness.CreateServer("SSH");
            ServerProfileDto rdp = harness.CreateServer("RDP");
            harness.PersistServerAsync(ssh).GetAwaiter().GetResult();
            harness.PersistServerAsync(rdp).GetAwaiter().GetResult();
            ServerItemViewModel sshVm = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, ssh.Id, StringComparison.Ordinal));
            ServerItemViewModel rdpVm = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, rdp.Id, StringComparison.Ordinal));
            var bulkContext = new BulkSelectionContext([sshVm, rdpVm], rdpVm);
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(
                bulkContext,
                harness.Main,
                new RecordingContextMenuCallbacks());

            MenuItem bulkEdit = AssertMenuItem(menu, harness.Main.Localize("TreeCtxBulkEditMenu"));
            MenuItem? setGateway = FindChildMenuItem(
                bulkEdit,
                harness.Main.Localize("TreeCtxBulkEditGateway"));
            Assert.NotNull(setGateway);
            Assert.Same(harness.Main.ServerList.BulkEditGatewayCommand, setGateway!.Command);
        });
    }

    [Fact]
    public void ServerContextMenu_MultiSelectBulkCredentials_UsesPerActionEligibleCounts()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto[] servers =
            [
                harness.CreateServer("RDP"),
                harness.CreateServer("VNC"),
                harness.CreateServer("TOOL:PING"),
                harness.CreateServer("LOCAL"),
                harness.CreateServer("UNKNOWN"),
                harness.CreateServer("TELNET"),
                harness.CreateServer("CITRIX")
            ];
            foreach (ServerProfileDto server in servers)
            {
                harness.PersistServerAsync(server).GetAwaiter().GetResult();
            }

            ServerItemViewModel[] items = servers
                .Select(server => Assert.Single(
                    harness.Main.ServerList.Servers,
                    item => string.Equals(item.Id, server.Id, StringComparison.Ordinal)))
                .ToArray();
            harness.Main.ServerList.SelectSingle(items[0]);
            foreach (ServerItemViewModel item in items.Skip(1))
            {
                harness.Main.ServerList.ToggleSelection(item);
            }

            var bulkContext = new BulkSelectionContext(items, items[^1]);
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(
                bulkContext,
                harness.Main,
                new RecordingContextMenuCallbacks());

            MenuItem bulkEdit = AssertMenuItem(menu, harness.Main.Localize("TreeCtxBulkEditMenu"));
            string usernameHeader = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                harness.Main.Localize("TreeCtxBulkEditUsername"),
                1);
            string passwordHeader = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                harness.Main.Localize("TreeCtxBulkEditPassword"),
                2);
            MenuItem? username = FindChildMenuItem(bulkEdit, usernameHeader);
            MenuItem? password = FindChildMenuItem(bulkEdit, passwordHeader);

            Assert.NotNull(username);
            Assert.NotNull(password);
            Assert.True(username!.IsEnabled);
            Assert.True(password!.IsEnabled);
            Assert.Same(harness.Main.ServerList.BulkEditUsernameCommand, username.Command);
            Assert.Same(harness.Main.ServerList.BulkEditPasswordCommand, password.Command);
        });
    }

    [Fact]
    public void ServerContextMenu_MultiSelectBulkCredentials_DisablesActionsWithNoEligibleTargets()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto tool = harness.CreateServer("TOOL:PING");
            ServerProfileDto local = harness.CreateServer("LOCAL");
            harness.PersistServerAsync(tool).GetAwaiter().GetResult();
            harness.PersistServerAsync(local).GetAwaiter().GetResult();
            ServerItemViewModel toolVm = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, tool.Id, StringComparison.Ordinal));
            ServerItemViewModel localVm = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, local.Id, StringComparison.Ordinal));
            harness.Main.ServerList.SelectSingle(toolVm);
            harness.Main.ServerList.ToggleSelection(localVm);
            var bulkContext = new BulkSelectionContext([toolVm, localVm], localVm);
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(
                bulkContext,
                harness.Main,
                new RecordingContextMenuCallbacks());

            MenuItem bulkEdit = AssertMenuItem(menu, harness.Main.Localize("TreeCtxBulkEditMenu"));
            string usernameHeader = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                harness.Main.Localize("TreeCtxBulkEditUsername"),
                0);
            string passwordHeader = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                harness.Main.Localize("TreeCtxBulkEditPassword"),
                0);
            MenuItem? username = FindChildMenuItem(bulkEdit, usernameHeader);
            MenuItem? password = FindChildMenuItem(bulkEdit, passwordHeader);

            Assert.NotNull(username);
            Assert.NotNull(password);
            Assert.False(username!.IsEnabled);
            Assert.False(password!.IsEnabled);
        });
    }

    [Fact]
    public void ToolContextMenu_ToolNode_OffersEnabledRenameWithF2AndInvokesCallback()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto tool = harness.CreateServer("TOOL:PING");
            harness.PersistServerAsync(tool).GetAwaiter().GetResult();
            ServerItemViewModel toolVm = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, tool.Id, StringComparison.Ordinal));
            var callbacks = new RecordingContextMenuCallbacks();
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(toolVm, harness.Main, callbacks);

            MenuItem rename = AssertMenuItem(menu, harness.Main.Localize("TreeCtxRename"));
            Assert.True(rename.IsEnabled);
            Assert.Equal("F2", rename.InputGestureText);
            rename.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(toolVm, callbacks.RenamedNode);
            Assert.Equal(
                [
                    harness.Main.Localize("TreeCtxOpenToolInTab"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxMoveToGroup"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxRename"),
                    harness.Main.Localize("TreeCtxRemoveTool")
                ],
                GetTopLevelMenuShape(menu));
        });
    }

    [Fact]
    public void NonToolContextMenus_PreserveServerAndFolderItemOrder()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto server = harness.CreateServer("RDP");
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
            ServerItemViewModel serverVm = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, server.Id, StringComparison.Ordinal));
            var folder = new FolderViewModel { Name = "Ops", FullPath = "Ops" };
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());
            var callbacks = new RecordingContextMenuCallbacks();

            ContextMenu serverMenu =
                factory.CreateTreeContextMenu(serverVm, harness.Main, callbacks);
            ContextMenu folderMenu =
                factory.CreateTreeContextMenu(folder, harness.Main, callbacks);

            Assert.Equal(
                [
                    harness.Main.Localize("TreeCtxConnect"),
                    harness.Main.Localize("MenuItemConnectWith"),
                    harness.Main.Localize("TreeCtxConnectAs"),
                    harness.Main.Localize("TreeCtxOpenInSplit"),
                    harness.Main.Localize("TreeCtxRename"),
                    harness.Main.Localize("TreeCtxEdit"),
                    harness.Main.Localize("TreeCtxDuplicate"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxMoveToGroup"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxCopyHostname"),
                    harness.Main.Localize("TreeCtxCopyUsername"),
                    harness.Main.Localize("TreeCtxCopyAddress"),
                    harness.Main.Localize("TreeCtxTestReachability"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxNotes"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxDelete")
                ],
                GetTopLevelMenuShape(serverMenu));
            Assert.Equal(
                [
                    string.Format(
                        harness.Main.Localize("TreeCtxConnectAllCount"),
                        0),
                    "<separator>",
                    harness.Main.Localize("DialogTitleAddServer"),
                    harness.Main.Localize("TreeCtxNewGroup"),
                    harness.Main.Localize("AddMenuTool"),
                    "<separator>",
                    harness.Main.Localize("TreeCtxRename"),
                    harness.Main.Localize("TreeCtxDeleteGroup")
                ],
                GetTopLevelMenuShape(folderMenu));
        });
    }

    [Fact]
    public void FolderMenu_FilteredProjection_KeepsConnectVisibleButDeleteConfirmsCanonicalScope()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto visibleServer = harness.CreateServer("SSH");
            visibleServer.Id = "visible-server";
            visibleServer.DisplayName = "Visible server";
            visibleServer.Group = "Ops";
            ServerProfileDto nestedServer = harness.CreateServer("RDP");
            nestedServer.Id = "nested-server";
            nestedServer.DisplayName = "Nested server";
            nestedServer.Group = "Ops/Child";
            ServerProfileDto hiddenTool = harness.CreateServer("TOOL:Hosts");
            hiddenTool.Id = "hidden-tool";
            hiddenTool.DisplayName = "Hidden tool";
            hiddenTool.Group = "Ops";
            ServerProfileDto prefixSibling = harness.CreateServer("SFTP");
            prefixSibling.Id = "prefix-sibling";
            prefixSibling.DisplayName = "Prefix sibling";
            prefixSibling.Group = "Ops2";
            harness.PersistServerAsync(visibleServer).GetAwaiter().GetResult();
            harness.PersistServerAsync(nestedServer).GetAwaiter().GetResult();
            harness.PersistServerAsync(hiddenTool).GetAwaiter().GetResult();
            harness.PersistServerAsync(prefixSibling).GetAwaiter().GetResult();
            harness.Main.ConfigManager.MergeSettingAsync(settings =>
            {
                settings.DefaultTheme = "Vesper";
                settings.EmptyGroups = ["Ops", "Ops/Child", "Ops2"];
                settings.TreeExpandedNodes = ["Ops", "Ops/Child", "Ops2"];
                settings.GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Ops"] = new() { SshUsername = "ops-default" },
                    ["Ops/Child"] = new() { SshUsername = "child-default" },
                    ["Ops2"] = new() { SshUsername = "prefix-default" }
                };
            }).GetAwaiter().GetResult();
            AppSettings persistedSettings = harness.Main.ConfigManager
                .LoadSettingsAsync()
                .GetAwaiter()
                .GetResult();
            List<ServerProfileDto> persistedServers = harness.Main.ConfigManager
                .LoadServersAsync()
                .GetAwaiter()
                .GetResult();
            harness.Main.ServerList.LoadServers(persistedServers, persistedSettings);
            ServerItemViewModel visibleItem = Assert.Single(
                harness.Main.ServerList.Servers,
                item => string.Equals(item.Id, visibleServer.Id, StringComparison.Ordinal));
            FolderViewModel filteredFolder = new()
            {
                Name = "Ops",
                FullPath = "Ops"
            };
            filteredFolder.Servers.Add(visibleItem);
            ContextMenuFactory factory = new(new ExternalToolProviderService());
            ContextMenu menu = factory.CreateTreeContextMenu(
                filteredFolder,
                harness.Main,
                new RecordingContextMenuCallbacks());
            string expectedConnectHeader = string.Format(
                harness.Main.Localize("TreeCtxConnectAllCount"),
                1);
            MenuItem connectAll = AssertMenuItem(menu, expectedConnectHeader);
            harness.DialogService.ConfirmResult = false;
            List<(string Id, string? Group)> beforeRefusal = harness.Main.ConfigManager
                .LoadServersAsync()
                .GetAwaiter()
                .GetResult()
                .OrderBy(server => server.Id, StringComparer.Ordinal)
                .Select(server => (server.Id, server.Group))
                .ToList();
            byte[] settingsBeforeRefusal = File.ReadAllBytes(
                harness.Main.ConfigManager.SettingsPath);
            byte[] serversBeforeRefusal = File.ReadAllBytes(
                harness.Main.ConfigManager.ServersPath);

            MenuItem deleteGroup = AssertMenuItem(
                menu,
                harness.Main.Localize("TreeCtxDeleteGroup"));
            deleteGroup.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

            Assert.True(connectAll.IsEnabled);
            Assert.Equal(expectedConnectHeader, Assert.IsType<string>(connectAll.Header));
            Assert.Equal(1, harness.DialogService.ConfirmCallCount);
            Assert.Equal("warning", harness.DialogService.LastConfirmSeverity);
            string confirmation = Assert.IsType<string>(harness.DialogService.LastConfirmMessage);
            Assert.Contains("{1}", harness.Main.Localize("TreeCtxDeleteGroupConfirm"), StringComparison.Ordinal);
            Assert.Contains("3", confirmation, StringComparison.Ordinal);
            Assert.Equal(
                string.Format(
                    harness.Main.Localize("TreeCtxDeleteGroupConfirm"),
                    filteredFolder.Name,
                    3),
                confirmation);
            List<(string Id, string? Group)> afterRefusal = harness.Main.ConfigManager
                .LoadServersAsync()
                .GetAwaiter()
                .GetResult()
                .OrderBy(server => server.Id, StringComparer.Ordinal)
                .Select(server => (server.Id, server.Group))
                .ToList();
            Assert.Equal(beforeRefusal, afterRefusal);
            Assert.Equal(
                settingsBeforeRefusal,
                File.ReadAllBytes(harness.Main.ConfigManager.SettingsPath));
            Assert.Equal(
                serversBeforeRefusal,
                File.ReadAllBytes(harness.Main.ConfigManager.ServersPath));
        });
    }

    // A1 of BL-0094, and it crosses the junction deliberately: a command that persists and
    // a menu item that exists can both be green while nothing binds one to the other - the
    // shape that left the SFTP close guard attached to no host.
    //
    // The binding is asserted by IDENTITY and the command is deliberately not executed
    // here. RunOnStaThread installs no Dispatcher and no SynchronizationContext, so an
    // awaited command resumes on the thread pool, and the MenuItem still subscribed to its
    // CanExecuteChanged is then touched from the wrong thread - which killed the entire
    // test host, passing in isolation and crashing the full run. Production invokes it on
    // the UI dispatcher, where the continuation comes back. What the command DOES is
    // measured off-STA by AddGatewayOutsidePanel_PersistsImmediatelyAndLeavesThePanelClean.
    [Fact]
    public void EmptyAreaContextMenu_AddGateway_IsBoundToTheCommandThatPersists()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();

            ContextMenuFactory factory = new(new ExternalToolProviderService());
            ContextMenu menu = factory.CreateTreeContextMenu(
                null,
                harness.Main,
                new RecordingContextMenuCallbacks());

            MenuItem addGateway = Assert.IsType<MenuItem>(
                FindMenuItem(menu, harness.Main.Localize("BtnAddGateway")));
            Assert.Same(harness.Main.Settings.AddGatewayOutsidePanelCommand, addGateway.Command);
        });
    }

    private static string[] GetTopLevelMenuShape(ContextMenu menu)
    {
        return menu.Items
            .Cast<object>()
            .Select(item => item switch
            {
                Separator => "<separator>",
                MenuItem menuItem => Assert.IsType<string>(menuItem.Header),
                _ => throw new InvalidOperationException(
                    $"Unexpected context menu item type: {item.GetType().FullName}")
            })
            .ToArray();
    }

    private static MenuItem? FindChildMenuItem(MenuItem parent, string header)
    {
        foreach (object raw in parent.Items)
        {
            if (raw is MenuItem menuItem
                && menuItem.Header is string itemHeader
                && string.Equals(itemHeader, header, StringComparison.Ordinal))
            {
                return menuItem;
            }
        }

        return null;
    }

    private static ContextMenu CreateServerMenu(MainViewModel vm, ServerItemViewModel server)
    {
        ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());
        return factory.CreateTreeContextMenu(server, vm, new RecordingContextMenuCallbacks());
    }

    private sealed class RecordingContextMenuCallbacks : IContextMenuCallbacks
    {
        public object? RenamedNode { get; private set; }

        public void OpenNotesForServer(ServerItemViewModel server, NoteTemplateKind templateKind)
        {
        }

        public void LaunchExternalTool(ServerItemViewModel server, ExternalToolDefinition tool)
        {
        }

        public void LaunchDetectedTool(ServerItemViewModel server, ExternalToolInfo tool)
        {
        }

        public void AddToolFromMenu(string? group)
        {
        }

        public void BeginInlineRename(object node)
        {
            RenamedNode = node;
        }
    }
}
