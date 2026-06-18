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

namespace TwinShell.Core.Constants;

/// <summary>
/// Database-related constants for TwinShell application.
/// </summary>
public static class DatabaseConstants
{
    /// <summary>
    /// Default database connection string for SQLite.
    /// </summary>
    public const string DefaultConnectionString = "Data Source=twinshell.db";

    /// <summary>
    /// Default database file name.
    /// </summary>
    public const string DefaultDatabaseFileName = "twinshell.db";

    /// <summary>
    /// Configuration file name for JSON export/import.
    /// </summary>
    public const string ConfigurationFileName = "TwinShell-Config";

    /// <summary>
    /// JSON file extension.
    /// </summary>
    public const string JsonFileExtension = ".json";

    /// <summary>
    /// JSON file filter for dialogs.
    /// </summary>
    public const string JsonFileFilter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
}
