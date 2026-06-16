/*
 * Copyright 2026 Julien Bombled
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

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.Configuration;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Pure import/export workflow for the Command Library. Builds and writes the
/// JSON envelope on export and parses/merges actions on import without touching
/// WPF, dialogs, or view-model busy state. The maximum import file size is
/// injectable so the limit can be exercised in tests.
/// </summary>
public sealed class CommandLibraryTransferService(
    long maxImportFileSizeBytes = AppConstants.MaxImportFileSizeBytes) : ICommandLibraryTransferService
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <inheritdoc/>
    public async Task<int> ExportAsync(IActionService actionService, string path)
    {
        var actions = (await actionService.GetAllActionsAsync()).ToList();

        var envelope = new
        {
            schemaVersion = "1.0",
            exportDate = DateTime.UtcNow,
            totalActions = actions.Count,
            actions
        };

        var json = JsonSerializer.Serialize(envelope, ExportOptions);
        await File.WriteAllTextAsync(path, json);

        return actions.Count;
    }

    /// <inheritdoc/>
    public async Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > maxImportFileSizeBytes)
        {
            return CommandLibraryImportResult.FileTooLarge();
        }

        var json = await File.ReadAllTextAsync(path);
        var actions = ParseImportJson(json);
        if (actions is null || actions.Count == 0)
        {
            return CommandLibraryImportResult.InvalidFormat();
        }

        int imported = 0, updated = 0, skipped = 0;

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Title)
                || string.IsNullOrWhiteSpace(action.Category)
                || action.Title.Length > AppConstants.MaxImportActionTitleLength
                || action.Category.Length > AppConstants.MaxImportActionCategoryLength)
            {
                skipped++;
                continue;
            }

            var existing = await actionService.GetActionByPublicIdAsync(action.PublicId)
                ?? await actionService.GetActionByIdAsync(action.Id);
            if (existing is not null)
            {
                if (existing.IsUserCreated)
                {
                    action.Id = existing.Id;
                    action.PublicId = existing.PublicId;
                    action.IsUserCreated = existing.IsUserCreated;
                    action.UpdatedAt = DateTime.UtcNow;
                    await actionService.UpdateActionAsync(action);
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                action.IsUserCreated = true;
                action.CreatedAt = DateTime.UtcNow;
                action.UpdatedAt = DateTime.UtcNow;
                await actionService.CreateActionAsync(action);
                imported++;
            }
        }

        return CommandLibraryImportResult.Success(imported, updated, skipped);
    }

    private static List<ActionModel>? ParseImportJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Bulk format: { "actions": [...] }
            if (root.TryGetProperty("actions", out var actionsElement)
                && actionsElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<ActionModel>>(
                    actionsElement.GetRawText(), ImportOptions);
            }

            // Single-action format: { "id": "...", "title": "..." }
            if (root.TryGetProperty("id", out _) && root.TryGetProperty("title", out _))
            {
                var single = JsonSerializer.Deserialize<ActionModel>(json, ImportOptions);
                return single is not null ? [single] : null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
