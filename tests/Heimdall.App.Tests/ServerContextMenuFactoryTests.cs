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
                new NullContextMenuCallbacks());

            MenuItem bulkEdit = AssertMenuItem(menu, harness.Main.Localize("TreeCtxBulkEditMenu"));
            MenuItem? setGateway = FindChildMenuItem(
                bulkEdit,
                harness.Main.Localize("TreeCtxBulkEditGateway"));
            Assert.NotNull(setGateway);
            Assert.Same(harness.Main.ServerList.BulkEditGatewayCommand, setGateway!.Command);
        });
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
        return factory.CreateTreeContextMenu(server, vm, new NullContextMenuCallbacks());
    }

    private sealed class NullContextMenuCallbacks : IContextMenuCallbacks
    {
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
    }
}
