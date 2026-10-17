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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The MobaXterm user variables: the values that replace <c>[NAME]</c> in a MobaXterm session
/// file when it is imported.
/// </summary>
/// <remarks>
/// <para>Edited like the external tools: a list of the user's own entries, which the value
/// comparison cannot read back, so an edit keeps the panel dirty until it is saved or undone, and
/// a factory reset leaves them as they are on disk.</para>
/// <para>Every row is validated as it is edited and shows its own error. A row in error refuses
/// the save, as any field in error does, so nothing invalid reaches the settings file; the save
/// also writes valid rows only, so a caller that skips the check cannot write one either.</para>
/// </remarks>
public partial class SettingsViewModel
{
    [ObservableProperty]
    private ObservableCollection<MobaXtermVariableItemViewModel> _mobaXtermUserVariables = new();

    /// <summary>Set by a save refused over a variable row; cleared by the next edit to the list.</summary>
    private string? _mobaXtermVariablesValidationError;

    /// <summary>Whether any row is in error, as last validated.</summary>
    public bool HasInvalidMobaXtermVariables => MobaXtermUserVariables.Any(row => !row.IsValid);

    [RelayCommand]
    private void AddMobaXtermVariable()
    {
        MobaXtermUserVariables.Add(new MobaXtermVariableItemViewModel());
        MarkEditTheComparisonCannotSee();
    }

    [RelayCommand]
    private void RemoveMobaXtermVariable(MobaXtermVariableItemViewModel? row)
    {
        if (row is null || !MobaXtermUserVariables.Remove(row))
        {
            return;
        }

        MarkEditTheComparisonCannotSee();
    }

    /// <summary>Replaces the rows with the variables in <paramref name="settings"/>.</summary>
    private void LoadMobaXtermUserVariables(AppSettings settings)
    {
        UnsubscribeMobaXtermVariableTracking();

        MobaXtermUserVariables = new ObservableCollection<MobaXtermVariableItemViewModel>(
            (settings.MobaXtermUserVariables ?? [])
                .Select(pair => new MobaXtermVariableItemViewModel { Name = pair.Key, Value = pair.Value }));

        SubscribeMobaXtermVariableTracking();
        _mobaXtermVariablesValidationError = null;
        RevalidateMobaXtermVariables();
    }

    /// <summary>The variables Save writes: the valid rows, trimmed.</summary>
    /// <remarks>
    /// Reads the rows and changes nothing: Save calls it inside the settings write, which may run
    /// off the UI thread, so it decides validity itself rather than through the row errors.
    /// </remarks>
    private Dictionary<string, string> BuildMobaXtermUserVariables()
    {
        List<MobaXtermVariableItemViewModel> rows = MobaXtermUserVariables.ToList();
        List<(string? NameErrorKey, string? ValueErrorKey)> errors = MobaXtermVariableErrorKeys(rows);
        var variables = new Dictionary<string, string>();
        for (int index = 0; index < rows.Count; index++)
        {
            if (errors[index] is (null, null))
            {
                variables[rows[index].Name.Trim()] = rows[index].Value.Trim();
            }
        }

        return variables;
    }

    /// <summary>
    /// The locale key of each row's name and value error, in row order; null where there is none.
    /// A name is required, must be one the import recognises, and must not repeat another row's
    /// name in any case; a value is required.
    /// </summary>
    private static List<(string? NameErrorKey, string? ValueErrorKey)> MobaXtermVariableErrorKeys(
        IReadOnlyList<MobaXtermVariableItemViewModel> rows)
    {
        Dictionary<string, int> nameCounts = rows
            .Select(row => row.Name.Trim())
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return rows
            .Select(row =>
            {
                string name = row.Name.Trim();
                string? nameErrorKey = name.Length == 0
                    ? "SettingsMobaXtermVariableNameRequired"
                    : !MobaXtermVariableResolver.IsValidName(name)
                        ? "SettingsMobaXtermVariableNameInvalid"
                        : nameCounts.TryGetValue(name, out int count) && count > 1
                            ? "SettingsMobaXtermVariableNameDuplicate"
                            : null;
                string? valueErrorKey = string.IsNullOrWhiteSpace(row.Value)
                    ? "SettingsMobaXtermVariableValueRequired"
                    : null;
                return (nameErrorKey, valueErrorKey);
            })
            .ToList();
    }

    /// <summary>Sets the localized error of every row from <see cref="MobaXtermVariableErrorKeys"/>.</summary>
    private void RevalidateMobaXtermVariables()
    {
        List<MobaXtermVariableItemViewModel> rows = MobaXtermUserVariables.ToList();
        List<(string? NameErrorKey, string? ValueErrorKey)> errors = MobaXtermVariableErrorKeys(rows);
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].NameError = errors[index].NameErrorKey is { } nameKey ? _localizer[nameKey] : null;
            rows[index].ValueError = errors[index].ValueErrorKey is { } valueKey ? _localizer[valueKey] : null;
        }

        OnPropertyChanged(nameof(HasInvalidMobaXtermVariables));
    }

    /// <summary>
    /// The banner text when a row is in error, or null; recorded for the summary that a refused
    /// save shows.
    /// </summary>
    private string? ValidateMobaXtermVariables()
    {
        RevalidateMobaXtermVariables();
        return HasInvalidMobaXtermVariables
            ? _localizer["ValidationMobaXtermVariablesInvalid"]
            : null;
    }

    private void SubscribeMobaXtermVariableTracking()
    {
        foreach (MobaXtermVariableItemViewModel row in MobaXtermUserVariables)
        {
            row.PropertyChanged += OnMobaXtermVariablePropertyChanged;
        }

        MobaXtermUserVariables.CollectionChanged += OnMobaXtermVariablesCollectionChanged;
    }

    private void UnsubscribeMobaXtermVariableTracking()
    {
        if (MobaXtermUserVariables is null)
        {
            return;
        }

        foreach (MobaXtermVariableItemViewModel row in MobaXtermUserVariables)
        {
            row.PropertyChanged -= OnMobaXtermVariablePropertyChanged;
        }

        MobaXtermUserVariables.CollectionChanged -= OnMobaXtermVariablesCollectionChanged;
    }

    private void OnMobaXtermVariablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the two fields the user types into are edits; the errors this panel sets on the row
        // raise changes too, and reacting to those would loop.
        if (e.PropertyName is not (nameof(MobaXtermVariableItemViewModel.Name)
            or nameof(MobaXtermVariableItemViewModel.Value)))
        {
            return;
        }

        MarkEditTheComparisonCannotSee();
        OnMobaXtermVariablesEdited();
    }

    private void OnMobaXtermVariablesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MobaXtermVariableItemViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnMobaXtermVariablePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MobaXtermVariableItemViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnMobaXtermVariablePropertyChanged;
            }
        }

        OnMobaXtermVariablesEdited();
    }

    /// <summary>
    /// Revalidates every row, since one row's name decides whether another is a duplicate, and
    /// withdraws the banner a refused save raised once the list changes.
    /// </summary>
    private void OnMobaXtermVariablesEdited()
    {
        RevalidateMobaXtermVariables();
        if (_mobaXtermVariablesValidationError is null)
        {
            return;
        }

        _mobaXtermVariablesValidationError = null;
        RefreshValidationSummary();
    }
}
