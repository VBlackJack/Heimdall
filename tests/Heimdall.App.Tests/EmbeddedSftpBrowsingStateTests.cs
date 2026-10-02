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

using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// What the browser says about its listing: why a navigation failed, why the list is empty, what
/// stays selected when the list is rebuilt, and how the columns sort.
/// </summary>
public sealed class EmbeddedSftpBrowsingStateTests
{
    // ------------------------------------------------------------------
    // A failed navigation is not a failed transfer
    // ------------------------------------------------------------------

    [Fact]
    public async Task Navigating_ToAPathThatDoesNotExist_NamesThePathAndKeepsThePreviousFolderInThePathBar()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/home", SftpTestKit.File("/home/a.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);
        await viewModel.LoadDirectoryAsync("/home");
        browser.ListingFailure = path => path == "/nowhere" ? new SftpPathNotFoundException("gone") : null;

        viewModel.PathBarText = "/nowhere";
        await viewModel.NavigateToPath("/nowhere");

        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal(localizer.Format("SftpErrorListingNotFound", "/nowhere"), viewModel.StatusText);
        Assert.DoesNotContain(localizer["SftpStatusTransferFailed"], viewModel.StatusText, StringComparison.Ordinal);

        // The list still shows /home: the path bar must not claim otherwise.
        Assert.Equal("/home", viewModel.CurrentPath);
        Assert.Equal("/home", viewModel.PathBarText);
        Assert.Single(viewModel.Files);
    }

    public static TheoryData<Exception, bool, string> ListingFailures() => new()
    {
        { new SftpPathNotFoundException("gone"), true, "SftpErrorListingNotFound" },
        { new SftpPermissionDeniedException("denied"), true, "SftpErrorListingDenied" },
        { new TimeoutException("slow"), true, "SftpErrorListingTimeout" },
        { new SshOperationTimeoutException("slow"), true, "SftpErrorListingTimeout" },
        { new SshConnectionException("closed"), true, "SftpErrorListingDisconnected" },
        { new SocketException(), true, "SftpErrorListingDisconnected" },
        { new IOException("anything"), false, "SftpErrorListingDisconnected" },
        { new InvalidOperationException("odd"), true, "SftpErrorListingFailed" },
    };

    [Theory]
    [MemberData(nameof(ListingFailures))]
    public void ListingErrorClassifier_MapsTheCauseToItsOwnMessage(
        Exception exception,
        bool browserConnected,
        string expectedKey)
    {
        SftpListingFailure failure = SftpListingErrorClassifier.Classify(exception, browserConnected);

        Assert.Equal(expectedKey, SftpListingErrorClassifier.LocaleKey(failure));
    }

    [Fact]
    public async Task Navigating_WhenTheSessionIsGone_SaysTheConnectionIsLostRatherThanTransferFailed()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);
        browser.ListingFailure = _ =>
        {
            browser.IsConnected = false;
            return new IOException("reset");
        };

        // The load gate refuses a disconnected browser before listing, so lose the link mid-listing.
        browser.IsConnected = true;
        await viewModel.NavigateToPath("/srv");

        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal(localizer.Format("SftpErrorListingDisconnected", "/srv"), viewModel.StatusText);
    }

    [Fact]
    public async Task Navigating_FailedSilently_StillRestoresThePathBar()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/", SftpTestKit.File("/a.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/");
        browser.ListingFailure = path => path == "/typo" ? new InvalidOperationException("nope") : null;

        viewModel.PathBarText = "/typo";
        await viewModel.NavigateInitialAsync("/typo");

        Assert.Equal("/", viewModel.PathBarText);
    }

    // ------------------------------------------------------------------
    // Empty state
    // ------------------------------------------------------------------

    [Fact]
    public async Task EmptyState_AnEmptyFolder_SaysSoWithoutAnAction()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/empty");
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        await viewModel.LoadDirectoryAsync("/empty");

        Assert.Equal(EmbeddedSftpViewModel.SftpEmptyState.EmptyDirectory, viewModel.EmptyState);
        Assert.True(viewModel.ShowEmptyDirectory);
        Assert.Equal(localizer["SftpEmptyDirectory"], viewModel.EmptyStateText);
        Assert.False(viewModel.HasEmptyStateAction);
    }

    [Fact]
    public async Task EmptyState_AFilterThatHidesEveryEntry_IsNotCalledAnEmptyFolder()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/logs", SftpTestKit.File("/logs/app.txt"), SftpTestKit.File("/logs/db.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);
        await viewModel.LoadDirectoryAsync("/logs");

        viewModel.FilterText = "zzz";

        Assert.Equal(EmbeddedSftpViewModel.SftpEmptyState.NoFilterMatch, viewModel.EmptyState);
        Assert.Equal(localizer.Format("SftpEmptyNoFilterMatch", "zzz"), viewModel.EmptyStateText);
        Assert.NotEqual(localizer["SftpEmptyDirectory"], viewModel.EmptyStateText);
        Assert.Equal(localizer["SftpEmptyClearFilter"], viewModel.EmptyStateActionText);

        viewModel.RunEmptyStateActionCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.FilterText);
        Assert.Equal(2, viewModel.Files.Count);
        Assert.Equal(EmbeddedSftpViewModel.SftpEmptyState.None, viewModel.EmptyState);
        Assert.False(viewModel.ShowEmptyDirectory);
    }

    [Fact]
    public async Task EmptyState_OnlyHiddenEntries_OffersToShowThem()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/home", SftpTestKit.File("/home/.bashrc"), SftpTestKit.File("/home/.profile"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);
        await viewModel.LoadDirectoryAsync("/home");

        viewModel.ShowHidden = false;

        Assert.Equal(EmbeddedSftpViewModel.SftpEmptyState.HiddenEntriesOnly, viewModel.EmptyState);
        Assert.Equal(localizer["SftpEmptyHiddenOnly"], viewModel.EmptyStateText);

        viewModel.RunEmptyStateActionCommand.Execute(null);

        Assert.True(viewModel.ShowHidden);
        Assert.Equal(2, viewModel.Files.Count);
    }

    [Fact]
    public async Task Filter_IsDroppedWhenArrivingInAnotherFolder_ButKeptByARefresh()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/a", SftpTestKit.File("/a/log1.txt"), SftpTestKit.File("/a/other.txt"));
        browser.AddDirectory("/b", SftpTestKit.File("/b/notes.md"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/a");
        viewModel.FilterText = "log";

        await viewModel.Refresh();

        Assert.Equal("log", viewModel.FilterText);
        Assert.Single(viewModel.Files);

        await viewModel.LoadDirectoryAsync("/b");

        Assert.Equal(string.Empty, viewModel.FilterText);
        Assert.Equal("notes.md", Assert.Single(viewModel.Files).Name);
        Assert.False(viewModel.ShowEmptyDirectory);
    }

    // ------------------------------------------------------------------
    // Rebuilding the list
    // ------------------------------------------------------------------

    [Fact]
    public void BulkObservableCollection_ReplaceAll_RaisesOneResetNotMoreWhateverTheSize()
    {
        BulkObservableCollection<int> collection = [];
        List<NotifyCollectionChangedAction> actions = [];
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.ReplaceAll(Enumerable.Range(0, 5000));

        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(5000, collection.Count);
    }

    [Fact]
    public async Task ApplyFilterAndSort_ReplacesTheListInOneNotification()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/big", Enumerable.Range(0, 300).Select(i => SftpTestKit.File($"/big/f{i}.txt")).ToArray());
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/big");
        int notifications = 0;
        viewModel.Files.CollectionChanged += (_, _) => notifications++;

        viewModel.ToggleSortColumn("Size");

        // One clear-then-add-each loop would have produced 301.
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Refresh_ReportsTheSelectionBackByPath_SoItSurvivesTheRebuild()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"), SftpTestKit.File("/d/b.txt"), SftpTestKit.File("/d/c.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/d");
        SftpFileInfo b = viewModel.Files.Single(file => file.Name == "b.txt");
        viewModel.SetSelection([b], b);
        List<IReadOnlyList<SftpFileInfo>> restores = [];
        viewModel.SelectionRestoreRequested += restores.Add;

        await viewModel.Refresh();

        IReadOnlyList<SftpFileInfo> restored = Assert.Single(restores);
        Assert.Equal(["/d/b.txt"], restored.Select(file => file.FullPath));
    }

    [Fact]
    public async Task Refresh_ASelectedEntryThatIsGone_IsNotReportedBack()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"), SftpTestKit.File("/d/b.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/d");
        SftpFileInfo b = viewModel.Files.Single(file => file.Name == "b.txt");
        viewModel.SetSelection([b], b);
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"));
        int restores = 0;
        viewModel.SelectionRestoreRequested += _ => restores++;

        await viewModel.Refresh();

        Assert.Equal(0, restores);
    }

    [Fact]
    public async Task RequestSelectAfterRefresh_SelectsTheEntryJustCreated()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"), SftpTestKit.Directory("/d/new"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/d");
        List<IReadOnlyList<SftpFileInfo>> restores = [];
        viewModel.SelectionRestoreRequested += restores.Add;

        viewModel.RequestSelectAfterRefresh("/d/new");
        await viewModel.Refresh();

        Assert.Equal(["/d/new"], Assert.Single(restores).Select(file => file.FullPath));
    }

    [Fact]
    public async Task CreateFolder_SelectsTheNewFolderOnceTheListingIsRefreshed()
    {
        (IDialogService dialog, ScriptedDialogProxy script) = ScriptedDialogProxy.Create();
        script.Input = (_, _, _) => "made";
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, dialogService: dialog);
        await viewModel.LoadDirectoryAsync("/d");
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"), SftpTestKit.Directory("/d/made"));
        List<IReadOnlyList<SftpFileInfo>> restores = [];
        viewModel.SelectionRestoreRequested += restores.Add;

        await viewModel.CreateFolderAsync();

        Assert.Equal(["/d/made"], Assert.Single(restores).Select(file => file.FullPath));
    }

    [Fact]
    public async Task Rename_AsksForANewNameNotAFolderName()
    {
        (IDialogService dialog, ScriptedDialogProxy script) = ScriptedDialogProxy.Create();
        script.Input = (_, _, _) => null;
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, dialogService: dialog);

        await viewModel.RenameEntryAsync(SftpTestKit.File("/d/a.txt"));

        (string _, string prompt) = Assert.Single(script.Inputs);
        Assert.Equal("SftpRenameNewName", prompt);
        Assert.NotEqual("SftpNewFolderName", prompt);
    }

    // ------------------------------------------------------------------
    // Sorting
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sort_ByPermissions_OrdersByModeNotByTheLettersOfTheString()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory(
            "/p",
            SftpTestKit.File("/p/setuid", permissions: "rwsr-xr-x"),
            SftpTestKit.File("/p/plain", permissions: "rwxr-xr-x"),
            SftpTestKit.File("/p/locked", permissions: "r--------"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.LoadDirectoryAsync("/p");

        viewModel.ToggleSortColumn("Permissions");

        // 0400, 0755, 4755. Compared as text, "rwsr-xr-x" would come before "rwxr-xr-x" because
        // 's' sorts before 'x', which puts the setuid binary below the plain one.
        Assert.Equal(["locked", "plain", "setuid"], viewModel.Files.Select(file => file.Name));
    }

    [Fact]
    public async Task SortDescription_NamesTheColumnAndTheDirectoryForAssistiveTechnology()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(localizer: localizer);

        Assert.Equal(localizer.Format("SftpSortAscending", localizer["SftpColName"]), viewModel.SortDescription);

        viewModel.ToggleSortColumn("Size");
        viewModel.ToggleSortColumn("Size");

        Assert.Equal(localizer.Format("SftpSortDescending", localizer["SftpColSize"]), viewModel.SortDescription);
    }

    [Fact]
    public void SortDescription_RaisesAChangeWhenTheOrderChanges()
    {
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel();
        List<string?> raised = [];
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.ToggleSortColumn("Owner");

        Assert.Contains(nameof(EmbeddedSftpViewModel.SortDescription), raised);
    }

    // ------------------------------------------------------------------
    // Errors stay until replaced
    // ------------------------------------------------------------------

    [Fact]
    public void ErrorStatus_StaysUntilTheNextStatusReplacesIt_WithNoTimerLeftToFadeIt()
    {
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel();

        viewModel.SetErrorStatus("refused");

        Assert.True(viewModel.IsErrorStatus);
        Assert.DoesNotContain(
            typeof(EmbeddedSftpViewModel).GetProperties().Select(property => property.Name),
            name => name == "IsErrorHighlighted");

        viewModel.UpdateStatus("fine");

        Assert.False(viewModel.IsErrorStatus);
    }

    // ------------------------------------------------------------------
    // Toolbar
    // ------------------------------------------------------------------

    [Fact]
    public void CanDownloadSelected_NeedsAConnectionAndSomethingDownloadable()
    {
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel();
        SftpFileInfo file = SftpTestKit.File("/d/a.txt");
        SftpFileInfo link = SftpTestKit.Link("/d/l");

        Assert.False(viewModel.CanDownloadSelected);

        viewModel.SetSelection([link], link);
        Assert.False(viewModel.CanDownloadSelected);

        viewModel.SetSelection([file], file);
        Assert.True(viewModel.CanDownloadSelected);

        viewModel.IsConnected = false;
        Assert.False(viewModel.CanDownloadSelected);
    }

    // ------------------------------------------------------------------
    // Bookmarks live beyond the pane
    // ------------------------------------------------------------------

    [Fact]
    public void Bookmarks_AreSavedPerServer_AndRemovable()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        SftpBrowserStateStore store = new(scratch.Path);
        EmbeddedSftpViewModel first = SftpTestKit.CreateViewModel();
        first.AttachStateStore(store);
        SetEndpoint(first, "host:22:alice");
        first.CurrentPath = "/srv/app";
        first.AddBookmark();
        first.CurrentPath = "/var/log";
        first.AddBookmark();

        EmbeddedSftpViewModel second = SftpTestKit.CreateViewModel();
        second.AttachStateStore(new SftpBrowserStateStore(scratch.Path));
        SetEndpoint(second, "host:22:alice");
        LoadBookmarks(second);

        Assert.Equal(["/srv/app", "/var/log"], second.Bookmarks);

        second.RemoveBookmark("/srv/app");

        EmbeddedSftpViewModel third = SftpTestKit.CreateViewModel();
        third.AttachStateStore(new SftpBrowserStateStore(scratch.Path));
        SetEndpoint(third, "host:22:alice");
        LoadBookmarks(third);
        Assert.Equal(["/var/log"], third.Bookmarks);

        EmbeddedSftpViewModel otherServer = SftpTestKit.CreateViewModel();
        otherServer.AttachStateStore(new SftpBrowserStateStore(scratch.Path));
        SetEndpoint(otherServer, "other:22:alice");
        LoadBookmarks(otherServer);
        Assert.Empty(otherServer.Bookmarks);
    }

    // ------------------------------------------------------------------
    // Breadcrumb
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/", new[] { "/" }, new[] { "/" })]
    [InlineData("", new[] { "/" }, new[] { "/" })]
    [InlineData("/home/user", new[] { "/", "home", "user" }, new[] { "/", "/home", "/home/user" })]
    [InlineData("/srv//app/", new[] { "/", "srv", "app" }, new[] { "/", "/srv", "/srv/app" })]
    public void BuildPathSegments_SplitsThePathFromTheRootDown(string path, string[] names, string[] fullPaths)
    {
        IReadOnlyList<SftpPathSegment> segments = EmbeddedSftpViewModel.BuildPathSegments(path);

        Assert.Equal(names, segments.Select(segment => segment.Name));
        Assert.Equal(fullPaths, segments.Select(segment => segment.FullPath));
    }

    [Fact]
    public async Task PathSegments_FollowTheCurrentFolder()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/a/b");
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        Assert.Equal(["/"], viewModel.PathSegments.Select(segment => segment.FullPath));

        await viewModel.LoadDirectoryAsync("/a/b");

        Assert.Equal(["/", "/a", "/a/b"], viewModel.PathSegments.Select(segment => segment.FullPath));
    }

    private static void SetEndpoint(EmbeddedSftpViewModel viewModel, string key)
    {
        typeof(EmbeddedSftpViewModel)
            .GetField("_endpointKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(viewModel, key);
    }

    private static void LoadBookmarks(EmbeddedSftpViewModel viewModel)
    {
        typeof(EmbeddedSftpViewModel)
            .GetMethod("LoadBookmarksFromStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, null);
    }
}
