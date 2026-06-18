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
/// Maps between CommandTemplate domain model and CommandTemplateEntity
/// </summary>
public static class CommandTemplateMapper
{
    private static JsonSerializerOptions JsonOptions => JsonOptionsHelper.CompactStorage;

    public static CommandTemplateEntity ToEntity(CommandTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return new CommandTemplateEntity
        {
            Id = template.Id,
            PublicId = template.PublicId,
            Platform = template.Platform,
            Name = template.Name,
            CommandPattern = template.CommandPattern,
            ParametersJson = JsonSerializer.Serialize(template.Parameters, JsonOptions)
        };
    }

    public static CommandTemplate ToModel(CommandTemplateEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new CommandTemplate
        {
            Id = entity.Id,
            PublicId = entity.PublicId,
            Platform = entity.Platform,
            Name = entity.Name,
            CommandPattern = entity.CommandPattern,
            Parameters = JsonSerializer.Deserialize<List<TemplateParameter>>(entity.ParametersJson, JsonOptions) ?? new List<TemplateParameter>()
        };
    }
}
