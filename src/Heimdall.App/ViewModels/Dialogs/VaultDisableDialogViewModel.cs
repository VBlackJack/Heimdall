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
/// ViewModel for the "Disable master password" dialog. Authorizes with the
/// master password, then drives <see cref="VaultLifecycleService.DisableAsync"/>
/// (reverse-migrates the confidential set back to OS-level DPAPI protection and
/// clears the wrapped DEK) behind a busy indicator. A wrong password surfaces the
/// single generic unlock error. No password is logged; the buffer is zeroed.
/// </summary>
public partial class VaultDisableDialogViewModel : ObservableObject
{
    private readonly Func<char[], Task> _disableAsync;
    private readonly LocalizationManager _localizer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>Create the disable dialog ViewModel.</summary>
    /// <param name="disableAsync">The disable operation (master password -> reverse-migrate + clear).</param>
    /// <param name="localizer">The localization source.</param>
    public VaultDisableDialogViewModel(Func<char[], Task> disableAsync, LocalizationManager localizer)
    {
        _disableAsync = disableAsync;
        _localizer = localizer;
    }

    /// <summary>Whether the disable action is currently available.</summary>
    public bool CanSubmit => !IsBusy;

    [RelayCommand]
    private async Task Disable(char[]? password)
    {
        if (password is null || password.Length == 0)
        {
            return;
        }

        try
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = "";
            BusyMessage = _localizer.GetString("VaultDisableBusy");

            try
            {
                await Task.Run(() => _disableAsync(password)).ConfigureAwait(true);
                IsCompleted = true;
            }
            catch (VaultUnlockException)
            {
                // Wrong password (or corruption) — single generic message.
                ErrorMessage = _localizer.GetString("VaultUnlockError");
            }
            catch (Exception ex)
            {
                FileLogger.Error("Vault disable failed", ex);
                ErrorMessage = _localizer.GetString("VaultDisableError");
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            Array.Clear(password);
        }
    }
}
