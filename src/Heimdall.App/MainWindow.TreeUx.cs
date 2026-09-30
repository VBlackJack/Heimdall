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
using System.Windows.Input;
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
        ContextMenuFactory.AddBulkMoveTargets(menu.Items, vm, context.Items);
        OpenTreeActionMenu(sender, menu);
    }

    /// <summary>A keyboard gesture the sessions filter box answers.</summary>
    internal enum SessionFilterGesture
    {
        None,
        Clear,
        FocusFirstSession,
        ConnectOnlyMatch,
    }

    /// <summary>
    /// What the sessions filter box does with its keys, handed in as delegates so the wiring the
    /// window uses is the wiring a test drives.
    /// </summary>
    /// <param name="MatchCount">How many sessions the filter currently leaves, after any pending pass.</param>
    /// <param name="ApplyPendingFilter">Applies a debounced search pass now.</param>
    /// <param name="Clear">Empties the search.</param>
    /// <param name="FocusFirstSession">Moves focus to the first visible session row.</param>
    /// <param name="ConnectOnlyMatch">Opens the one remaining session.</param>
    internal sealed record SessionFilterKeyTarget(
        Func<int> MatchCount,
        Action ApplyPendingFilter,
        Action Clear,
        Func<bool> FocusFirstSession,
        Action ConnectOnlyMatch);

    /// <summary>
    /// Resolves a filter-box gesture without reading global keyboard state.
    /// </summary>
    /// <param name="key">The key raised by the filter box.</param>
    /// <param name="modifiers">The exact modifier combination for the gesture.</param>
    /// <param name="hasQuery">Whether the box holds text.</param>
    /// <param name="matchCount">How many sessions the filter leaves.</param>
    internal static SessionFilterGesture ResolveSessionFilterGesture(
        Key key,
        ModifierKeys modifiers,
        bool hasQuery,
        int matchCount)
    {
        if (modifiers != ModifierKeys.None)
        {
            return SessionFilterGesture.None;
        }

        return key switch
        {
            // An empty box has nothing to clear: the key is left to the shell.
            Key.Escape when hasQuery => SessionFilterGesture.Clear,
            Key.Down when matchCount > 0 => SessionFilterGesture.FocusFirstSession,

            // Only an unambiguous result is opened; with two or more, Enter would be a guess.
            Key.Enter when hasQuery && matchCount == 1 => SessionFilterGesture.ConnectOnlyMatch,
            _ => SessionFilterGesture.None,
        };
    }

    /// <summary>
    /// Attaches the filter box's keyboard gestures: Escape clears the search, Down moves into
    /// the tree, Enter opens the session when exactly one matches.
    /// </summary>
    /// <param name="box">The filter box.</param>
    /// <param name="target">What each gesture acts on.</param>
    internal static void AttachSessionFilterKeys(System.Windows.Controls.TextBox box, SessionFilterKeyTarget target)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(target);

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                // A debounced pass may still be pending: count what the query WILL leave.
                target.ApplyPendingFilter();
            }

            SessionFilterGesture gesture = ResolveSessionFilterGesture(
                e.Key,
                Keyboard.Modifiers,
                !string.IsNullOrEmpty(box.Text),
                target.MatchCount());
            switch (gesture)
            {
                case SessionFilterGesture.Clear:
                    target.Clear();
                    e.Handled = true;
                    break;

                case SessionFilterGesture.FocusFirstSession:
                    e.Handled = target.FocusFirstSession();
                    break;

                case SessionFilterGesture.ConnectOnlyMatch:
                    target.ConnectOnlyMatch();
                    e.Handled = true;
                    break;
            }
        };
    }

    private void WireSessionFilterKeys()
    {
        AttachSessionFilterKeys(Mw_FilterBox, new SessionFilterKeyTarget(
            MatchCount: () => DataContext is MainViewModel vm ? vm.ServerList.Servers.Count : 0,
            ApplyPendingFilter: () => (DataContext as MainViewModel)?.ServerList.ApplyPendingSearchNow(),
            Clear: () =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.ServerList.SearchText = "";
                }
            },
            FocusFirstSession: FocusFirstVisibleSessionRow,
            ConnectOnlyMatch: () =>
            {
                if (DataContext is MainViewModel vm && vm.ServerList.Servers.Count == 1)
                {
                    ServerItemViewModel only = vm.ServerList.Servers[0];
                    vm.ServerList.SelectSingle(only);
                    ApplyTreeActivation(
                        only,
                        target => OpenSessionTreeToolTab(vm, target),
                        target => ConnectSessionTreeServer(vm, target));
                }
            }));
    }

    private bool FocusFirstVisibleSessionRow()
    {
        if (DataContext is not MainViewModel vm
            || SelectionHelpers.EnumerateVisibleLeaves(vm.ServerList.GroupedServers).FirstOrDefault() is not { } first)
        {
            return false;
        }

        TreeViewItem? container = GetOrRealizeSessionTreeItem(first);
        if (container is null)
        {
            return false;
        }

        container.BringIntoView();
        return container.Focus();
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
