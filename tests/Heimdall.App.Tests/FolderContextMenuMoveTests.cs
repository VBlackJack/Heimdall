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
/// The folder menu's "Move to" lists what a drop would accept, and nothing else.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void FolderContextMenu_MoveTo_ExcludesItselfItsDescendantsAndDisablesTheCurrentParent()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistFolders(harness, "Prod", "Prod/Linux", "Prod/Linux/Web", "Archive");
            FolderViewModel linux = Assert.Single(
                Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == "Prod").SubFolders);
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(linux, harness.Main, new RecordingContextMenuCallbacks());

            MenuItem moveTo = AssertMenuItem(menu, harness.Main.Localize("TreeCtxMoveFolderTo"));
            MenuItem[] entries = [.. moveTo.Items.OfType<MenuItem>()];
            string[] headers = [.. entries.Select(entry => (string)entry.Header)];
            Assert.Equal(
                [harness.Main.Localize("TreeCtxMoveFolderToTopLevel"), "Archive", "Prod"],
                headers);
            Assert.True(entries[0].IsEnabled);
            Assert.True(entries[1].IsEnabled);
            Assert.False(entries[2].IsEnabled);
        });
    }

    [Fact]
    public void FolderContextMenu_MoveTo_TopLevelIsDisabledForATopLevelFolder()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistFolders(harness, "Prod", "Archive");
            FolderViewModel prod = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == "Prod");
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(prod, harness.Main, new RecordingContextMenuCallbacks());

            MenuItem moveTo = AssertMenuItem(menu, harness.Main.Localize("TreeCtxMoveFolderTo"));
            MenuItem topLevel = Assert.IsType<MenuItem>(moveTo.Items[0]);
            Assert.Equal(harness.Main.Localize("TreeCtxMoveFolderToTopLevel"), topLevel.Header);
            Assert.False(topLevel.IsEnabled);
        });
    }

    /// <summary>One SSH session per path, so each folder exists in the tree.</summary>
    private static void PersistFolders(TestHarness harness, params string[] paths)
    {
        foreach (string path in paths)
        {
            ServerProfileDto server = harness.CreateServer("SSH");
            server.Id = "server-" + path.Replace('/', '-').ToLowerInvariant();
            server.DisplayName = server.Id;
            server.Group = path;
            harness.PersistServerAsync(server).GetAwaiter().GetResult();
        }
    }
}
