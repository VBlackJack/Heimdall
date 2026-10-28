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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Repository implementation for Action persistence
/// </summary>
public sealed class ActionRepository : IActionRepository
{
    private readonly TwinShellDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ActionRepository> _logger;

    // Cache key for categories
    private const string CategoriesCacheKey = "ActionRepository_Categories";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public ActionRepository(TwinShellDbContext context, IMemoryCache cache, ILogger<ActionRepository> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Every live action. A retired one is left out: it is kept only so the history and
    /// favorites that point at it stay readable through <see cref="GetByIdAsync"/>.
    /// </summary>
    public async Task<IEnumerable<Core.Models.Action>> GetAllAsync()
    {
        // PERFORMANCE: AsNoTracking for read-only queries reduces memory overhead by 40-60%
        var entities = await _context.Actions
            .AsNoTracking()
            .Where(a => !a.IsRetired)
            .Include(a => a.WindowsCommandTemplate)
            .Include(a => a.LinuxCommandTemplate)
            .Include(a => a.Translations)
            .ToListAsync();

        return entities.Select(ActionMapper.ToModel);
    }

    public async Task<Core.Models.Action?> GetByIdAsync(string id)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var entity = await _context.Actions
            .AsNoTracking()
            .Include(a => a.WindowsCommandTemplate)
            .Include(a => a.LinuxCommandTemplate)
            .Include(a => a.Translations)
            .FirstOrDefaultAsync(a => a.Id == id);

        return entity != null ? ActionMapper.ToModel(entity) : null;
    }

    public async Task<IEnumerable<Core.Models.Action>> GetByCategoryAsync(string category)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var entities = await _context.Actions
            .AsNoTracking()
            .Include(a => a.WindowsCommandTemplate)
            .Include(a => a.LinuxCommandTemplate)
            .Include(a => a.Translations)
            .Where(a => a.Category == category && !a.IsRetired)
            .ToListAsync();

        return entities.Select(ActionMapper.ToModel);
    }

    public async Task<IEnumerable<string>> GetAllCategoriesAsync()
    {
        // PERFORMANCE: Use IMemoryCache with GetOrCreateAsync for thread-safe caching
        var categories = await _cache.GetOrCreateAsync(CategoriesCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            // Fetch from database
            var result = await _context.Actions
                .AsNoTracking()
                .Where(a => !a.IsRetired)
                .Select(a => a.Category)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();

            return result.AsReadOnly();
        });

        return categories ?? Enumerable.Empty<string>();
    }

    /// <summary>
    /// Invalidates the categories cache (call after add/update/delete operations that change categories)
    /// </summary>
    private void InvalidateCategoriesCache()
    {
        _cache.Remove(CategoriesCacheKey);
    }

    public async Task AddAsync(Core.Models.Action action)
    {
        try
        {
            // BUGFIX: Removed explicit transaction - EF Core automatically wraps SaveChangesAsync() in a transaction
            // Add command templates first if they exist
            if (action.WindowsCommandTemplate != null)
            {
                var windowsTemplateEntity = CommandTemplateMapper.ToEntity(action.WindowsCommandTemplate);
                if (!await _context.CommandTemplates.AnyAsync(t => t.Id == windowsTemplateEntity.Id))
                {
                    _context.CommandTemplates.Add(windowsTemplateEntity);
                }
            }

            if (action.LinuxCommandTemplate != null)
            {
                var linuxTemplateEntity = CommandTemplateMapper.ToEntity(action.LinuxCommandTemplate);
                if (!await _context.CommandTemplates.AnyAsync(t => t.Id == linuxTemplateEntity.Id))
                {
                    _context.CommandTemplates.Add(linuxTemplateEntity);
                }
            }

            var entity = ActionMapper.ToEntity(action);
            _context.Actions.Add(entity);
            // EF Core ensures all tracked changes are saved atomically in a single transaction
            await _context.SaveChangesAsync();

            // Invalidate cache since a new action may have a new category
            InvalidateCategoriesCache();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while adding action: {ActionId}", action.Id);
            throw;
        }
    }

    public async Task UpdateAsync(Core.Models.Action action)
    {
        try
        {
            // BUGFIX: Handle EF Core tracking - detach any existing tracked entities first
            // Add or update Windows command template if it exists
            if (action.WindowsCommandTemplate != null)
            {
                var windowsTemplateEntity = CommandTemplateMapper.ToEntity(action.WindowsCommandTemplate);
                var existingWindows = await _context.CommandTemplates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == windowsTemplateEntity.Id);

                // Detach any tracked entity with the same ID
                var trackedWindows = _context.ChangeTracker.Entries<Entities.CommandTemplateEntity>()
                    .FirstOrDefault(e => e.Entity.Id == windowsTemplateEntity.Id);
                if (trackedWindows != null)
                {
                    trackedWindows.State = EntityState.Detached;
                }

                if (existingWindows == null)
                {
                    _context.CommandTemplates.Add(windowsTemplateEntity);
                }
                else
                {
                    _context.CommandTemplates.Update(windowsTemplateEntity);
                }
            }

            // Add or update Linux command template if it exists
            if (action.LinuxCommandTemplate != null)
            {
                var linuxTemplateEntity = CommandTemplateMapper.ToEntity(action.LinuxCommandTemplate);
                var existingLinux = await _context.CommandTemplates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == linuxTemplateEntity.Id);

                // Detach any tracked entity with the same ID
                var trackedLinux = _context.ChangeTracker.Entries<Entities.CommandTemplateEntity>()
                    .FirstOrDefault(e => e.Entity.Id == linuxTemplateEntity.Id);
                if (trackedLinux != null)
                {
                    trackedLinux.State = EntityState.Detached;
                }

                if (existingLinux == null)
                {
                    _context.CommandTemplates.Add(linuxTemplateEntity);
                }
                else
                {
                    _context.CommandTemplates.Update(linuxTemplateEntity);
                }
            }

            var entity = ActionMapper.ToEntity(action);

            // Detach any tracked Action entity with the same ID
            var trackedAction = _context.ChangeTracker.Entries<Entities.ActionEntity>()
                .FirstOrDefault(e => e.Entity.Id == entity.Id);
            if (trackedAction != null)
            {
                trackedAction.State = EntityState.Detached;
            }

            var translations = DetachTranslations(entity);
            _context.Actions.Update(entity);

            await using var transaction = await BeginTransactionIfNoneAsync();
            await ReplaceTranslationsAsync([entity.Id], translations);
            await _context.SaveChangesAsync();
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            // Invalidate cache since category may have changed
            InvalidateCategoriesCache();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while updating action: {ActionId}", action.Id);
            throw;
        }
    }

    public async Task DeleteAsync(string id)
    {
        try
        {
            var entity = await _context.Actions.FindAsync(id);
            if (entity != null)
            {
                if (entity.IsUserCreated)
                {
                    await RecordSharedDeleteAsync(entity);
                }

                _context.Actions.Remove(entity);
                await _context.SaveChangesAsync();

                // Invalidate cache since a category may now be empty
                InvalidateCategoriesCache();
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while deleting action: {ActionId}", id);
            throw;
        }
    }

    /// <summary>
    /// Records the delete of a user action, and of each of its templates no other action uses,
    /// for the next sync export: the folder only learns of a delete through its tombstone.
    /// </summary>
    private async Task RecordSharedDeleteAsync(Entities.ActionEntity entity)
    {
        DateTime deletedAt = DateTime.UtcNow;
        await SyncTombstoneRepository.RecordAsync(_context, entity.PublicId, deletedAt);

        foreach (string templateId in new[] { entity.WindowsCommandTemplateId, entity.LinuxCommandTemplateId }.OfType<string>().Distinct())
        {
            bool usedElsewhere = await _context.Actions.AnyAsync(a =>
                a.Id != entity.Id && (a.WindowsCommandTemplateId == templateId || a.LinuxCommandTemplateId == templateId));
            Guid? templatePublicId = await _context.CommandTemplates
                .Where(t => t.Id == templateId)
                .Select(t => (Guid?)t.PublicId)
                .FirstOrDefaultAsync();
            if (!usedElsewhere && templatePublicId is { } publicId)
            {
                await SyncTombstoneRepository.RecordAsync(_context, publicId, deletedAt);
            }
        }
    }

    public async Task<bool> ExistsAsync(string id)
    {
        return await _context.Actions.AnyAsync(a => a.Id == id);
    }

    public async Task<int> CountAsync()
    {
        return await _context.Actions.CountAsync(a => !a.IsRetired);
    }

    /// <summary>
    /// PERFORMANCE: Efficiently counts actions in a category using database-level COUNT
    /// This avoids loading all actions into memory just to count them
    /// </summary>
    public async Task<int> CountByCategoryAsync(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return 0;

        return await _context.Actions
            .Where(a => a.Category == category && !a.IsRetired)
            .CountAsync();
    }

    /// <summary>
    /// PERFORMANCE: Batch update category for all actions in a category using single SQL UPDATE
    /// This prevents N+1 queries when renaming or deleting categories
    /// </summary>
    public async Task<int> UpdateCategoryForActionsAsync(string oldCategory, string? newCategory)
    {
        if (string.IsNullOrWhiteSpace(oldCategory))
            return 0;

        var targetCategory = newCategory ?? string.Empty;
        var now = DateTime.UtcNow;

        // Use EF Core's ExecuteUpdateAsync for a single SQL UPDATE statement
        // This is much more efficient than loading all entities and updating them individually
        var result = await _context.Actions
            .Where(a => a.Category == oldCategory)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Category, targetCategory)
                .SetProperty(a => a.UpdatedAt, now));

        // Invalidate cache since categories changed
        if (result > 0)
        {
            InvalidateCategoriesCache();
        }

        return result;
    }

    /// <summary>
    /// Gets an action by its public ID (for GitOps sync)
    /// </summary>
    public async Task<Core.Models.Action?> GetByPublicIdAsync(Guid publicId)
    {
        var entity = await _context.Actions
            .AsNoTracking()
            .Include(a => a.WindowsCommandTemplate)
            .Include(a => a.LinuxCommandTemplate)
            .Include(a => a.Translations)
            .FirstOrDefaultAsync(a => a.PublicId == publicId);

        return entity != null ? ActionMapper.ToModel(entity) : null;
    }

    /// <summary>
    /// Gets all actions with their associated command templates
    /// </summary>
    public async Task<IEnumerable<Core.Models.Action>> GetAllWithTemplatesAsync()
    {
        var entities = await _context.Actions
            .AsNoTracking()
            .Include(a => a.WindowsCommandTemplate)
            .Include(a => a.LinuxCommandTemplate)
            .Include(a => a.Translations)
            .ToListAsync();

        return entities.Select(ActionMapper.ToModel);
    }

    /// <summary>
    /// PERFORMANCE: Adds multiple actions in a single batch operation
    /// Uses EF Core's AddRange for better performance than individual adds
    /// </summary>
    public async Task AddRangeAsync(IEnumerable<Core.Models.Action> actions)
    {
        try
        {
            var actionsList = actions.ToList();
            if (!actionsList.Any())
                return;

            // Collect all templates first
            var templates = new List<Entities.CommandTemplateEntity>();
            foreach (var action in actionsList)
            {
                if (action.WindowsCommandTemplate != null)
                {
                    templates.Add(CommandTemplateMapper.ToEntity(action.WindowsCommandTemplate));
                }
                if (action.LinuxCommandTemplate != null)
                {
                    templates.Add(CommandTemplateMapper.ToEntity(action.LinuxCommandTemplate));
                }
            }

            // Add templates in batch (filter out duplicates by Id)
            if (templates.Any())
            {
                var uniqueTemplates = templates
                    .GroupBy(t => t.Id)
                    .Select(g => g.First())
                    .ToList();
                _context.CommandTemplates.AddRange(uniqueTemplates);
            }

            // Add actions in batch
            var entities = actionsList.Select(ActionMapper.ToEntity).ToList();
            _context.Actions.AddRange(entities);

            // Single SaveChanges for all operations
            await _context.SaveChangesAsync();

            // Invalidate cache
            InvalidateCategoriesCache();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while batch adding {Count} actions", actions.Count());
            throw;
        }
    }

    /// <summary>
    /// PERFORMANCE: Updates multiple actions in a single batch operation
    /// Uses EF Core's UpdateRange for better performance than individual updates
    /// </summary>
    public async Task UpdateRangeAsync(IEnumerable<Core.Models.Action> actions)
    {
        try
        {
            var actionsList = actions.ToList();
            if (!actionsList.Any())
                return;

            // Update templates first
            foreach (var action in actionsList)
            {
                if (action.WindowsCommandTemplate != null)
                {
                    var templateEntity = CommandTemplateMapper.ToEntity(action.WindowsCommandTemplate);
                    var tracked = _context.ChangeTracker.Entries<Entities.CommandTemplateEntity>()
                        .FirstOrDefault(e => e.Entity.Id == templateEntity.Id);
                    if (tracked != null)
                    {
                        tracked.State = EntityState.Detached;
                    }
                    _context.CommandTemplates.Update(templateEntity);
                }

                if (action.LinuxCommandTemplate != null)
                {
                    var templateEntity = CommandTemplateMapper.ToEntity(action.LinuxCommandTemplate);
                    var tracked = _context.ChangeTracker.Entries<Entities.CommandTemplateEntity>()
                        .FirstOrDefault(e => e.Entity.Id == templateEntity.Id);
                    if (tracked != null)
                    {
                        tracked.State = EntityState.Detached;
                    }
                    _context.CommandTemplates.Update(templateEntity);
                }
            }

            // Update actions in batch
            var entities = actionsList.Select(ActionMapper.ToEntity).ToList();

            // Detach any tracked entities first
            foreach (var entity in entities)
            {
                var tracked = _context.ChangeTracker.Entries<Entities.ActionEntity>()
                    .FirstOrDefault(e => e.Entity.Id == entity.Id);
                if (tracked != null)
                {
                    tracked.State = EntityState.Detached;
                }
            }

            var translations = entities.SelectMany(DetachTranslations).ToList();
            _context.Actions.UpdateRange(entities);

            // Single SaveChanges for all operations
            await using var transaction = await BeginTransactionIfNoneAsync();
            await ReplaceTranslationsAsync(entities.Select(e => e.Id).ToList(), translations);
            await _context.SaveChangesAsync();
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            // Invalidate cache
            InvalidateCategoriesCache();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while batch updating {Count} actions", actions.Count());
            throw;
        }
    }

    /// <summary>
    /// Takes the translation rows off <paramref name="entity"/> so an <c>Update</c> of the
    /// action does not mark them as existing rows to modify.
    /// </summary>
    /// <remarks>
    /// An update replaces the action's set of translations rather than merging it: a culture
    /// dropped from the action has to leave the database too, and the rows carry fresh ids
    /// whenever they come from a file.
    /// </remarks>
    private static List<Entities.ActionTranslationEntity> DetachTranslations(Entities.ActionEntity entity)
    {
        var translations = entity.Translations.ToList();
        entity.Translations = new();
        return translations;
    }

    /// <summary>
    /// Deletes the stored translations of <paramref name="actionIds"/> and queues
    /// <paramref name="translations"/> in their place, to be written by the caller's
    /// <c>SaveChanges</c>.
    /// </summary>
    private async Task ReplaceTranslationsAsync(
        IReadOnlyCollection<string> actionIds,
        List<Entities.ActionTranslationEntity> translations)
    {
        foreach (var tracked in _context.ChangeTracker.Entries<Entities.ActionTranslationEntity>()
            .Where(e => actionIds.Contains(e.Entity.ActionId))
            .ToList())
        {
            tracked.State = EntityState.Detached;
        }

        await _context.ActionTranslations
            .Where(t => actionIds.Contains(t.ActionId))
            .ExecuteDeleteAsync();

        _context.ActionTranslations.AddRange(translations);
    }

    /// <summary>
    /// Opens a transaction when the caller has not, so the delete of the old translations and
    /// the save of the new ones land together. Returns null when one is already open: the
    /// caller that opened it owns the commit.
    /// </summary>
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionIfNoneAsync()
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await _context.Database.BeginTransactionAsync();
    }
}
