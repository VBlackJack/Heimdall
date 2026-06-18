/*
 * Copyright 2025 Julien Bombled
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

using TwinShell.Core.Models;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for managing user settings and preferences.
/// Settings are persisted in JSON format at %APPDATA%/TwinShell/settings.json
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the current user settings.
    /// </summary>
    UserSettings CurrentSettings { get; }

    /// <summary>
    /// Loads user settings from the JSON file.
    /// If the file doesn't exist, returns default settings.
    /// </summary>
    /// <returns>The loaded or default user settings.</returns>
    Task<UserSettings> LoadSettingsAsync();

    /// <summary>
    /// Saves user settings to the JSON file.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    /// <returns>True if save was successful, false otherwise.</returns>
    Task<bool> SaveSettingsAsync(UserSettings settings);

    /// <summary>
    /// Resets settings to default values and saves to file.
    /// </summary>
    /// <returns>The default settings.</returns>
    Task<UserSettings> ResetToDefaultAsync();

    /// <summary>
    /// Gets the path where settings are stored.
    /// </summary>
    /// <returns>The full path to settings.json</returns>
    string GetSettingsFilePath();

    /// <summary>
    /// Validates user settings to ensure all values are within acceptable ranges.
    /// </summary>
    /// <param name="settings">The settings to validate.</param>
    /// <returns>True if settings are valid, false otherwise.</returns>
    bool ValidateSettings(UserSettings settings);
}
