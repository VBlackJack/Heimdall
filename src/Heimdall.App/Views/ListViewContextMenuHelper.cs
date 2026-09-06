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
using System.Windows.Input;
using System.Windows.Media;

namespace Heimdall.App.Views;

/// <summary>
/// Makes a right-click select the row under the pointer before its context menu opens.
/// </summary>
/// <remarks>
/// The tool DataGrids have had this for a long time through
/// <see cref="Tools.ToolContextMenuHelper.SelectRowOnRightClick"/>; the two file-browser
/// ListViews never did, so every command of their context menu applied to the row selected
/// BEFORE the click. Chmod, duplicate, copy path, download and edit confirm nothing, so the
/// user found out from the result.
/// </remarks>
public static class ListViewContextMenuHelper
{
    /// <summary>Attach to the ListView's <c>PreviewMouseRightButtonDown</c>.</summary>
    public static void SelectRowOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView listView)
        {
            return;
        }

        HitTestResult? hit = VisualTreeHelper.HitTest(listView, e.GetPosition(listView));
        if (hit?.VisualHit is null)
        {
            return;
        }

        System.Windows.Controls.ListViewItem? row = FindVisualParent<System.Windows.Controls.ListViewItem>(hit.VisualHit);
        if (row is not null)
        {
            SelectForContextMenu(listView, row.Content);
        }
    }

    /// <summary>
    /// The selection rule, without the hit test. A right-click on a row that is already part of
    /// the selection keeps the multi-selection, so a batch command still applies to the batch;
    /// a right-click elsewhere selects that row alone.
    /// </summary>
    internal static void SelectForContextMenu(System.Windows.Controls.ListView listView, object? item)
    {
        ArgumentNullException.ThrowIfNull(listView);

        if (item is null || listView.SelectedItems.Contains(item))
        {
            return;
        }

        listView.SelectedItems.Clear();
        listView.SelectedItem = item;
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T found)
            {
                return found;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
