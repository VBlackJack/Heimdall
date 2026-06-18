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
/// Service for managing user favorites
/// </summary>
// BUGFIX: Implement IDisposable to properly dispose SemaphoreSlim
public sealed class FavoritesService : IFavoritesService, IDisposable
{
    private readonly IFavoritesRepository _repository;
    private readonly SemaphoreSlim _favoritesLock = new SemaphoreSlim(1, 1);
    private bool _disposed;

    public FavoritesService(IFavoritesRepository repository)
    {
        _repository = repository;
    }

    public async Task<(bool Success, string? ErrorMessage)> AddFavoriteAsync(string actionId, string? userId = null)
    {
        // Use lock to ensure atomicity of check and add operations
        await _favoritesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Check if already favorited
            if (await _repository.IsFavoriteAsync(actionId, userId).ConfigureAwait(false))
            {
                return (false, "This action is already in your favorites.");
            }

            // Check limit
            var currentCount = await _repository.GetCountAsync(userId).ConfigureAwait(false);
            // BUGFIX: Use UIConstants.MaxFavoritesCount instead of local constant
            if (currentCount >= UIConstants.MaxFavoritesCount)
            {
                return (false, $"You have reached the maximum limit of {UIConstants.MaxFavoritesCount} favorites. Please remove some favorites before adding new ones.");
            }

            // Add favorite
            var favorite = new UserFavorite
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                ActionId = actionId,
                CreatedAt = DateTime.UtcNow,
                DisplayOrder = currentCount // Add at the end
            };

            await _repository.AddAsync(favorite).ConfigureAwait(false);
            return (true, null);
        }
        finally
        {
            _favoritesLock.Release();
        }
    }

    public async Task RemoveFavoriteAsync(string actionId, string? userId = null)
    {
        await _repository.RemoveByActionIdAsync(actionId, userId).ConfigureAwait(false);
    }

    public async Task<bool> ToggleFavoriteAsync(string actionId, string? userId = null)
    {
        // Use lock to ensure atomicity of toggle operation
        await _favoritesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var isFavorite = await _repository.IsFavoriteAsync(actionId, userId).ConfigureAwait(false);

            if (isFavorite)
            {
                await _repository.RemoveByActionIdAsync(actionId, userId).ConfigureAwait(false);
                return false;
            }
            else
            {
                // Check limit before adding
                var currentCount = await _repository.GetCountAsync(userId).ConfigureAwait(false);
                if (currentCount >= UIConstants.MaxFavoritesCount)
                {
                    return false;
                }

                var favorite = new UserFavorite
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    ActionId = actionId,
                    CreatedAt = DateTime.UtcNow,
                    DisplayOrder = currentCount
                };

                await _repository.AddAsync(favorite).ConfigureAwait(false);
                return true;
            }
        }
        finally
        {
            _favoritesLock.Release();
        }
    }

    public async Task<bool> IsFavoriteAsync(string actionId, string? userId = null)
    {
        return await _repository.IsFavoriteAsync(actionId, userId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<UserFavorite>> GetAllFavoritesAsync(string? userId = null)
    {
        return await _repository.GetAllAsync(userId).ConfigureAwait(false);
    }

    public async Task<int> GetFavoriteCountAsync(string? userId = null)
    {
        return await _repository.GetCountAsync(userId).ConfigureAwait(false);
    }

    public async Task ReorderFavoriteAsync(string favoriteId, int newOrder)
    {
        await _repository.UpdateDisplayOrderAsync(favoriteId, newOrder).ConfigureAwait(false);
    }

    public async Task ClearAllFavoritesAsync(string? userId = null)
    {
        await _repository.ClearAllAsync(userId).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispose resources to prevent memory leaks
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _favoritesLock?.Dispose();
        _disposed = true;
    }
}
