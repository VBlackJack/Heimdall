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
public partial class VaultDisableDialogViewModel : ObservableObject, IPasswordRefocusHost
{
    private const string StoreNameSeparator = ", ";
    private const string MessageSeparator = " ";
    private const string PresetsStoreNameKey = "VaultStorePasswordPresets";
    private const string PresetsAllLabelKey = "VaultStorePasswordPresetsAll";

    private readonly Func<char[], bool, Task> _disableAsync;
    private readonly Func<Task> _retryRestoreAsync;
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

    /// <summary>
    /// Whether the disable was undone but the key could not be written back to disk: Heimdall must
    /// not be closed, and the Retry action is offered. The dialog never reports completion then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isRetryPending;

    /// <summary>Whether some stored values are unreadable and can be removed to turn the master password off.</summary>
    [ObservableProperty]
    private bool _hasUnreadable;

    /// <summary>The text of the explicit "remove the unreadable entries and turn off" action.</summary>
    [ObservableProperty]
    private string _dropButtonText = "";

    /// <summary>Create the disable dialog ViewModel.</summary>
    /// <param name="disableAsync">The disable operation (master password -> reverse-migrate + clear).</param>
    /// <param name="localizer">The localization source.</param>
    public VaultDisableDialogViewModel(Func<char[], Task> disableAsync, LocalizationManager localizer)
        : this((password, _) => disableAsync(password), () => Task.CompletedTask, localizer)
    {
    }

    /// <summary>Create the disable dialog ViewModel with the unreadable-entries and retry actions.</summary>
    /// <param name="disableAsync">The disable operation; its flag removes the unreadable entries.</param>
    /// <param name="retryRestoreAsync">Finishes a disable that was undone but could not write the key back.</param>
    /// <param name="localizer">The localization source.</param>
    public VaultDisableDialogViewModel(
        Func<char[], bool, Task> disableAsync,
        Func<Task> retryRestoreAsync,
        LocalizationManager localizer)
    {
        _disableAsync = disableAsync;
        _retryRestoreAsync = retryRestoreAsync;
        _localizer = localizer;
    }

    /// <summary>
    /// Fires when the password box should take the keyboard focus back: the attempt failed and the
    /// box, disabled while it ran, accepts input again.
    /// </summary>
    public PasswordRefocusSignal PasswordRefocus { get; } = new();

    /// <summary>Whether the disable action is currently available.</summary>
    public bool CanSubmit => !IsBusy && !IsRetryPending;

    /// <summary>
    /// The stores that still hold vault secrets, or the generic failure when a step failed, followed
    /// by the warning when the vault could not be fully put back.
    /// </summary>
    private string DescribeResidue(VaultResidueException ex)
    {
        string message = ex.StoreNameKeys.Count > 0
            ? _localizer.Format(
                "VaultDisableResidueError",
                string.Join(StoreNameSeparator, ex.StoreNameKeys.Select(key => _localizer.GetString(key))))
            : _localizer.GetString("VaultDisableError");
        return ex.ProtectionRestored
            ? message
            : message + MessageSeparator + _localizer.GetString("VaultDisableRestoreIncomplete");
    }

    [RelayCommand]
    private Task Disable(char[]? password) => RunDisableAsync(password, dropUnreadable: false);

    /// <summary>The explicit action: removes the unreadable entries and turns the master password off.</summary>
    [RelayCommand]
    private Task DropUnreadable(char[]? password) => RunDisableAsync(password, dropUnreadable: true);

    /// <summary>Writes the key back and seals everything again after a disable that could not.</summary>
    [RelayCommand]
    private async Task RetryRestore()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = "";
        BusyMessage = _localizer.GetString("VaultDisableRetryBusy");
        try
        {
            await Task.Run(_retryRestoreAsync).ConfigureAwait(true);
            IsRetryPending = false;
            ErrorMessage = _localizer.GetString("VaultDisableRetryDone");
        }
        catch (VaultDisableIncompleteException)
        {
            ErrorMessage = _localizer.GetString("VaultDisableIncompleteError");
        }
        catch (VaultResidueException ex)
        {
            IsRetryPending = false;
            ErrorMessage = DescribeResidue(ex);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Vault restore retry failed", ex);
            ErrorMessage = _localizer.GetString("VaultDisableIncompleteError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunDisableAsync(char[]? password, bool dropUnreadable)
    {
        if (password is null || password.Length == 0)
        {
            return;
        }

        try
        {
            if (IsBusy || IsRetryPending)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = "";
            HasUnreadable = false;
            BusyMessage = _localizer.GetString("VaultDisableBusy");

            try
            {
                await Task.Run(() => _disableAsync(password, dropUnreadable)).ConfigureAwait(true);
                IsCompleted = true;
            }
            catch (VaultUnlockException)
            {
                // Wrong password (or corruption) - single generic message.
                ErrorMessage = _localizer.GetString("VaultUnlockError");
            }
            catch (VaultBusyException)
            {
                ErrorMessage = _localizer.GetString("VaultDisableBusyError");
            }
            catch (VaultDisableIncompleteException)
            {
                // The key is held in memory only: closing Heimdall now would lose what it seals.
                IsRetryPending = true;
                ErrorMessage = _localizer.GetString("VaultDisableIncompleteError");
            }
            catch (VaultUnreadableSecretsException ex)
            {
                DescribeUnreadable(ex);
            }
            catch (VaultResidueException ex)
            {
                ErrorMessage = DescribeResidue(ex);
            }
            catch (VaultStoreException ex)
            {
                // Names the store only: the message carries no secret and no path.
                ErrorMessage = _localizer.Format("VaultStoreFailureError", _localizer.GetString(ex.StoreNameKey));
            }
            catch (Exception ex)
            {
                FileLogger.Error("Vault disable failed", ex);
                ErrorMessage = _localizer.GetString("VaultDisableError");
            }
            finally
            {
                IsBusy = false;
                if (!IsCompleted)
                {
                    PasswordRefocus.Arm();
                }

                PasswordRefocus.Release(passwordBoxEnabled: !IsBusy);
            }
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>
    /// The saved presets are one sealed value, so what is lost is all of them or none: they are named
    /// that way instead of as a store with a count that would suggest a part.
    /// </summary>
    private static string UnreadableLabelKey(string storeNameKey) =>
        string.Equals(storeNameKey, PresetsStoreNameKey, StringComparison.Ordinal)
            ? PresetsAllLabelKey
            : storeNameKey;

    /// <summary>Lists the stores and how many entries each holds that nothing can open any more.</summary>
    private void DescribeUnreadable(VaultUnreadableSecretsException ex)
    {
        string stores = string.Join(
            StoreNameSeparator,
            ex.Counts.Select(entry => _localizer.Format(
                "VaultUnreadableStoreCount", _localizer.GetString(UnreadableLabelKey(entry.Key)), entry.Value)));
        string message = _localizer.Format("VaultDisableUnreadableError", stores);
        ErrorMessage = ex.ProtectionRestored
            ? message
            : message + MessageSeparator + _localizer.GetString("VaultDisableRestoreIncomplete");
        DropButtonText = _localizer.Format("VaultDisableDropUnreadableBtn", ex.TotalCount);
        HasUnreadable = true;
    }
}
