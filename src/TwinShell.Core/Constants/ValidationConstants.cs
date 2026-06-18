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
/// Validation constants for input validation across TwinShell.
/// </summary>
public static class ValidationConstants
{
    /// <summary>
    /// Maximum length for a parameter value (256 characters).
    /// </summary>
    public const int MaxParameterLength = 256;

    /// <summary>
    /// Maximum length for a command (1024 characters).
    /// </summary>
    public const int MaxCommandLength = 1024;

    /// <summary>
    /// Maximum number of parameters a single command template may declare (50).
    /// </summary>
    public const int MaxParametersPerTemplate = 50;

    /// <summary>
    /// Maximum length for a file path (260 characters, standard Windows MAX_PATH).
    /// </summary>
    public const int MaxPathLength = 260;

    /// <summary>
    /// Minimum search text length before triggering search.
    /// </summary>
    public const int MinSearchLength = 3;

    /// <summary>
    /// Minimum number of days for auto cleanup (1 day).
    /// </summary>
    public const int MinAutoCleanupDays = 1;

    /// <summary>
    /// Maximum number of days for auto cleanup (3650 days = ~10 years).
    /// </summary>
    public const int MaxAutoCleanupDays = 3650;

    /// <summary>
    /// Minimum number of history items to keep (10 items).
    /// </summary>
    public const int MinHistoryItems = 10;

    /// <summary>
    /// Maximum number of history items to keep (100,000 items).
    /// </summary>
    public const int MaxHistoryItems = 100000;

    /// <summary>
    /// Maximum number of recent commands to keep (50 commands).
    /// </summary>
    public const int MaxRecentCommands = 50;

    /// <summary>
    /// History retention in days (90 days).
    /// </summary>
    public const int HistoryRetentionDays = 90;

    /// <summary>
    /// Maximum file size for imports/exports (10MB).
    /// </summary>
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Default number of history entries to load (1000 entries).
    /// </summary>
    public const int DefaultHistoryLoadCount = 1000;

    /// <summary>
    /// Default history page size for pagination (50 items).
    /// </summary>
    public const int DefaultHistoryPageSize = 50;

    /// <summary>
    /// Maximum number of favorites in import (1000 items).
    /// </summary>
    public const int MaxFavoritesInImport = 1000;

    /// <summary>
    /// Maximum number of history entries in import (10000 items).
    /// </summary>
    public const int MaxHistoryInImport = 10000;

    // ===== Action Field Length Constraints =====

    /// <summary>
    /// Maximum length for action title (200 characters).
    /// </summary>
    public const int MaxActionTitleLength = 200;

    /// <summary>
    /// Maximum length for action description (2000 characters).
    /// </summary>
    public const int MaxActionDescriptionLength = 2000;

    /// <summary>
    /// Maximum length for action category name (100 characters).
    /// </summary>
    public const int MaxActionCategoryLength = 100;

    /// <summary>
    /// Maximum length for action notes (5000 characters).
    /// </summary>
    public const int MaxActionNotesLength = 5000;

    /// <summary>
    /// Maximum number of tags per action (20 tags).
    /// </summary>
    public const int MaxActionTagsCount = 20;

    /// <summary>
    /// Maximum number of examples per action (10 examples).
    /// </summary>
    public const int MaxActionExamplesCount = 10;

    /// <summary>
    /// Maximum number of links per action (10 links).
    /// </summary>
    public const int MaxActionLinksCount = 10;

    /// <summary>
    /// Maximum length for command template name (200 characters).
    /// </summary>
    public const int MaxTemplateNameLength = 200;

    /// <summary>
    /// Maximum length for command template pattern (1000 characters).
    /// </summary>
    public const int MaxTemplateCommandPatternLength = 1000;

    // ===== Service Limits =====

    /// <summary>
    /// Maximum number of user favorites (50).
    /// </summary>
    public const int MaxFavorites = 50;

    /// <summary>
    /// Maximum number of custom categories (50).
    /// </summary>
    public const int MaxCustomCategories = 50;

    /// <summary>
    /// Default audit log retention in days (90 days).
    /// </summary>
    public const int DefaultAuditLogRetentionDays = 90;

    /// <summary>
    /// Default audit log limit per query (100 entries).
    /// </summary>
    public const int DefaultAuditLogLimit = 100;

    /// <summary>
    /// Maximum search term length for package managers (200 characters).
    /// </summary>
    public const int MaxSearchTermLength = 200;

    /// <summary>
    /// Maximum module name length (100 characters).
    /// </summary>
    public const int MaxModuleNameLength = 100;

    // ===== UI Constants =====

    /// <summary>
    /// Default snackbar notification duration in milliseconds (3000ms = 3 seconds).
    /// </summary>
    public const int DefaultSnackbarDurationMs = 3000;
}
