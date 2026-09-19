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
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Security;
using TwinShell.Persistence.Entities;

namespace TwinShell.Persistence.Mappers;

/// <summary>
/// Maps between CommandHistory domain model and CommandHistoryEntity.
/// </summary>
/// <remarks>
/// The generated command and the parameter values are what the user typed, so they are
/// sealed on the way in and opened on the way out through
/// <see cref="HistorySecretEnvelope"/>. Everything else - the action title, the category,
/// the timestamp - stays in clear, because the panel has to stay readable and sortable
/// when the sealed halves cannot be opened.
/// </remarks>
public static class CommandHistoryMapper
{
    /// <summary>
    /// Stands in for a command whose stored form could not be opened.
    /// </summary>
    /// <remarks>
    /// Empty rather than a message: this value flows into the model that the clipboard and
    /// any future replay read from, and a human-readable apology there would be copied and
    /// pasted into a terminal. The view decides how to say "unreadable"; the model says
    /// nothing at all.
    /// </remarks>
    public const string UnreadableCommand = "";

    private static JsonSerializerOptions JsonOptions => JsonOptionsHelper.CompactStorage;

    public static CommandHistoryEntity ToEntity(CommandHistory history, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(protector);

        return new CommandHistoryEntity
        {
            Id = history.Id,
            UserId = history.UserId,
            ActionId = history.ActionId,
            GeneratedCommand = HistorySecretEnvelope.Seal(history.GeneratedCommand, protector),
            ParametersJson = HistorySecretEnvelope.Seal(
                JsonSerializer.Serialize(history.Parameters, JsonOptions), protector),
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

    public static CommandHistory ToModel(CommandHistoryEntity entity, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(protector);

        var command = HistorySecretEnvelope.Open(entity.GeneratedCommand, protector);
        var parametersJson = HistorySecretEnvelope.Open(entity.ParametersJson, protector);

        var history = new CommandHistory
        {
            Id = entity.Id,
            UserId = entity.UserId,
            ActionId = entity.ActionId,
            GeneratedCommand = command ?? UnreadableCommand,
            IsReadable = command is not null,
            Parameters = DeserializeParameters(parametersJson),
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

    /// <summary>
    /// Reads the parameter dictionary, treating an unopenable or malformed payload as an
    /// absence of parameters rather than as a failure of the whole row.
    /// </summary>
    private static Dictionary<string, string> DeserializeParameters(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
