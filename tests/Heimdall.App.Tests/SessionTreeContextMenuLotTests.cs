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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The session tree's context menus after the 2026-09-30 lot: localized gestures, explained
/// disabled entries, the favorites toggle, nested folder pickers and a folder deletion that
/// reports its failure.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void ServerMenu_GesturesComeFromTheLocale_AndDeleteShowsTheKeyThatWorks()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerItemViewModel server = PersistSession(harness, "alpha", "Ops");

            ContextMenu menu = CreateServerMenu(harness.Main, server);

            Assert.Equal(
                harness.Main.Localize("TreeCtxGestureRename"),
                AssertMenuItem(menu, harness.Main.Localize("TreeCtxRename")).InputGestureText);
            Assert.Equal(
                harness.Main.Localize("TreeCtxGestureEdit"),
                AssertMenuItem(menu, harness.Main.Localize("TreeCtxEdit")).InputGestureText);
            Assert.Equal(
                "Del",
                AssertMenuItem(menu, harness.Main.Localize("TreeCtxDelete")).InputGestureText);
        });
    }

    [Fact]
    public void ServerMenu_DisabledEntriesSayWhy()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            ServerItemViewModel server = PersistSession(harness, "alpha", "Ops");
            Assert.True(string.IsNullOrWhiteSpace(server.Username));

            ContextMenu menu = CreateServerMenu(harness.Main, server);

            MenuItem copyUsername = AssertMenuItem(menu, harness.Main.Localize("TreeCtxCopyUsername"));
            Assert.False(copyUsername.IsEnabled);
            Assert.Equal(harness.Main.Localize("TreeCtxCopyUsernameDisabledReason"), copyUsername.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(copyUsername));

            MenuItem split = AssertMenuItem(menu, harness.Main.Localize("TreeCtxOpenInSplit"));
            Assert.False(split.IsEnabled);
            Assert.Equal(harness.Main.Localize("TreeCtxOpenInSplitDisabledReason"), split.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(split));
        });
    }

    [Fact]
    public void ServerMenu_MoveToFolder_NestsFoldersLikeTheTree()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistSession(harness, "alpha", "Prod/Linux");
            PersistSession(harness, "beta", "Prod/Windows");
            PersistSession(harness, "gamma", "Archive");
            ServerItemViewModel server = Session(harness, "alpha");

            MenuItem moveTo = AssertMenuItem(
                CreateServerMenu(harness.Main, server),
                harness.Main.Localize("TreeCtxMoveToGroup"));

            string[] topLevel = [.. moveTo.Items.OfType<MenuItem>().Select(item => (string)item.Header)];
            Assert.Equal([harness.Main.Localize("TreeNodeNoGroup"), "Archive", "Prod"], topLevel);

            MenuItem prod = moveTo.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Prod");
            string[] inProd = [.. prod.Items.OfType<MenuItem>().Select(item => (string)item.Header)];
            Assert.Equal(
                [string.Format(harness.Main.Localize("TreeCtxMoveIntoFolder"), "Prod"), "Linux", "Windows"],
                inProd);

            MenuItem linux = prod.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Linux");
            Assert.False(linux.IsEnabled);
            ServerMoveToGroupRequest request = Assert.IsType<ServerMoveToGroupRequest>(
                prod.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Windows").CommandParameter);
            Assert.Equal("Prod/Windows", request.GroupName);
        });
    }

    [Fact]
    public void DeleteFolderFromMenu_ReportsAFailureInsteadOfThrowing()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistSession(harness, "alpha", "Ops");
            FolderViewModel ops = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == "Ops");
            harness.DialogService.ConfirmResult = true;

            ContextMenuFactory.DeleteFolderFromMenuAsync(
                    harness.Main,
                    ops,
                    _ => throw new IOException("settings.json is locked"))
                .GetAwaiter()
                .GetResult();

            Assert.Equal(
                string.Format(harness.Main.Localize("StatusFolderDeleteFailed"), "Ops"),
                harness.Main.StatusText);
        });
    }

    [Fact]
    public void DeleteFolderFromMenu_WithdrawsTheUndoOfTheChangeBeforeIt()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistSession(harness, "alpha", "Ops");
            PersistSession(harness, "beta", "Lab");
            Assert.True(harness.Main.ServerList
                .MoveServersToGroupAsync([Session(harness, "alpha")], "Lab")
                .GetAwaiter()
                .GetResult() > 0);
            Assert.True(harness.Main.ServerList.CanUndoTreeOrganization);
            FolderViewModel lab = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == "Lab");
            harness.DialogService.ConfirmResult = true;

            ContextMenuFactory.DeleteFolderFromMenuAsync(
                    harness.Main,
                    lab,
                    path => new FolderDeletionService(harness.Main.ConfigManager).DeleteAsync(
                        path,
                        harness.Main.ServerList.FlushExpandStateForCloseAsync))
                .GetAwaiter()
                .GetResult();

            Assert.False(harness.Main.ServerList.CanUndoTreeOrganization);
        });
    }

    [Fact]
    public void CreateFolderFromMenu_ReportsAFailureInsteadOfThrowing()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            harness.DialogService.InputAnswer = () => throw new IOException("the dialog failed");

            ContextMenuFactory.CreateFolderFromMenuAsync(harness.Main, parentPath: null)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(harness.Main.Localize("StatusFolderCreateFailed"), harness.Main.StatusText);
        });
    }

    private static ServerItemViewModel PersistSession(TestHarness harness, string id, string group)
    {
        ServerProfileDto server = harness.CreateServer("SSH");
        server.Id = id;
        server.DisplayName = id;
        server.Group = group;
        server.SshUsername = null;
        harness.PersistServerAsync(server).GetAwaiter().GetResult();
        return Session(harness, id);
    }

    /// <summary>The current row of a session: every persist reloads the list and replaces it.</summary>
    private static ServerItemViewModel Session(TestHarness harness, string id) =>
        Assert.Single(
            harness.Main.ServerList.AllServers,
            item => string.Equals(item.Id, id, StringComparison.Ordinal));
}
