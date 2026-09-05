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
/// The folder menu offers the badge palette and a way back to the themed default.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void FolderContextMenu_OffersThePaletteAndNone_WithTheCurrentOneChecked()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto ssh = harness.CreateServer("SSH");
            ssh.Group = "ops";
            harness.Main.ConfigManager.MergeSettingAsync(settings =>
                settings.GroupDefaults["ops"] = new GroupDefaultsDto { Color = "#EF4444" })
                .GetAwaiter().GetResult();
            harness.PersistServerAsync(ssh).GetAwaiter().GetResult();
            FolderViewModel ops = Assert.Single(
                harness.Main.ServerList.GroupedServers,
                folder => folder.FullPath == "ops");
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(ops, harness.Main, new RecordingContextMenuCallbacks());

            MenuItem colour = AssertMenuItem(menu, harness.Main.Localize("TreeCtxFolderColor"));
            MenuItem[] entries = [.. colour.Items.OfType<MenuItem>()];
            Assert.Equal(BadgeColorPalette.Entries.Count + 1, entries.Length);
            Assert.Single(colour.Items.OfType<Separator>());
            MenuItem red = Assert.Single(entries, item => string.Equals((string?)item.Tag, "#EF4444", StringComparison.Ordinal));
            Assert.True(red.IsChecked);
            Assert.NotNull(red.Icon);
            MenuItem none = Assert.Single(entries, item => item.Tag is null);
            Assert.Equal(harness.Main.Localize("TreeCtxFolderColorNone"), none.Header);
            Assert.False(none.IsChecked);
            Assert.Null(none.Icon);
        });
    }

    [Fact]
    public void FolderContextMenu_NoGroupFolder_HasNoColourEntry()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerProfileDto ssh = harness.CreateServer("SSH");
            harness.PersistServerAsync(ssh).GetAwaiter().GetResult();
            FolderViewModel noGroup = Assert.Single(
                harness.Main.ServerList.GroupedServers,
                folder => folder.FullPath.Length == 0);
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(noGroup, harness.Main, new RecordingContextMenuCallbacks());

            Assert.DoesNotContain(
                harness.Main.Localize("TreeCtxFolderColor"),
                menu.Items.OfType<MenuItem>().Select(item => item.Header as string));
        });
    }
}
