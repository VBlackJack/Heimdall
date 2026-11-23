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

using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// The one-time step that keeps the Recipes pack off for an install that predates it.
/// </summary>
/// <remarks>
/// <para>A pack missing from <see cref="AppSettings.DisabledToolPacks"/> is on, so an existing
/// install would otherwise switch the Recipes pack on by itself the day it gains the pack. The step
/// adds <see cref="ToolPack.Recipes"/> to the list and records that it ran in
/// <see cref="AppSettings.RecipesPackMigrated"/>; once recorded it never runs again, so a user who
/// later turns the pack on is not turned off a second time.</para>
/// <para><see cref="RunAsync"/> is the startup step: it writes only when the marker is missing, and
/// a settings file that comes from a newer schema is never written (the configuration manager
/// refuses, and the step ends there). Loading settings does not run the step, so reading a file
/// never changes what it says.</para>
/// </remarks>
public static class ToolPackMigration
{
    /// <summary>
    /// Runs the step against the persisted settings when it has not run yet.
    /// </summary>
    /// <param name="configManager">The configuration manager that owns the settings file.</param>
    /// <returns>True when the settings were written.</returns>
    public static async Task<bool> RunAsync(IConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);

        AppSettings current = await configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (current.RecipesPackMigrated)
        {
            return false;
        }

        try
        {
            await configManager.MergeSettingAsync(settings => DisableRecipesPackOnce(settings)).ConfigureAwait(false);
            return true;
        }
        catch (ConfigurationSchemaVersionException ex)
        {
            Logging.FileLogger.Warn($"Tool pack migration skipped: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lists the Recipes pack among the turned-off packs and records the step, unless it already ran.
    /// </summary>
    /// <param name="settings">The settings to bring up to date, changed in place.</param>
    /// <returns>True when the settings changed.</returns>
    public static bool DisableRecipesPackOnce(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.RecipesPackMigrated)
        {
            return false;
        }

        string packName = nameof(ToolPack.Recipes);
        settings.DisabledToolPacks ??= [];
        bool alreadyListed = settings.DisabledToolPacks.Any(entry =>
            string.Equals(entry?.Trim(), packName, StringComparison.OrdinalIgnoreCase));
        if (!alreadyListed)
        {
            settings.DisabledToolPacks.Add(packName);
        }

        RecordRecipesPackChoice(settings);
        return true;
    }

    /// <summary>
    /// Records that the state of the Recipes pack is the user's or the step's own, which is what ends
    /// the fail-closed rule that keeps the pack off while the marker is missing. The one place that
    /// sets the marker besides the step itself: turning the switch on in Settings goes through it.
    /// </summary>
    /// <param name="settings">The settings to mark, changed in place.</param>
    public static void RecordRecipesPackChoice(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.RecipesPackMigrated = true;
    }
}
