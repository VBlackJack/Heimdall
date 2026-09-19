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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// Display model for a single template parameter shown in the generator panel.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is an observable property so XAML bindings and the
/// owning <see cref="CommandLibraryViewModel"/> can react to user edits
/// (regenerate the command output) via <see cref="ObservableObject.PropertyChanged"/>.
/// </remarks>
public sealed partial class CommandLibraryParameterEntry : ObservableObject
{
    /// <summary>Parameter machine name as defined in the template.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Localized display label for the parameter.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Current value entered by the user (or the default value).</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Whether the parameter must be filled in for the command to be valid.</summary>
    public bool Required { get; init; }

    /// <summary>Optional human-readable description shown as a tooltip.</summary>
    public string? Description { get; init; }

    /// <summary>Parameter type hint (string, int, hostname, ipaddress, choice, ...).</summary>
    public string Type { get; init; } = "string";

    /// <summary>
    /// The values this parameter offers, when it offers a fixed set. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>
    /// True when the panel should offer a list to pick from rather than a box to type in.
    /// </summary>
    /// <remarks>
    /// A choice with nothing left to offer falls back to a box, matching what the
    /// generator does: it validates membership only when there is a set to be a member of,
    /// so a panel that offered an empty list would refuse what the generator accepts.
    /// </remarks>
    public bool IsChoice => AllowedValues.Count > 0;

    /// <summary>True when the panel should offer a box to type in.</summary>
    public bool IsFreeText => !IsChoice;

    /// <summary>
    /// Display label used by the parameter editor; appends a "*" marker for
    /// required fields and falls back to <see cref="Name"/> when no label was
    /// provided in the template.
    /// </summary>
    public string DisplayLabel
        => $"{(string.IsNullOrWhiteSpace(Label) ? Name : Label)}{(Required ? " *" : string.Empty)}";

    /// <summary>
    /// Tooltip showing the parameter type and (when present) the description.
    /// </summary>
    public string DisplayTooltip => string.IsNullOrEmpty(Description)
        ? $"[{Type}]"
        : $"[{Type}] {Description}";
}
