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

using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for managing actions
/// </summary>
public interface IActionService
{
    Task<IEnumerable<ActionModel>> GetAllActionsAsync();
    Task<ActionModel?> GetActionByIdAsync(string id);
    Task<ActionModel?> GetActionByPublicIdAsync(Guid publicId);
    Task<IEnumerable<ActionModel>> GetActionsByCategoryAsync(string category);
    Task<IEnumerable<string>> GetAllCategoriesAsync();
    Task<IEnumerable<ActionModel>> FilterActionsAsync(
        IEnumerable<ActionModel> actions,
        Platform? platform = null,
        CriticalityLevel? level = null);
    Task<ActionModel> CreateActionAsync(ActionModel action);
    Task UpdateActionAsync(ActionModel action);
    Task DeleteActionAsync(string id);

    /// <summary>
    /// Gets the count of actions for a specific category
    /// </summary>
    Task<int> GetActionCountByCategoryAsync(string category);

    /// <summary>
    /// Renames a category across all actions that use it
    /// </summary>
    Task<bool> RenameCategoryAsync(string oldName, string newName);

    /// <summary>
    /// Deletes a category by removing it from all actions (sets to empty string)
    /// </summary>
    Task<bool> DeleteCategoryAsync(string categoryName);
}
