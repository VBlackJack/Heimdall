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
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Repository implementation for managing custom categories.
/// </summary>
public sealed class CustomCategoryRepository : ICustomCategoryRepository
{
    private readonly TwinShellDbContext _context;

    public CustomCategoryRepository(TwinShellDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<CustomCategory>> GetAllAsync()
    {
        // BUGFIX: Add AsNoTracking for read-only query performance
        var entities = await _context.CustomCategories
            .AsNoTracking()
            .Include(c => c.ActionMappings)
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync();

        // PERFORMANCE: Only fetch mappings for returned categories instead of all mappings
        var categoryIds = entities.Select(e => e.Id).ToList();
        var mappings = await _context.ActionCategoryMappings
            .AsNoTracking()
            .Where(m => categoryIds.Contains(m.CategoryId))
            .ToListAsync();

        return entities.Select(e => CustomCategoryMapper.ToDomain(e, mappings));
    }

    public async Task<CustomCategory?> GetByIdAsync(string id)
    {
        // BUGFIX: Add AsNoTracking for read-only query performance
        var entity = await _context.CustomCategories
            .AsNoTracking()
            .Include(c => c.ActionMappings)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (entity == null)
            return null;

        var mappings = await _context.ActionCategoryMappings
            .Where(m => m.CategoryId == id)
            .ToListAsync();

        return CustomCategoryMapper.ToDomain(entity, mappings);
    }

    public async Task<CustomCategory> CreateAsync(CustomCategory category)
    {
        var entity = CustomCategoryMapper.ToEntity(category);
        _context.CustomCategories.Add(entity);
        await _context.SaveChangesAsync();

        return CustomCategoryMapper.ToDomain(entity);
    }

    public async Task UpdateAsync(CustomCategory category)
    {
        var entity = await _context.CustomCategories.FindAsync(category.Id);
        if (entity == null)
            throw new InvalidOperationException($"Category with ID {category.Id} not found");

        CustomCategoryMapper.UpdateEntity(entity, category);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.CustomCategories.FindAsync(id);
        if (entity != null)
        {
            _context.CustomCategories.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<CustomCategory>> GetVisibleCategoriesAsync()
    {
        var entities = await _context.CustomCategories
            .Include(c => c.ActionMappings)
            .Where(c => !c.IsHidden)
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync();

        // PERFORMANCE: Only fetch mappings for returned categories instead of all mappings
        var categoryIds = entities.Select(e => e.Id).ToList();
        var mappings = await _context.ActionCategoryMappings
            .AsNoTracking()
            .Where(m => categoryIds.Contains(m.CategoryId))
            .ToListAsync();

        return entities.Select(e => CustomCategoryMapper.ToDomain(e, mappings));
    }

    public async Task<IEnumerable<string>> GetActionIdsForCategoryAsync(string categoryId)
    {
        return await _context.ActionCategoryMappings
            .Where(m => m.CategoryId == categoryId)
            .Select(m => m.ActionId)
            .ToListAsync();
    }

    public async Task AddActionToCategoryAsync(string actionId, string categoryId)
    {
        // Check if mapping already exists
        var exists = await _context.ActionCategoryMappings
            .AnyAsync(m => m.ActionId == actionId && m.CategoryId == categoryId);

        if (exists)
            return;

        var mapping = new ActionCategoryMappingEntity
        {
            ActionId = actionId,
            CategoryId = categoryId
        };

        _context.ActionCategoryMappings.Add(mapping);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveActionFromCategoryAsync(string actionId, string categoryId)
    {
        var mapping = await _context.ActionCategoryMappings
            .FirstOrDefaultAsync(m => m.ActionId == actionId && m.CategoryId == categoryId);

        if (mapping != null)
        {
            _context.ActionCategoryMappings.Remove(mapping);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<bool> IsCategorySystemAsync(string categoryId)
    {
        var category = await _context.CustomCategories.FindAsync(categoryId);
        return category?.IsSystemCategory ?? false;
    }

    public async Task<int> GetNextDisplayOrderAsync()
    {
        var maxOrder = await _context.CustomCategories.MaxAsync(c => (int?)c.DisplayOrder);
        return (maxOrder ?? 0) + 1;
    }

    public async Task<int> GetCountAsync()
    {
        return await _context.CustomCategories.CountAsync();
    }

    public async Task<bool> ExistsByNameAsync(string name, string? excludeId = null)
    {
        return await _context.CustomCategories
            .AnyAsync(c => c.Name.ToLower() == name.ToLower() &&
                          (excludeId == null || c.Id != excludeId));
    }

    public async Task UpdateBatchAsync(IEnumerable<CustomCategory> categories)
    {
        foreach (var category in categories)
        {
            var entity = await _context.CustomCategories.FindAsync(category.Id);
            if (entity != null)
            {
                CustomCategoryMapper.UpdateEntity(entity, category);
            }
        }
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Gets a category by its public ID (for GitOps sync)
    /// </summary>
    public async Task<CustomCategory?> GetByPublicIdAsync(Guid publicId)
    {
        var entity = await _context.CustomCategories
            .AsNoTracking()
            .Include(c => c.ActionMappings)
            .FirstOrDefaultAsync(c => c.PublicId == publicId);

        if (entity == null)
            return null;

        var mappings = await _context.ActionCategoryMappings
            .AsNoTracking()
            .Where(m => m.CategoryId == entity.Id)
            .ToListAsync();

        return CustomCategoryMapper.ToDomain(entity, mappings);
    }
}
