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
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Where the import report of a shared source shows: the issue count on its root, the root's
/// context menu, and the status line of a reload that changes the count.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string OtherSourceId = "othersrc1";

    [Fact]
    public void SharedRoot_ShowsItsIssueCount_OnlyWhenTheReportFoundSomething()
    {
        using TestHarness harness = CreateReportHarness(out _, out _);

        FolderViewModel reported = SharedRoot(harness, SharedSessionTestData.SourceId);
        FolderViewModel clean = SharedRoot(harness, OtherSourceId);

        Assert.True(reported.HasIssues);
        Assert.Equal(2, reported.IssueCount);
        Assert.Equal(harness.Main.GetLocalizer().FormatCount(2, "SessionTreeSharedRootIssuesOne", "SessionTreeSharedRootIssues", 2), reported.IssueBadgeText);
        Assert.Equal("2 issues", reported.IssueBadgeText);
        Assert.False(clean.HasIssues);
        Assert.Equal(string.Empty, clean.IssueBadgeText);
    }

    [Fact]
    public void SharedRootMenu_ShowImportReport_OpensTheReportOfThatSource()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateReportHarness(out SharedSessionImportReport report, out _);
            FolderViewModel root = SharedRoot(harness, SharedSessionTestData.SourceId);
            ContextMenuFactory factory = new(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(root, harness.Main, new RecordingContextMenuCallbacks());
            MenuItem item = AssertMenuItem(menu, harness.Main.Localize("TreeCtxShowImportReport"));
            Assert.True(item.IsEnabled);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            var shown = Assert.Single(harness.DialogService.SharedReports);
            Assert.Same(report, shown.Report);

            // A sub-folder of the source has no report of its own.
            ContextMenu folderMenu = factory.CreateTreeContextMenu(Assert.Single(root.SubFolders), harness.Main, new RecordingContextMenuCallbacks());
            Assert.Null(FindMenuItem(folderMenu, harness.Main.Localize("TreeCtxShowImportReport")));
        });
    }

    [Fact]
    public void SharedRootMenu_ForASourceThatCouldNotBeRead_DisablesTheReportWithTheReason()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = CreateReportHarness(out _, out _, failOther: true);
            FolderViewModel broken = SharedRoot(harness, OtherSourceId);
            ContextMenuFactory factory = new(new ExternalToolProviderService());

            ContextMenu menu = factory.CreateTreeContextMenu(broken, harness.Main, new RecordingContextMenuCallbacks());

            MenuItem item = AssertMenuItem(menu, harness.Main.Localize("TreeCtxShowImportReport"));
            Assert.False(item.IsEnabled);
            Assert.Equal(broken.ErrorMessage, item.ToolTip);
        });
    }

    [Fact]
    public void ReloadStatus_MentionsTheIssueCount_OnlyWhenItChanged()
    {
        using TestHarness harness = CreateReportHarness(out _, out SharedSessionSourceState state);
        ServerListViewModel list = harness.Main.ServerList;

        string changed = list.DescribeSharedSourceReload(new SharedSessionSourceReload(state, PreviousIssueCount: 5));
        string unchanged = list.DescribeSharedSourceReload(new SharedSessionSourceReload(state, PreviousIssueCount: 2));

        Assert.Equal("Shared sessions updated: Team, 2 sessions. Import report: 2 issues (was 5).", changed);
        Assert.Equal("Shared sessions updated: Team, 2 sessions", unchanged);
    }

    private static TestHarness CreateReportHarness(
        out SharedSessionImportReport report,
        out SharedSessionSourceState state,
        bool failOther = false)
    {
        SharedSessionTestData data = SharedSessionTestData.Create();
        report = new SharedSessionImportReport(
            SharedSessionTestData.SourceId,
            "Team",
            [
                new SharedSessionReportEntry(SharedSessionReportCheck.NameHostMismatch, SharedSessionTestData.SharedFolder, "app02", "app03.example.test"),
                new SharedSessionReportEntry(SharedSessionReportCheck.GatewayOutlier, SharedSessionTestData.SharedFolder, "app04", "gw2", "gw1"),
            ]);
        state = StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, "Team", 2) with { Report = report };
        SharedSessionSourceState other = failOther
            ? StubSharedSessionProvider.Failed(OtherSourceId, "Other", "The file was not found.")
            : StubSharedSessionProvider.Loaded(OtherSourceId, "Other", 0) with { Report = SharedSessionImportReport.Empty(OtherSourceId, "Other") };
        StubSharedSessionProvider provider = new([data.Profile, data.DirectProfile], [data.Gateway], [state, other]);

        TestHarness harness = TestHarness.Create(profileLookupFactory: configManager => new ProfileLookup(configManager, provider));
        harness.Main.ServerList.LoadServers([], new AppSettings());
        return harness;
    }

    private static FolderViewModel SharedRoot(TestHarness harness, string sourceId) =>
        Assert.Single(harness.Main.ServerList.GroupedServers, folder => folder.IsSharedRoot && folder.SharedSourceId == sourceId);
}
