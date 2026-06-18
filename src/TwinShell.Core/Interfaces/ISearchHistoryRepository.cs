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
/// Repository interface for SearchHistory persistence
/// </summary>
public interface ISearchHistoryRepository
{
    /// <summary>
    /// Add or update a search history entry.
    /// If an entry with the same normalized search term already exists, it will be updated.
    /// </summary>
    Task<SearchHistory> AddOrUpdateAsync(SearchHistory searchHistory);

    /// <summary>
    /// Get the most recent search history entries
    /// </summary>
    /// <param name="limit">Maximum number of entries to retrieve</param>
    /// <param name="userId">Optional user ID to filter by</param>
    Task<IEnumerable<SearchHistory>> GetRecentAsync(int limit = 10, string? userId = null);

    /// <summary>
    /// Get the most popular searches (sorted by search count)
    /// </summary>
    /// <param name="limit">Maximum number of entries to retrieve</param>
    /// <param name="userId">Optional user ID to filter by</param>
    Task<IEnumerable<SearchHistory>> GetPopularAsync(int limit = 10, string? userId = null);

    /// <summary>
    /// Search for search history entries matching a partial term
    /// </summary>
    /// <param name="partialTerm">Partial search term</param>
    /// <param name="limit">Maximum number of entries to retrieve</param>
    /// <param name="userId">Optional user ID to filter by</param>
    Task<IEnumerable<SearchHistory>> SearchAsync(string partialTerm, int limit = 5, string? userId = null);

    /// <summary>
    /// Get search history entry by normalized search term
    /// </summary>
    /// <param name="normalizedSearchTerm">Normalized search term</param>
    /// <param name="userId">Optional user ID</param>
    Task<SearchHistory?> GetByNormalizedTermAsync(string normalizedSearchTerm, string? userId = null);

    /// <summary>
    /// Delete a search history entry
    /// </summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// Clear all search history
    /// </summary>
    /// <param name="userId">Optional user ID to clear only that user's history</param>
    Task ClearAllAsync(string? userId = null);

    /// <summary>
    /// Delete old search history entries (older than specified days)
    /// </summary>
    /// <param name="daysToKeep">Number of days to keep</param>
    Task DeleteOlderThanAsync(int daysToKeep);
}
