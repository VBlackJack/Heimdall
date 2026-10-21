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
            CommandsJson = SerializeCommands(batch.Commands),
            TagsJson = SerializeTags(batch.Tags),
            VariablesJson = SerializeVariables(batch.Variables),
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
            Commands = DeserializeCommands(entity.CommandsJson),
            Tags = JsonSerializer.Deserialize<List<string>>(entity.TagsJson, JsonOptions) ?? new List<string>(),
            Variables = DeserializeVariables(entity.VariablesJson),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            LastExecutedAt = entity.LastExecutedAt,
            IsUserCreated = entity.IsUserCreated
        };
    }

    /// <summary>Serializes the steps of a batch for the <c>CommandsJson</c> column.</summary>
    public static string SerializeCommands(List<BatchCommandItem> commands)
        => JsonSerializer.Serialize(commands, JsonOptions);

    /// <summary>Serializes the tags of a batch for the <c>TagsJson</c> column.</summary>
    public static string SerializeTags(List<string> tags)
        => JsonSerializer.Serialize(tags, JsonOptions);

    /// <summary>Serializes the runbook variables of a batch for the <c>VariablesJson</c> column.</summary>
    public static string SerializeVariables(Dictionary<string, string>? variables)
        => JsonSerializer.Serialize(variables ?? new Dictionary<string, string>(StringComparer.Ordinal), JsonOptions);

    /// <summary>
    /// Reads the steps back in their stored <see cref="BatchCommandItem.Order"/>, which is the
    /// order the operator gave them; the position in the JSON array is not trusted for it.
    /// </summary>
    private static List<BatchCommandItem> DeserializeCommands(string? json)
    {
        List<BatchCommandItem> commands = string.IsNullOrWhiteSpace(json)
            ? new List<BatchCommandItem>()
            : JsonSerializer.Deserialize<List<BatchCommandItem>>(json, JsonOptions) ?? new List<BatchCommandItem>();

        foreach (BatchCommandItem command in commands)
        {
            // A row written before steps carried parameters reads the property as null.
            command.Parameters ??= new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return commands.OrderBy(command => command.Order).ToList();
    }

    private static Dictionary<string, string> DeserializeVariables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string>? variables = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        return variables is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(variables, StringComparer.Ordinal);
    }
}
