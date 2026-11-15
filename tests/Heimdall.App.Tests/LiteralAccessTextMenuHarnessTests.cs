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

using System.Windows.Automation;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The tree and tab list menus show names with underscores literally. These need the shell
/// harness of this class, so they stay in its partial; the helper's own tests are in
/// <see cref="LiteralAccessTextTests"/>.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void TabListMenu_TabTitleWithUnderscore_ShowsItLiterally()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            SessionTabViewModel session = CreateSession("tab-id", "SSH");
            session.Title = "web_front_01";
            harness.Main.Connection.ActiveSessions.Add(session);
            string shown = string.IsNullOrEmpty(session.AccessibleName) ? session.DisplayTitle : session.AccessibleName;
            Assert.Contains('_', shown);

            ContextMenu menu = TabListMenuFactory.Create(harness.Main.Connection);

            MenuItem item = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal(LiteralAccessText.Literal(shown), item.Header);
            Assert.Equal(shown, AutomationProperties.GetName(item));
        });
    }

    [Fact]
    public void FolderContextMenu_MoveTo_FolderNamesWithUnderscoresAreShownLiterally()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            PersistFolders(harness, "Prod", "My_Archive", "My_Archive/Old_Logs");
            FolderViewModel prod = Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.FullPath == "Prod");
            ContextMenuFactory factory = new ContextMenuFactory(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(prod, harness.Main, new RecordingContextMenuCallbacks());

            MenuItem moveTo = AssertMenuItem(menu, harness.Main.Localize("TreeCtxMoveFolderTo"));
            MenuItem branch = AssertMenuItem(moveTo, "My__Archive");
            Assert.Equal("My_Archive", AutomationProperties.GetName(branch));

            string moveIntoTemplate = harness.Main.Localize("TreeCtxMoveIntoFolder");
            MenuItem here = AssertMenuItem(branch, string.Format(moveIntoTemplate, "My__Archive"));
            Assert.Equal(string.Format(moveIntoTemplate, "My_Archive"), AutomationProperties.GetName(here));

            MenuItem child = AssertMenuItem(branch, "Old__Logs");
            Assert.Equal("Old_Logs", AutomationProperties.GetName(child));
        });
    }
}
