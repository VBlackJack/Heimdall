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
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// One MobaXterm user variable in the settings panel: a name, its value, and what the value
/// resolves to when a session file is imported.
/// </summary>
/// <remarks>
/// The errors are set by the panel, not computed here: whether a name is a duplicate depends on
/// the other rows, so only the owner of the list can say.
/// </remarks>
public partial class MobaXtermVariableItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedValue))]
    [NotifyPropertyChangedFor(nameof(IsResolved))]
    private string _value = string.Empty;

    /// <summary>The localized reason the name cannot be saved, or null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string? _nameError;

    /// <summary>The localized reason the value cannot be saved, or null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValueError))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string? _valueError;

    public bool HasNameError => NameError is not null;

    public bool HasValueError => ValueError is not null;

    /// <summary>Whether the row may be saved, as last validated by the panel.</summary>
    public bool IsValid => NameError is null && ValueError is null;

    /// <summary>
    /// What the import replaces the variable with, through the same resolver the import uses;
    /// null when the value resolves to nothing.
    /// </summary>
    public string? ResolvedValue => MobaXtermVariableResolver.ResolveValue(Value);

    public bool IsResolved => ResolvedValue is not null;
}
