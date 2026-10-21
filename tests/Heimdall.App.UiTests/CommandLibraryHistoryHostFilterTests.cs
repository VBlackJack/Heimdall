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
using System.Windows.Media;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests;

/// <summary>
/// The "This host only" filter is applied to the view model's collection view; this checks
/// that the list the panel renders is that view, so the filter reaches the screen.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryHistoryHostFilterTests
{
    [StaFact]
    public void TheRenderedHistoryList_ShowsOnlyThisHostsRows_WhileTheBoxIsTicked()
    {
        Window? window = null;
        int ticked = -1;
        int unticked = -1;

        WpfTestHost.Invoke(() =>
        {
            var viewModel = new CommandLibraryViewModel(
                serviceProvider: null!,
                configManager: null!,
                WpfTestHost.Localizer,
                dialogService: null!,
                gitSyncService: null!,
                transferService: null!);

            // Without services the load fails and is logged; the host is recorded before that.
            _ = viewModel.InitializeAsync("srv-01");

            viewModel.HistoryEntries.Add(Entry("Same host", "srv-01"));
            viewModel.HistoryEntries.Add(Entry("Other host", "srv-02"));
            viewModel.IsHistoryVisible = true;

            var view = new CommandLibraryView { DataContext = viewModel };
            window = new Window
            {
                Width = 640,
                Height = 700,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            window.Show();
            view.UpdateLayout();

            var list = FindHistoryList(view)
                ?? throw new InvalidOperationException("History ListView not found in the view.");

            // Re-ticking applies the filter the way a reload does.
            viewModel.HistoryThisHostOnly = false;
            viewModel.HistoryThisHostOnly = true;
            list.UpdateLayout();
            ticked = list.Items.Count;

            viewModel.HistoryThisHostOnly = false;
            list.UpdateLayout();
            unticked = list.Items.Count;
        });

        try
        {
            Assert.Equal(1, ticked);
            Assert.Equal(2, unticked);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    private static CommandLibraryHistoryEntry Entry(string title, string host) => new()
    {
        ActionTitle = title,
        GeneratedCommand = "uptime",
        Timestamp = "now",
        TargetHost = host
    };

    private static ListView? FindHistoryList(DependencyObject root)
    {
        foreach (var list in Descendants(root).OfType<ListView>())
        {
            var binding = System.Windows.Data.BindingOperations.GetBinding(
                list, ItemsControl.ItemsSourceProperty);
            if (binding?.Path?.Path == nameof(CommandLibraryViewModel.HistoryEntries))
            {
                return list;
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
