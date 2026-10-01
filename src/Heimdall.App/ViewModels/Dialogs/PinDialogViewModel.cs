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
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the PIN entry dialog. Manages PIN verification, lockout state,
/// and brute-force protection via <see cref="PinManager"/>.
/// </summary>
public partial class PinDialogViewModel : ObservableObject
{
    private readonly PinManager _pinManager;
    private readonly LocalizationManager _localizer;
    private readonly string _storedHash;
    private readonly string _storedSalt;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isLockedOut;

    [ObservableProperty]
    private string _lockoutMessage = "";

    [ObservableProperty]
    private bool _isVerified;

    public PinDialogViewModel(
        PinManager pinManager,
        LocalizationManager localizer,
        string storedHash,
        string storedSalt)
    {
        _pinManager = pinManager;
        _localizer = localizer;
        _storedHash = storedHash;
        _storedSalt = storedSalt;

        UpdateLockoutState();
    }

    /// <summary>
    /// Verifies the entered PIN against the stored hash.
    /// </summary>
    [RelayCommand]
    private void VerifyPin(string pin)
    {
        if (string.IsNullOrEmpty(pin))
        {
            return;
        }

        if (_pinManager.IsLockedOut)
        {
            UpdateLockoutState();
            return;
        }

        if (_pinManager.Verify(pin, _storedHash, _storedSalt))
        {
            _pinManager.ResetFailures();
            IsVerified = true;
            ErrorMessage = "";
        }
        else
        {
            var failures = _pinManager.RegisterFailure();
            var remaining = _pinManager.MaxAttempts - failures;

            if (_pinManager.IsLockedOut)
            {
                UpdateLockoutState();
            }
            else
            {
                ErrorMessage = _localizer.FormatCount(remaining, "PinInvalidRemainingOne", "PinInvalidRemaining", remaining);
            }
        }
    }

    private void UpdateLockoutState()
    {
        IsLockedOut = _pinManager.IsLockedOut;

        if (IsLockedOut)
        {
            ErrorMessage = "";
            TimeSpan remaining = _pinManager.LockoutRemaining;
            int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            LockoutMessage = _localizer.FormatCount(minutes, "PinLockedOutOne", "PinLockedOut", minutes);
            return;
        }

        LockoutMessage = "";
    }
}
