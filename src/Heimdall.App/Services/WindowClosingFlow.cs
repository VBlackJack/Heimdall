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

using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Coordinates the asynchronous work that must finish before the main window
/// is allowed to close.
/// </summary>
internal static class WindowClosingFlow
{
    internal static async Task<bool> TryPrepareCloseAsync(
        bool settingsDirty,
        Func<Task<bool?>> promptSaveDiscardCancelAsync,
        Func<Task<bool>> trySaveSettingsAsync,
        Func<Task> persistWindowStateAsync,
        Action warnSaveFailed)
    {
        return await TryPrepareCloseAsync(
            settingsDirty,
            connectedSessionCount: 0,
            promptSaveDiscardCancelAsync,
            trySaveSettingsAsync,
            () => Task.FromResult(true),
            persistWindowStateAsync,
            warnSaveFailed);
    }

    internal static async Task<bool> TryPrepareCloseAsync(
        bool settingsDirty,
        int connectedSessionCount,
        Func<Task<bool?>> promptSaveDiscardCancelAsync,
        Func<Task<bool>> trySaveSettingsAsync,
        Func<Task<bool>> promptCloseConnectedSessionsAsync,
        Func<Task> persistWindowStateAsync,
        Action warnSaveFailed)
    {
        return await TryPrepareCloseAsync(
            settingsDirty,
            connectedSessionCount,
            promptSaveDiscardCancelAsync,
            trySaveSettingsAsync,
            promptCloseConnectedSessionsAsync,
            static () => Task.CompletedTask,
            persistWindowStateAsync,
            warnSaveFailed);
    }

    internal static async Task<bool> TryPrepareCloseAsync(
        bool settingsDirty,
        int connectedSessionCount,
        Func<Task<bool?>> promptSaveDiscardCancelAsync,
        Func<Task<bool>> trySaveSettingsAsync,
        Func<Task<bool>> promptCloseConnectedSessionsAsync,
        Func<Task> flushExpandStateAsync,
        Func<Task> persistWindowStateAsync,
        Action warnSaveFailed,
        Func<Task>? discardSettingsAsync = null)
    {
        ArgumentNullException.ThrowIfNull(promptSaveDiscardCancelAsync);
        ArgumentNullException.ThrowIfNull(trySaveSettingsAsync);
        ArgumentNullException.ThrowIfNull(promptCloseConnectedSessionsAsync);
        ArgumentNullException.ThrowIfNull(flushExpandStateAsync);
        ArgumentNullException.ThrowIfNull(persistWindowStateAsync);
        ArgumentNullException.ThrowIfNull(warnSaveFailed);

        try
        {
            if (settingsDirty)
            {
                bool? choice = await promptSaveDiscardCancelAsync();
                if (choice is null)
                {
                    return false;
                }

                if (choice == true && !await trySaveSettingsAsync())
                {
                    warnSaveFailed();
                    return false;
                }

                // Discard has to undo the settings that were already applied on screen, not
                // only the ones waiting to be written. Theme, accent and now language take
                // effect the moment they are picked, and this flow does not always end in a
                // closed window: declining the connected-sessions prompt below leaves the
                // application running, with edits the user just abandoned still in force.
                if (choice == false && discardSettingsAsync is not null)
                {
                    await discardSettingsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("Main window close preparation failed", ex);
        }

        if (connectedSessionCount > 0)
        {
            try
            {
                if (!await promptCloseConnectedSessionsAsync())
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("Main window session close confirmation failed", ex);
                return false;
            }
        }

        try
        {
            await flushExpandStateAsync();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Tree expand-state persistence failed during close: {ex.Message}");
        }

        try
        {
            await persistWindowStateAsync();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Window state persistence failed during close: {ex.Message}");
        }

        return true;
    }
}
