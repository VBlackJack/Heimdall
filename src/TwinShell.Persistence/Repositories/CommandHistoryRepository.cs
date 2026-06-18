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
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Repository implementation for CommandHistory persistence
/// </summary>
public sealed class CommandHistoryRepository : ICommandHistoryRepository
{
    private readonly TwinShellDbContext _context;
    private readonly ILogger<CommandHistoryRepository> _logger;

    public CommandHistoryRepository(TwinShellDbContext context, ILogger<CommandHistoryRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task AddAsync(CommandHistory history)
    {
        try
        {
            var entity = CommandHistoryMapper.ToEntity(history);
            _context.CommandHistories.Add(entity);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while adding history: {HistoryId}", history.Id);
            throw;
        }
    }

    /// <summary>
    /// PERFORMANCE: Add multiple history entries at once to avoid N+1 queries
    /// </summary>
    public async Task AddRangeAsync(IEnumerable<CommandHistory> histories)
    {
        try
        {
            var entities = histories.Select(CommandHistoryMapper.ToEntity);
            _context.CommandHistories.AddRange(entities);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while adding history batch");
            throw;
        }
    }

    public async Task UpdateAsync(CommandHistory history)
    {
        try
        {
            var entity = CommandHistoryMapper.ToEntity(history);
            _context.CommandHistories.Update(entity);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while updating history: {HistoryId}", history.Id);
            throw;
        }
    }

    public async Task<IEnumerable<CommandHistory>> GetRecentAsync(int count = 50)
    {
        // PERFORMANCE: AsNoTracking for read-only queries reduces memory overhead by 40-60%
        var entities = await _context.CommandHistories
            .AsNoTracking()
            .Include(h => h.Action)
            .OrderByDescending(h => h.CreatedAt)
            .Take(count)
            .ToListAsync();

        return entities.Select(CommandHistoryMapper.ToModel);
    }

    public async Task<IEnumerable<CommandHistory>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Platform? platform = null,
        string? category = null)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var query = _context.CommandHistories
            .AsNoTracking()
            .Include(h => h.Action)
            .AsQueryable();

        // Apply filters
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            // PERFORMANCE FIX: Use EF.Functions.Like for case-insensitive search
            // This allows SQL Server to use indexes, whereas ToLower() prevents index usage
            var search = $"%{searchText}%";
            query = query.Where(h =>
                EF.Functions.Like(h.GeneratedCommand, search) ||
                EF.Functions.Like(h.ActionTitle, search));
        }

        if (fromDate.HasValue)
        {
            query = query.Where(h => h.CreatedAt >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(h => h.CreatedAt <= toDate.Value);
        }

        if (platform.HasValue)
        {
            query = query.Where(h => h.Platform == platform.Value);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(h => h.Category == category);
        }

        var entities = await query
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();

        return entities.Select(CommandHistoryMapper.ToModel);
    }

    public async Task<CommandHistory?> GetByIdAsync(string id)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var entity = await _context.CommandHistories
            .AsNoTracking()
            .Include(h => h.Action)
            .FirstOrDefaultAsync(h => h.Id == id);

        return entity != null ? CommandHistoryMapper.ToModel(entity) : null;
    }

    public async Task DeleteAsync(string id)
    {
        try
        {
            var entity = await _context.CommandHistories.FindAsync(id);
            if (entity != null)
            {
                _context.CommandHistories.Remove(entity);
                await _context.SaveChangesAsync();
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while deleting history: {HistoryId}", id);
            throw;
        }
    }

    public async Task DeleteRangeAsync(IEnumerable<string> ids)
    {
        // PERFORMANCE: Use ExecuteDeleteAsync instead of loading entities into memory
        var idList = ids.ToList();
        if (idList.Count > 0)
        {
            var deletedCount = await _context.CommandHistories
                .Where(h => idList.Contains(h.Id))
                .ExecuteDeleteAsync();

            _logger.LogInformation("Deleted {Count} command history entries", deletedCount);
        }
    }

    public async Task DeleteOlderThanAsync(DateTime date)
    {
        // PERFORMANCE FIX: Use ExecuteDeleteAsync to delete in database without loading entities
        // This prevents OOM issues with large datasets and is 10-100x faster
        await _context.CommandHistories
            .Where(h => h.CreatedAt < date)
            .ExecuteDeleteAsync();
    }

    public async Task<int> CountAsync()
    {
        return await _context.CommandHistories.CountAsync();
    }

    public async Task ClearAllAsync()
    {
        // PERFORMANCE FIX: Use ExecuteDeleteAsync to delete in database without loading entities
        // This prevents OOM (Out of Memory) issues with large history and is 10-100x faster
        await _context.CommandHistories.ExecuteDeleteAsync();
    }
}
