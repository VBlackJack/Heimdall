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
/// One runbook variable: in the editor, its name and default value; in a run, its name and the
/// value the operator gives it for that run.
/// </summary>
public sealed partial class RunbookVariableEntry : ObservableObject
{
    /// <summary>The name a step refers to as <c>{{name}}</c>.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The default value in the editor, or the value used by the current run.</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>The name as a reference, for the run form's label.</summary>
    public string Reference => RunbookVariables.FormatReference(Name);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Reference));
}
