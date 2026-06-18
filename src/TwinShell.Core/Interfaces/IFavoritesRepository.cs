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
/// Repository interface for UserFavorite persistence
/// </summary>
public interface IFavoritesRepository
{
    /// <summary>
    /// Add a new favorite
    /// </summary>
    Task AddAsync(UserFavorite favorite);

    /// <summary>
    /// PERFORMANCE: Add multiple favorites at once
    /// </summary>
    Task AddRangeAsync(IEnumerable<UserFavorite> favorites);

    /// <summary>
    /// Get all favorites for a user, ordered by DisplayOrder
    /// </summary>
    /// <param name="userId">User ID (nullable for single-user mode)</param>
    Task<IEnumerable<UserFavorite>> GetAllAsync(string? userId = null);

    /// <summary>
    /// Get favorite by action ID
    /// </summary>
    Task<UserFavorite?> GetByActionIdAsync(string actionId, string? userId = null);

    /// <summary>
    /// Check if an action is favorited
    /// </summary>
    Task<bool> IsFavoriteAsync(string actionId, string? userId = null);

    /// <summary>
    /// Remove a favorite
    /// </summary>
    Task RemoveAsync(string favoriteId);

    /// <summary>
    /// Remove favorite by action ID
    /// </summary>
    Task RemoveByActionIdAsync(string actionId, string? userId = null);

    /// <summary>
    /// Get count of favorites for a user
    /// </summary>
    Task<int> GetCountAsync(string? userId = null);

    /// <summary>
    /// Update favorite display order
    /// </summary>
    Task UpdateDisplayOrderAsync(string favoriteId, int newOrder);

    /// <summary>
    /// Clear all favorites for a user
    /// </summary>
    Task ClearAllAsync(string? userId = null);
}
