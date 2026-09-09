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

    private bool _resettingTreeFilters;
    private TreeOrganizationHistory? _treeOrganizationHistory;
    private TreeOrganizationHistory OrganizationHistory => _treeOrganizationHistory ??= new(_configManager);
    public bool CanUndoTreeOrganization => _treeOrganizationHistory?.CanUndo == true;

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
    public async Task<T> WithOrganizationUndoAsync<T>(Func<Task<T>> operation,
        Func<T, FolderRenamePlan?>? reverseFolder = null,
        IReadOnlyCollection<string>? serverIds = null)
    {
        try
        {
            return await OrganizationHistory.ExecuteAsync(operation, reverseFolder, serverIds);
        }
        finally
        {
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
            OnPropertyChanged(nameof(CanUndoTreeOrganization));
            UndoTreeOrganizationCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>A removable, accessible active filter.</summary>
public sealed record TreeFilterChip(string Label, string RemoveAccessibilityName, IRelayCommand RemoveCommand);
