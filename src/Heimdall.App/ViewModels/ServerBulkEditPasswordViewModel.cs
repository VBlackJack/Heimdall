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
/// View model for the bulk password edit dialog. Requires the user to type
/// the new password twice (confirmation) because <c>PasswordBox</c> cannot
/// be read back visually.
/// </summary>
public sealed class ServerBulkEditPasswordViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string? _validationError;
    private bool _isApplyEnabled;
    private string? _resolvedPassword;

    public ServerBulkEditPasswordViewModel(LocalizationManager localizer, int count)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Count = count;
        Header = _localizer.FormatCount(count, "BulkEditPasswordHeaderOne", "BulkEditPasswordHeader", count);
        Validate();
    }

    public int Count { get; }

    public string Header { get; }

    public string Password
    {
        get => _password;
        set
        {
            if (!SetProperty(ref _password, value))
            {
                return;
            }

            Validate();
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (!SetProperty(ref _confirmPassword, value))
            {
                return;
            }

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

    public string? ResolvedPassword
    {
        get => _resolvedPassword;
        private set => SetProperty(ref _resolvedPassword, value);
    }

    private void Validate()
    {
        var raw = Password ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            ValidationError = null;
            ResolvedPassword = null;
            IsApplyEnabled = false;
            return;
        }

        if (raw.Any(char.IsControl))
        {
            ValidationError = _localizer["BulkEditPasswordValidationControl"];
            ResolvedPassword = null;
            IsApplyEnabled = false;
            return;
        }

        var confirm = ConfirmPassword ?? string.Empty;
        if (!string.Equals(raw, confirm, StringComparison.Ordinal))
        {
            ValidationError = _localizer["BulkEditPasswordValidationMismatch"];
            ResolvedPassword = null;
            IsApplyEnabled = false;
            return;
        }

        ValidationError = null;
        ResolvedPassword = raw;
        IsApplyEnabled = true;
    }
}
