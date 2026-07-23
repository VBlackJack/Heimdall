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
using Heimdall.App.Theming;
using Heimdall.App.ViewModels;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Heimdall.App;

/// <summary>
/// Partial of <see cref="MainWindow"/> hosting the session
/// <see cref="TreeView"/> interaction handlers: selection, double-click,
/// right-click pre-selection, keyboard context menu, and drag-drop between
/// folders or to the no-group root target. All transient state lives in the <c>_treeState</c> field
/// (<see cref="TreeInteractionState"/>); this file only contains the WPF
/// event handlers that mutate that state and poke the named XAML elements
/// (<c>SessionTreeView</c>, <c>SessionTreeNoGroupDropZone</c>,
/// <c>SessionDetailPanel</c>, <c>ToolDetailPanel</c>, <c>Mw_Detail*</c>,
/// <c>Mw_ToolDetail*</c>).
/// </summary>
public partial class MainWindow
{
    private IInlineRenameNode? _inlineRenameNode;

    // ── Keyboard context menu (Apps / Shift+F10) ─────────────────────

    /// <summary>
    /// Opens the TreeView context menu via keyboard (Shift+F10 or Apps key).
    /// Positions the menu at the selected TreeViewItem rather than at the mouse cursor.
    /// </summary>
    private void OpenTreeViewKeyboardContextMenu(MainViewModel vm)
    {
        if (!SessionTreeView.IsKeyboardFocusWithin)
        {
            return;
        }

        var target = vm.ServerList.CreateBulkSelectionContext() ?? SessionTreeView.SelectedItem;
        var menu = _contextMenuFactory.CreateTreeContextMenu(target, vm, this);

        // Try to position the menu at the selected item's location
        var placementTarget = target is BulkSelectionContext bulkContext
            ? (object?)bulkContext.Primary ?? vm.ServerList.SelectedServer
            : target;
        var container = TreeInteractionState.FindTreeViewItemContainer(SessionTreeView, placementTarget);
        if (container is not null)
        {
            menu.PlacementTarget = container;
            menu.Placement = PlacementMode.Bottom;
        }
        else
        {
            menu.PlacementTarget = SessionTreeView;
            menu.Placement = PlacementMode.Center;
        }

        SessionTreeView.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ── Selection + detail panel switch ──────────────────────────────

    /// <summary>
    /// Handles TreeView selection changes. Only updates the ViewModel when a
    /// server item (leaf node) is selected, ignoring group node selections.
    /// </summary>
    private void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var preserveMultiSelection = _treeState.SuppressSelectedItemSync
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        _treeState.SuppressSelectedItemSync = false;

        if (preserveMultiSelection)
        {
            if (sender is TreeView treeView && e.NewValue is FolderViewModel folder)
            {
                var container = TreeInteractionState.FindTreeViewItemContainer(treeView, folder);
                if (container is not null)
                {
                    container.IsSelected = false;
                }
            }

            ShowTreeSelection(vm, vm.ServerList.SelectedServer);
            return;
        }

        if (e.NewValue is ServerItemViewModel server)
        {
            vm.ServerList.SelectSingle(server);
            ShowTreeSelection(vm, server);
        }
        else
        {
            if (sender is TreeView treeView && e.NewValue is FolderViewModel folder)
            {
                var container = TreeInteractionState.FindTreeViewItemContainer(treeView, folder);
                if (container is not null)
                {
                    container.IsSelected = false;
                }
            }

            ShowTreeSelection(vm, vm.ServerList.SelectedServer);
        }
    }

    /// <summary>
    /// Populates the tool-specific detail panel with name, category, and description.
    /// </summary>
    private void UpdateToolDetailPanel(MainViewModel vm, string connectionType)
    {
        var toolId = connectionType["TOOL:".Length..];
        var desc = ToolRegistry.GetById(toolId);
        if (desc is null) return;

        Mw_ToolDetailName.Text = vm.Localize(desc.LabelKey);
        Mw_ToolDetailCategory.Text = vm.Localize(desc.CategoryLabelKey);

        var descKey = desc.DescriptionKey ?? $"ToolDesc{desc.Id}";
        var description = vm.Localize(descKey);
        Mw_ToolDetailDescription.Text = description != descKey ? description : "";

        Mw_ToolDetailOpenBtn.Content = vm.Localize("DetailBtnOpenInTab");
    }

    // ── Double-click → connect / open tool ───────────────────────────

    /// <summary>
    /// Handles double-click on a server item in the TreeView to initiate a connection.
    /// Ensures only server leaf nodes trigger a connection (not group headers).
    /// </summary>
    private void OnTreeViewDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var server = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ServerItemViewModel
            ?? vm.ServerList.SelectedServer;
        if (server is null) return;

        if (server.ConnectionType?.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var toolId = server.ConnectionType["TOOL:".Length..];
            vm.TrackRecentTool(toolId.ToUpperInvariant());
            var context = new Core.Models.ToolContext(
                TargetHost: server.RemoteServer,
                TargetPort: server.RemotePort > 0 ? (int?)server.RemotePort : null,
                Argument: server.RemoteServer);
            _ = vm.OpenToolTabAsync(toolId, server.DisplayName, context);
        }
        else if (vm.ServerList.ConnectCommand.CanExecute(server))
        {
            vm.ServerList.ConnectCommand.Execute(server);
        }
    }

    private void OnSessionTreeViewItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (DataContext is not MainViewModel vm || sender is not TreeViewItem treeViewItem)
        {
            return;
        }

        if (treeViewItem.DataContext is not ServerItemViewModel server)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _treeState.SuppressSelectedItemSync = true;
            vm.ServerList.ToggleSelection(server);
            treeViewItem.Focus();
            ShowTreeSelection(vm, vm.ServerList.SelectedServer);
            e.Handled = true;
            return;
        }

        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            _treeState.SuppressSelectedItemSync = true;
            vm.ServerList.ExtendSelectionTo(server);
            treeViewItem.Focus();
            ShowTreeSelection(vm, vm.ServerList.SelectedServer);
            e.Handled = true;
            return;
        }

        _treeState.SuppressSelectedItemSync = false;
        vm.ServerList.SelectSingle(server);
    }

    // ── Right-click pre-selection + context menu opening ─────────────

    private void OnTreeViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var treeViewItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);

        _treeState.ContextTargetFromPointer = true;
        _treeState.ContextPointerHitEmptyArea = treeViewItem is null;
        _treeState.ContextTarget = treeViewItem?.DataContext;

        if (treeViewItem is not null)
        {
            treeViewItem.Focus();

            if (treeViewItem.DataContext is ServerItemViewModel server && DataContext is MainViewModel vm)
            {
                if (vm.ServerList.ShouldOpenBulkContextMenu(server))
                {
                    _treeState.ContextTarget = vm.ServerList.CreateBulkSelectionContext() ?? (object)server;
                    ShowTreeSelection(vm, vm.ServerList.SelectedServer);
                    return;
                }

                vm.ServerList.SelectSingle(server);
                _treeState.ContextTarget = server;
                ShowTreeSelection(vm, server);
            }
        }
    }

    private void OnTreeViewContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not TreeView treeView)
        {
            return;
        }

        object? target;
        if (_treeState.ContextTargetFromPointer)
        {
            target = _treeState.ContextPointerHitEmptyArea ? null : _treeState.ContextTarget;
        }
        else
        {
            target = vm.ServerList.CreateBulkSelectionContext() ?? treeView.SelectedItem;
        }

        _treeState.ContextTargetFromPointer = false;
        _treeState.ContextPointerHitEmptyArea = false;
        _treeState.ContextTarget = target;

        var menu = _contextMenuFactory.CreateTreeContextMenu(target, vm, this);
        menu.PlacementTarget = treeView;
        menu.Placement = PlacementMode.MousePoint;
        treeView.ContextMenu = menu;
    }

    private async void OnSessionTreeViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2
            && Keyboard.Modifiers == ModifierKeys.None
            && !IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            object? focusedNode =
                FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject)?.DataContext
                ?? SessionTreeView.SelectedItem;
            e.Handled = BeginSessionTreeInlineRename(focusedNode);
            return;
        }

        if (e.Key != Key.Delete
            || Keyboard.Modifiers != ModifierKeys.None
            || DataContext is not MainViewModel vm
            || vm.ServerList.SelectionCount <= 1
            || !vm.ServerList.DeleteSelectedCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        await vm.ServerList.DeleteSelectedCommand.ExecuteAsync(null);
    }

    private async void OnInlineRenameEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not WpfTextBox editor || editor.DataContext is not IInlineRenameNode node)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SessionTreeInlineRename.CancelEdit(node, RestoreInlineRenameFocus);
            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        await CommitInlineRenameAsync(node, editor);
    }

    private bool BeginSessionTreeInlineRename(object? node)
    {
        if (node is not IInlineRenameNode editableNode)
        {
            return false;
        }

        if (_inlineRenameNode is not null && !ReferenceEquals(_inlineRenameNode, editableNode))
        {
            _inlineRenameNode.CancelInlineEdit();
        }

        if (!SessionTreeInlineRename.TryBeginEdit(node))
        {
            return false;
        }

        _inlineRenameNode = editableNode;
        RefocusInlineRenameEditor(editableNode);
        return true;
    }

    private async Task CommitInlineRenameAsync(IInlineRenameNode node, WpfTextBox editor)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        string requestedName = node.EditName.Trim();
        if (requestedName.Length == 0)
        {
            SessionTreeInlineRename.CancelEdit(node, RestoreInlineRenameFocus);
            return;
        }

        editor.IsEnabled = false;
        try
        {
            switch (node)
            {
                case ServerItemViewModel server:
                    await CommitServerInlineRenameAsync(vm, server);
                    break;

                case FolderViewModel folder:
                    await CommitFolderInlineRenameAsync(vm, folder);
                    break;
            }
        }
        finally
        {
            editor.IsEnabled = true;
        }
    }

    private async Task CommitServerInlineRenameAsync(
        MainViewModel vm,
        ServerItemViewModel server)
    {
        try
        {
            ServerRenameResult result =
                await new ServerRenameService(vm.ConfigManager)
                    .RenameAsync(server.Id, server.EditName);

            switch (result.Status)
            {
                case ServerRenameStatus.Renamed:
                    vm.ServerList.ApplyInlineServerRename(
                        server,
                        result.Server
                            ?? throw new InvalidOperationException(
                                "A successful server rename must return the persisted server."));
                    SessionTreeInlineRename.CompleteEdit(server, RestoreInlineRenameFocus);
                    break;

                case ServerRenameStatus.NoChange:
                    SessionTreeInlineRename.CompleteEdit(server, RestoreInlineRenameFocus);
                    break;

                case ServerRenameStatus.InvalidName:
                    vm.DialogService.ShowWarning(
                        vm.Localize("InlineRenameDialogTitle"),
                        vm.Localize("InlineRenameServerInvalid"));
                    RefocusInlineRenameEditor(server);
                    break;

                case ServerRenameStatus.NameTooLong:
                    vm.DialogService.ShowWarning(
                        vm.Localize("InlineRenameDialogTitle"),
                        string.Format(
                            vm.Localize("InlineRenameServerTooLong"),
                            ServerRenameService.MaxDisplayNameLength));
                    RefocusInlineRenameEditor(server);
                    break;

                case ServerRenameStatus.NotFound:
                    vm.DialogService.ShowError(
                        vm.Localize("InlineRenameDialogTitle"),
                        vm.Localize("InlineRenameServerSaveFailed"));
                    RefocusInlineRenameEditor(server);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unexpected server rename status: {result.Status}.");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Server inline rename failed", ex);
            vm.DialogService.ShowError(
                vm.Localize("InlineRenameDialogTitle"),
                vm.Localize("InlineRenameServerSaveFailed"));
            RefocusInlineRenameEditor(server);
        }
    }

    private async Task CommitFolderInlineRenameAsync(
        MainViewModel vm,
        FolderViewModel folder)
    {
        string oldPath = folder.FullPath;
        try
        {
            FolderRenameResult result =
                await new FolderRenameService(vm.ConfigManager)
                    .RenameAsync(oldPath, folder.EditName);

            switch (result.Status)
            {
                case FolderRenameStatus.Renamed:
                    vm.ServerList.ApplyInlineFolderRename(folder, oldPath, result);
                    SessionTreeInlineRename.CompleteEdit(folder, RestoreInlineRenameFocus);
                    vm.StatusText = string.Format(
                        vm.Localize("StatusGroupRenamed"),
                        oldPath,
                        result.NewPath);
                    break;

                case FolderRenameStatus.NoChange:
                    SessionTreeInlineRename.CompleteEdit(folder, RestoreInlineRenameFocus);
                    break;

                case FolderRenameStatus.InvalidSegment:
                    vm.DialogService.ShowWarning(
                        vm.Localize("RenameGroupDialogTitle"),
                        vm.Localize("RenameGroupErrorInvalidSegment"));
                    RefocusInlineRenameEditor(folder);
                    break;

                case FolderRenameStatus.SiblingCollision:
                    vm.DialogService.ShowWarning(
                        vm.Localize("RenameGroupDialogTitle"),
                        vm.Localize("RenameGroupErrorSiblingCollision"));
                    RefocusInlineRenameEditor(folder);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unexpected folder rename status: {result.Status}.");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Folder inline rename failed", ex);
            vm.DialogService.ShowError(
                vm.Localize("RenameGroupDialogTitle"),
                vm.Localize("RenameGroupErrorPersistence"));
            RefocusInlineRenameEditor(folder);
        }
    }

    private void RefocusInlineRenameEditor(IInlineRenameNode node)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                TreeViewItem? container =
                    TreeInteractionState.FindTreeViewItemContainer(SessionTreeView, node);
                WpfTextBox? editor = container is null
                    ? null
                    : FindVisualDescendant<WpfTextBox>(
                        container,
                        candidate => ReferenceEquals(candidate.DataContext, node) && candidate.IsVisible);
                if (editor is null)
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }));
    }

    private void RestoreInlineRenameFocus(IInlineRenameNode node)
    {
        if (ReferenceEquals(_inlineRenameNode, node))
        {
            _inlineRenameNode = null;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                TreeViewItem? container =
                    TreeInteractionState.FindTreeViewItemContainer(SessionTreeView, node);
                if (container is null)
                {
                    return;
                }

                if (node is ServerItemViewModel)
                {
                    container.IsSelected = true;
                }

                container.BringIntoView();
                container.Focus();
            }));
    }

    internal static bool IsInlineRenameEditorSource(DependencyObject? source)
        => FindAncestor<WpfTextBox>(source) is not null;

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Predicate<T> predicate)
        where T : DependencyObject
    {
        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            T? descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    /// <inheritdoc />
    void IContextMenuCallbacks.BeginInlineRename(object node)
        => BeginSessionTreeInlineRename(node);

    // ── TreeView drag-drop: move servers between groups/projects ─────

    private void OnTreeViewDragStart(object sender, MouseButtonEventArgs e)
    {
        if (IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            _treeState.DragInProgress = false;
            return;
        }

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
        {
            _treeState.DragInProgress = false;
            return;
        }

        _treeState.DragStartPoint = e.GetPosition(null);
        _treeState.DragInProgress = false;
    }

    private void OnTreeViewDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            _treeState.DragInProgress = false;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed
            || _treeState.DragInProgress
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
        {
            return;
        }

        var pos = e.GetPosition(null);
        var diff = pos - _treeState.DragStartPoint;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Find the ServerItemViewModel being dragged
        var treeViewItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);

        if (treeViewItem?.DataContext is not ServerItemViewModel serverItem)
        {
            return;
        }

        _treeState.DragInProgress = true;
        var data = new System.Windows.DataObject("HeimdallServer", serverItem);
        DragDrop.DoDragDrop(treeViewItem, data, System.Windows.DragDropEffects.Move);
        _treeState.DragInProgress = false;
    }

    private void ShowTreeSelection(MainViewModel vm, ServerItemViewModel? server)
    {
        if (server is null)
        {
            SessionDetailPanel.Visibility = Visibility.Collapsed;
            ToolDetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        WarmDns(server);

        var isTool = server.ConnectionType?.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase) == true;
        if (isTool)
        {
            SessionDetailPanel.Visibility = Visibility.Collapsed;
            ToolDetailPanel.Visibility = Visibility.Visible;
            UpdateToolDetailPanel(vm, server.ConnectionType!);
            return;
        }

        SessionDetailPanel.Visibility = Visibility.Visible;
        ToolDetailPanel.Visibility = Visibility.Collapsed;
        Mw_DetailConnectBtn.Content = vm.Localize("DetailBtnConnect");
        Mw_DetailHostPort.Visibility = Visibility.Visible;
    }

    private static void WarmDns(ServerItemViewModel server)
    {
        if (string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            return;
        }

        _ = WarmDnsAsync(server.RemoteServer);
    }

    /// <summary>
    /// Best-effort DNS cache pre-warm so a later connect resolves faster. Hosts
    /// reachable only through a gateway will not resolve here; that is expected
    /// and handled (logged at Debug) rather than left as an unobserved task
    /// exception.
    /// </summary>
    private static async System.Threading.Tasks.Task WarmDnsAsync(string host)
    {
        try
        {
            _ = await System.Net.Dns.GetHostEntryAsync(host).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Debug(
                $"WarmDns: '{host}' did not resolve ({ex.GetType().Name}).");
        }
    }

    private void ClearDropHighlight()
    {
        if (_treeState.LastDropHighlight is not null)
        {
            DropTargetVisualState.SetIsDropTarget(_treeState.LastDropHighlight, false);
            _treeState.LastDropHighlight = null;
        }
    }

    private void OnTreeViewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.None;

        if (!e.Data.GetDataPresent("HeimdallServer")
            || e.Data.GetData("HeimdallServer") is not ServerItemViewModel serverItem
            || DataContext is not MainViewModel vm)
        {
            ClearDropHighlight();
            e.Handled = true;
            return;
        }

        ClearDropHighlight();

        var resolved = TryResolveTreeGroupDropTarget(sender, e, vm, out var targetContainer, out var targetGroup, out _);
        var allowed = resolved && IsAllowedTreeGroupDrop(vm.ServerList, serverItem, targetGroup);

        if (!resolved || !allowed)
        {
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;

        if (targetContainer is not null)
        {
            DropTargetVisualState.SetIsDropTarget(targetContainer, true);
            _treeState.LastDropHighlight = targetContainer;
        }

        e.Handled = true;
    }

    private void OnTreeViewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropHighlight();
    }

    private async void OnTreeViewDrop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropHighlight();

        if (!e.Data.GetDataPresent("HeimdallServer"))
        {
            return;
        }

        if (e.Data.GetData("HeimdallServer") is not ServerItemViewModel serverItem
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        var resolved = TryResolveTreeGroupDropTarget(sender, e, vm, out _, out var targetGroup, out var targetDisplayName);
        var allowed = resolved && IsAllowedTreeGroupDrop(vm.ServerList, serverItem, targetGroup);

        if (!resolved || !allowed)
        {
            return;
        }

        var moved = await vm.ServerList.MoveServerToGroupAsync(serverItem, targetGroup);
        if (moved)
        {
            vm.StatusText = string.Format(
                vm.Localize("StatusMovedToGroup"),
                serverItem.DisplayName,
                targetDisplayName);
        }
    }

    private bool TryResolveTreeGroupDropTarget(
        object sender,
        System.Windows.DragEventArgs e,
        MainViewModel vm,
        out TreeViewItem? targetContainer,
        out string? targetGroup,
        out string targetDisplayName)
    {
        targetContainer = null;
        targetGroup = null;
        targetDisplayName = vm.Localize("TreeNodeNoGroup");

        var target = FindAncestorFolderTreeViewItem(e.OriginalSource as DependencyObject);
        if (target?.DataContext is FolderViewModel folder)
        {
            targetContainer = target;
            targetGroup = folder.FullPath;
            targetDisplayName = folder.Name;
            return true;
        }

        if (ReferenceEquals(sender, SessionTreeNoGroupDropZone))
        {
            return true;
        }

        return ReferenceEquals(sender, SessionTreeView)
            && FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is null;
    }

    private static bool IsAllowedTreeGroupDrop(
        ServerListViewModel serverList,
        ServerItemViewModel server,
        string? targetGroup)
    {
        var normalizedTarget = NormalizeGroupTargetKey(targetGroup);
        var normalizedCurrent = NormalizeGroupTargetKey(server.Group);
        return !string.Equals(normalizedCurrent, normalizedTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGroupTargetKey(string? groupPath)
    {
        return string.IsNullOrWhiteSpace(groupPath)
            ? string.Empty
            : groupPath.Replace('\\', '/');
    }

    private static TreeViewItem? FindAncestorFolderTreeViewItem(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is TreeViewItem item && item.DataContext is FolderViewModel)
            {
                return item;
            }

            current = GetParentObject(current);
        }

        return null;
    }
}
