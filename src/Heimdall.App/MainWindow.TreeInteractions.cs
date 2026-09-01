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

using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Heimdall.App;

/// <summary>
/// Partial of <see cref="MainWindow"/> hosting the session
/// <see cref="TreeView"/> interaction handlers: selection, double-click,
/// right-click pre-selection, keyboard context menu, and drag-drop between
/// folders or to the no-group root target. Transient state lives in the <c>_treeState</c> field
/// (<see cref="TreeInteractionState"/>) and, for the selection warm-up alone, in
/// <c>_dnsWarmupGate</c>; this file only contains the WPF
/// event handlers that mutate that state and poke the named XAML elements
/// (<c>SessionTreeView</c>, <c>SessionTreeNoGroupDropZone</c>,
/// <c>SessionDetailPanel</c>, <c>ToolDetailPanel</c>, <c>Mw_Detail*</c>,
/// <c>Mw_ToolDetail*</c>).
/// </summary>
public partial class MainWindow
{
    private readonly DnsWarmupGate _dnsWarmupGate = new();
    private IInlineRenameNode? _inlineRenameNode;
    private bool _inlineRenameCommitInProgress;

    // ── Keyboard context menu (Apps / Shift+F10) ─────────────────────

    /// <summary>
    /// Opens the TreeView context menu via keyboard (Shift+F10 or Apps key).
    /// Positions the menu at the focused TreeViewItem rather than at the mouse cursor.
    /// </summary>
    private void OpenTreeViewKeyboardContextMenu(MainViewModel vm)
    {
        if (!SessionTreeView.IsKeyboardFocusWithin)
        {
            return;
        }

        TreeViewItem? focusedContainer = TreeInteractionState.FindFocusedTreeViewItem(
            SessionTreeView,
            Keyboard.FocusedElement as DependencyObject);
        object? focusedTarget = focusedContainer?.DataContext;

        // A focused folder must win because folders are deliberately not selected.
        // Preserve the existing bulk action menu when focus remains on a selected server.
        object? target = TreeInteractionState.ResolveKeyboardContextTarget(
            focusedTarget,
            vm.ServerList.CreateBulkSelectionContext(),
            SessionTreeView.SelectedItem);
        var menu = _contextMenuFactory.CreateTreeContextMenu(target, vm, this);

        // Try to position the menu at the selected item's location
        var placementTarget = target is BulkSelectionContext bulkContext
            ? (object?)bulkContext.Primary ?? vm.ServerList.SelectedServer
            : target;
        var container = ReferenceEquals(placementTarget, focusedTarget)
            ? focusedContainer
            : GetOrRealizeSessionTreeItem(placementTarget);
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
        var toolId = ConnectionTypeCatalog.StripToolPrefix(connectionType);
        var desc = ToolRegistry.GetById(toolId);
        if (desc is null) return;

        Mw_ToolDetailName.Text = vm.Localize(desc.LabelKey);
        Mw_ToolDetailCategory.Text = vm.Localize(desc.CategoryLabelKey);

        var descKey = desc.DescriptionKey ?? $"ToolDesc{desc.Id}";
        var description = vm.Localize(descKey);
        Mw_ToolDetailDescription.Text = description != descKey ? description : "";

        Mw_ToolDetailOpenBtn.Content = vm.Localize("DetailBtnOpenInTab");
    }

    // ── Activation (double-click / Enter) → connect / open tool ──────

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

        TreeViewItem? hitContainer = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        ServerItemViewModel? server = ResolveTreeDoubleClickServer(
            hitContainer?.DataContext,
            vm.ServerList.SelectedServer);
        if (server is null) return;

        ApplyTreeActivation(
            server,
            target => OpenSessionTreeToolTab(vm, target),
            target => ConnectSessionTreeServer(vm, target));
    }

    /// <summary>
    /// Resolves a double-click target only when the hit container is a server.
    /// </summary>
    /// <param name="hitTarget">The data context of the container under the pointer.</param>
    /// <param name="selectedServer">The globally selected server, which must not affect hit testing.</param>
    /// <returns>The hit server, or <see langword="null"/> for any other target.</returns>
    internal static ServerItemViewModel? ResolveTreeDoubleClickServer(
        object? hitTarget,
        ServerItemViewModel? selectedServer)
    {
        return hitTarget as ServerItemViewModel;
    }

    /// <summary>
    /// Resolves the session an activation key press acts on, without reading global keyboard state.
    /// </summary>
    /// <param name="key">The key raised by the sessions tree.</param>
    /// <param name="modifiers">The exact modifier combination for the gesture.</param>
    /// <param name="isInlineRenameEditorSource">Whether the event originated inside the rename editor.</param>
    /// <param name="isRepeat">Whether the key press comes from keyboard auto-repeat.</param>
    /// <param name="focusedNode">The data context of the container owning keyboard focus.</param>
    /// <param name="selectedItem">The tree's native selection, used only when nothing holds focus.</param>
    /// <param name="isSelected">Reports whether a session belongs to the current selection.</param>
    /// <returns>The session to activate, or <see langword="null"/> when the gesture does nothing.</returns>
    internal static ServerItemViewModel? ResolveTreeActivationTarget(
        Key key,
        ModifierKeys modifiers,
        bool isInlineRenameEditorSource,
        bool isRepeat,
        object? focusedNode,
        object? selectedItem,
        Func<ServerItemViewModel, bool> isSelected)
    {
        ArgumentNullException.ThrowIfNull(isSelected);

        if (key != Key.Enter || modifiers != ModifierKeys.None || isInlineRenameEditorSource)
        {
            return null;
        }

        // A held Enter repeats about thirty times a second. Connecting absorbs that through
        // ConnectCommand.CanExecute, but the tool branch reaches AddSession, which counts
        // nothing and caps nothing, so one held key would fill the workspace with tabs.
        if (isRepeat)
        {
            return null;
        }

        // The fallback is on the node, not on the cast: focus parked on a folder
        // must resolve to nothing rather than activate a server selected elsewhere.
        if ((focusedNode ?? selectedItem) is not ServerItemViewModel candidate)
        {
            return null;
        }

        // Ctrl+Space and Ctrl+click leave keyboard focus on the row they have just removed
        // from the selection, so the focused row is not always the highlighted one. The help
        // this gesture exists to honour promises the selected server, never the focused one.
        return isSelected(candidate) ? candidate : null;
    }

    /// <summary>
    /// Routes a resolved activation target to the tool tab or to the connect command.
    /// </summary>
    /// <param name="server">The session to activate, or null when the gesture resolved to nothing.</param>
    /// <param name="openTool">Opens the tool tab for a tool session.</param>
    /// <param name="connect">Connects a remote session.</param>
    /// <returns>True when the gesture acted and must be consumed.</returns>
    internal static bool ApplyTreeActivation(
        ServerItemViewModel? server,
        Action<ServerItemViewModel> openTool,
        Action<ServerItemViewModel> connect)
    {
        if (server is null)
        {
            return false;
        }

        if (ConnectionTypeCatalog.IsToolConnectionType(server.ConnectionType))
        {
            openTool(server);
        }
        else
        {
            connect(server);
        }

        return true;
    }

    private static void OpenSessionTreeToolTab(MainViewModel vm, ServerItemViewModel server)
    {
        var toolId = ConnectionTypeCatalog.StripToolPrefix(server.ConnectionType);
        vm.TrackRecentTool(toolId.ToUpperInvariant());
        var context = new Core.Models.ToolContext(
            TargetHost: server.RemoteServer,
            TargetPort: server.RemotePort > 0 ? (int?)server.RemotePort : null,
            Argument: server.RemoteServer);
        _ = vm.OpenToolTabAsync(toolId, server.DisplayName, context);
    }

    private static void ConnectSessionTreeServer(MainViewModel vm, ServerItemViewModel server)
    {
        // ConnectAsync disallows concurrent execution, so CanExecute is false while a
        // connect is already in flight; asking first keeps a second activation, from the
        // tree or from anywhere else, from restarting a connection that is under way.
        if (vm.ServerList.ConnectCommand.CanExecute(server))
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

        ModifierKeys modifiers = Keyboard.Modifiers;
        (bool toggle, bool extend, bool additive) = ResolveTreePointerSelection(modifiers);
        if (additive)
        {
            _treeState.SuppressSelectedItemSync = true;
            vm.ServerList.AddSelectionRangeTo(server);
            treeViewItem.Focus();
            ShowTreeSelection(vm, vm.ServerList.SelectedServer);
            e.Handled = true;
            return;
        }

        if (toggle)
        {
            vm.ServerList.ToggleSelection(server);
            SynchronizeNativeTreeSelection(
                _treeState,
                treeViewItem);
            ShowTreeSelection(vm, vm.ServerList.SelectedServer);
            e.Handled = true;
            return;
        }

        if (extend)
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

    /// <summary>
    /// Resolves pointer selection modifiers with additive range taking precedence over toggle.
    /// </summary>
    /// <param name="modifiers">The pointer event's keyboard modifier mask.</param>
    /// <returns>Whether the gesture toggles, replaces with a range, or adds a range.</returns>
    internal static (bool Toggle, bool Extend, bool Additive) ResolveTreePointerSelection(
        ModifierKeys modifiers)
    {
        bool control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (control && shift)
        {
            return (false, false, true);
        }

        if (control)
        {
            return (true, false, false);
        }

        return shift
            ? (false, true, false)
            : default;
    }

    /// <summary>
    /// Clears native selection after a Ctrl toggle while preserving pointer focus.
    /// </summary>
    /// <param name="treeState">The transient TreeView interaction state.</param>
    /// <param name="pointerContainer">The container targeted by the pointer.</param>
    internal static void SynchronizeNativeTreeSelection(
        TreeInteractionState treeState,
        TreeViewItem pointerContainer)
    {
        treeState.SuppressSelectedItemSync = true;
        try
        {
            pointerContainer.Focus();
        }
        finally
        {
            treeState.SuppressSelectedItemSync = false;
        }

        treeState.SuppressSelectedItemSync = true;
        try
        {
            pointerContainer.IsSelected = false;
        }
        finally
        {
            treeState.SuppressSelectedItemSync = false;
        }
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
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool isSelectionGesture = (e.Key == Key.Space && modifiers == ModifierKeys.Control)
            || (e.Key is Key.Up or Key.Down && modifiers == ModifierKeys.Shift);
        if (isSelectionGesture
            && DataContext is MainViewModel selectionViewModel
            && !IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            TreeViewItem? focusedContainer = FindAncestor<TreeViewItem>(
                Keyboard.FocusedElement as DependencyObject);
            ServerItemViewModel? focusedServer = focusedContainer?.DataContext as ServerItemViewModel;
            IReadOnlyList<ServerItemViewModel> visibleServers = e.Key == Key.Space
                ? []
                : SelectionHelpers
                    .EnumerateVisibleLeaves(selectionViewModel.ServerList.GroupedServers)
                    .ToList();
            (bool handled, bool toggle, ServerItemViewModel? target) =
                ResolveTreeKeyboardSelection(
                    e.Key,
                    modifiers,
                    focusedServer,
                    visibleServers);

            TreeViewItem? targetContainer = target is null
                ? null
                : ReferenceEquals(target, focusedServer)
                    ? focusedContainer
                    : GetOrRealizeSessionTreeItem(target);
            bool selectionHandled = ApplyTreeKeyboardSelection(
                handled,
                toggle,
                target,
                targetContainer,
                selectionViewModel.ServerList.ToggleSelection,
                selectionViewModel.ServerList.ExtendSelectionTo,
                container => SynchronizeNativeTreeSelection(_treeState, container));

            if (selectionHandled)
            {
                e.Handled = true;
                if (target is not null && targetContainer is not null)
                {
                    ShowTreeSelection(
                        selectionViewModel,
                        selectionViewModel.ServerList.SelectedServer);
                }

                return;
            }
        }

        if (e.Key == Key.F2
            && modifiers == ModifierKeys.None
            && !IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            object? focusedNode =
                FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject)?.DataContext
                ?? SessionTreeView.SelectedItem;
            e.Handled = BeginSessionTreeInlineRename(focusedNode);
            return;
        }

        // The cheap key test keeps the visual-tree walk below off every other keystroke.
        // Leaving the event unhandled lets it tunnel on to the rename editor, which
        // commits the pending edit in OnInlineRenameEditorPreviewKeyDown.
        if (e.Key == Key.Enter && DataContext is MainViewModel activationViewModel)
        {
            ServerItemViewModel? activationTarget = ResolveTreeActivationTarget(
                e.Key,
                modifiers,
                IsInlineRenameEditorSource(e.OriginalSource as DependencyObject),
                e.IsRepeat,
                FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject)?.DataContext,
                SessionTreeView.SelectedItem,
                activationViewModel.ServerList.SelectedItems.Contains);
            e.Handled = ApplyTreeActivation(
                activationTarget,
                target => OpenSessionTreeToolTab(activationViewModel, target),
                target => ConnectSessionTreeServer(activationViewModel, target));
            return;
        }

        if (e.Key != Key.Delete
            || modifiers != ModifierKeys.None
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        (bool deleteHandled, bool deleteSelection) = ResolveTreeDeletion(
            e.Key,
            modifiers,
            FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject)?.DataContext,
            vm.ServerList.SelectionCount);

        if (!deleteHandled
            || (deleteSelection && !vm.ServerList.DeleteSelectedCommand.CanExecute(null)))
        {
            return;
        }

        // Consumed before the branch that acts on nothing returns: an unhandled press reaches
        // the window-level Delete shortcut, which deletes the selected session.
        e.Handled = true;

        if (!deleteSelection)
        {
            return;
        }

        await vm.ServerList.DeleteSelectedCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Resolves what a Delete press does, without reading global keyboard state.
    /// </summary>
    /// <param name="key">The key raised by the sessions tree.</param>
    /// <param name="modifiers">The exact modifier combination for the gesture.</param>
    /// <param name="focusedNode">The data context of the container owning keyboard focus.</param>
    /// <param name="selectionCount">How many sessions the view model reports as selected.</param>
    /// <returns>
    /// Whether the tree consumes the press, and whether it deletes the current selection.
    /// </returns>
    internal static (bool Handled, bool DeleteSelection) ResolveTreeDeletion(
        Key key,
        ModifierKeys modifiers,
        object? focusedNode,
        int selectionCount)
    {
        if (key != Key.Delete || modifiers != ModifierKeys.None)
        {
            return default;
        }

        // Clicking a folder never changes the selection - OnTreeViewSelectedItemChanged pushes
        // the container's IsSelected back to false - so the session selected before the click
        // keeps the highlight and the detail panel while the folder keeps only the focus ring.
        // Enter already refuses to act on a session the focus ring is not on
        // (ResolveTreeActivationTarget); Delete is the destructive half of the same gesture and
        // resolving it by a different rule is what makes it surprising.
        if (focusedNode is FolderViewModel)
        {
            return (true, false);
        }

        // A single selection stays with the window-level shortcut, which owns the confirmation
        // and the Ctrl+Del gesture the help documents.
        return selectionCount > 1
            ? (true, true)
            : default;
    }

    /// <summary>
    /// Resolves a keyboard multi-selection gesture without reading global keyboard state.
    /// </summary>
    /// <param name="key">The key raised by the sessions tree.</param>
    /// <param name="modifiers">The exact modifier combination for the gesture.</param>
    /// <param name="focusedServer">The server whose container owns keyboard focus.</param>
    /// <param name="visibleServers">The visible server leaves in display order.</param>
    /// <returns>A handled flag, whether the action is a toggle, and the action target.</returns>
    internal static (bool Handled, bool Toggle, ServerItemViewModel? Target)
        ResolveTreeKeyboardSelection(
            Key key,
            ModifierKeys modifiers,
            ServerItemViewModel? focusedServer,
            IReadOnlyList<ServerItemViewModel> visibleServers)
    {
        if (focusedServer is null)
        {
            return default;
        }

        if (key == Key.Space && modifiers == ModifierKeys.Control)
        {
            return (true, true, focusedServer);
        }

        if (modifiers != ModifierKeys.Shift || key is not (Key.Up or Key.Down))
        {
            return default;
        }

        int focusedIndex = -1;
        for (int index = 0; index < visibleServers.Count; index++)
        {
            if (ReferenceEquals(visibleServers[index], focusedServer))
            {
                focusedIndex = index;
                break;
            }
        }

        if (focusedIndex < 0)
        {
            return default;
        }

        int targetIndex = key == Key.Down
            ? focusedIndex + 1
            : focusedIndex - 1;
        if (targetIndex < 0 || targetIndex >= visibleServers.Count)
        {
            return (true, false, null);
        }

        return (true, false, visibleServers[targetIndex]);
    }

    /// <summary>
    /// Applies a resolved keyboard selection while consuming handled decisions fail-closed.
    /// </summary>
    /// <param name="handled">Whether the resolver recognized the gesture.</param>
    /// <param name="toggle">Whether the target should be toggled instead of extended to.</param>
    /// <param name="target">The resolved server target, or null for a boundary no-op.</param>
    /// <param name="targetContainer">The realized native container for the target.</param>
    /// <param name="toggleSelection">Applies a logical toggle to the target.</param>
    /// <param name="extendSelection">Extends logical selection to the target.</param>
    /// <param name="synchronizeNativeSelection">Synchronizes native selection with the logical state.</param>
    /// <returns>True when the keyboard event must be consumed.</returns>
    internal static bool ApplyTreeKeyboardSelection(
        bool handled,
        bool toggle,
        ServerItemViewModel? target,
        TreeViewItem? targetContainer,
        Action<ServerItemViewModel> toggleSelection,
        Action<ServerItemViewModel> extendSelection,
        Action<TreeViewItem> synchronizeNativeSelection)
    {
        if (!handled)
        {
            return false;
        }

        if (target is null || targetContainer is null)
        {
            return true;
        }

        if (toggle)
        {
            toggleSelection(target);
        }
        else
        {
            extendSelection(target);
        }

        synchronizeNativeSelection(targetContainer);
        return true;
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
        await CommitInlineRenameAsync(node, editor, restoreFocusAfterCompletion: true);
    }

    private async void OnInlineRenameEditorCommitRequested(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not WpfTextBox editor || editor.DataContext is not IInlineRenameNode node)
        {
            return;
        }

        e.Handled = true;
        await CommitInlineRenameAsync(node, editor, restoreFocusAfterCompletion: false);
    }

    private bool BeginSessionTreeInlineRename(object? node)
    {
        if (_inlineRenameCommitInProgress)
        {
            return false;
        }

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

    private async Task CommitInlineRenameAsync(
        IInlineRenameNode node,
        WpfTextBox editor,
        bool restoreFocusAfterCompletion)
    {
        if (_inlineRenameCommitInProgress
            || !node.IsEditing
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        _inlineRenameCommitInProgress = true;
        editor.IsEnabled = false;
        try
        {
            switch (node)
            {
                case ServerItemViewModel server:
                    await CommitServerInlineRenameAsync(
                        vm,
                        server,
                        restoreFocusAfterCompletion);
                    break;

                case FolderViewModel folder:
                    await CommitFolderInlineRenameAsync(
                        vm,
                        folder,
                        restoreFocusAfterCompletion);
                    break;
            }
        }
        finally
        {
            editor.IsEnabled = true;
            _inlineRenameCommitInProgress = false;
        }
    }

    private async Task CommitServerInlineRenameAsync(
        MainViewModel vm,
        ServerItemViewModel server,
        bool restoreFocusAfterCompletion)
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
                    CompleteInlineRename(server, restoreFocusAfterCompletion);
                    break;

                case ServerRenameStatus.NoChange:
                    CompleteInlineRename(server, restoreFocusAfterCompletion);
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
        FolderViewModel folder,
        bool restoreFocusAfterCompletion)
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
                    CompleteInlineRename(folder, restoreFocusAfterCompletion);
                    vm.StatusText = string.Format(
                        vm.Localize("StatusGroupRenamed"),
                        oldPath,
                        result.NewPath);
                    break;

                case FolderRenameStatus.NoChange:
                    CompleteInlineRename(folder, restoreFocusAfterCompletion);
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

    private void CompleteInlineRename(
        IInlineRenameNode node,
        bool restoreFocusAfterCompletion)
    {
        Action<IInlineRenameNode> completion = restoreFocusAfterCompletion
            ? RestoreInlineRenameFocus
            : ReleaseInlineRename;
        SessionTreeInlineRename.CompleteEdit(node, completion);
    }

    private void ReleaseInlineRename(IInlineRenameNode node)
    {
        if (ReferenceEquals(_inlineRenameNode, node))
        {
            _inlineRenameNode = null;
        }
    }

    private void RefocusInlineRenameEditor(IInlineRenameNode node)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                TreeViewItem? container =
                    GetOrRealizeSessionTreeItem(node);
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
        if (!ReferenceEquals(_inlineRenameNode, node))
        {
            return;
        }

        _inlineRenameNode = null;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                TreeViewItem? container =
                    GetOrRealizeSessionTreeItem(node);
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

    private TreeViewItem? GetOrRealizeSessionTreeItem(object? item)
    {
        TreeViewItem? realized =
            TreeInteractionState.FindTreeViewItemContainer(SessionTreeView, item);
        if (realized is not null
            || item is null
            || DataContext is not MainViewModel vm
            || !TreeInteractionState.TryBuildItemPath(
                vm.ServerList.GroupedServers,
                item,
                out IReadOnlyList<object> itemPath))
        {
            return realized;
        }

        return TreeInteractionState.RealizeTreeViewItemContainer(
            SessionTreeView,
            itemPath);
    }

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
        _treeState.ResetDrag();

        if (IsInlineRenameEditorSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
        {
            return;
        }

        TreeViewItem? pressedContainer =
            FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        _treeState.CaptureDragCandidate(e.GetPosition(null), pressedContainer);
    }

    private void OnTreeViewDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        bool hasDisallowedModifiers =
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        if (!_treeState.TryStartDrag(
                e.GetPosition(null),
                e.LeftButton == MouseButtonState.Pressed,
                hasDisallowedModifiers,
                out TreeViewItem? sourceContainer,
                out ServerItemViewModel? sourceServer)
            || sourceContainer is null
            || sourceServer is null)
        {
            return;
        }

        ExecuteTreeDrag(
            _treeState,
            sourceContainer,
            sourceServer,
            static (container, data) =>
                DragDrop.DoDragDrop(container, data, System.Windows.DragDropEffects.Move));
    }

    internal static void ExecuteTreeDrag(
        TreeInteractionState treeState,
        TreeViewItem sourceContainer,
        ServerItemViewModel sourceServer,
        Action<TreeViewItem, System.Windows.DataObject> executeDrag)
    {
        ArgumentNullException.ThrowIfNull(treeState);
        ArgumentNullException.ThrowIfNull(sourceContainer);
        ArgumentNullException.ThrowIfNull(sourceServer);
        ArgumentNullException.ThrowIfNull(executeDrag);

        System.Windows.DataObject data = new("HeimdallServer", sourceServer);
        try
        {
            executeDrag(sourceContainer, data);
        }
        finally
        {
            treeState.ResetDrag();
        }
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

        var isTool = ConnectionTypeCatalog.IsToolConnectionType(server.ConnectionType);
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

    private void WarmDns(ServerItemViewModel server)
    {
        if (!_dnsWarmupGate.ShouldWarm(server.RemoteServer))
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
        FolderViewModel? folder = target?.DataContext as FolderViewModel;
        bool acceptsRootTarget =
            ReferenceEquals(sender, SessionTreeNoGroupDropZone)
            || (ReferenceEquals(sender, SessionTreeView)
                && FindAncestor<TreeViewItem>(
                    e.OriginalSource as DependencyObject) is null);
        if (!TreeInteractionState.TryResolveGroupDropTarget(
                folder,
                acceptsRootTarget,
                out targetGroup))
        {
            return false;
        }

        if (folder is not null)
        {
            targetContainer = target;
            targetDisplayName = folder.Name;
        }

        return true;
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

/// <summary>
/// Remembers the host the sessions tree last pre-warmed, so re-showing the same selection does
/// not resolve it again.
/// </summary>
/// <remarks>
/// The warm-up hangs off <c>ShowTreeSelection</c>, which also runs when a folder row is clicked
/// and when a right-click pre-selects a row. Neither moves the selection, so both re-resolved the
/// host of a session that was already warmed - a network call whose answer was already in the
/// resolver cache.
/// </remarks>
internal sealed class DnsWarmupGate
{
    private string? _lastWarmedHost;

    /// <summary>
    /// Reports whether <paramref name="host"/> still needs a warm-up, and records it when it does.
    /// </summary>
    /// <param name="host">The host of the session being shown.</param>
    /// <returns><see langword="true"/> when the caller should resolve the host.</returns>
    public bool ShouldWarm([NotNullWhen(true)] string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Host names are case-insensitive, so two spellings of one host are one warm-up.
        if (string.Equals(host, _lastWarmedHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _lastWarmedHost = host;
        return true;
    }
}
