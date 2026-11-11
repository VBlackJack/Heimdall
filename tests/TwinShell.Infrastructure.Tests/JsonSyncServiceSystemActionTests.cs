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

using System.Text.Json.Nodes;
using FluentAssertions;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// A sync file must not overwrite a built-in action: the seed refresher never restores an
/// action flagged as user created, so a hijacked built-in would stay hijacked.
/// </summary>
public sealed class JsonSyncServiceSystemActionTests
{
    private const string HijackedTitle = "Hijacked title";
    private const string StoredTitle = "Stored title";

    [Fact]
    public async Task Import_FileTargetingASystemAction_LeavesTemplateLevelAndFlagUnchanged()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            (ActionModel action, CommandTemplate original, CommandTemplate other) = await SeedAsync(fixture);
            string file = await ExportAndHijackAsync(fixture, folder, other);
            file.Should().NotBeNullOrEmpty();
            await MakeStoredCopyOlderSystemActionAsync(fixture, action);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.ActionsUpdated.Should().Be(0);
            result.ActionsSkipped.Should().Be(1);
            result.Warnings.Should().Contain(w => w.Contains("built-in action", StringComparison.Ordinal));
            ActionModel? stored = await fixture.Actions.GetByPublicIdAsync(action.PublicId);
            stored.Should().NotBeNull();
            stored!.IsUserCreated.Should().BeFalse();
            stored.Level.Should().Be(CriticalityLevel.Dangerous);
            stored.Title.Should().Be(StoredTitle);
            stored.WindowsCommandTemplate.Should().NotBeNull();
            stored.WindowsCommandTemplate!.PublicId.Should().Be(original.PublicId);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_FileTargetingAUserCreatedAction_StillUpdatesIt()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            (ActionModel action, CommandTemplate _, CommandTemplate other) = await SeedAsync(fixture);
            await ExportAndHijackAsync(fixture, folder, other);
            ActionModel stored = (await fixture.Actions.GetByPublicIdAsync(action.PublicId))!;
            stored.UpdatedAt = stored.UpdatedAt.AddYears(-1);
            await fixture.Actions.UpdateAsync(stored);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.ActionsUpdated.Should().Be(1);
            ActionModel? updated = await fixture.Actions.GetByPublicIdAsync(action.PublicId);
            updated!.IsUserCreated.Should().BeTrue();
            updated.Title.Should().Be(HijackedTitle);
            updated.Level.Should().Be(CriticalityLevel.Info);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_FileTargetingASystemBatch_LeavesItUnchangedAndReportsIt()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            DateTime createdAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            CommandBatch batch = new()
            {
                Id = "batch-system-target",
                PublicId = Guid.NewGuid(),
                Name = StoredTitle,
                Commands = [],
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                IsUserCreated = true
            };
            await fixture.Batches.AddAsync(batch);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            string file = Directory.GetFiles(Path.Combine(folder, "batches"), "*.json").Single();
            JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
            json["name"] = HijackedTitle;
            await File.WriteAllTextAsync(file, json.ToJsonString());
            CommandBatch stored = (await fixture.Batches.GetByPublicIdAsync(batch.PublicId))!;
            stored.IsUserCreated = false;
            stored.UpdatedAt = createdAt.AddYears(-1);
            await fixture.Batches.UpdateAsync(stored);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.BatchesUpdated.Should().Be(0);
            result.BatchesSkipped.Should().Be(1);
            result.Warnings.Should().Contain(w => w.Contains("built-in batch", StringComparison.Ordinal));
            CommandBatch? after = await fixture.Batches.GetByPublicIdAsync(batch.PublicId);
            after!.IsUserCreated.Should().BeFalse();
            after.Name.Should().Be(StoredTitle);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    private static async Task<(ActionModel Action, CommandTemplate Original, CommandTemplate Other)> SeedAsync(
        TwinShellSyncFixture fixture)
    {
        CommandTemplate original = CreateTemplate("template-original", "Original template");
        CommandTemplate other = CreateTemplate("template-other", "Other template");
        await fixture.Templates.AddAsync(original);
        await fixture.Templates.AddAsync(other);

        DateTime createdAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        ActionModel action = new()
        {
            Id = "action-system-target",
            PublicId = Guid.NewGuid(),
            Title = StoredTitle,
            Description = "Sync target",
            Category = "Development",
            Platform = Platform.Windows,
            Level = CriticalityLevel.Dangerous,
            Tags = ["sync"],
            WindowsCommandTemplateId = original.Id,
            WindowsCommandTemplate = original,
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddHours(1),
            IsUserCreated = true
        };
        await fixture.Actions.AddAsync(action);
        return (action, original, other);
    }

    private static async Task<string> ExportAndHijackAsync(
        TwinShellSyncFixture fixture,
        string folder,
        CommandTemplate other)
    {
        (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
        string file = Directory.GetFiles(Path.Combine(folder, "actions"), "*.json", SearchOption.AllDirectories).Single();
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        json["title"] = HijackedTitle;
        json["level"] = "Info";
        json["windowsTemplateId"] = other.PublicId.ToString();
        await File.WriteAllTextAsync(file, json.ToJsonString());
        return file;
    }

    private static async Task MakeStoredCopyOlderSystemActionAsync(TwinShellSyncFixture fixture, ActionModel action)
    {
        ActionModel stored = (await fixture.Actions.GetByPublicIdAsync(action.PublicId))!;
        stored.IsUserCreated = false;
        stored.UpdatedAt = stored.UpdatedAt.AddYears(-1);
        await fixture.Actions.UpdateAsync(stored);
    }

    private static CommandTemplate CreateTemplate(string id, string name)
    {
        return new CommandTemplate
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            Name = name,
            Platform = Platform.Windows,
            CommandPattern = "Get-Date",
            Parameters = []
        };
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "heimdall_twinshell_system_action_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
