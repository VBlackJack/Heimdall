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

using System.Text.Json;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using TwinShell.Persistence.Entities;

namespace TwinShell.Persistence.Mappers;

/// <summary>
/// Maps between CommandBatch domain model and CommandBatchEntity
/// </summary>
public static class CommandBatchMapper
{
    private static JsonSerializerOptions JsonOptions => JsonOptionsHelper.CompactStorage;

    public static CommandBatchEntity ToEntity(CommandBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return new CommandBatchEntity
        {
            Id = batch.Id,
            PublicId = batch.PublicId,
            Name = batch.Name,
            Description = batch.Description,
            ExecutionMode = batch.ExecutionMode,
            CommandsJson = JsonSerializer.Serialize(batch.Commands, JsonOptions),
            TagsJson = JsonSerializer.Serialize(batch.Tags, JsonOptions),
            CreatedAt = batch.CreatedAt,
            UpdatedAt = batch.UpdatedAt,
            LastExecutedAt = batch.LastExecutedAt,
            IsUserCreated = batch.IsUserCreated
        };
    }

    public static CommandBatch ToModel(CommandBatchEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new CommandBatch
        {
            Id = entity.Id,
            PublicId = entity.PublicId,
            Name = entity.Name,
            Description = entity.Description,
            ExecutionMode = entity.ExecutionMode,
            Commands = JsonSerializer.Deserialize<List<BatchCommandItem>>(entity.CommandsJson, JsonOptions) ?? new List<BatchCommandItem>(),
            Tags = JsonSerializer.Deserialize<List<string>>(entity.TagsJson, JsonOptions) ?? new List<string>(),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            LastExecutedAt = entity.LastExecutedAt,
            IsUserCreated = entity.IsUserCreated
        };
    }
}
