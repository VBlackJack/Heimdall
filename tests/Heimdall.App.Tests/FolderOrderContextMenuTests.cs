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

using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The "Move up" and "Move down" entries of folder and session menus.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const int MoveUpDelta = -1;

    private const int MoveDownDelta = 1;

    [Fact]
    public void FolderMenu_OffersMoveUpAndDown_DisabledAtTheEdges_AndRoutesTheClick()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistFolders(harness, "Prod", "Archive", "Lab");
            ContextMenuFactory factory = new(new ExternalToolProviderService());
            RecordingContextMenuCallbacks callbacks = new();
            FolderViewModel archive = TopLevelFolder(harness, "Archive");
            FolderViewModel prod = TopLevelFolder(harness, "Prod");
            FolderViewModel lab = TopLevelFolder(harness, "Lab");

            ContextMenu first = factory.CreateTreeContextMenu(archive, harness.Main, callbacks);
            ContextMenu middle = factory.CreateTreeContextMenu(lab, harness.Main, callbacks);
            ContextMenu last = factory.CreateTreeContextMenu(prod, harness.Main, callbacks);

            Assert.False(AssertMenuItem(first, harness.Main.Localize("TreeCtxMoveUp")).IsEnabled);
            Assert.True(AssertMenuItem(first, harness.Main.Localize("TreeCtxMoveDown")).IsEnabled);
            Assert.True(AssertMenuItem(middle, harness.Main.Localize("TreeCtxMoveUp")).IsEnabled);
            Assert.True(AssertMenuItem(middle, harness.Main.Localize("TreeCtxMoveDown")).IsEnabled);
            Assert.True(AssertMenuItem(last, harness.Main.Localize("TreeCtxMoveUp")).IsEnabled);
            Assert.False(AssertMenuItem(last, harness.Main.Localize("TreeCtxMoveDown")).IsEnabled);

            MenuItem moveDown = AssertMenuItem(middle, harness.Main.Localize("TreeCtxMoveDown"));
            moveDown.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal((lab, MoveDownDelta), Assert.Single(callbacks.Nudges));
            Assert.Equal(harness.Main.Localize("TreeCtxGestureMoveDown"), moveDown.InputGestureText);
        });
    }

    [Fact]
    public void SessionMenu_OffersMoveUpAndDown_DisabledAtTheEdges_AndRoutesTheClick()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            foreach (string name in new[] { "aaa", "bbb", "ccc" })
            {
                PersistOrdered(harness, name);
            }

            // Persisting a session rebuilds the rows, so they are looked up once all exist.
            ServerItemViewModel first = OrderedRow(harness, "aaa");
            ServerItemViewModel second = OrderedRow(harness, "bbb");
            ServerItemViewModel third = OrderedRow(harness, "ccc");
            ContextMenuFactory factory = new(new ExternalToolProviderService());
            RecordingContextMenuCallbacks callbacks = new();

            ContextMenu firstMenu = factory.CreateTreeContextMenu(first, harness.Main, callbacks);
            ContextMenu secondMenu = factory.CreateTreeContextMenu(second, harness.Main, callbacks);
            ContextMenu thirdMenu = factory.CreateTreeContextMenu(third, harness.Main, callbacks);

            Assert.False(AssertMenuItem(firstMenu, harness.Main.Localize("TreeCtxMoveUp")).IsEnabled);
            Assert.True(AssertMenuItem(firstMenu, harness.Main.Localize("TreeCtxMoveDown")).IsEnabled);
            Assert.True(AssertMenuItem(secondMenu, harness.Main.Localize("TreeCtxMoveUp")).IsEnabled);
            Assert.True(AssertMenuItem(secondMenu, harness.Main.Localize("TreeCtxMoveDown")).IsEnabled);
            Assert.True(AssertMenuItem(thirdMenu, harness.Main.Localize("TreeCtxMoveUp")).IsEnabled);
            Assert.False(AssertMenuItem(thirdMenu, harness.Main.Localize("TreeCtxMoveDown")).IsEnabled);

            AssertMenuItem(secondMenu, harness.Main.Localize("TreeCtxMoveUp"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal((second, MoveUpDelta), Assert.Single(callbacks.Nudges));
        });
    }

    [Fact]
    public void SharedRowsAndFolders_OfferNoMoveEntries_WhereLocalOnesDo()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateSharedHarness(out _);
            ServerItemViewModel local = PersistLocal(harness);
            ServerItemViewModel shared = SharedRowOf(harness);
            FolderViewModel sharedRoot = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.IsSharedRoot);
            FolderViewModel sharedFolder = Assert.Single(sharedRoot.SubFolders);
            FolderViewModel localFolder = Assert.Single(harness.Main.ServerList.GroupedServers, folder => !folder.IsShared);
            ContextMenuFactory factory = new(new ExternalToolProviderService());

            Assert.NotNull(FindMenuItem(CreateServerMenu(harness.Main, local), harness.Main.Localize("TreeCtxMoveUp")));
            Assert.NotNull(FindMenuItem(
                factory.CreateTreeContextMenu(localFolder, harness.Main, new RecordingContextMenuCallbacks()),
                harness.Main.Localize("TreeCtxMoveUp")));

            foreach (string key in new[] { "TreeCtxMoveUp", "TreeCtxMoveDown" })
            {
                string header = harness.Main.Localize(key);
                Assert.Null(FindMenuItem(CreateServerMenu(harness.Main, shared), header));
                Assert.Null(FindMenuItem(
                    factory.CreateTreeContextMenu(sharedFolder, harness.Main, new RecordingContextMenuCallbacks()),
                    header));
                Assert.Null(FindMenuItem(
                    factory.CreateTreeContextMenu(sharedRoot, harness.Main, new RecordingContextMenuCallbacks()),
                    header));
            }

            Assert.False(harness.Main.ServerList.CanNudgeServer(shared, MoveDownDelta));
            Assert.False(harness.Main.ServerList.CanNudgeFolder(sharedFolder, MoveDownDelta));
        });
    }

    private static FolderViewModel TopLevelFolder(TestHarness harness, string path) =>
        Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == path);

    /// <summary>A session in one local folder, named so the alphabetical order is the creation order.</summary>
    private static void PersistOrdered(TestHarness harness, string name)
    {
        ServerProfileDto server = harness.CreateServer("SSH");
        server.Id = "ordered-" + name;
        server.DisplayName = name;
        server.Group = "Ordered";
        harness.PersistServerAsync(server).GetAwaiter().GetResult();
    }

    private static ServerItemViewModel OrderedRow(TestHarness harness, string name) =>
        Assert.Single(harness.Main.ServerList.Servers, item => item.Id == "ordered-" + name);
}
