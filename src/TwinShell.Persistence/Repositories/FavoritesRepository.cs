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
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Repository implementation for UserFavorite persistence
/// </summary>
public sealed class FavoritesRepository : IFavoritesRepository
{
    private readonly TwinShellDbContext _context;
    private readonly ILogger<FavoritesRepository> _logger;

    public FavoritesRepository(TwinShellDbContext context, ILogger<FavoritesRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Adds a new favorite to the database.
    /// </summary>
    /// <param name="favorite">The favorite to add</param>
    public async Task AddAsync(UserFavorite favorite)
    {
        try
        {
            var entity = UserFavoriteMapper.ToEntity(favorite);
            _context.UserFavorites.Add(entity);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while adding favorite: {FavoriteId}", favorite.Id);
            throw;
        }
    }

    /// <summary>
    /// PERFORMANCE: Add multiple favorites at once to avoid N+1 queries
    /// </summary>
    public async Task AddRangeAsync(IEnumerable<UserFavorite> favorites)
    {
        try
        {
            var entities = favorites.Select(UserFavoriteMapper.ToEntity);
            _context.UserFavorites.AddRange(entities);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while adding favorites batch");
            throw;
        }
    }

    /// <summary>
    /// Gets all favorites for a user, ordered by display order.
    /// </summary>
    /// <param name="userId">Optional user ID filter</param>
    /// <returns>Collection of user favorites</returns>
    public async Task<IEnumerable<UserFavorite>> GetAllAsync(string? userId = null)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var query = _context.UserFavorites
            .AsNoTracking()
            .Include(f => f.Action)
                .ThenInclude(a => a!.WindowsCommandTemplate)
            .Include(f => f.Action)
                .ThenInclude(a => a!.LinuxCommandTemplate)
            .AsQueryable();

        if (userId != null)
        {
            query = query.Where(f => f.UserId == userId);
        }
        else
        {
            query = query.Where(f => f.UserId == null);
        }

        var entities = await query
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync();

        return entities.Select(UserFavoriteMapper.ToModel);
    }

    /// <summary>
    /// Gets a favorite by action ID.
    /// </summary>
    /// <param name="actionId">The action ID to find</param>
    /// <param name="userId">Optional user ID filter</param>
    /// <returns>The favorite if found, null otherwise</returns>
    public async Task<UserFavorite?> GetByActionIdAsync(string actionId, string? userId = null)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var query = _context.UserFavorites
            .AsNoTracking()
            .Include(f => f.Action)
            .AsQueryable();

        if (userId != null)
        {
            query = query.Where(f => f.UserId == userId && f.ActionId == actionId);
        }
        else
        {
            query = query.Where(f => f.UserId == null && f.ActionId == actionId);
        }

        var entity = await query.FirstOrDefaultAsync();
        return entity != null ? UserFavoriteMapper.ToModel(entity) : null;
    }

    /// <summary>
    /// Checks if an action is marked as favorite.
    /// </summary>
    /// <param name="actionId">The action ID to check</param>
    /// <param name="userId">Optional user ID filter</param>
    /// <returns>True if the action is a favorite</returns>
    public async Task<bool> IsFavoriteAsync(string actionId, string? userId = null)
    {
        if (userId != null)
        {
            return await _context.UserFavorites
                .AnyAsync(f => f.UserId == userId && f.ActionId == actionId);
        }
        else
        {
            return await _context.UserFavorites
                .AnyAsync(f => f.UserId == null && f.ActionId == actionId);
        }
    }

    /// <summary>
    /// Removes a favorite by its ID.
    /// </summary>
    /// <param name="favoriteId">The favorite ID to remove</param>
    public async Task RemoveAsync(string favoriteId)
    {
        try
        {
            var entity = await _context.UserFavorites.FindAsync(favoriteId);
            if (entity != null)
            {
                _context.UserFavorites.Remove(entity);
                await _context.SaveChangesAsync();
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while removing favorite: {FavoriteId}", favoriteId);
            throw;
        }
    }

    /// <summary>
    /// Removes a favorite by action ID.
    /// </summary>
    /// <param name="actionId">The action ID to unfavorite</param>
    /// <param name="userId">Optional user ID filter</param>
    public async Task RemoveByActionIdAsync(string actionId, string? userId = null)
    {
        var query = _context.UserFavorites.AsQueryable();

        if (userId != null)
        {
            query = query.Where(f => f.UserId == userId && f.ActionId == actionId);
        }
        else
        {
            query = query.Where(f => f.UserId == null && f.ActionId == actionId);
        }

        var entity = await query.FirstOrDefaultAsync();
        if (entity != null)
        {
            _context.UserFavorites.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Gets the count of favorites for a user.
    /// </summary>
    /// <param name="userId">Optional user ID filter</param>
    /// <returns>Number of favorites</returns>
    public async Task<int> GetCountAsync(string? userId = null)
    {
        if (userId != null)
        {
            return await _context.UserFavorites.CountAsync(f => f.UserId == userId);
        }
        else
        {
            return await _context.UserFavorites.CountAsync(f => f.UserId == null);
        }
    }

    /// <summary>
    /// Updates the display order of a favorite.
    /// </summary>
    /// <param name="favoriteId">The favorite ID to update</param>
    /// <param name="newOrder">The new display order</param>
    public async Task UpdateDisplayOrderAsync(string favoriteId, int newOrder)
    {
        var entity = await _context.UserFavorites.FindAsync(favoriteId);
        if (entity != null)
        {
            entity.DisplayOrder = newOrder;
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Clears all favorites for a user.
    /// </summary>
    /// <param name="userId">Optional user ID filter</param>
    public async Task ClearAllAsync(string? userId = null)
    {
        // PERFORMANCE: Use ExecuteDeleteAsync instead of loading entities into memory
        int deletedCount;
        if (userId != null)
        {
            deletedCount = await _context.UserFavorites
                .Where(f => f.UserId == userId)
                .ExecuteDeleteAsync();
        }
        else
        {
            deletedCount = await _context.UserFavorites
                .Where(f => f.UserId == null)
                .ExecuteDeleteAsync();
        }

        _logger.LogInformation("Cleared {Count} favorites for user {UserId}", deletedCount, userId ?? "default");
    }
}
