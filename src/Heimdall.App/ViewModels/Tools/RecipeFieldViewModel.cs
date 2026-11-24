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

using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.Core.Recipes;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// One input field of a recipe as the view shows it: its label, its value, and the reason the last
/// run refused the value, if it did. The value is held as text (a switch as a flag), exactly as the
/// user typed it; what it may contain is decided by Core when the run starts.
/// </summary>
public sealed partial class RecipeFieldViewModel : ObservableObject
{
    private const string EmptyChoice = "";

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string? _selectedChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _isLocked;

    /// <summary>Creates the field of an input, filled with its default.</summary>
    /// <param name="input">The input.</param>
    /// <param name="label">The label to show; the English label unless the recipe translates it.</param>
    public RecipeFieldViewModel(RecipeInput input, string label)
    {
        ArgumentNullException.ThrowIfNull(input);

        Input = input;
        _label = label;

        if (input.Type == RecipeInputType.Choice)
        {
            // An optional choice may be left empty, so the closed list gets an empty entry.
            List<string> choices = [];
            if (input.IsOptional)
            {
                choices.Add(EmptyChoice);
            }

            choices.AddRange(input.Choices ?? []);
            Choices = choices;
            _selectedChoice = input.Default is { } preset && choices.Contains(preset, StringComparer.Ordinal)
                ? preset
                : choices.Count > 0 && input.Required ? choices[0] : EmptyChoice;
        }
        else if (input.Type == RecipeInputType.Bool)
        {
            _isChecked = string.Equals(input.Default, RecipeFormat.TrueText, StringComparison.Ordinal);
        }
        else
        {
            _text = input.Default ?? string.Empty;
        }
    }

    /// <summary>The input this field edits.</summary>
    public RecipeInput Input { get; }

    /// <summary>The name of the input, which the expansion keys its value by.</summary>
    public string Name => Input.Name;

    /// <summary>True for a field edited as text.</summary>
    public bool IsText => Input.Type is not (RecipeInputType.Bool or RecipeInputType.Choice);

    /// <summary>True for a switch.</summary>
    public bool IsBool => Input.Type == RecipeInputType.Bool;

    /// <summary>True when the label is its own line; a switch carries its label inside itself.</summary>
    public bool ShowLabel => !IsBool;

    /// <summary>True for a closed list.</summary>
    public bool IsChoice => Input.Type == RecipeInputType.Choice;

    /// <summary>The values a closed list offers; empty for any other field.</summary>
    public IReadOnlyList<string> Choices { get; } = [];

    /// <summary>The name an assistive technology reads for the field.</summary>
    public string AutomationName => Label;

    /// <summary>True when the last run refused the value.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>The value to hand to <see cref="RecipeExpander"/>: a flag as <c>true</c> or <c>false</c>.</summary>
    public string? Value => Input.Type switch
    {
        RecipeInputType.Bool => IsChecked ? RecipeFormat.TrueText : RecipeFormat.FalseText,
        RecipeInputType.Choice => SelectedChoice,
        _ => Text,
    };

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(AutomationName));

    partial void OnTextChanged(string value) => Error = string.Empty;

    partial void OnIsCheckedChanged(bool value) => Error = string.Empty;

    partial void OnSelectedChoiceChanged(string? value) => Error = string.Empty;
}
