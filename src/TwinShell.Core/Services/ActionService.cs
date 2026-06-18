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
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing actions
/// </summary>
public sealed class ActionService : IActionService
{
    private readonly IActionRepository _repository;

    public ActionService(IActionRepository repository)
    {
        _repository = repository;
    }

    public async Task<IEnumerable<ActionModel>> GetAllActionsAsync()
    {
        return await _repository.GetAllAsync().ConfigureAwait(false);
    }

    public async Task<ActionModel?> GetActionByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id).ConfigureAwait(false);
    }

    public async Task<ActionModel?> GetActionByPublicIdAsync(Guid publicId)
    {
        return await _repository.GetByPublicIdAsync(publicId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<ActionModel>> GetActionsByCategoryAsync(string category)
    {
        return await _repository.GetByCategoryAsync(category).ConfigureAwait(false);
    }

    public async Task<IEnumerable<string>> GetAllCategoriesAsync()
    {
        return await _repository.GetAllCategoriesAsync().ConfigureAwait(false);
    }

    public async Task<IEnumerable<ActionModel>> FilterActionsAsync(
        IEnumerable<ActionModel> actions,
        Platform? platform = null,
        CriticalityLevel? level = null)
    {
        var filtered = actions.AsEnumerable();

        if (platform.HasValue)
        {
            filtered = filtered.Where(a =>
                a.Platform == platform.Value ||
                a.Platform == Platform.Both);
        }

        if (level.HasValue)
        {
            filtered = filtered.Where(a => a.Level == level.Value);
        }

        return await Task.FromResult(filtered).ConfigureAwait(false);
    }

    public async Task<ActionModel> CreateActionAsync(ActionModel action)
    {
        // BUGFIX: Add validation to prevent invalid data from being saved
        if (!ValidateAction(action, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(action));
        }

        action.Id = Guid.NewGuid().ToString();
        action.CreatedAt = DateTime.UtcNow;
        action.UpdatedAt = DateTime.UtcNow;
        action.IsUserCreated = true;

        await _repository.AddAsync(action).ConfigureAwait(false);
        return action;
    }

    public async Task UpdateActionAsync(ActionModel action)
    {
        // BUGFIX: Add validation to prevent invalid data from being saved
        if (!ValidateAction(action, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(action));
        }

        action.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(action).ConfigureAwait(false);
    }

    public async Task DeleteActionAsync(string id)
    {
        await _repository.DeleteAsync(id).ConfigureAwait(false);
    }

    public async Task<int> GetActionCountByCategoryAsync(string category)
    {
        // PERFORMANCE: Use database-level COUNT instead of loading all actions into memory
        return await _repository.CountByCategoryAsync(category).ConfigureAwait(false);
    }

    public async Task<bool> RenameCategoryAsync(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return false;

        if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            return true; // Nothing to do

        // PERFORMANCE: Use batch update instead of N+1 individual updates
        await _repository.UpdateCategoryForActionsAsync(oldName, newName).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteCategoryAsync(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return false;

        // PERFORMANCE: Use batch update instead of N+1 individual updates
        // Pass null to clear the category (sets to empty string)
        await _repository.UpdateCategoryForActionsAsync(categoryName, null).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Validates action data using centralized validation constants
    /// </summary>
    private static bool ValidateAction(ActionModel action, out string errorMessage)
    {
        // Check required fields
        if (string.IsNullOrWhiteSpace(action?.Title))
        {
            errorMessage = "Title is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(action.Category))
        {
            errorMessage = "Category is required.";
            return false;
        }

        // Check field lengths
        if (action.Title.Length > ValidationConstants.MaxActionTitleLength)
        {
            errorMessage = $"Title cannot exceed {ValidationConstants.MaxActionTitleLength} characters.";
            return false;
        }

        if (action.Category.Length > ValidationConstants.MaxActionCategoryLength)
        {
            errorMessage = $"Category cannot exceed {ValidationConstants.MaxActionCategoryLength} characters.";
            return false;
        }

        if ((action.Description?.Length ?? 0) > ValidationConstants.MaxActionDescriptionLength)
        {
            errorMessage = $"Description cannot exceed {ValidationConstants.MaxActionDescriptionLength} characters.";
            return false;
        }

        if ((action.Notes?.Length ?? 0) > ValidationConstants.MaxActionNotesLength)
        {
            errorMessage = $"Notes cannot exceed {ValidationConstants.MaxActionNotesLength} characters.";
            return false;
        }

        // Check collections sizes
        if ((action.Tags?.Count ?? 0) > ValidationConstants.MaxActionTagsCount)
        {
            errorMessage = $"Cannot have more than {ValidationConstants.MaxActionTagsCount} tags.";
            return false;
        }

        if ((action.Examples?.Count ?? 0) > ValidationConstants.MaxActionExamplesCount)
        {
            errorMessage = $"Cannot have more than {ValidationConstants.MaxActionExamplesCount} examples.";
            return false;
        }

        if ((action.Links?.Count ?? 0) > ValidationConstants.MaxActionLinksCount)
        {
            errorMessage = $"Cannot have more than {ValidationConstants.MaxActionLinksCount} links.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }
}
