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

using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing search history for autocomplete and suggestions.
/// Stores recent searches to improve user experience.
/// </summary>
public sealed class SearchHistoryService : ISearchHistoryService
{
    private readonly ISearchHistoryRepository _repository;

    public SearchHistoryService(ISearchHistoryRepository repository)
    {
        _repository = repository;
    }

    public async Task AddSearchAsync(string searchTerm, int resultCount, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return;
        }

        var searchHistory = new SearchHistory
        {
            SearchTerm = searchTerm,
            NormalizedSearchTerm = TextNormalizer.NormalizeForSearch(searchTerm),
            ResultCount = resultCount,
            WasSuccessful = resultCount > 0,
            UserId = userId
        };

        await _repository.AddOrUpdateAsync(searchHistory).ConfigureAwait(false);
    }

    public async Task<IEnumerable<SearchHistory>> GetRecentSearchesAsync(int limit = 10, string? userId = null)
    {
        return await _repository.GetRecentAsync(limit, userId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<string>> GetSearchSuggestionsAsync(string partialTerm, int limit = 5, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(partialTerm))
        {
            // Return recent searches if no partial term provided
            var recent = await _repository.GetRecentAsync(limit, userId).ConfigureAwait(false);
            return recent.Select(h => h.SearchTerm);
        }

        var matches = await _repository.SearchAsync(partialTerm, limit, userId).ConfigureAwait(false);
        return matches.Select(h => h.SearchTerm);
    }

    public async Task ClearHistoryAsync(string? userId = null)
    {
        await _repository.ClearAllAsync(userId).ConfigureAwait(false);
    }

    public async Task DeleteSearchAsync(string id)
    {
        await _repository.DeleteAsync(id).ConfigureAwait(false);
    }

    public async Task<IEnumerable<SearchHistory>> GetPopularSearchesAsync(int limit = 10, string? userId = null)
    {
        return await _repository.GetPopularAsync(limit, userId).ConfigureAwait(false);
    }
}
