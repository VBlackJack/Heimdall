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

using TwinShell.Core.Constants;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing custom user-defined categories.
/// </summary>
public sealed class CustomCategoryService : ICustomCategoryService
{
    private readonly ICustomCategoryRepository _repository;

    public CustomCategoryService(ICustomCategoryRepository repository)
    {
        _repository = repository;
    }

    public async Task<IEnumerable<CustomCategory>> GetAllCategoriesAsync()
    {
        return await _repository.GetAllAsync().ConfigureAwait(false);
    }

    public async Task<IEnumerable<CustomCategory>> GetVisibleCategoriesAsync()
    {
        return await _repository.GetVisibleCategoriesAsync().ConfigureAwait(false);
    }

    public async Task<CustomCategory?> GetCategoryByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id).ConfigureAwait(false);
    }

    public async Task<CustomCategory> CreateCategoryAsync(string name, string iconKey = "folder", string colorHex = "#2196F3", string? description = null)
    {
        // Validate name
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name cannot be empty", nameof(name));

        if (name.Length > 100)
            throw new ArgumentException("Category name cannot exceed 100 characters", nameof(name));

        // Check uniqueness
        var isUnique = await ValidateCategoryNameAsync(name).ConfigureAwait(false);
        if (!isUnique)
            throw new InvalidOperationException($"A category with the name '{name}' already exists");

        // Check category count limit - PERFORMANCE: Use CountAsync instead of GetAllAsync
        var categoryCount = await _repository.GetCountAsync().ConfigureAwait(false);
        if (categoryCount >= ValidationConstants.MaxCustomCategories)
            throw new InvalidOperationException($"Maximum number of categories ({ValidationConstants.MaxCustomCategories}) reached");

        // Create category
        var category = new CustomCategory
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IconKey = iconKey,
            ColorHex = colorHex,
            Description = description,
            IsSystemCategory = false,
            IsHidden = false,
            DisplayOrder = await _repository.GetNextDisplayOrderAsync().ConfigureAwait(false),
            CreatedAt = DateTime.UtcNow
        };

        return await _repository.CreateAsync(category).ConfigureAwait(false);
    }

    public async Task<bool> UpdateCategoryAsync(CustomCategory category)
    {
        // Check if category exists
        var existing = await _repository.GetByIdAsync(category.Id).ConfigureAwait(false);
        if (existing == null)
            return false;

        // Prevent modification of system categories
        if (existing.IsSystemCategory)
            throw new InvalidOperationException("System categories cannot be modified");

        // Validate name uniqueness (excluding current category)
        var isUnique = await ValidateCategoryNameAsync(category.Name, category.Id).ConfigureAwait(false);
        if (!isUnique)
            throw new InvalidOperationException($"A category with the name '{category.Name}' already exists");

        await _repository.UpdateAsync(category).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteCategoryAsync(string id)
    {
        // Check if category exists and is not a system category
        var isSystem = await _repository.IsCategorySystemAsync(id).ConfigureAwait(false);
        if (isSystem)
            throw new InvalidOperationException("System categories cannot be deleted");

        var category = await _repository.GetByIdAsync(id).ConfigureAwait(false);
        if (category == null)
            return false;

        await _repository.DeleteAsync(id).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ToggleCategoryVisibilityAsync(string id)
    {
        var category = await _repository.GetByIdAsync(id).ConfigureAwait(false);
        if (category == null)
            return false;

        category.IsHidden = !category.IsHidden;
        category.ModifiedAt = DateTime.UtcNow;

        await _repository.UpdateAsync(category).ConfigureAwait(false);
        return true;
    }

    public async Task ReorderCategoriesAsync(IEnumerable<string> categoryIdsInOrder)
    {
        var categories = await _repository.GetAllAsync().ConfigureAwait(false);
        var categoryDict = categories.ToDictionary(c => c.Id);

        // PERFORMANCE: Batch update instead of N individual updates
        var categoriesToUpdate = new List<CustomCategory>();
        int order = 0;
        foreach (var categoryId in categoryIdsInOrder)
        {
            if (categoryDict.TryGetValue(categoryId, out var category))
            {
                category.DisplayOrder = order++;
                category.ModifiedAt = DateTime.UtcNow;
                categoriesToUpdate.Add(category);
            }
        }

        if (categoriesToUpdate.Count > 0)
        {
            await _repository.UpdateBatchAsync(categoriesToUpdate).ConfigureAwait(false);
        }
    }

    public async Task AddActionToCategoryAsync(string actionId, string categoryId)
    {
        if (string.IsNullOrEmpty(actionId) || string.IsNullOrEmpty(categoryId))
            throw new ArgumentException("ActionId and CategoryId cannot be empty");

        await _repository.AddActionToCategoryAsync(actionId, categoryId).ConfigureAwait(false);
    }

    public async Task RemoveActionFromCategoryAsync(string actionId, string categoryId)
    {
        await _repository.RemoveActionFromCategoryAsync(actionId, categoryId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<string>> GetActionsInCategoryAsync(string categoryId)
    {
        return await _repository.GetActionIdsForCategoryAsync(categoryId).ConfigureAwait(false);
    }

    public async Task<bool> ValidateCategoryNameAsync(string name, string? excludeCategoryId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // PERFORMANCE: Use ExistsByNameAsync instead of loading all categories
        var exists = await _repository.ExistsByNameAsync(name, excludeCategoryId).ConfigureAwait(false);
        return !exists;
    }
}
