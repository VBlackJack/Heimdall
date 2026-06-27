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
/// ViewModel for the "Enable master password" dialog. Gates the action on a live
/// <see cref="MasterPasswordPolicy"/> check and a confirm match, then drives
/// <see cref="VaultLifecycleService.EnableAsync"/> (which migrates every stored
/// secret) behind a busy indicator. No password is ever logged; the buffer is
/// zeroed after the attempt.
/// </summary>
public partial class VaultEnableDialogViewModel : ObservableObject
{
    private readonly Func<char[], Task> _enableAsync;
    private readonly LocalizationManager _localizer;

    [ObservableProperty]
    private string _policyMessage = "";

    [ObservableProperty]
    private bool _isPolicyMet;

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

    /// <summary>Create the enable dialog ViewModel.</summary>
    /// <param name="enableAsync">The enable operation (new master password -> wrap DEK + migrate).</param>
    /// <param name="localizer">The localization source.</param>
    public VaultEnableDialogViewModel(Func<char[], Task> enableAsync, LocalizationManager localizer)
    {
        _enableAsync = enableAsync;
        _localizer = localizer;
        PolicyMessage = localizer.GetString("VaultPolicyHint");
    }

    /// <summary>Whether the enable action is currently available.</summary>
    public bool CanSubmit => IsPasswordAcceptable && IsConfirmMatch && !IsBusy;

    /// <summary>
    /// Live-evaluate the candidate password against the policy and the confirm
    /// field. Called on every keystroke; the spans are not retained.
    /// </summary>
    public void Evaluate(ReadOnlySpan<char> password, ReadOnlySpan<char> confirm)
    {
        var result = MasterPasswordPolicy.Validate(password);
        IsPasswordAcceptable = result.IsAcceptable;
        IsPolicyMet = result.IsAcceptable;
        PolicyMessage = result.IsAcceptable
            ? _localizer.GetString("VaultPolicyOk")
            : DescribePolicyError(result.Error, _localizer);
        IsConfirmMatch = password.Length > 0 && password.SequenceEqual(confirm);
    }

    [RelayCommand]
    private async Task Enable(char[]? password)
    {
        if (password is null)
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
            BusyMessage = _localizer.GetString("VaultEnableBusy");

            try
            {
                // Migration re-encrypts every stored secret; offload so the UI stays responsive.
                await Task.Run(() => _enableAsync(password)).ConfigureAwait(true);
                IsCompleted = true;
            }
            catch (Exception ex)
            {
                // Recoverable: the lifecycle persisted a resumable marker before migrating.
                FileLogger.Error("Vault enable failed", ex);
                ErrorMessage = _localizer.GetString("VaultEnableError");
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

    internal static string DescribePolicyError(MasterPasswordPolicyError? error, LocalizationManager localizer) =>
        error switch
        {
            MasterPasswordPolicyError.TooShort => localizer.GetString("VaultPolicyTooShort"),
            MasterPasswordPolicyError.InsufficientComplexity => localizer.GetString("VaultPolicyComplexity"),
            _ => localizer.GetString("VaultPolicyHint"),
        };
}
