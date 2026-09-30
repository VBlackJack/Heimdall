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

    /// <summary>
    /// The selection a filter or a collapsed folder took off the screen, kept by id so it
    /// survives a reload, until the view shows it again or the user selects something else.
    /// </summary>
    private HiddenSelection? _hiddenSelection;

    /// <summary>
    /// Set while the view, rather than the user, changes the selection, so that change does not
    /// read as the user moving on from the remembered one.
    /// </summary>
    private bool _synchronizingSelection;

    public SessionSelectionCollection SelectedItems { get; } = [];

    /// <summary>
    /// Raised when a selection a view change had hidden is put back in full, so the tree can
    /// scroll the restored primary row into view.
    /// </summary>
    public event Action<ServerItemViewModel>? HiddenSelectionRestored;

    /// <summary>
    /// The session the user last chose: the selected one, or the one a filter or a collapsed
    /// folder is hiding for now.
    /// </summary>
    /// <remarks>
    /// This is what is persisted and what a reload restores. Writing the on-screen selection
    /// instead recorded a search that happened to hide the session as the user deselecting it,
    /// and the next start opened on nothing.
    /// </remarks>
    internal string? EffectiveSelectedServerId => SelectedServer?.Id ?? _hiddenSelection?.PrimaryId;

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

    /// <summary>Whether more than one session is selected.</summary>
    public bool HasMultiSelection => SelectionCount > 1;

    /// <summary>
    /// The selection size, spelled out for the live region; empty for zero or one session.
    /// </summary>
    /// <remarks>
    /// A single row announces itself when it takes focus, so a count of one would be said
    /// twice. The multi-selection is the part a screen reader cannot see on the rows.
    /// </remarks>
    public string SelectionCountText =>
        HasMultiSelection ? _localizer.Format("SessionTreeSelectionCount", SelectionCount) : "";

    /// <summary>
    /// Selects every session on screen, in tree order, keeping the current primary when it is
    /// among them.
    /// </summary>
    /// <remarks>
    /// Visible means inside an expanded branch, which is the same set every range gesture
    /// walks; a collapsed branch keeps its sessions out, as it keeps them out of a Shift range.
    /// </remarks>
    public void SelectAllVisible()
    {
        List<ServerItemViewModel> visible = SelectionHelpers.EnumerateVisibleLeaves(GroupedServers).ToList();
        ApplySelection(visible, SelectedServer, _selectionAnchor, updateSelectedServer: true);
    }

    private void InitializeSelectionModel()
    {
        SelectedItems.CollectionChanged += OnSelectedItemsChanged;
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(SelectionCountText));
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
        NotifyToolDetailChanged();

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
        if (!_synchronizingSelection)
        {
            // The user chose something: the selection a view change was holding back is no
            // longer the one to bring back.
            _hiddenSelection = null;
        }

        var normalized = NormalizeSelection(requestedItems);
        var selectedSet = new HashSet<ServerItemViewModel>(normalized, ReferenceEqualityComparer.Instance);

        List<ServerItemViewModel> previousSelection = SelectedItems.ToList();

        // Publish membership before row notifications: automation peers query the host
        // synchronously when IsSelected changes. One replacement, one notification.
        SelectedItems.ReplaceAll(normalized);

        foreach (ServerItemViewModel previouslySelected in previousSelection)
        {
            if (!selectedSet.Contains(previouslySelected))
            {
                previouslySelected.IsSelected = false;
            }
        }

        foreach (ServerItemViewModel item in normalized)
        {
            item.IsSelected = true;
        }

        var primary = normalized.Count == 0
            ? null
            : preferredPrimary is not null && selectedSet.Contains(preferredPrimary)
                ? preferredPrimary
                : normalized[^1];

        _selectionAnchor = normalized.Count == 0
            ? null
            : preferredAnchor is not null && selectedSet.Contains(preferredAnchor)
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
        var seen = new HashSet<ServerItemViewModel>(ReferenceEqualityComparer.Instance);
        var listed = new HashSet<ServerItemViewModel>(Servers, ReferenceEqualityComparer.Instance);

        foreach (var item in requestedItems)
        {
            if (!listed.Contains(item) || !seen.Add(item))
            {
                continue;
            }

            normalized.Add(item);
        }

        return normalized;
    }

    /// <summary>
    /// Brings the selection in line with what the tree shows after a filter pass, a collapse or
    /// an expand, remembering what the view hid and putting it back once it is shown again.
    /// </summary>
    /// <param name="preferredSelectedServerId">A session to select when it is visible, used by a reload.</param>
    /// <remarks>
    /// A search that hid the selected session used to clear the selection for good: clearing the
    /// search brought the row back unselected, the detail pane stayed blank, and the close flush
    /// saved the empty selection over the one the user had made. Collapsing the folder holding the
    /// selection did the same. A view change now only hides the selection; the user's own choice
    /// is what replaces it.
    /// </remarks>
    private void SynchronizeSelection(string? preferredSelectedServerId)
    {
        List<ServerItemViewModel> visibleLeaves = SelectionHelpers
            .EnumerateVisibleLeaves(GroupedServers)
            .ToList();
        var visible = new HashSet<ServerItemViewModel>(visibleLeaves, ReferenceEqualityComparer.Instance);

        ServerItemViewModel? restored;
        _synchronizingSelection = true;
        try
        {
            restored = SynchronizeSelectionCore(visibleLeaves, visible, preferredSelectedServerId);
        }
        finally
        {
            _synchronizingSelection = false;
        }

        if (restored is not null)
        {
            HiddenSelectionRestored?.Invoke(restored);
        }
    }

    private ServerItemViewModel? SynchronizeSelectionCore(
        List<ServerItemViewModel> visibleLeaves,
        HashSet<ServerItemViewModel> visible,
        string? preferredSelectedServerId)
    {
        if (!string.IsNullOrWhiteSpace(preferredSelectedServerId))
        {
            ServerItemViewModel? preferred = visibleLeaves.FirstOrDefault(
                server => string.Equals(server.Id, preferredSelectedServerId, StringComparison.Ordinal));

            if (preferred is not null)
            {
                _hiddenSelection = null;
                SelectSingle(preferred);
                return null;
            }

            // Asked for a session the view is hiding - a reload under a filter, or a start with
            // its folder closed: hold it back rather than forget it.
            if (_hiddenSelection is null
                && _allServers.Any(server => string.Equals(server.Id, preferredSelectedServerId, StringComparison.Ordinal)))
            {
                _hiddenSelection = new HiddenSelection(
                    [preferredSelectedServerId],
                    preferredSelectedServerId,
                    preferredSelectedServerId);
            }
        }

        if (_hiddenSelection is { } hidden)
        {
            Dictionary<string, ServerItemViewModel> byId = new(StringComparer.Ordinal);
            foreach (ServerItemViewModel server in _allServers)
            {
                byId.TryAdd(server.Id, server);
            }

            List<ServerItemViewModel> remembered = hidden.Ids
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<ServerItemViewModel>()
                .ToList();
            if (remembered.Count > 0)
            {
                List<ServerItemViewModel> shown = remembered.Where(visible.Contains).ToList();
                ServerItemViewModel? primary = shown.FirstOrDefault(server =>
                    string.Equals(server.Id, hidden.PrimaryId, StringComparison.Ordinal));
                ServerItemViewModel? anchor = shown.FirstOrDefault(server =>
                    string.Equals(server.Id, hidden.AnchorId, StringComparison.Ordinal));
                bool complete = shown.Count == remembered.Count;
                if (complete)
                {
                    _hiddenSelection = null;
                }

                ApplySelection(shown, primary, anchor, updateSelectedServer: true);
                return complete ? SelectedServer : null;
            }

            // Every remembered session is gone from the inventory: nothing is left to restore.
            _hiddenSelection = null;
        }

        var visibleSelection = SelectedItems
            .Where(visible.Contains)
            .ToList();

        if (visibleSelection.Count < SelectedItems.Count)
        {
            _hiddenSelection = new HiddenSelection(
                SelectedItems.Select(server => server.Id).ToList(),
                SelectedServer?.Id,
                _selectionAnchor?.Id);
        }

        if (visibleSelection.Count == 0)
        {
            ClearSelection();
            return null;
        }

        var primaryVisible = SelectedServer is not null && visible.Contains(SelectedServer)
            ? SelectedServer
            : visibleSelection.LastOrDefault();
        var anchorVisible = _selectionAnchor is not null && visible.Contains(_selectionAnchor)
            ? _selectionAnchor
            : primaryVisible;

        ApplySelection(visibleSelection, primaryVisible, anchorVisible, updateSelectedServer: true);
        return null;
    }

    /// <summary>A selection a view change took off the screen, by id.</summary>
    private sealed record HiddenSelection(IReadOnlyList<string> Ids, string? PrimaryId, string? AnchorId);
}
