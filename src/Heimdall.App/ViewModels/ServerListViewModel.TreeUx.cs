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
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel
{
    public ObservableCollection<TreeFilterChip> ActiveFilterChips { get; } = [];
    public bool HasTreeFilters => ActiveFilterChips.Count > 0;
    public bool HasNoTreeResults => HasTreeFilters && HasAppliedFilterResult && FilteredCount == 0;
    public bool ShowSearchContext => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasNoInventory => _allServers.Count == 0;

    /// <summary>
    /// How long the undo bar offers the last organization change before it withdraws.
    /// </summary>
    /// <remarks>
    /// The bar used to stay until the next change, so a user coming back to it minutes later saw
    /// "Undo" beside nothing they remembered doing, and one delete-folder later it offered to
    /// undo a move that was no longer the last thing done.
    /// </remarks>
    internal static readonly TimeSpan OrganizationUndoLifetime = TimeSpan.FromSeconds(30);

    private bool _resettingTreeFilters;
    private TreeOrganizationHistory? _treeOrganizationHistory;
    private TreeOrganizationHistory OrganizationHistory => _treeOrganizationHistory ??= new(_configManager);
    private TreeOrganizationChange _lastOrganizationChange;
    private ITimer? _organizationUndoExpiry;
    public bool CanUndoTreeOrganization => _treeOrganizationHistory?.CanUndo == true;

    /// <summary>What the undo bar says it would undo: the last recorded change, by name.</summary>
    public string UndoTreeOrganizationText => _localizer[_lastOrganizationChange switch
    {
        TreeOrganizationChange.Rename => "TreeUxChangedRename",
        TreeOrganizationChange.FolderRename => "TreeUxChangedFolderRename",
        TreeOrganizationChange.FolderMove => "TreeUxChangedFolderMove",
        TreeOrganizationChange.Reorder => "TreeUxChangedReorder",
        _ => "TreeUxChangedMove",
    }];

    /// <summary>
    /// Withdraws the undo offer, for an organization change the history cannot reverse: left in
    /// place, the bar would offer to undo an older change as if it were the last one.
    /// </summary>
    public void ClearOrganizationUndo()
    {
        _organizationUndoExpiry?.Dispose();
        _organizationUndoExpiry = null;
        _treeOrganizationHistory?.Clear();
        OnPropertyChanged(nameof(CanUndoTreeOrganization));
        UndoTreeOrganizationCommand.NotifyCanExecuteChanged();
    }

    private void OfferOrganizationUndo(TreeOrganizationChange change)
    {
        _lastOrganizationChange = change;
        OnPropertyChanged(nameof(UndoTreeOrganizationText));
        _organizationUndoExpiry?.Dispose();
        _organizationUndoExpiry = _timeProvider.CreateTimer(
            _ => _ = _uiDispatcher.InvokeAsync(ClearOrganizationUndo),
            null,
            OrganizationUndoLifetime,
            Timeout.InfiniteTimeSpan);
    }

    [RelayCommand]
    private void ClearTreeSearch() => SearchText = "";

    [RelayCommand]
    private void ResetTreeFilters()
    {
        _resettingTreeFilters = true;
        try
        {
            SearchText = "";
            SelectedProject = "";
            FavoriteFilterEnabled = false;
            ConnectedFilterEnabled = false;
            GatewayFilterEnabled = false;
            foreach (ProtocolFilterOptionViewModel option in ProtocolFilters)
            {
                option.IsSelected = false;
            }
        }
        finally
        {
            _resettingTreeFilters = false;
        }

        ApplyDiscreteFilter();
    }

    private void RefreshTreeFilterChips()
    {
        ActiveFilterChips.Clear();
        if (!string.IsNullOrWhiteSpace(SearchText))
            AddChip(SearchText, () => SearchText = "");
        if (!string.IsNullOrWhiteSpace(SelectedProject))
            AddChip(SelectedProject, () => SelectedProject = "");
        foreach (ProtocolFilterOptionViewModel option in ProtocolFilters.Where(option => option.IsSelected))
            AddChip(option.DisplayName, () => option.IsSelected = false);
        if (FavoriteFilterEnabled) AddChip(_localizer["FilterFavorite"], () => FavoriteFilterEnabled = false);
        if (ConnectedFilterEnabled) AddChip(_localizer["FilterConnected"], () => ConnectedFilterEnabled = false);
        if (GatewayFilterEnabled) AddChip(_localizer["FilterGateway"], () => GatewayFilterEnabled = false);
        OnPropertyChanged(nameof(HasTreeFilters));
        OnPropertyChanged(nameof(ShowSearchContext));

        void AddChip(string label, Action remove) => ActiveFilterChips.Add(
            new TreeFilterChip(label, _localizer.Format("TreeUxRemoveFilter", label), new RelayCommand(remove)));
    }

    /// <summary>Records the latest successful organization change for explicit undo.</summary>
    /// <param name="change">What the change is, so the undo bar can name it.</param>
    /// <param name="operation">The change itself.</param>
    /// <param name="reverseFolder">How to reverse a folder path change, when it is one.</param>
    /// <param name="serverIds">The sessions whose organization fields the change may touch.</param>
    public async Task<T> WithOrganizationUndoAsync<T>(
        TreeOrganizationChange change,
        Func<Task<T>> operation,
        Func<T, FolderRenamePlan?>? reverseFolder = null,
        IReadOnlyCollection<string>? serverIds = null)
    {
        int recordedBefore = OrganizationHistory.RecordedCount;
        try
        {
            return await OrganizationHistory.ExecuteAsync(operation, reverseFolder, serverIds);
        }
        finally
        {
            if (OrganizationHistory.RecordedCount != recordedBefore && OrganizationHistory.CanUndo)
            {
                OfferOrganizationUndo(change);
            }

            OnPropertyChanged(nameof(CanUndoTreeOrganization));
            UndoTreeOrganizationCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndoTreeOrganization))]
    private async Task UndoTreeOrganizationAsync()
    {
        try
        {
            await FlushExpandStateForCloseAsync();
            bool restored = await OrganizationHistory.UndoAsync();
            if (restored)
            {
                LoadServers(await _configManager.LoadServersAsync(), await _configManager.LoadSettingsAsync());
            }
            StatusMessageRequested?.Invoke(_localizer[restored ? "TreeUxUndoDone" : "TreeUxUndoConflict"]);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Tree organization undo failed", ex);
            _dialogService.ShowError(_localizer["TreeUxUndo"], _localizer["TreeUxUndoFailed"]);
        }
        finally
        {
            _organizationUndoExpiry?.Dispose();
            _organizationUndoExpiry = null;
            OnPropertyChanged(nameof(CanUndoTreeOrganization));
            UndoTreeOrganizationCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>A removable, accessible active filter.</summary>
public sealed record TreeFilterChip(string Label, string RemoveAccessibilityName, IRelayCommand RemoveCommand);

/// <summary>The kinds of organization change the undo bar can name.</summary>
public enum TreeOrganizationChange
{
    /// <summary>Sessions moved into another folder.</summary>
    Move,

    /// <summary>Sessions reordered within their folder.</summary>
    Reorder,

    /// <summary>A session renamed.</summary>
    Rename,

    /// <summary>A folder renamed.</summary>
    FolderRename,

    /// <summary>A folder moved under another parent.</summary>
    FolderMove,
}
