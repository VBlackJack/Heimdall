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
using TwinShell.Persistence.Entities;

namespace TwinShell.Persistence.Mappers;

/// <summary>
/// Maps between UserFavorite domain model and UserFavoriteEntity
/// </summary>
public static class UserFavoriteMapper
{
    public static UserFavoriteEntity ToEntity(UserFavorite favorite)
    {
        ArgumentNullException.ThrowIfNull(favorite);

        return new UserFavoriteEntity
        {
            Id = favorite.Id,
            UserId = favorite.UserId,
            ActionId = favorite.ActionId,
            CreatedAt = favorite.CreatedAt,
            DisplayOrder = favorite.DisplayOrder
        };
    }

    public static UserFavorite ToModel(UserFavoriteEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var favorite = new UserFavorite
        {
            Id = entity.Id,
            UserId = entity.UserId,
            ActionId = entity.ActionId,
            CreatedAt = entity.CreatedAt,
            DisplayOrder = entity.DisplayOrder
        };

        if (entity.Action != null)
        {
            favorite.Action = ActionMapper.ToModel(entity.Action);
        }

        return favorite;
    }
}
