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
/// Service interface for managing user favorites
/// </summary>
public interface IFavoritesService
{
    /// <summary>
    /// Add an action to favorites
    /// </summary>
    /// <param name="actionId">Action ID to favorite</param>
    /// <param name="userId">User ID (nullable for single-user mode)</param>
    /// <returns>Success result with error message if limit exceeded</returns>
    Task<(bool Success, string? ErrorMessage)> AddFavoriteAsync(string actionId, string? userId = null);

    /// <summary>
    /// Remove an action from favorites
    /// </summary>
    Task RemoveFavoriteAsync(string actionId, string? userId = null);

    /// <summary>
    /// Toggle favorite status for an action
    /// </summary>
    /// <returns>New favorite status (true if now favorited, false if unfavorited)</returns>
    Task<bool> ToggleFavoriteAsync(string actionId, string? userId = null);

    /// <summary>
    /// Check if an action is favorited
    /// </summary>
    Task<bool> IsFavoriteAsync(string actionId, string? userId = null);

    /// <summary>
    /// Get all favorite actions
    /// </summary>
    Task<IEnumerable<UserFavorite>> GetAllFavoritesAsync(string? userId = null);

    /// <summary>
    /// Get count of favorites
    /// </summary>
    Task<int> GetFavoriteCountAsync(string? userId = null);

    /// <summary>
    /// Reorder a favorite
    /// </summary>
    Task ReorderFavoriteAsync(string favoriteId, int newOrder);

    /// <summary>
    /// Clear all favorites
    /// </summary>
    Task ClearAllFavoritesAsync(string? userId = null);
}
