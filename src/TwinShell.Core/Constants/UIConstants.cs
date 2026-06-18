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
/// UI-related constants for TwinShell application.
/// </summary>
public static class UIConstants
{
    /// <summary>
    /// The internal name for the favorites category.
    /// </summary>
    public const string FavoritesCategoryName = "Favorites";

    /// <summary>
    /// The display name for the favorites category (with emoji).
    /// </summary>
    public const string FavoritesCategoryDisplay = "⭐ Favorites";

    /// <summary>
    /// The display name for the "All" category (shows all commands).
    /// </summary>
    public const string AllCategoryDisplay = "📋 All Commands";

    /// <summary>
    /// Maximum number of favorites a user can have.
    /// </summary>
    public const int MaxFavoritesCount = 50;

    /// <summary>
    /// Maximum number of recent commands to display.
    /// </summary>
    public const int MaxRecentCommandsCount = 10;

    /// <summary>
    /// Default status message shown when the app is ready.
    /// </summary>
    public const string DefaultStatusMessage = "Ready";

    /// <summary>
    /// Status message shown when loading actions.
    /// </summary>
    public const string LoadingActionsMessage = "Loading actions...";

    /// <summary>
    /// Status message shown when refreshing.
    /// </summary>
    public const string RefreshingMessage = "Refreshing...";
}
