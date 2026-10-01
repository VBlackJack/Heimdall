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
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Minimal view model for the first bulk-edit dialog.
/// Port is the only supported field in b72.
/// </summary>
public sealed class ServerBulkEditViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly int? _initialPort;
    private bool _isInitializing;
    private bool _hasEdited;
    private string _input = string.Empty;
    private string? _validationError;
    private bool _isApplyEnabled;
    private int? _resolvedPort;

    public ServerBulkEditViewModel(LocalizationManager localizer, int count, int? initialPort)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Count = count;
        Header = _localizer.FormatCount(count, "BulkEditPortHeaderOne", "BulkEditPortHeader", count);
        MixedValuesHint = _localizer["BulkEditPortMixedValuesHint"];
        _initialPort = initialPort;

        _isInitializing = true;
        Input = initialPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isInitializing = false;
        Validate();
    }

    public int Count { get; }

    public string Header { get; }

    public string MixedValuesHint { get; }

    public bool ShowMixedValuesHint => !_initialPort.HasValue && string.IsNullOrWhiteSpace(Input);

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

    public int? ResolvedPort
    {
        get => _resolvedPort;
        private set => SetProperty(ref _resolvedPort, value);
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Input))
        {
            ValidationError = null;
            ResolvedPort = null;
            IsApplyEnabled = false;
            return;
        }

        if (!int.TryParse(Input, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
            || parsedPort < 1
            || parsedPort > 65535)
        {
            ValidationError = _localizer["BulkEditPortValidationError"];
            ResolvedPort = null;
            IsApplyEnabled = false;
            return;
        }

        ValidationError = null;
        ResolvedPort = parsedPort;
        IsApplyEnabled = _hasEdited || !_initialPort.HasValue;
    }
}
