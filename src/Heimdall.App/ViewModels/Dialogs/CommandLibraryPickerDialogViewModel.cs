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
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;

namespace Heimdall.App.ViewModels.Dialogs;

public partial class CommandLibraryPickerDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly IServiceScopeFactory _scopeFactory;
    private AutoPrefillContext? _prefillContext;
    private string? _preselectedActionId;
    private IReadOnlyDictionary<string, string>? _existingValues;

    public ObservableCollection<CommandLibraryPickerItem> Actions { get; } = [];
    public ObservableCollection<TemplateParameterInputViewModel> Parameters { get; } = [];
    private readonly ICollectionView _actionsView;

    public ICollectionView ActionsView => _actionsView;

    public CommandLibraryPickerDialogViewModel(LocalizationManager localizer, IServiceScopeFactory scopeFactory)
    {
        _localizer = localizer;
        _scopeFactory = scopeFactory;
        _actionsView = CollectionViewSource.GetDefaultView(Actions);
    }

    public string? PlatformFilter
    {
        get => _platformFilter;
        set
        {
            SetProperty(ref _platformFilter, value);
            _actionsView.Refresh();
        }
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    private string? _platformFilter;

    [ObservableProperty]
    private CommandLibraryPickerItem? _selectedAction;

    [ObservableProperty]
    private string? _errorMessage;

    public string DialogTitle => _localizer["DialogTitleCommandLibraryPicker"];

    public string? ResultActionId { get; private set; }

    public Dictionary<string, string>? ResultParams { get; private set; }

    public string? ResultActionTitle { get; private set; }

    public event Action? CloseRequested;

    public bool CanConfirm =>
        SelectedAction is not null
        && SelectedAction.HasLinuxTemplate
        && Parameters.All(parameter => !parameter.Required || !string.IsNullOrWhiteSpace(parameter.Value));

    public Task InitializeAsync(AutoPrefillContext? prefillContext = null)
    {
        _prefillContext = prefillContext;
        _preselectedActionId = null;
        _existingValues = null;
        ResultActionId = null;
        ResultActionTitle = null;
        ResultParams = null;
        return LoadAsync();
    }

    public Task InitializeForChangeAsync(
        AutoPrefillContext prefillContext,
        string existingActionId,
        IReadOnlyDictionary<string, string> existingValues)
    {
        ArgumentNullException.ThrowIfNull(prefillContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingActionId);
        ArgumentNullException.ThrowIfNull(existingValues);

        _prefillContext = prefillContext;
        _preselectedActionId = existingActionId;
        _existingValues = existingValues;
        ResultActionId = null;
        ResultActionTitle = null;
        ResultParams = null;
        return LoadAsync();
    }

    partial void OnSearchTextChanged(string value) => _actionsView.Refresh();

    partial void OnSelectedActionChanged(CommandLibraryPickerItem? value)
    {
        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged -= OnParameterPropertyChanged;
        }

        Parameters.Clear();
        ErrorMessage = null;

        if (value is null)
        {
            OnPropertyChanged(nameof(CanConfirm));
            ConfirmCommand.NotifyCanExecuteChanged();
            return;
        }

        if (!value.HasLinuxTemplate)
        {
            ErrorMessage = _localizer["LabelCommandLibraryPickerNoLinuxTemplate"];
            OnPropertyChanged(nameof(CanConfirm));
            ConfirmCommand.NotifyCanExecuteChanged();
            return;
        }

        foreach (var parameter in value.LinuxParameters)
        {
            var vm = new TemplateParameterInputViewModel(parameter);
            vm.PropertyChanged += OnParameterPropertyChanged;
            Parameters.Add(vm);
        }

        ApplyParameterValues(value);

        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var actionService = scope.ServiceProvider.GetRequiredService<IActionService>();
        var items = (await actionService.GetAllActionsAsync().ConfigureAwait(false))
            .Select(action => new CommandLibraryPickerItem
            {
                ActionId = action.Id,
                Title = action.Title,
                Category = action.Category,
                HasLinuxTemplate = action.LinuxCommandTemplate is not null,
                HasWindowsTemplate = action.WindowsCommandTemplate is not null,
                LinuxParameters = action.LinuxCommandTemplate?.Parameters?.ToList() ?? [],
                Presentation = CommandPresentationResolver.Resolve(action, key => _localizer[key])
            })
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Actions.Clear();
        foreach (var item in items)
        {
            Actions.Add(item);
        }

        _actionsView.Filter = FilterAction;
        _actionsView.Refresh();

        if (!string.IsNullOrWhiteSpace(_preselectedActionId))
        {
            var preselected = Actions.FirstOrDefault(item =>
                string.Equals(item.ActionId, _preselectedActionId, StringComparison.Ordinal));
            if (preselected is not null)
            {
                SelectedAction = preselected;
            }
            else
            {
                ErrorMessage = _localizer["ErrorCommandLibraryPickerActionMissing"];
            }
        }

        ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (SelectedAction is null)
        {
            return;
        }

        var missing = Parameters.FirstOrDefault(parameter =>
            parameter.Required && string.IsNullOrWhiteSpace(parameter.Value));
        if (missing is not null)
        {
            ErrorMessage = _localizer.Format("ErrorCommandLibraryPickerRequiredParam", missing.Label);
            OnPropertyChanged(nameof(CanConfirm));
            return;
        }

        ResultActionId = SelectedAction.ActionId;
        ResultActionTitle = SelectedAction.Title;
        ResultParams = Parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .ToDictionary(parameter => parameter.Name, parameter => parameter.Value, StringComparer.Ordinal);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        ResultActionId = null;
        ResultActionTitle = null;
        ResultParams = null;
        CloseRequested?.Invoke();
    }

    private bool FilterAction(object item)
    {
        if (item is not CommandLibraryPickerItem action)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText)
            && action.Title.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0
            && action.Category.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return PlatformFilter switch
        {
            "Linux" => action.HasLinuxTemplate,
            "Windows" => action.HasWindowsTemplate,
            "Both" => action.HasLinuxTemplate && action.HasWindowsTemplate,
            _ => true
        };
    }

    private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(TemplateParameterInputViewModel.Value), StringComparison.Ordinal))
        {
            return;
        }

        ErrorMessage = null;
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private void ApplyParameterValues(CommandLibraryPickerItem action)
    {
        if (_existingValues is not null)
        {
            foreach (var parameter in Parameters)
            {
                if (_existingValues.TryGetValue(parameter.Name, out var existing))
                {
                    parameter.Value = existing ?? string.Empty;
                }
            }
        }

        if (_prefillContext is null)
        {
            return;
        }

        var prefills = PostConnectParameterAutoPrefiller.Prefill(
            action.LinuxParameters,
            _prefillContext,
            _existingValues);

        foreach (var parameter in Parameters)
        {
            if (_existingValues?.ContainsKey(parameter.Name) == true)
            {
                continue;
            }

            if (prefills.TryGetValue(parameter.Name, out var prefilled)
                && !string.IsNullOrEmpty(prefilled))
            {
                parameter.ApplyPrefilledValue(prefilled);
            }
        }
    }
}

public sealed record CommandLibraryPickerResult(
    string ActionId,
    string ActionTitle,
    Dictionary<string, string> Parameters);
