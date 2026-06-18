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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Repository implementation for SearchHistory persistence
/// </summary>
public sealed class SearchHistoryRepository : ISearchHistoryRepository
{
    private readonly TwinShellDbContext _context;
    private readonly ILogger<SearchHistoryRepository> _logger;

    public SearchHistoryRepository(TwinShellDbContext context, ILogger<SearchHistoryRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<SearchHistory> AddOrUpdateAsync(SearchHistory searchHistory)
    {
        try
        {
            // Normalize the search term for consistent storage
            searchHistory.NormalizedSearchTerm = TextNormalizer.NormalizeForSearch(searchHistory.SearchTerm);

            // Try to find existing entry
            var existing = await GetByNormalizedTermAsync(searchHistory.NormalizedSearchTerm, searchHistory.UserId);

            if (existing != null)
            {
                // Update existing entry
                existing.SearchCount++;
                existing.ResultCount = searchHistory.ResultCount;
                existing.LastSearchedAt = DateTime.UtcNow;
                existing.WasSuccessful = searchHistory.WasSuccessful;

                var entity = SearchHistoryMapper.ToEntity(existing);
                _context.SearchHistories.Update(entity);
            }
            else
            {
                // Add new entry
                searchHistory.Id = Guid.NewGuid().ToString();
                searchHistory.CreatedAt = DateTime.UtcNow;
                searchHistory.LastSearchedAt = DateTime.UtcNow;

                var entity = SearchHistoryMapper.ToEntity(searchHistory);
                _context.SearchHistories.Add(entity);
            }

            await _context.SaveChangesAsync();
            return existing ?? searchHistory;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while adding/updating search history: {SearchTerm}", searchHistory.SearchTerm);
            throw;
        }
    }

    public async Task<IEnumerable<SearchHistory>> GetRecentAsync(int limit = 10, string? userId = null)
    {
        var query = _context.SearchHistories
            .AsNoTracking()
            .Where(h => h.WasSuccessful);

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(h => h.UserId == userId);
        }

        var entities = await query
            .OrderByDescending(h => h.LastSearchedAt)
            .Take(limit)
            .ToListAsync();

        return entities.Select(SearchHistoryMapper.ToModel);
    }

    public async Task<IEnumerable<SearchHistory>> GetPopularAsync(int limit = 10, string? userId = null)
    {
        var query = _context.SearchHistories
            .AsNoTracking()
            .Where(h => h.WasSuccessful);

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(h => h.UserId == userId);
        }

        var entities = await query
            .OrderByDescending(h => h.SearchCount)
            .ThenByDescending(h => h.LastSearchedAt)
            .Take(limit)
            .ToListAsync();

        return entities.Select(SearchHistoryMapper.ToModel);
    }

    public async Task<IEnumerable<SearchHistory>> SearchAsync(string partialTerm, int limit = 5, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(partialTerm))
        {
            return Enumerable.Empty<SearchHistory>();
        }

        var normalizedPartial = TextNormalizer.NormalizeForSearch(partialTerm);
        var search = $"%{normalizedPartial}%";

        var query = _context.SearchHistories
            .AsNoTracking()
            .Where(h => h.WasSuccessful);

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(h => h.UserId == userId);
        }

        var entities = await query
            .Where(h => EF.Functions.Like(h.NormalizedSearchTerm, search))
            .OrderByDescending(h => h.SearchCount)
            .ThenByDescending(h => h.LastSearchedAt)
            .Take(limit)
            .ToListAsync();

        return entities.Select(SearchHistoryMapper.ToModel);
    }

    public async Task<SearchHistory?> GetByNormalizedTermAsync(string normalizedSearchTerm, string? userId = null)
    {
        var query = _context.SearchHistories
            .AsNoTracking()
            .Where(h => h.NormalizedSearchTerm == normalizedSearchTerm);

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(h => h.UserId == userId);
        }
        else
        {
            query = query.Where(h => h.UserId == null);
        }

        var entity = await query.FirstOrDefaultAsync();
        return entity != null ? SearchHistoryMapper.ToModel(entity) : null;
    }

    public async Task DeleteAsync(string id)
    {
        try
        {
            var entity = await _context.SearchHistories.FindAsync(id);
            if (entity != null)
            {
                _context.SearchHistories.Remove(entity);
                await _context.SaveChangesAsync();
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while deleting search history: {HistoryId}", id);
            throw;
        }
    }

    public async Task ClearAllAsync(string? userId = null)
    {
        // PERFORMANCE FIX: Use ExecuteDeleteAsync to delete in database without loading entities
        // This prevents OOM issues with large search history and is 10-100x faster
        if (!string.IsNullOrEmpty(userId))
        {
            await _context.SearchHistories
                .Where(h => h.UserId == userId)
                .ExecuteDeleteAsync();
        }
        else
        {
            await _context.SearchHistories.ExecuteDeleteAsync();
        }
    }

    public async Task DeleteOlderThanAsync(int daysToKeep)
    {
        if (daysToKeep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(daysToKeep), "Value must be positive");
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);

        // PERFORMANCE: Use ExecuteDeleteAsync instead of loading entities into memory
        var deletedCount = await _context.SearchHistories
            .Where(h => h.LastSearchedAt < cutoffDate)
            .ExecuteDeleteAsync();

        _logger.LogInformation("Deleted {Count} search history entries older than {Days} days", deletedCount, daysToKeep);
    }
}
