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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Heimdall.App.Controls;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel : ISessionTreeSelectionHost
{
    private bool _suppressSelectedServerSync;
    private ServerItemViewModel? _selectionAnchor;

    public ObservableCollection<ServerItemViewModel> SelectedItems { get; } = [];

    public int SelectionCount => SelectedItems.Count;

    /// <summary>Whether the detail pane of a remote session belongs on screen.</summary>
    /// <remarks>
    /// The two panes are driven from here and from nowhere else. The window used to write their
    /// visibility as local values from its tree handlers, and a local value on a bound dependency
    /// property replaces the binding: the first click on the tree severed the binding the markup
    /// declared, and from then on every selection change that did not pass through a tree handler
    /// - a search with no match, the collapse of the folder holding the selection, a UI Automation
    /// Select - left the pane in whatever state the last click had put it.
    /// </remarks>
    public bool ShowSessionDetail =>
        SelectedServer is { } selected
        && !ConnectionTypeCatalog.IsToolConnectionType(selected.ConnectionType);

    /// <summary>Whether the detail pane of a tool belongs on screen.</summary>
    public bool ShowToolDetail =>
        SelectedServer is { } selected
        && ConnectionTypeCatalog.IsToolConnectionType(selected.ConnectionType);

    private void InitializeSelectionModel()
    {
        SelectedItems.CollectionChanged += OnSelectedItemsChanged;
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedToProjectCommand.NotifyCanExecuteChanged();
        MoveSelectedToGroupCommand.NotifyCanExecuteChanged();
        BulkEditGatewayCommand.NotifyCanExecuteChanged();
    }

    // --- ISessionTreeSelectionHost -------------------------------------------------------------
    // The session tree's automation peers read selection from here rather than from the native
    // TreeViewItem.IsSelected flag, which the tree clears on purpose. Items that are not servers
    // (group and folder rows) are never selected, so they answer false and ignore the mutators
    // instead of throwing - an automation client is allowed to ask about any row it can see.

    bool ISessionTreeSelectionHost.IsItemSelected(object? item)
        => item is ServerItemViewModel server && SelectedItems.Contains(server);

    void ISessionTreeSelectionHost.SelectOnlyItem(object? item)
    {
        if (item is ServerItemViewModel server)
        {
            SelectSingle(server);
        }
    }

    void ISessionTreeSelectionHost.AddItemToSelection(object? item)
    {
        if (item is ServerItemViewModel server && !SelectedItems.Contains(server))
        {
            ToggleSelection(server);
        }
    }

    void ISessionTreeSelectionHost.RemoveItemFromSelection(object? item)
    {
        if (item is ServerItemViewModel server && SelectedItems.Contains(server))
        {
            ToggleSelection(server);
        }
    }

    public void SelectSingle(ServerItemViewModel? item)
    {
        if (item is null || !Servers.Contains(item))
        {
            ApplySelection([], null, null, updateSelectedServer: true);
            return;
        }

        ApplySelection([item], item, item, updateSelectedServer: true);
    }

    public void ToggleSelection(ServerItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!Servers.Contains(item))
        {
            return;
        }

        if (SelectedItems.Contains(item))
        {
            var remaining = SelectedItems
                .Where(selected => !ReferenceEquals(selected, item))
                .ToList();
            var nextPrimary = remaining.LastOrDefault();
            var nextAnchor = _selectionAnchor is not null && remaining.Contains(_selectionAnchor)
                ? _selectionAnchor
                : nextPrimary;

            ApplySelection(remaining, nextPrimary, nextAnchor, updateSelectedServer: true);
            return;
        }

        var updated = SelectedItems.ToList();
        updated.Add(item);
        ApplySelection(updated, item, item, updateSelectedServer: true);
    }

    public void ExtendSelectionTo(ServerItemViewModel item)
    {
        ExtendSelectionTo(item, additive: false);
    }

    /// <summary>
    /// Adds the visible range from the existing anchor to the target to the current selection.
    /// </summary>
    /// <param name="item">The range target and resulting primary selection.</param>
    internal void AddSelectionRangeTo(ServerItemViewModel item)
    {
        ExtendSelectionTo(item, additive: true);
    }

    private void ExtendSelectionTo(ServerItemViewModel item, bool additive)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!Servers.Contains(item))
        {
            return;
        }

        if (_selectionAnchor is null || !Servers.Contains(_selectionAnchor))
        {
            SelectSingle(item);
            return;
        }

        var visibleLeaves = SelectionHelpers.EnumerateVisibleLeaves(GroupedServers).ToList();
        var anchorIndex = visibleLeaves.IndexOf(_selectionAnchor);
        var itemIndex = visibleLeaves.IndexOf(item);

        if (anchorIndex < 0 || itemIndex < 0)
        {
            SelectSingle(item);
            return;
        }

        var start = Math.Min(anchorIndex, itemIndex);
        var length = Math.Abs(itemIndex - anchorIndex) + 1;
        var range = visibleLeaves.GetRange(start, length);
        IReadOnlyList<ServerItemViewModel> requestedItems = additive
            ? SelectedItems.Concat(range).ToList()
            : range;

        ApplySelection(requestedItems, item, _selectionAnchor, updateSelectedServer: true);
    }

    public void ClearSelection()
    {
        ApplySelection([], null, null, updateSelectedServer: true);
    }

    partial void OnSelectedServerChanged(ServerItemViewModel? value)
    {
        OnPropertyChanged(nameof(ShowSessionDetail));
        OnPropertyChanged(nameof(ShowToolDetail));

        if (_suppressSelectedServerSync)
        {
            return;
        }

        if (value is null || !Servers.Contains(value))
        {
            ApplySelection([], null, null, updateSelectedServer: false);
            return;
        }

        ApplySelection([value], value, value, updateSelectedServer: false);
    }

    private void ApplySelection(
        IReadOnlyList<ServerItemViewModel> requestedItems,
        ServerItemViewModel? preferredPrimary,
        ServerItemViewModel? preferredAnchor,
        bool updateSelectedServer)
    {
        var normalized = NormalizeSelection(requestedItems);
        var selectedSet = normalized.Count == 0
            ? new HashSet<ServerItemViewModel>()
            : normalized.ToHashSet();

        foreach (var previouslySelected in SelectedItems.ToList())
        {
            if (!selectedSet.Contains(previouslySelected))
            {
                previouslySelected.IsSelected = false;
            }
        }

        SelectedItems.Clear();
        foreach (var item in normalized)
        {
            item.IsSelected = true;
            SelectedItems.Add(item);
        }

        var primary = normalized.Count == 0
            ? null
            : preferredPrimary is not null && normalized.Contains(preferredPrimary)
                ? preferredPrimary
                : normalized[^1];

        _selectionAnchor = normalized.Count == 0
            ? null
            : preferredAnchor is not null && normalized.Contains(preferredAnchor)
                ? preferredAnchor
                : primary;

        if (!updateSelectedServer)
        {
            return;
        }

        _suppressSelectedServerSync = true;
        try
        {
            SelectedServer = primary;
        }
        finally
        {
            _suppressSelectedServerSync = false;
        }
    }

    private List<ServerItemViewModel> NormalizeSelection(IReadOnlyList<ServerItemViewModel> requestedItems)
    {
        var normalized = new List<ServerItemViewModel>(requestedItems.Count);
        var seen = new HashSet<ServerItemViewModel>();

        foreach (var item in requestedItems)
        {
            if (!Servers.Contains(item) || !seen.Add(item))
            {
                continue;
            }

            normalized.Add(item);
        }

        return normalized;
    }
}
