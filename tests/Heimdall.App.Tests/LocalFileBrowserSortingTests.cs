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
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The local file browser sorts by its column headers like the remote one, rebuilds its list in a
/// single notification, and says why a file would not open.
/// </summary>
public sealed class LocalFileBrowserSortingTests
{
    private static readonly DateTime Early = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Late = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void OrderEntries_ByName_KeepsFoldersFirstAndReversesWithinEachGroup()
    {
        List<LocalFileEntry> entries = Sample();

        Assert.Equal(
            ["alpha", "zeta", "a.txt", "b.txt", "c.txt"],
            LocalFileBrowserViewModel.OrderEntries(entries, "Name", ListSortDirection.Ascending).Select(entry => entry.Name));
        Assert.Equal(
            ["zeta", "alpha", "c.txt", "b.txt", "a.txt"],
            LocalFileBrowserViewModel.OrderEntries(entries, "Name", ListSortDirection.Descending).Select(entry => entry.Name));
    }

    [Fact]
    public void OrderEntries_BySize_OrdersFilesByTheirLengthAndFoldersStayOnTop()
    {
        Assert.Equal(
            ["alpha", "zeta", "b.txt", "c.txt", "a.txt"],
            LocalFileBrowserViewModel.OrderEntries(Sample(), "Size", ListSortDirection.Ascending).Select(entry => entry.Name));
        Assert.Equal(
            ["alpha", "zeta", "a.txt", "c.txt", "b.txt"],
            LocalFileBrowserViewModel.OrderEntries(Sample(), "Size", ListSortDirection.Descending).Select(entry => entry.Name));
    }

    [Fact]
    public void OrderEntries_ByDate_OrdersByTheModificationTime()
    {
        Assert.Equal(
            ["zeta", "alpha", "b.txt", "a.txt", "c.txt"],
            LocalFileBrowserViewModel.OrderEntries(Sample(), "Modified", ListSortDirection.Ascending).Select(entry => entry.Name));
    }

    [Fact]
    public async Task ToggleSortColumn_SortsTheListAndReversesOnTheSecondClick()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        File.WriteAllText(Path.Combine(scratch.Path, "small.txt"), "x");
        File.WriteAllText(Path.Combine(scratch.Path, "large.txt"), new string('x', 500));
        LocalFileBrowserViewModel viewModel = new(scratch.Path);
        await viewModel.LoadDirectory(scratch.Path);

        viewModel.ToggleSortColumn("Size");
        Assert.Equal(["small.txt", "large.txt"], viewModel.Files.Select(entry => entry.Name));
        Assert.Equal("Size", viewModel.SortColumn);

        viewModel.ToggleSortColumn("Size");
        Assert.Equal(["large.txt", "small.txt"], viewModel.Files.Select(entry => entry.Name));
        Assert.Equal(ListSortDirection.Descending, viewModel.SortDirection);
    }

    [Fact]
    public async Task ApplyingTheFilterOrTheSort_ReplacesTheListInOneNotification()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        for (int index = 0; index < 50; index++)
        {
            File.WriteAllText(Path.Combine(scratch.Path, $"f{index:D2}.txt"), "x");
        }

        LocalFileBrowserViewModel viewModel = new(scratch.Path);
        await viewModel.LoadDirectory(scratch.Path);
        int notifications = 0;
        viewModel.Files.CollectionChanged += (_, e) =>
        {
            notifications++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
        };

        viewModel.ToggleSortColumn("Modified");

        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ReportOpenFailure_ShowsTheReasonInTheStatusAsAnError_UntilTheNextListing()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        LocalFileBrowserViewModel viewModel = new(scratch.Path, localizer);
        await viewModel.LoadDirectory(scratch.Path);

        viewModel.ReportOpenFailure("tool.exe", "no application is associated");

        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal(localizer.Format("FileBrowserOpenFailed", "tool.exe", "no application is associated"), viewModel.StatusText);

        await viewModel.Refresh();

        Assert.False(viewModel.IsErrorStatus);
    }

    private static List<LocalFileEntry> Sample() =>
    [
        new("a.txt", @"C:\t\a.txt", false, 300, Late),
        new("b.txt", @"C:\t\b.txt", false, 100, Early.AddDays(1)),
        new("c.txt", @"C:\t\c.txt", false, 200, Late.AddDays(5)),
        new("alpha", @"C:\t\alpha", true, 0, Early.AddDays(10)),
        new("zeta", @"C:\t\zeta", true, 0, Early),
    ];
}
