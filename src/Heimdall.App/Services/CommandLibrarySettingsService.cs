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

using System.Windows;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Computes transient Settings UI state related to the command library Git sync section.
/// </summary>
public sealed class CommandLibrarySettingsService
{
    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;

    public CommandLibrarySettingsService(
        IConfigManager configManager,
        LocalizationManager localizer)
    {
        _configManager = configManager;
        _localizer = localizer;
    }

    /// <summary>
    /// Checks whether a Git sync token is currently stored in settings.
    /// </summary>
    public async Task<bool> HasSavedTokenAsync()
    {
        AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        return !string.IsNullOrEmpty(settings.CmdLibGitSyncToken);
    }

    /// <summary>
    /// Persists a Git sync token and reports whether the write completed.
    /// </summary>
    public async Task<bool> TrySaveTokenAsync(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return false;
        }

        try
        {
            // Vault-aware write: a v2 (HMS1, master-password-protected) blob when the
            // vault is active, legacy DPAPI when no vault. Matches the vault-aware
            // reader (GitTokenReader). Protect throws if the vault is enabled but
            // locked; that is caught below and reported as a failed save.
            string encrypted = CredentialProtector.Protect(plaintext);
            await _configManager.MergeSettingAsync(
                    (AppSettings settings) => settings.CmdLibGitSyncToken = encrypted)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Failed to persist Command Library Git token: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the persisted Git sync token and reports whether the write completed.
    /// </summary>
    public async Task<bool> TryClearTokenAsync()
    {
        try
        {
            await _configManager.MergeSettingAsync(
                    (AppSettings settings) => settings.CmdLibGitSyncToken = null)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Failed to clear Command Library Git token: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Builds the transient token status displayed in the Settings UI.
    /// </summary>
    public CommandLibraryTokenStatus GetTokenStatus(bool hasToken)
    {
        return new CommandLibraryTokenStatus(
            hasToken ? _localizer["SettingsCmdLibSyncTokenSaved"] : string.Empty,
            hasToken ? Visibility.Visible : Visibility.Collapsed);
    }

    public string GetSaveErrorStatusText() => _localizer["SettingsCmdLibSyncTokenSaveError"];
}

/// <summary>
/// Represents the transient display state of the command library token controls.
/// </summary>
public readonly record struct CommandLibraryTokenStatus(
    string StatusText,
    Visibility ClearButtonVisibility);
