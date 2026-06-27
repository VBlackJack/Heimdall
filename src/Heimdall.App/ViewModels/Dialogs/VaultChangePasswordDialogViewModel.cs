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
using Heimdall.Core.Logging;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Input for <see cref="VaultChangePasswordDialogViewModel"/>: the current and
/// new master passwords as pinned char arrays the ViewModel zeroes after use.
/// </summary>
/// <param name="CurrentPassword">The current master password.</param>
/// <param name="NewPassword">The replacement master password.</param>
public sealed record VaultChangePasswordInput(char[] CurrentPassword, char[] NewPassword);

/// <summary>
/// ViewModel for the "Change master password" dialog. Gates the new password on
/// the policy + confirm match, then drives
/// <see cref="VaultLifecycleService.ChangeMasterPasswordAsync"/> (re-wrap only,
/// the DEK is unchanged). A wrong current password surfaces the single generic
/// unlock error. No password is logged; both buffers are zeroed after the attempt.
/// </summary>
public partial class VaultChangePasswordDialogViewModel : ObservableObject
{
    private readonly Func<char[], char[], Task> _changeAsync;
    private readonly LocalizationManager _localizer;

    [ObservableProperty]
    private string _policyMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isPasswordAcceptable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isConfirmMatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>Create the change-password dialog ViewModel.</summary>
    /// <param name="changeAsync">The change operation (current, new) -> re-wrap DEK.</param>
    /// <param name="localizer">The localization source.</param>
    public VaultChangePasswordDialogViewModel(Func<char[], char[], Task> changeAsync, LocalizationManager localizer)
    {
        _changeAsync = changeAsync;
        _localizer = localizer;
        PolicyMessage = localizer.GetString("VaultPolicyHint");
    }

    /// <summary>Whether the change action is currently available (the current password is validated on submit).</summary>
    public bool CanSubmit => IsPasswordAcceptable && IsConfirmMatch && !IsBusy;

    /// <summary>Live-evaluate the new password against the policy and the confirm field.</summary>
    public void Evaluate(ReadOnlySpan<char> newPassword, ReadOnlySpan<char> confirm)
    {
        var result = MasterPasswordPolicy.Validate(newPassword);
        IsPasswordAcceptable = result.IsAcceptable;
        PolicyMessage = result.IsAcceptable
            ? _localizer.GetString("VaultPolicyOk")
            : VaultEnableDialogViewModel.DescribePolicyError(result.Error, _localizer);
        IsConfirmMatch = newPassword.Length > 0 && newPassword.SequenceEqual(confirm);
    }

    [RelayCommand]
    private async Task Change(VaultChangePasswordInput? input)
    {
        if (input is null)
        {
            return;
        }

        try
        {
            if (!CanSubmit)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = "";
            BusyMessage = _localizer.GetString("VaultChangeBusy");

            try
            {
                await Task.Run(() => _changeAsync(input.CurrentPassword, input.NewPassword)).ConfigureAwait(true);
                IsCompleted = true;
            }
            catch (VaultUnlockException)
            {
                // Wrong current password (or corruption) — single generic message.
                ErrorMessage = _localizer.GetString("VaultUnlockError");
            }
            catch (Exception ex)
            {
                FileLogger.Error("Vault change-password failed", ex);
                ErrorMessage = _localizer.GetString("VaultChangeError");
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            Array.Clear(input.CurrentPassword);
            Array.Clear(input.NewPassword);
        }
    }
}
