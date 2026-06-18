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
/// Maps between CommandHistory domain model and CommandHistoryEntity
/// </summary>
public static class CommandHistoryMapper
{
    private static JsonSerializerOptions JsonOptions => JsonOptionsHelper.CompactStorage;

    public static CommandHistoryEntity ToEntity(CommandHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        return new CommandHistoryEntity
        {
            Id = history.Id,
            UserId = history.UserId,
            ActionId = history.ActionId,
            GeneratedCommand = history.GeneratedCommand,
            ParametersJson = JsonSerializer.Serialize(history.Parameters, JsonOptions),
            Platform = history.Platform,
            CreatedAt = history.CreatedAt,
            Category = history.Category,
            ActionTitle = history.ActionTitle,
            IsExecuted = history.IsExecuted,
            ExitCode = history.ExitCode,
            ExecutionDurationTicks = history.ExecutionDuration?.Ticks,
            ExecutionSuccess = history.ExecutionSuccess
        };
    }

    public static CommandHistory ToModel(CommandHistoryEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var history = new CommandHistory
        {
            Id = entity.Id,
            UserId = entity.UserId,
            ActionId = entity.ActionId,
            GeneratedCommand = entity.GeneratedCommand,
            Parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.ParametersJson, JsonOptions)
                ?? new Dictionary<string, string>(),
            Platform = entity.Platform,
            CreatedAt = entity.CreatedAt,
            Category = entity.Category,
            ActionTitle = entity.ActionTitle,
            IsExecuted = entity.IsExecuted,
            ExitCode = entity.ExitCode,
            ExecutionDuration = entity.ExecutionDurationTicks.HasValue
                ? TimeSpan.FromTicks(entity.ExecutionDurationTicks.Value)
                : null,
            ExecutionSuccess = entity.ExecutionSuccess
        };

        if (entity.Action != null)
        {
            history.Action = ActionMapper.ToModel(entity.Action);
        }

        return history;
    }
}
