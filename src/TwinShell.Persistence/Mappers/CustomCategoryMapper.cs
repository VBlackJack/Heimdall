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
/// Mapper for converting between CustomCategory domain model and CustomCategoryEntity.
/// </summary>
public static class CustomCategoryMapper
{
    public static CustomCategory ToDomain(CustomCategoryEntity entity, IEnumerable<ActionCategoryMappingEntity>? mappings = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var actionIds = mappings?.Where(m => m.CategoryId == entity.Id)
                                .Select(m => m.ActionId)
                                .ToList() ?? new List<string>();

        return new CustomCategory
        {
            Id = entity.Id,
            PublicId = entity.PublicId,
            Name = entity.Name,
            IconKey = entity.IconKey,
            ColorHex = entity.ColorHex,
            IsSystemCategory = entity.IsSystemCategory,
            DisplayOrder = entity.DisplayOrder,
            IsHidden = entity.IsHidden,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt,
            ModifiedAt = entity.ModifiedAt,
            ActionIds = actionIds
        };
    }

    public static CustomCategoryEntity ToEntity(CustomCategory domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        return new CustomCategoryEntity
        {
            Id = domain.Id,
            PublicId = domain.PublicId,
            Name = domain.Name,
            IconKey = domain.IconKey,
            ColorHex = domain.ColorHex,
            IsSystemCategory = domain.IsSystemCategory,
            DisplayOrder = domain.DisplayOrder,
            IsHidden = domain.IsHidden,
            Description = domain.Description,
            CreatedAt = domain.CreatedAt,
            ModifiedAt = domain.ModifiedAt
        };
    }

    public static void UpdateEntity(CustomCategoryEntity entity, CustomCategory domain)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(domain);

        entity.Name = domain.Name;
        entity.IconKey = domain.IconKey;
        entity.ColorHex = domain.ColorHex;
        entity.DisplayOrder = domain.DisplayOrder;
        entity.IsHidden = domain.IsHidden;
        entity.Description = domain.Description;
        entity.ModifiedAt = DateTime.UtcNow;
    }
}
