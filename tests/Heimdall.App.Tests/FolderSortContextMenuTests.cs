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
/// The "Sort A to Z" entries of the folder, "(No Folder)" and empty-area menus.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void FolderMenu_OffersSortAlphabetically_WhenArranged_AndRoutesTheClick()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistSortSession(harness, "arranged", "Arranged", sortOrder: 10);
            PersistSortSession(harness, "plain", "Plain", sortOrder: 0);
            ContextMenuFactory factory = new(new ExternalToolProviderService());
            SortRecordingCallbacks callbacks = new();
            FolderViewModel arranged = TopLevelFolder(harness, "Arranged");
            string header = harness.Main.Localize("TreeCtxSortAlphabetically");

            MenuItem item = AssertMenuItem(factory.CreateTreeContextMenu(arranged, harness.Main, callbacks), header);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(arranged, Assert.Single(callbacks.SortedFolders));
        });
    }

    [Fact]
    public void FolderMenu_SortAlphabetically_IsAbsent_WhenNothingWasArranged()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistSortSession(harness, "plain", "Plain", sortOrder: 0);
            ContextMenuFactory factory = new(new ExternalToolProviderService());

            Assert.Null(FindMenuItem(
                factory.CreateTreeContextMenu(TopLevelFolder(harness, "Plain"), harness.Main, new SortRecordingCallbacks()),
                harness.Main.Localize("TreeCtxSortAlphabetically")));
        });
    }

    [Fact]
    public void NoFolderMenu_OffersSortAlphabetically_AndSharedFoldersDoNot()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateSharedHarness(out _);
            ServerProfileDto root = harness.CreateServer("SSH");
            root.Id = "sort-root";
            root.DisplayName = "Root";
            root.Group = "";
            root.SortOrder = 10;
            harness.PersistServerAsync(root).GetAwaiter().GetResult();
            ContextMenuFactory factory = new(new ExternalToolProviderService());
            SortRecordingCallbacks callbacks = new();
            string header = harness.Main.Localize("TreeCtxSortAlphabetically");
            FolderViewModel noFolder = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == "");
            FolderViewModel sharedRoot = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.IsSharedRoot);
            FolderViewModel sharedFolder = Assert.Single(sharedRoot.SubFolders);

            MenuItem item = AssertMenuItem(factory.CreateTreeContextMenu(noFolder, harness.Main, callbacks), header);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(noFolder, Assert.Single(callbacks.SortedFolders));
            Assert.Null(FindMenuItem(factory.CreateTreeContextMenu(sharedFolder, harness.Main, callbacks), header));
            Assert.Null(FindMenuItem(factory.CreateTreeContextMenu(sharedRoot, harness.Main, callbacks), header));
        });
    }

    [Fact]
    public void EmptyAreaMenu_OffersSortTopLevelAlphabetically_OnlyWhenTopLevelWasArranged()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistSortSession(harness, "alpha", "Alpha", sortOrder: 0);
            PersistSortSession(harness, "beta", "Beta", sortOrder: 0);
            ContextMenuFactory factory = new(new ExternalToolProviderService());
            SortRecordingCallbacks callbacks = new();
            string header = harness.Main.Localize("TreeCtxSortTopLevelAlphabetically");

            Assert.Null(FindMenuItem(factory.CreateTreeContextMenu(null, harness.Main, callbacks), header));

            harness.Main.ConfigManager.MergeSettingAsync(settings => settings.FolderOrder = ["Beta", "Alpha"])
                .GetAwaiter().GetResult();
            harness.Main.ServerList.LoadServers(
                harness.Main.ConfigManager.LoadServersAsync().GetAwaiter().GetResult(),
                harness.Main.ConfigManager.LoadSettingsAsync().GetAwaiter().GetResult());

            MenuItem arranged = AssertMenuItem(factory.CreateTreeContextMenu(null, harness.Main, callbacks), header);
            arranged.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, callbacks.TopLevelSorts);
        });
    }

    private static void PersistSortSession(TestHarness harness, string id, string group, int sortOrder)
    {
        ServerProfileDto server = harness.CreateServer("SSH");
        server.Id = "sort-" + id;
        server.DisplayName = id;
        server.Group = group;
        server.SortOrder = sortOrder;
        harness.PersistServerAsync(server).GetAwaiter().GetResult();
    }

    /// <summary>Records which sort the menu asked for, and nothing else.</summary>
    private sealed class SortRecordingCallbacks : IContextMenuCallbacks
    {
        public List<FolderViewModel> SortedFolders { get; } = [];

        public int TopLevelSorts { get; private set; }

        public Task SortFolderAlphabeticallyAsync(FolderViewModel folder)
        {
            SortedFolders.Add(folder);
            return Task.CompletedTask;
        }

        public Task SortTopLevelAlphabeticallyAsync()
        {
            TopLevelSorts++;
            return Task.CompletedTask;
        }

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
