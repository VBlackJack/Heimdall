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
/// Service for managing custom user-defined categories.
/// </summary>
public interface ICustomCategoryService
{
    /// <summary>
    /// Gets all custom categories, ordered by display order.
    /// </summary>
    Task<IEnumerable<CustomCategory>> GetAllCategoriesAsync();

    /// <summary>
    /// Gets only visible (non-hidden) categories.
    /// </summary>
    Task<IEnumerable<CustomCategory>> GetVisibleCategoriesAsync();

    /// <summary>
    /// Gets a custom category by ID.
    /// </summary>
    Task<CustomCategory?> GetCategoryByIdAsync(string id);

    /// <summary>
    /// Creates a new custom category.
    /// </summary>
    /// <param name="name">Category name</param>
    /// <param name="iconKey">Icon key (e.g., "folder", "star")</param>
    /// <param name="colorHex">Color in hex format (e.g., "#2196F3")</param>
    /// <param name="description">Optional description</param>
    Task<CustomCategory> CreateCategoryAsync(string name, string iconKey = "folder", string colorHex = "#2196F3", string? description = null);

    /// <summary>
    /// Updates an existing custom category.
    /// System categories cannot be modified.
    /// </summary>
    Task<bool> UpdateCategoryAsync(CustomCategory category);

    /// <summary>
    /// Deletes a custom category.
    /// System categories cannot be deleted.
    /// </summary>
    Task<bool> DeleteCategoryAsync(string id);

    /// <summary>
    /// Toggles the hidden state of a category.
    /// </summary>
    Task<bool> ToggleCategoryVisibilityAsync(string id);

    /// <summary>
    /// Reorders categories by updating display orders.
    /// </summary>
    Task ReorderCategoriesAsync(IEnumerable<string> categoryIdsInOrder);

    /// <summary>
    /// Adds an action to a category.
    /// </summary>
    Task AddActionToCategoryAsync(string actionId, string categoryId);

    /// <summary>
    /// Removes an action from a category.
    /// </summary>
    Task RemoveActionFromCategoryAsync(string actionId, string categoryId);

    /// <summary>
    /// Gets all actions assigned to a specific category.
    /// </summary>
    Task<IEnumerable<string>> GetActionsInCategoryAsync(string categoryId);

    /// <summary>
    /// Validates category name (unique, not empty, length limits).
    /// </summary>
    Task<bool> ValidateCategoryNameAsync(string name, string? excludeCategoryId = null);
}
