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
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The context menus of shared rows and folders: what connects stays, what writes is gone or
/// disabled with its reason.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private static readonly string[] SharedRowWriteKeys =
    [
        "TreeCtxRename",
        "TreeCtxEdit",
        "TreeCtxDuplicate",
        "TreeCtxFavoriteAdd",
        "TreeCtxFavoriteRemove",
        "TreeCtxMoveToGroup",
        "TreeCtxDelete",
    ];

    [Fact]
    public void SharedRowMenu_KeepsConnectAndCopy_AndLeavesOutEveryWrite()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateSharedHarness(out _);
            ServerItemViewModel local = PersistLocal(harness);
            ServerItemViewModel shared = SharedRowOf(harness);

            ContextMenu sharedMenu = CreateServerMenu(harness.Main, shared);
            ContextMenu localMenu = CreateServerMenu(harness.Main, local);

            // The control: every write the shared menu lacks is on a local row's menu.
            foreach (string key in SharedRowWriteKeys.Where(key => key != "TreeCtxFavoriteRemove"))
            {
                Assert.NotNull(FindMenuItem(localMenu, harness.Main.Localize(key)));
            }

            foreach (string key in SharedRowWriteKeys)
            {
                Assert.Null(FindMenuItem(sharedMenu, harness.Main.Localize(key)));
            }

            Assert.NotNull(FindMenuItem(sharedMenu, harness.Main.Localize("TreeCtxConnect")));
            Assert.NotNull(FindMenuItem(sharedMenu, harness.Main.Localize("TreeCtxOpenInSplit")));
            Assert.NotNull(FindMenuItem(sharedMenu, harness.Main.Localize("TreeCtxCopyHostname")));
            Assert.NotNull(FindMenuItem(sharedMenu, harness.Main.Localize("TreeCtxTestReachability")));
            MenuItem notice = AssertMenuItem(sharedMenu, shared.SharedReadOnlyText);
            Assert.False(notice.IsEnabled);
        });
    }

    [Fact]
    public void SharedFolderMenu_OffersConnectAllAndNothingElse()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateSharedHarness(out _);
            PersistLocal(harness);
            FolderViewModel sharedRoot = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.IsSharedRoot);
            FolderViewModel sharedFolder = Assert.Single(sharedRoot.SubFolders);
            FolderViewModel localFolder = Assert.Single(harness.Main.ServerList.GroupedServers, folder => !folder.IsShared);
            ContextMenuFactory factory = new(new ExternalToolProviderService());

            ContextMenu sharedMenu = factory.CreateTreeContextMenu(sharedFolder, harness.Main, new RecordingContextMenuCallbacks());
            ContextMenu localMenu = factory.CreateTreeContextMenu(localFolder, harness.Main, new RecordingContextMenuCallbacks());

            string[] folderWrites =
            [
                harness.Main.Localize("DialogTitleAddServer"),
                harness.Main.Localize("TreeCtxNewGroup"),
                harness.Main.Localize("TreeCtxRename"),
                harness.Main.Localize("TreeCtxDeleteGroup"),
            ];
            foreach (string header in folderWrites)
            {
                Assert.NotNull(FindMenuItem(localMenu, header));
                Assert.Null(FindMenuItem(sharedMenu, header));
            }

            MenuItem connectAll = AssertMenuItem(
                sharedMenu,
                string.Format(harness.Main.Localize("TreeCtxConnectAllCount"), 2));
            Assert.True(connectAll.IsEnabled);
            Assert.Equal(2, sharedMenu.Items.OfType<MenuItem>().Count());
        });
    }

    [Fact]
    public void BulkMenu_WithASharedRow_DisablesTheWritesWithTheReason_AndKeepsConnect()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateSharedHarness(out _);
            ServerItemViewModel local = PersistLocal(harness);
            ServerItemViewModel shared = SharedRowOf(harness);
            ServerListViewModel list = harness.Main.ServerList;
            list.SelectSingle(local);
            list.ToggleSelection(shared);
            var bulkContext = new BulkSelectionContext([local, shared], shared);
            ContextMenuFactory factory = new(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(bulkContext, harness.Main, new RecordingContextMenuCallbacks());

            string reason = list.DescribeSharedRefusal(1, 2);
            MenuItem connect = AssertMenuItem(menu, string.Format(harness.Main.Localize("TreeCtxConnectSelected"), 2));
            Assert.True(connect.IsEnabled);
            foreach (string header in new[]
            {
                harness.Main.Localize("TreeCtxBulkDuplicate"),
                harness.Main.Localize("TreeCtxBulkEditMenu"),
                harness.Main.Localize("TreeCtxFavoriteAdd"),
                string.Format(harness.Main.Localize("TreeCtxDeleteSelected"), 2),
            })
            {
                MenuItem item = AssertMenuItem(menu, header);
                Assert.False(item.IsEnabled, header);
                Assert.Equal(reason, item.ToolTip);
            }

            Assert.False(AssertMenuItem(menu, reason).IsEnabled);
        });
    }

    private static TestHarness CreateSharedHarness(out StubSharedSessionProvider provider)
    {
        SharedSessionTestData data = SharedSessionTestData.Create();
        StubSharedSessionProvider created = new(
            [data.Profile, data.DirectProfile],
            [data.Gateway],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, "Team", 2)]);
        provider = created;
        return TestHarness.Create(profileLookupFactory: configManager => new ProfileLookup(configManager, created));
    }

    private static ServerItemViewModel PersistLocal(TestHarness harness)
    {
        ServerProfileDto server = harness.CreateServer("SSH");
        server.Group = SharedSessionTestData.SharedFolder;
        harness.PersistServerAsync(server).GetAwaiter().GetResult();
        return Assert.Single(harness.Main.ServerList.Servers, item => item.Id == server.Id);
    }

    private static ServerItemViewModel SharedRowOf(TestHarness harness) =>
        Assert.Single(
            harness.Main.ServerList.Servers,
            item => item.IsShared && !string.IsNullOrEmpty(item.Username));
}
