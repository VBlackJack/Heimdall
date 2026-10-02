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
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests;

/// <summary>
/// A right-click used to leave the selection where it was, so every command of the context menu
/// applied to the row selected BEFORE the click.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class ListViewContextMenuHelperTests
{
    [Fact]
    public async Task SelectForContextMenu_RowOutsideTheSelection_BecomesTheOnlySelectedRow()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            ListView listView = new() { SelectionMode = SelectionMode.Extended, ItemsSource = new[] { "a", "b", "c" } };
            listView.SelectedItems.Add("a");

            ListViewContextMenuHelper.SelectForContextMenu(listView, "b");

            Assert.Equal(["b"], listView.SelectedItems.Cast<string>());
        }).Task;
    }

    [Fact]
    public async Task SelectForContextMenu_NoRow_EmptiesTheSelectionSoTheMenuIsAboutTheFolder()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            ListView listView = new() { SelectionMode = SelectionMode.Extended, ItemsSource = new[] { "a", "b", "c" } };
            listView.SelectedItems.Add("a");
            listView.SelectedItems.Add("c");

            ListViewContextMenuHelper.SelectForContextMenu(listView, null);

            Assert.Empty(listView.SelectedItems);
        }).Task;
    }

    [Fact]
    public async Task IsListBody_AHeaderIsNotTheBody_ButTheListItselfIs()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            ListView listView = new()
            {
                ItemsSource = new[] { "a" },
                Width = 300,
                Height = 300,
                View = new GridView { Columns = { new GridViewColumn { Header = "Name", Width = 100 } } },
            };
            System.Windows.Window window = new() { Content = listView, Width = 320, Height = 340, ShowActivated = false };
            window.Show();
            try
            {
                listView.UpdateLayout();
                GridViewColumnHeader? header = FindDescendant<GridViewColumnHeader>(listView);

                Assert.NotNull(header);
                Assert.False(ListViewContextMenuHelper.IsListBody(header!));
                Assert.True(ListViewContextMenuHelper.IsListBody(listView));
            }
            finally
            {
                window.Close();
            }
        }).Task;
    }

    private static T? FindDescendant<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    [Fact]
    public async Task SelectForContextMenu_RowAlreadySelected_KeepsTheMultiSelection()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            ListView listView = new() { SelectionMode = SelectionMode.Extended, ItemsSource = new[] { "a", "b", "c" } };
            listView.SelectedItems.Add("a");
            listView.SelectedItems.Add("b");

            ListViewContextMenuHelper.SelectForContextMenu(listView, "b");

            Assert.Equal(["a", "b"], listView.SelectedItems.Cast<string>());
        }).Task;
    }
}
