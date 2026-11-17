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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the restore-on-launch session snapshot dialog.
/// </summary>
public partial class SnapshotRestoreDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private bool _syncingSelection;

    public SnapshotRestoreDialogViewModel(
        LocalizationManager localizer,
        SessionSnapshotFile snapshot,
        IEnumerable<ServerItemViewModel> servers)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(servers);

        _localizer = localizer;
        Snapshot = snapshot;

        DialogTitle = _localizer["DialogSnapshotRestoreTitle"];
        MessageText = _localizer["DialogSnapshotRestoreMessage"];
        WarningText = _localizer["DialogSnapshotRestoreWarning"];
        SavedAtText = _localizer.Format("DialogSnapshotRestoreSavedAt", snapshot.SavedAtUtc.ToLocalTime());
        SessionColumnHeader = _localizer["DialogSnapshotRestoreColumnSession"];
        ProtocolColumnHeader = _localizer["DialogSnapshotRestoreColumnProtocol"];
        SelectAllText = _localizer["DialogSnapshotRestoreSelectAll"];
        DontRestoreText = _localizer["DialogSnapshotRestoreBtnDontRestore"];
        RestoreSelectedText = _localizer["DialogSnapshotRestoreBtnRestoreSelected"];

        // TryAdd: a list holding one identifier twice must not stop the dialog from opening.
        var serverMap = new Dictionary<string, ServerItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (ServerItemViewModel server in servers)
        {
            serverMap.TryAdd(server.Id, server);
        }

        Sessions = new ObservableCollection<SnapshotRestoreItemViewModel>(
            snapshot.Sessions
                .OrderBy(session => session.Order)
                .Select(session => CreateItem(session, serverMap)));

        foreach (var session in Sessions)
        {
            session.PropertyChanged += OnSessionPropertyChanged;
        }

        RefreshSelectionState();
    }

    public SessionSnapshotFile Snapshot { get; }

    public string DialogTitle { get; }

    public string MessageText { get; }

    public string WarningText { get; }

    public string SavedAtText { get; }

    public string SessionColumnHeader { get; }

    public string ProtocolColumnHeader { get; }

    public string SelectAllText { get; }

    public string DontRestoreText { get; }

    public string RestoreSelectedText { get; }

    public ObservableCollection<SnapshotRestoreItemViewModel> Sessions { get; }

    [ObservableProperty]
    private bool _allSelected = true;

    public bool CanRestoreSelected => Sessions.Any(session => session.IsSelected);

    public SnapshotRestoreDialogResult? Result { get; private set; }

    public event Action? CloseRequested;

    partial void OnAllSelectedChanged(bool value)
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            foreach (var session in Sessions)
            {
                session.IsSelected = value;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        RefreshSelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSelected))]
    private void RestoreSelected()
    {
        if (!CanRestoreSelected)
        {
            return;
        }

        Result = new SnapshotRestoreDialogResult(
            SnapshotRestoreDialogAction.RestoreSelected,
            Sessions
                .Where(session => session.IsSelected)
                .OrderBy(session => session.Entry.Order)
                .Select(session => session.Entry)
                .ToList());

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void DontRestore()
    {
        Result = new SnapshotRestoreDialogResult(
            SnapshotRestoreDialogAction.DontRestore,
            []);

        CloseRequested?.Invoke();
    }

    private SnapshotRestoreItemViewModel CreateItem(
        SessionSnapshotEntry entry,
        IReadOnlyDictionary<string, ServerItemViewModel> serverMap)
    {
        var displayName = serverMap.TryGetValue(entry.ServerId, out var server)
            ? server.DisplayName
            : _localizer.Format("DialogSnapshotRestoreUnknownServer", entry.ServerId);

        return new SnapshotRestoreItemViewModel(entry, displayName, entry.ConnectionType);
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SnapshotRestoreItemViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        _syncingSelection = true;
        try
        {
            AllSelected = Sessions.Count > 0 && Sessions.All(session => session.IsSelected);
        }
        finally
        {
            _syncingSelection = false;
        }

        RestoreSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRestoreSelected));
    }
}

/// <summary>
/// Selectable row displayed in the restore dialog.
/// </summary>
public partial class SnapshotRestoreItemViewModel(
    SessionSnapshotEntry entry,
    string displayName,
    string protocolText) : ObservableObject
{
    public SessionSnapshotEntry Entry { get; } = entry;

    public string DisplayName { get; } = displayName;

    public string ProtocolText { get; } = protocolText;

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>
/// User choice returned by the session snapshot restore dialog.
/// </summary>
public sealed record SnapshotRestoreDialogResult(
    SnapshotRestoreDialogAction Action,
    IReadOnlyList<SessionSnapshotEntry> Sessions);

/// <summary>
/// Result action for the session snapshot restore dialog.
/// </summary>
public enum SnapshotRestoreDialogAction
{
    RestoreSelected,
    DontRestore,
}
