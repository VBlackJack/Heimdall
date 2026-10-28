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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.Dialogs;

public sealed partial class PostConnectStepItemViewModel : ObservableObject, IAccessibleItemViewModel
{
    private const string RowNameKey = "A11yPostConnectStepRow";
    private const string EmptyStepKey = "A11yPostConnectStepEmpty";

    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string? _commandLibraryId;

    [ObservableProperty]
    private Dictionary<string, string>? _commandLibraryParams;

    [ObservableProperty]
    private int _delayMs = 150;

    [ObservableProperty]
    private PostConnectFailurePolicy _onFailure = PostConnectFailurePolicy.Continue;

    [ObservableProperty]
    private string? _linkedActionTitle;

    [ObservableProperty]
    private bool _isBroken;

    public bool IsLinked => !string.IsNullOrWhiteSpace(CommandLibraryId);

    public string DisplayInput => IsLinked
        ? LinkedActionTitle ?? CommandLibraryId ?? string.Empty
        : Input;

    /// <summary>
    /// What a screen reader announces for the row: the command, or the library action it is linked
    /// to, so a list of steps reads as a list of commands rather than as a list of type names.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            string display = string.IsNullOrWhiteSpace(DisplayInput)
                ? LocalizationSource.Instance[EmptyStepKey]
                : DisplayInput;
            return string.Format(CultureInfo.CurrentCulture, LocalizationSource.Instance[RowNameKey], display);
        }
    }

    /// <inheritdoc />
    public string? AccessibleHelpText => null;

    partial void OnInputChanged(string value) => RaiseDisplayInputChanged();

    partial void OnCommandLibraryIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsLinked));
        RaiseDisplayInputChanged();
    }

    partial void OnLinkedActionTitleChanged(string? value) => RaiseDisplayInputChanged();

    private void RaiseDisplayInputChanged()
    {
        OnPropertyChanged(nameof(DisplayInput));
        OnPropertyChanged(nameof(AccessibleName));
    }

    public PostConnectStep ToModel()
    {
        return new PostConnectStep
        {
            Id = Id,
            Enabled = Enabled,
            Input = Input,
            CommandLibraryId = CommandLibraryId,
            CommandLibraryParams = CommandLibraryParams is null ? null : new Dictionary<string, string>(CommandLibraryParams, StringComparer.Ordinal),
            DelayMs = DelayMs,
            OnFailure = OnFailure
        };
    }

    public static PostConnectStepItemViewModel FromModel(PostConnectStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new PostConnectStepItemViewModel
        {
            Id = step.Id,
            Enabled = step.Enabled,
            Input = step.Input,
            CommandLibraryId = step.CommandLibraryId,
            CommandLibraryParams = step.CommandLibraryParams is null ? null : new Dictionary<string, string>(step.CommandLibraryParams, StringComparer.Ordinal),
            DelayMs = step.DelayMs,
            OnFailure = step.OnFailure
        };
    }
}
