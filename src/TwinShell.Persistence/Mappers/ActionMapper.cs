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
/// Maps between Action domain model and ActionEntity
/// </summary>
public static class ActionMapper
{
    private static JsonSerializerOptions JsonOptions => JsonOptionsHelper.CompactStorage;

    public static ActionEntity ToEntity(Core.Models.Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return new ActionEntity
        {
            Id = action.Id,
            PublicId = action.PublicId,
            Title = action.Title,
            Description = action.Description,
            Category = action.Category,
            Platform = action.Platform,
            Level = action.Level,
            TagsJson = JsonSerializer.Serialize(action.Tags, JsonOptions),
            WindowsCommandTemplateId = action.WindowsCommandTemplateId,
            LinuxCommandTemplateId = action.LinuxCommandTemplateId,
            ExamplesJson = JsonSerializer.Serialize(action.Examples, JsonOptions),
            WindowsExamplesJson = JsonSerializer.Serialize(action.WindowsExamples, JsonOptions),
            LinuxExamplesJson = JsonSerializer.Serialize(action.LinuxExamples, JsonOptions),
            Notes = action.Notes,
            LinksJson = JsonSerializer.Serialize(action.Links, JsonOptions),
            CreatedAt = action.CreatedAt,
            UpdatedAt = action.UpdatedAt,
            IsUserCreated = action.IsUserCreated
        };
    }

    public static Core.Models.Action ToModel(ActionEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var action = new Core.Models.Action
        {
            Id = entity.Id,
            PublicId = entity.PublicId,
            Title = entity.Title,
            Description = entity.Description,
            Category = entity.Category,
            Platform = entity.Platform,
            Level = entity.Level,
            Tags = JsonSerializer.Deserialize<List<string>>(entity.TagsJson, JsonOptions) ?? new List<string>(),
            WindowsCommandTemplateId = entity.WindowsCommandTemplateId,
            LinuxCommandTemplateId = entity.LinuxCommandTemplateId,
            Examples = JsonSerializer.Deserialize<List<CommandExample>>(entity.ExamplesJson, JsonOptions) ?? new List<CommandExample>(),
            WindowsExamples = JsonSerializer.Deserialize<List<CommandExample>>(entity.WindowsExamplesJson, JsonOptions) ?? new List<CommandExample>(),
            LinuxExamples = JsonSerializer.Deserialize<List<CommandExample>>(entity.LinuxExamplesJson, JsonOptions) ?? new List<CommandExample>(),
            Notes = entity.Notes,
            Links = JsonSerializer.Deserialize<List<ExternalLink>>(entity.LinksJson, JsonOptions) ?? new List<ExternalLink>(),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            IsUserCreated = entity.IsUserCreated
        };

        if (entity.WindowsCommandTemplate != null)
        {
            action.WindowsCommandTemplate = CommandTemplateMapper.ToModel(entity.WindowsCommandTemplate);
        }

        if (entity.LinuxCommandTemplate != null)
        {
            action.LinuxCommandTemplate = CommandTemplateMapper.ToModel(entity.LinuxCommandTemplate);
        }

        return action;
    }
}
