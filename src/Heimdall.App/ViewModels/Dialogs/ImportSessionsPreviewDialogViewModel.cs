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
using Heimdall.App.Services.Import;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Shared non-generic base class for session import preview dialogs.
/// </summary>
public abstract partial class ImportSessionsPreviewDialogViewModel(LocalizationManager localizer) : ObservableObject
{
    private readonly LocalizationManager _localizer = localizer;
    private bool _syncingSelection;

    public ObservableCollection<ImportSessionItemViewModel> Items { get; } = [];

    public ObservableCollection<ImportSessionDiagnosticViewModel> Diagnostics { get; } = [];

    public abstract string DialogTitle { get; }

    public string ConfirmText => _localizer["BtnConfirmImport"];

    public string CancelText => _localizer["BtnCancel"];

    public string ImportAllText => _localizer["ChkImportAll"];

    public string ImportColumnHeader => _localizer["HeaderImport"];

    public string AliasColumnHeader => _localizer["HeaderAlias"];

    public string HostNameColumnHeader => _localizer["HeaderHostName"];

    public string PortColumnHeader => _localizer["HeaderPort"];

    public string UserColumnHeader => _localizer["HeaderUser"];

    public string IdentityFileColumnHeader => _localizer["HeaderIdentityFile"];

    public string GatewayChainColumnHeader => _localizer["ColGatewayChain"];

    public string StatusColumnHeader => _localizer["HeaderStatus"];

    public string SummaryText
    {
        get
        {
            return ImportSummaryText.SessionPreview(
                _localizer,
                Items.Count,
                Items.Count(item => item.Status == ImportCandidateStatus.New),
                Items.Count(item => item.Status == ImportCandidateStatus.Duplicate),
                Items.Count(item => item.Status == ImportCandidateStatus.Invalid));
        }
    }

    public string DiagnosticsHeader => _localizer.Format("LabelDiagnosticsExpander", Diagnostics.Count);

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public ImportOutcome? Result { get; protected set; }

    public event Action<bool>? CloseRequested;

    [ObservableProperty]
    private bool _allSelected;

    [ObservableProperty]
    private bool _isDiagnosticsExpanded;

    public bool CanConfirm => Items.Any(item => item.IsSelectable && item.IsSelected);

    protected LocalizationManager Localizer => _localizer;

    protected void SetPreviewData(
        IEnumerable<ImportSessionItemViewModel> items,
        IEnumerable<ImportSessionDiagnosticViewModel> diagnostics)
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();
        Diagnostics.Clear();

        foreach (var item in items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        foreach (var diagnostic in diagnostics)
        {
            Diagnostics.Add(diagnostic);
        }

        RefreshState();
    }

    partial void OnAllSelectedChanged(bool value)
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            foreach (var item in Items.Where(item => item.IsSelectable))
            {
                item.IsSelected = value;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        RefreshState();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var selected = Items
            .Where(item => item.IsSelectable && item.IsSelected)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var outcome = await PerformImportAsync(selected).ConfigureAwait(true);
        Result = outcome with { WarningCount = outcome.WarningCount + Diagnostics.Count };
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(false);
    }

    protected abstract Task<ImportOutcome> PerformImportAsync(IReadOnlyList<ImportSessionItemViewModel> selectedItems);

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportSessionItemViewModel.IsSelected))
        {
            RefreshState();
        }
    }

    private void RefreshState()
    {
        var selectable = Items.Where(item => item.IsSelectable).ToList();

        _syncingSelection = true;
        try
        {
            AllSelected = selectable.Count > 0 && selectable.All(item => item.IsSelected);
        }
        finally
        {
            _syncingSelection = false;
        }

        ConfirmCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DiagnosticsHeader));
        OnPropertyChanged(nameof(HasDiagnostics));
    }
}
