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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Services;

/// <summary>
/// The sessions a drag out of the session tree carries.
/// </summary>
/// <remarks>
/// The payload used to be the single pressed session, which is why dragging a multi-selection into
/// a folder moved one row and left the other seven where they were. Carrying the set - even when it
/// holds one - keeps the drop from having to guess whether the selection was meant.
/// </remarks>
public sealed class TreeServerDragPayload
{
    /// <summary>The drag-and-drop format name this payload travels under.</summary>
    public const string DataFormat = "HeimdallServer";

    /// <summary>Creates a payload for a drag started on <paramref name="source"/>.</summary>
    /// <param name="source">The session the pointer went down on.</param>
    /// <param name="servers">Every session the drag carries, including the source.</param>
    public TreeServerDragPayload(
        ServerItemViewModel source,
        IReadOnlyList<ServerItemViewModel> servers)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(servers);

        Source = source;
        Servers = servers.Count == 0 ? [source] : servers;
    }

    /// <summary>The session the pointer went down on.</summary>
    public ServerItemViewModel Source { get; }

    /// <summary>Every session the drag carries, in selection order.</summary>
    public IReadOnlyList<ServerItemViewModel> Servers { get; }
}

/// <summary>
/// Holds transient state for the session <see cref="TreeView"/> interactions
/// in <c>MainWindow</c>: drag-drop tracking and right-click /
/// keyboard-context-menu targeting. Also exposes a pure static helper for
/// resolving a data item to its (possibly virtualized) <see cref="TreeViewItem"/>
/// container. Imperative event handlers live in <c>MainWindow.TreeInteractions.cs</c>;
/// this class owns only data and pure functions.
/// </summary>
public sealed class TreeInteractionState
{
    private TreeViewItem? _dragSourceContainer;
    private ServerItemViewModel? _dragSourceServer;
    private ServerItemViewModel? _deferredSingleSelection;

    /// <summary>Mouse position captured on left button down — start of a potential drag.</summary>
    public System.Windows.Point DragStartPoint { get; set; }

    /// <summary>True while a drag-drop operation is currently in flight.</summary>
    public bool DragInProgress { get; set; }

    /// <summary>
    /// Captures the only container and server that may become the source of the
    /// current pointer gesture. Non-server presses deliberately leave no candidate.
    /// </summary>
    internal void CaptureDragCandidate(
        System.Windows.Point startPoint,
        TreeViewItem? pressedContainer)
    {
        ResetDrag();

        if (pressedContainer?.DataContext is not ServerItemViewModel pressedServer)
        {
            return;
        }

        DragStartPoint = startPoint;
        _dragSourceContainer = pressedContainer;
        _dragSourceServer = pressedServer;
    }

    /// <summary>
    /// Resolves a threshold-crossing gesture from the immutable mouse-down snapshot.
    /// </summary>
    internal bool TryStartDrag(
        System.Windows.Point currentPoint,
        bool isLeftButtonPressed,
        bool hasDisallowedModifiers,
        out TreeViewItem? sourceContainer,
        out ServerItemViewModel? sourceServer)
    {
        sourceContainer = null;
        sourceServer = null;

        if (!isLeftButtonPressed || hasDisallowedModifiers)
        {
            ResetDrag();
            return false;
        }

        if (DragInProgress)
        {
            return false;
        }

        Vector distance = currentPoint - DragStartPoint;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        if (_dragSourceContainer is null
            || _dragSourceServer is null
            || !ReferenceEquals(_dragSourceContainer.DataContext, _dragSourceServer))
        {
            ResetDrag();
            return false;
        }

        sourceContainer = _dragSourceContainer;
        sourceServer = _dragSourceServer;
        DragInProgress = true;
        return true;
    }

    /// <summary>
    /// Records the session a plain press decided not to select yet, so the release can select it.
    /// </summary>
    /// <param name="server">The session under the pointer when the button went down.</param>
    internal void DeferSingleSelection(ServerItemViewModel server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _deferredSingleSelection = server;
    }

    /// <summary>
    /// Hands back the deferred session once, and only once.
    /// </summary>
    /// <param name="server">The session the release must select, when there is one.</param>
    /// <returns><see langword="true"/> when a deferral was pending.</returns>
    internal bool TryTakeDeferredSingleSelection(out ServerItemViewModel? server)
    {
        server = _deferredSingleSelection;
        _deferredSingleSelection = null;
        return server is not null;
    }

    /// <summary>Clears every transient value associated with a pointer drag.</summary>
    /// <remarks>
    /// The deferred selection is cleared here, alongside the drag candidate, because every caller
    /// of this method is a point where the pointer gesture restarts or ends: a new press, a move
    /// that no longer qualifies, and the return of DoDragDrop. That last one is what makes a drag
    /// cancelled mid-flight leave the selection alone - the deferral dies with the drag instead of
    /// being applied later by a release the drag loop has already swallowed.
    /// </remarks>
    internal void ResetDrag()
    {
        DragStartPoint = default;
        DragInProgress = false;
        _dragSourceContainer = null;
        _dragSourceServer = null;
        _deferredSingleSelection = null;
    }

    /// <summary>
    /// True when the next TreeView SelectedItemChanged notification should not
    /// resynchronize the ViewModel selection because a Ctrl/Shift gesture
    /// already updated the multi-selection explicitly.
    /// </summary>
    public bool SuppressSelectedItemSync { get; set; }

    /// <summary>
    /// Last <see cref="TreeViewItem"/> visually highlighted as a drop target.
    /// Cleared whenever the cursor leaves the candidate or the drop completes.
    /// </summary>
    public TreeViewItem? LastDropHighlight { get; set; }

    /// <summary>
    /// True when the upcoming <see cref="ContextMenu"/> opening was triggered
    /// by a right-click (preview mouse down captured a target). False when the
    /// menu is opening for some other reason — e.g. a keyboard shortcut.
    /// </summary>
    public bool ContextTargetFromPointer { get; set; }

    /// <summary>
    /// True when the right-click landed in the empty area of the <see cref="TreeView"/>
    /// (no <see cref="TreeViewItem"/> ancestor). Used to surface a root-scoped
    /// menu instead of an item-scoped one.
    /// </summary>
    public bool ContextPointerHitEmptyArea { get; set; }

    /// <summary>
    /// Data item the context menu should target. Resolved from the right-click
    /// hit-test result or the current <see cref="TreeView"/> selection.
    /// </summary>
    public object? ContextTarget { get; set; }

    /// <summary>
    /// Walks the <see cref="ItemContainerGenerator"/> hierarchy of
    /// <paramref name="parent"/> to find the <see cref="TreeViewItem"/>
    /// container for <paramref name="item"/>. Required because virtualized
    /// <see cref="TreeView"/>s only materialize visible containers.
    /// </summary>
    /// <returns>The matching container, or <c>null</c> if not realized.</returns>
    public static TreeViewItem? FindTreeViewItemContainer(ItemsControl parent, object? item)
    {
        if (item is null)
        {
            return null;
        }

        // Direct lookup on the immediate container generator
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
        {
            return direct;
        }

        // Recurse into expanded child containers (for nested items)
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem childContainer)
            {
                continue;
            }

            if (childContainer.DataContext == item)
            {
                return childContainer;
            }

            var nested = FindTreeViewItemContainer(childContainer, item);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the nearest focused item container that belongs to the supplied tree.
    /// Folder focus is intentionally independent from server selection.
    /// </summary>
    public static TreeViewItem? FindFocusedTreeViewItem(
        TreeView tree,
        DependencyObject? focusedElement)
    {
        ArgumentNullException.ThrowIfNull(tree);

        DependencyObject? current = focusedElement;
        while (current is not null && !ReferenceEquals(current, tree))
        {
            if (current is TreeViewItem item && BelongsToTree(item, tree))
            {
                return item;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// Resolves a keyboard context-menu target without turning folder focus into
    /// server selection. Existing server multi-selection retains precedence.
    /// </summary>
    public static object? ResolveKeyboardContextTarget(
        object? focusedTarget,
        object? bulkSelectionTarget,
        object? selectedTarget) =>
        focusedTarget is FolderViewModel
            ? focusedTarget
            : bulkSelectionTarget ?? focusedTarget ?? selectedTarget;

    /// <summary>
    /// Builds the root-to-node data path needed to materialize a virtualized
    /// folder or server container.
    /// </summary>
    public static bool TryBuildItemPath(
        IEnumerable<FolderViewModel> roots,
        object target,
        out IReadOnlyList<object> path)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(target);

        var candidatePath = new List<object>();
        foreach (FolderViewModel root in roots)
        {
            if (TryBuildItemPath(root, target, candidatePath))
            {
                path = candidatePath;
                return true;
            }
        }

        path = [];
        return false;
    }

    /// <summary>
    /// Materializes each direct child in a root-to-node item path. Ancestor
    /// folders are expanded before their child panel is queried.
    /// </summary>
    public static TreeViewItem? RealizeTreeViewItemContainer(
        ItemsControl root,
        IReadOnlyList<object> itemPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(itemPath);

        ItemsControl parent = root;
        TreeViewItem? current = null;
        foreach (object item in itemPath)
        {
            if (parent is TreeViewItem parentContainer)
            {
                parentContainer.IsExpanded = true;
            }

            current = RealizeDirectChildContainer(parent, item);
            if (current is null)
            {
                return null;
            }

            parent = current;
        }

        return current;
    }

    /// <summary>
    /// Returns how many direct item containers are currently realized by an
    /// items control. Intended for virtualization diagnostics and tests.
    /// </summary>
    public static int CountRealizedDirectContainers(ItemsControl parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var count = 0;
        for (var index = 0; index < parent.Items.Count; index++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Resolves the domain group represented by a drag target. Pointer hit
    /// testing supplies a realized folder when the cursor is over a folder;
    /// the explicit no-group zone and empty tree area opt into the root target.
    /// </summary>
    public static bool TryResolveGroupDropTarget(
        FolderViewModel? folder,
        bool acceptsRootTarget,
        out string? targetGroup)
    {
        if (folder is not null)
        {
            targetGroup = folder.FullPath;
            return true;
        }

        targetGroup = null;
        return acceptsRootTarget;
    }

    private static TreeViewItem? RealizeDirectChildContainer(
        ItemsControl parent,
        object item)
    {
        parent.ApplyTemplate();
        parent.UpdateLayout();

        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem existing)
        {
            return existing;
        }

        int index = parent.Items.IndexOf(item);
        if (index < 0)
        {
            return null;
        }

        VirtualizingTreePanel? itemsHost = FindItemsHost(parent, parent);
        if (itemsHost is null)
        {
            return null;
        }

        itemsHost.RealizeIndex(index);
        parent.UpdateLayout();
        return parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    }

    private static bool BelongsToTree(TreeViewItem item, TreeView tree)
    {
        ItemsControl? owner = ItemsControl.ItemsControlFromItemContainer(item);
        while (owner is TreeViewItem ownerItem)
        {
            owner = ItemsControl.ItemsControlFromItemContainer(ownerItem);
        }

        return ReferenceEquals(owner, tree);
    }

    private static VirtualizingTreePanel? FindItemsHost(
        DependencyObject current,
        ItemsControl owner)
    {
        if (current is VirtualizingTreePanel panel
            && panel.TemplatedParent is ItemsPresenter presenter
            && ReferenceEquals(presenter.TemplatedParent, owner))
        {
            return panel;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < childCount; index++)
        {
            VirtualizingTreePanel? result =
                FindItemsHost(VisualTreeHelper.GetChild(current, index), owner);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static bool TryBuildItemPath(
        FolderViewModel folder,
        object target,
        List<object> path)
    {
        path.Add(folder);
        if (ReferenceEquals(folder, target))
        {
            return true;
        }

        foreach (FolderViewModel child in folder.SubFolders)
        {
            if (TryBuildItemPath(child, target, path))
            {
                return true;
            }
        }

        if (target is ServerItemViewModel server
            && folder.Servers.Any(candidate => ReferenceEquals(candidate, server)))
        {
            path.Add(server);
            return true;
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }
}
