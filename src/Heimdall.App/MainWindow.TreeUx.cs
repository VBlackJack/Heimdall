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
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;

namespace Heimdall.App;

public partial class MainWindow
{
    private DispatcherTimer? _treeDragTimer;
    private FolderViewModel? _treeHoverFolder;
    private long _treeHoverStarted;
    private System.Windows.Point _treeDragPoint;
    private static readonly TimeSpan TreeDragTickInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan TreeDragExpandDelay = TimeSpan.FromMilliseconds(700);
    internal const double TreeDragEdgeSize = 28;

    private void OnTreeBulkMoreClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.ServerList.CreateBulkSelectionContext() is BulkSelectionContext context)
            OpenTreeActionMenu(sender, _contextMenuFactory.CreateTreeContextMenu(context, vm, this));
    }

    private void OnTreeBulkMoveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.ServerList.CreateBulkSelectionContext() is not BulkSelectionContext context)
            return;
        ContextMenu menu = new();
        foreach (GroupTarget group in vm.ServerList.GetBulkGroupTargets(context.Items, includeNoGroup: true))
        {
            menu.Items.Add(new MenuItem
            {
                Header = group.DisplayName,
                Command = vm.ServerList.MoveSelectedToGroupCommand,
                CommandParameter = new BulkMoveToGroupRequest(group.GroupName),
                IsEnabled = vm.ServerList.IsBulkMoveTargetEnabled(context.Items, group.GroupName),
            });
        }
        OpenTreeActionMenu(sender, menu);
    }

    private static void OpenTreeActionMenu(object sender, ContextMenu menu)
    {
        if (sender is not FrameworkElement target) return;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SetTreeDropFeedback(string text)
    {
        TreeDropFeedback.Text = text;
        TreeDropHint.IsOpen = !string.IsNullOrEmpty(text);
    }

    private void UpdateTreeDragNavigation(System.Windows.DragEventArgs e, FolderViewModel? folder)
    {
        _treeDragPoint = e.GetPosition(SessionTreeView);
        if (!ReferenceEquals(folder, _treeHoverFolder))
        {
            _treeHoverFolder = folder;
            _treeHoverStarted = Environment.TickCount64;
        }
        if (_treeDragTimer is not null) return;
        _treeDragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TreeDragTickInterval };
        _treeDragTimer.Tick += OnTreeDragNavigationTick;
        _treeDragTimer.Start();
    }

    private void OnTreeDragNavigationTick(object? sender, EventArgs e)
    {
        ScrollViewer? scroll = FindVisualDescendant<ScrollViewer>(SessionTreeView, _ => true);
        int direction = ResolveTreeDragScroll(_treeDragPoint.Y, SessionTreeView.ActualHeight);
        if (direction < 0) scroll?.LineUp();
        if (direction > 0) scroll?.LineDown();
        if (_treeHoverFolder is { IsExpanded: false } folder
            && Environment.TickCount64 - _treeHoverStarted >= TreeDragExpandDelay.TotalMilliseconds)
        {
            // Recheck the hit after scrolling; never open a row that moved away from the pointer.
            TreeViewItem? hit = FindAncestor<TreeViewItem>(SessionTreeView.InputHitTest(_treeDragPoint) as DependencyObject);
            if (ReferenceEquals(hit?.DataContext, folder)) folder.IsExpanded = true;
        }
    }

    internal static int ResolveTreeDragScroll(double y, double height) =>
        height <= 0 || y < 0 || y > height ? 0
        : y < Math.Min(TreeDragEdgeSize, height / 2) ? -1
        : y > height - Math.Min(TreeDragEdgeSize, height / 2) ? 1 : 0;

    private void StopTreeDragNavigation()
    {
        if (_treeDragTimer is not null)
        {
            _treeDragTimer.Stop();
            _treeDragTimer.Tick -= OnTreeDragNavigationTick;
            _treeDragTimer = null;
        }
        _treeHoverFolder = null;
        SetTreeDropFeedback("");
    }
}
