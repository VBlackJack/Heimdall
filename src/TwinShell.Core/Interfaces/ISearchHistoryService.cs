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
/// Service for managing search history for autocomplete and suggestions.
/// Stores recent searches to improve user experience.
/// </summary>
public interface ISearchHistoryService
{
    /// <summary>
    /// Records a search query and its results count.
    /// Updates existing entry if the same search was performed before.
    /// </summary>
    /// <param name="searchTerm">The search term used</param>
    /// <param name="resultCount">Number of results found</param>
    /// <param name="userId">Optional user ID</param>
    Task AddSearchAsync(string searchTerm, int resultCount, string? userId = null);

    /// <summary>
    /// Gets recent search history entries for autocomplete.
    /// Returns most recent/popular searches first.
    /// </summary>
    /// <param name="limit">Maximum number of entries to return (default: 10)</param>
    /// <param name="userId">Optional user ID to filter by</param>
    /// <returns>Recent search history entries</returns>
    Task<IEnumerable<SearchHistory>> GetRecentSearchesAsync(int limit = 10, string? userId = null);

    /// <summary>
    /// Gets search suggestions based on partial input.
    /// Uses fuzzy matching to suggest similar past searches.
    /// </summary>
    /// <param name="partialTerm">Partial search term</param>
    /// <param name="limit">Maximum number of suggestions (default: 5)</param>
    /// <param name="userId">Optional user ID to filter by</param>
    /// <returns>Suggested search terms</returns>
    Task<IEnumerable<string>> GetSearchSuggestionsAsync(string partialTerm, int limit = 5, string? userId = null);

    /// <summary>
    /// Clears all search history.
    /// </summary>
    /// <param name="userId">Optional user ID to clear only that user's history</param>
    Task ClearHistoryAsync(string? userId = null);

    /// <summary>
    /// Deletes a specific search history entry.
    /// </summary>
    /// <param name="id">Search history entry ID</param>
    Task DeleteSearchAsync(string id);

    /// <summary>
    /// Gets the most popular searches.
    /// </summary>
    /// <param name="limit">Maximum number of entries to return (default: 10)</param>
    /// <param name="userId">Optional user ID to filter by</param>
    /// <returns>Popular search history entries sorted by search count</returns>
    Task<IEnumerable<SearchHistory>> GetPopularSearchesAsync(int limit = 10, string? userId = null);
}
