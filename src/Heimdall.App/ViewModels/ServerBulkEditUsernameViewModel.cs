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

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Minimal view model for the second bulk-edit dialog.
/// Username is the only supported field in b74.
/// </summary>
public sealed class ServerBulkEditUsernameViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly string? _initialUsername;
    private bool _isInitializing;
    private bool _hasEdited;
    private string _input = string.Empty;
    private string? _validationError;
    private bool _isApplyEnabled;
    private string? _resolvedUsername;

    public ServerBulkEditUsernameViewModel(LocalizationManager localizer, int count, string? initialUsername)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Count = count;
        Header = _localizer.FormatCount(count, "BulkEditUsernameHeaderOne", "BulkEditUsernameHeader", count);
        MixedValuesHint = _localizer["BulkEditUsernameMixedValuesHint"];
        _initialUsername = initialUsername;

        _isInitializing = true;
        Input = initialUsername ?? string.Empty;
        _isInitializing = false;
        Validate();
    }

    public int Count { get; }

    public string Header { get; }

    public string MixedValuesHint { get; }

    public bool ShowMixedValuesHint => _initialUsername is null && string.IsNullOrWhiteSpace(Input);

    public string Input
    {
        get => _input;
        set
        {
            if (!SetProperty(ref _input, value))
            {
                return;
            }

            if (!_isInitializing)
            {
                _hasEdited = true;
            }

            OnPropertyChanged(nameof(ShowMixedValuesHint));
            Validate();
        }
    }

    public string? ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    public bool IsApplyEnabled
    {
        get => _isApplyEnabled;
        private set => SetProperty(ref _isApplyEnabled, value);
    }

    public string? ResolvedUsername
    {
        get => _resolvedUsername;
        private set => SetProperty(ref _resolvedUsername, value);
    }

    private void Validate()
    {
        var raw = Input ?? string.Empty;
        if (raw.Any(char.IsControl))
        {
            ValidationError = _localizer["BulkEditUsernameValidationError"];
            ResolvedUsername = null;
            IsApplyEnabled = false;
            return;
        }

        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            ValidationError = null;
            ResolvedUsername = null;
            IsApplyEnabled = false;
            return;
        }

        ValidationError = null;
        ResolvedUsername = trimmed;
        IsApplyEnabled = _hasEdited || string.IsNullOrEmpty(_initialUsername);
    }
}
