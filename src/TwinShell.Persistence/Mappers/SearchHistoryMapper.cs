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
/// Maps between SearchHistory domain model and SearchHistoryEntity
/// </summary>
public static class SearchHistoryMapper
{
    public static SearchHistoryEntity ToEntity(SearchHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        return new SearchHistoryEntity
        {
            Id = history.Id,
            SearchTerm = history.SearchTerm,
            NormalizedSearchTerm = history.NormalizedSearchTerm,
            SearchCount = history.SearchCount,
            ResultCount = history.ResultCount,
            LastSearchedAt = history.LastSearchedAt,
            CreatedAt = history.CreatedAt,
            WasSuccessful = history.WasSuccessful,
            UserId = history.UserId
        };
    }

    public static SearchHistory ToModel(SearchHistoryEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new SearchHistory
        {
            Id = entity.Id,
            SearchTerm = entity.SearchTerm,
            NormalizedSearchTerm = entity.NormalizedSearchTerm,
            SearchCount = entity.SearchCount,
            ResultCount = entity.ResultCount,
            LastSearchedAt = entity.LastSearchedAt,
            CreatedAt = entity.CreatedAt,
            WasSuccessful = entity.WasSuccessful,
            UserId = entity.UserId
        };
    }
}
