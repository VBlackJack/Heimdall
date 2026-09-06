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
