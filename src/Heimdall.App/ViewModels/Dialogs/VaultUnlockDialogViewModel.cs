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
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the startup vault unlock dialog. Drives the master-password
/// unlock through a supplied unlock delegate (<see cref="VaultLifecycleService.UnlockAsync"/>),
/// with brute-force lockout via <see cref="PinManager"/> as defense-in-depth on
/// top of Argon2id (the primary per-attempt rate-limiter).
/// </summary>
/// <remarks>
/// Wrong-password and a corrupted vault are indistinguishable: both surface the
/// single generic error. No master-password value is ever logged; the password
/// buffer is zeroed after every attempt.
/// </remarks>
public partial class VaultUnlockDialogViewModel : ObservableObject
{
    private readonly Func<char[], Task> _unlockAsync;
    private readonly Func<Task<VaultHelloUnlockResult>>? _unlockWithHelloAsync;
    private readonly Func<Task<bool>>? _confirmHelloReenrollAsync;
    private readonly Func<Task>? _reenrollHelloAsync;
    private readonly PinManager _lockout;
    private readonly LocalizationManager _localizer;
    private readonly bool _migrationInProgress;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(CanUnlockWithHello))]
    [NotifyCanExecuteChangedFor(nameof(UnlockWithHelloCommand))]
    private bool _isLockedOut;

    [ObservableProperty]
    private string _lockoutMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(CanUnlockWithHello))]
    [NotifyCanExecuteChangedFor(nameof(UnlockWithHelloCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = "";

    [ObservableProperty]
    private bool _isVerified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlockWithHello))]
    [NotifyCanExecuteChangedFor(nameof(UnlockWithHelloCommand))]
    private bool _showHelloUnlock;

    [ObservableProperty]
    private bool _hasPendingHelloReenroll;

    [ObservableProperty]
    private bool _isMasterPasswordVerified;

    /// <summary>
    /// Create the unlock ViewModel.
    /// </summary>
    /// <param name="unlockAsync">The unlock operation (master password -> unwrap DEK).</param>
    /// <param name="lockout">The brute-force lockout tracker.</param>
    /// <param name="localizer">The localization source.</param>
    /// <param name="migrationInProgress">Whether a forward migration will resume on unlock.</param>
    public VaultUnlockDialogViewModel(
        Func<char[], Task> unlockAsync,
        PinManager lockout,
        LocalizationManager localizer,
        bool migrationInProgress,
        Func<Task<VaultHelloUnlockResult>>? unlockWithHelloAsync = null,
        bool showHelloUnlock = false,
        Func<Task<bool>>? confirmHelloReenrollAsync = null,
        Func<Task>? reenrollHelloAsync = null)
    {
        _unlockAsync = unlockAsync;
        _unlockWithHelloAsync = unlockWithHelloAsync;
        _confirmHelloReenrollAsync = confirmHelloReenrollAsync;
        _reenrollHelloAsync = reenrollHelloAsync;
        _lockout = lockout;
        _localizer = localizer;
        _migrationInProgress = migrationInProgress;
        ShowHelloUnlock = showHelloUnlock && unlockWithHelloAsync is not null;

        UpdateLockoutState();
    }

    /// <summary>Whether the unlock action is currently available.</summary>
    public bool CanSubmit => !IsLockedOut && !IsBusy;

    /// <summary>Whether the Windows Hello unlock action is currently available.</summary>
    public bool CanUnlockWithHello => ShowHelloUnlock && !IsLockedOut && !IsBusy;

    private bool CanRunUnlockWithHello() => CanUnlockWithHello;

    /// <summary>
    /// Attempt to unlock the vault with the supplied master password. The buffer
    /// is always zeroed before returning.
    /// </summary>
    [RelayCommand]
    private async Task Unlock(char[]? password)
    {
        if (password is null || password.Length == 0)
        {
            return;
        }

        try
        {
            if (_lockout.IsLockedOut)
            {
                UpdateLockoutState();
                return;
            }

            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = "";
            BusyMessage = _migrationInProgress
                ? _localizer.GetString("VaultUnlockMigrationStatus")
                : _localizer.GetString("VaultUnlockBusy");

            try
            {
                // Offload to a worker thread: Argon2id derivation is synchronous and
                // CPU-bound, so this keeps the busy indicator responsive.
                await Task.Run(() => _unlockAsync(password)).ConfigureAwait(true);
                _lockout.ResetFailures();
                IsMasterPasswordVerified = true;
                await OfferHelloReenrollAfterMasterUnlockAsync().ConfigureAwait(true);
                IsVerified = true;
            }
            catch (VaultUnlockException)
            {
                // Wrong password OR corrupted vault — indistinguishable by contract.
                _lockout.RegisterFailure();
                if (_lockout.IsLockedOut)
                {
                    UpdateLockoutState();
                }
                else
                {
                    ErrorMessage = _localizer.GetString("VaultUnlockError");
                }
            }
            catch (Exception ex)
            {
                // Unexpected failure (e.g. migration resume I/O). Stay on the gate,
                // fail-closed. Never log password material.
                FileLogger.Error("Vault unlock encountered an unexpected error", ex);
                ErrorMessage = _localizer.GetString("VaultUnlockError");
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

    /// <summary>Attempt to unlock with Windows Hello instead of the master password.</summary>
    [RelayCommand(CanExecute = nameof(CanRunUnlockWithHello))]
    private async Task UnlockWithHello()
    {
        if (_unlockWithHelloAsync is null || !CanUnlockWithHello)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = "";
        BusyMessage = _localizer.GetString("VaultHelloUnlockBusy");

        try
        {
            var result = await _unlockWithHelloAsync().ConfigureAwait(true);
            if (result.Succeeded)
            {
                _lockout.ResetFailures();
                IsVerified = true;
                return;
            }

            var mapped = VaultHelloUnlockUx.Map(result.FailureReason);
            if (mapped.Action == VaultHelloUnlockUxAction.SilentFallback)
            {
                ErrorMessage = "";
                return;
            }

            if (mapped.Action == VaultHelloUnlockUxAction.TriggerReenroll)
            {
                HasPendingHelloReenroll = true;
            }

            ErrorMessage = mapped.MessageKey is null ? "" : _localizer.GetString(mapped.MessageKey);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "";
        }
        catch (Exception ex)
        {
            FileLogger.Error("Windows Hello vault unlock encountered an unexpected error", ex);
            ErrorMessage = _localizer.GetString("VaultHelloUnlockGenericFailure");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OfferHelloReenrollAfterMasterUnlockAsync()
    {
        if (!HasPendingHelloReenroll || _confirmHelloReenrollAsync is null || _reenrollHelloAsync is null)
        {
            return;
        }

        bool confirmed = await _confirmHelloReenrollAsync().ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        BusyMessage = _localizer.GetString("VaultHelloReenrollBusy");
        try
        {
            await _reenrollHelloAsync().ConfigureAwait(true);
            HasPendingHelloReenroll = false;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Windows Hello vault re-enrollment failed: {ex.Message}");
            ErrorMessage = _localizer.GetString("VaultHelloReenrollError");
        }
    }

    private void UpdateLockoutState()
    {
        IsLockedOut = _lockout.IsLockedOut;

        if (IsLockedOut)
        {
            ErrorMessage = "";
            TimeSpan remaining = _lockout.LockoutRemaining;
            LockoutMessage = _localizer.Format(
                "VaultUnlockLockedOut",
                (int)Math.Ceiling(remaining.TotalMinutes));
            return;
        }

        LockoutMessage = "";
    }
}
