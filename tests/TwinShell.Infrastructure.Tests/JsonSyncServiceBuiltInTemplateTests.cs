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
/// A template file must not rewrite the command of a template a built-in action uses, whether
/// or not the sync folder carries that action's own file.
/// </summary>
public sealed class JsonSyncServiceBuiltInTemplateTests
{
    private const string StoredPattern = "Get-Date";
    private const string ReplacedPattern = "Get-Process";
    private const string OwnerActionId = "action-owner";

    [Fact]
    public async Task Import_TemplateOnlyFileForABuiltInTemplate_KeepsThePatternAndReportsIt()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            CommandTemplate template = await SeedAsync(fixture);
            await ExportAndReplacePatternAsync(fixture, folder, template, keepActionFile: false, flagActionFileUserCreated: false);
            await MakeBuiltInAsync(fixture);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.TemplatesUpdated.Should().Be(0);
            result.TemplatesSkipped.Should().Be(1);
            result.Warnings.Should().Contain(w => w.Contains("built-in", StringComparison.Ordinal));
            (await fixture.Templates.GetByPublicIdAsync(template.PublicId))!.CommandPattern.Should().Be(StoredPattern);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_UserFlaggedActionFileForABuiltInTemplate_KeepsThePatternAndReportsIt()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            CommandTemplate template = await SeedAsync(fixture);
            await ExportAndReplacePatternAsync(fixture, folder, template, keepActionFile: true, flagActionFileUserCreated: true);
            await MakeBuiltInAsync(fixture);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.TemplatesUpdated.Should().Be(0);
            result.TemplatesSkipped.Should().Be(1);
            (await fixture.Templates.GetByPublicIdAsync(template.PublicId))!.CommandPattern.Should().Be(StoredPattern);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_TemplateOfAUserAction_IsStillUpdated()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            CommandTemplate template = await SeedAsync(fixture);
            await ExportAndReplacePatternAsync(fixture, folder, template, keepActionFile: false, flagActionFileUserCreated: false);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.TemplatesUpdated.Should().Be(1);
            result.TemplatesSkipped.Should().Be(0);
            (await fixture.Templates.GetByPublicIdAsync(template.PublicId))!.CommandPattern.Should().Be(ReplacedPattern);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_TemplateSharedByABuiltInAndAUserAction_IsProtected()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            CommandTemplate template = await SeedAsync(fixture);
            await AddActionAsync(fixture, template, "action-user-sharing", isUserCreated: true);
            await ExportAndReplacePatternAsync(fixture, folder, template, keepActionFile: false, flagActionFileUserCreated: false);
            await MakeBuiltInAsync(fixture);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.TemplatesSkipped.Should().Be(1);
            (await fixture.Templates.GetByPublicIdAsync(template.PublicId))!.CommandPattern.Should().Be(StoredPattern);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    private static async Task<CommandTemplate> SeedAsync(TwinShellSyncFixture fixture)
    {
        CommandTemplate template = new()
        {
            Id = "template-builtin-target",
            PublicId = Guid.NewGuid(),
            Name = "Built-in template",
            Platform = Platform.Windows,
            CommandPattern = StoredPattern,
            Parameters = []
        };
        await fixture.Templates.AddAsync(template);
        await AddActionAsync(fixture, template, OwnerActionId, isUserCreated: true);
        return template;
    }

    private static async Task AddActionAsync(
        TwinShellSyncFixture fixture,
        CommandTemplate template,
        string id,
        bool isUserCreated)
    {
        DateTime createdAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await fixture.Actions.AddAsync(new ActionModel
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            Title = id,
            Description = "Owner of the template",
            Category = "Development",
            Platform = Platform.Windows,
            Level = CriticalityLevel.Info,
            Tags = ["sync"],
            WindowsCommandTemplateId = template.Id,
            WindowsCommandTemplate = template,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            IsUserCreated = isUserCreated
        });
    }

    private static async Task MakeBuiltInAsync(TwinShellSyncFixture fixture)
    {
        ActionModel owner = (await fixture.Actions.GetByIdAsync(OwnerActionId))!;
        owner.IsUserCreated = false;
        await fixture.Actions.UpdateAsync(owner);
    }

    private static async Task ExportAndReplacePatternAsync(
        TwinShellSyncFixture fixture,
        string folder,
        CommandTemplate template,
        bool keepActionFile,
        bool flagActionFileUserCreated)
    {
        (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
        string templateFile = Directory
            .GetFiles(Path.Combine(folder, "templates"), "*.json", SearchOption.AllDirectories)
            .Single(path => File.ReadAllText(path).Contains(template.PublicId.ToString(), StringComparison.OrdinalIgnoreCase));
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(templateFile))!.AsObject();
        json["commandPattern"] = ReplacedPattern;
        await File.WriteAllTextAsync(templateFile, json.ToJsonString());

        foreach (string actionFile in Directory.GetFiles(Path.Combine(folder, "actions"), "*.json", SearchOption.AllDirectories))
        {
            if (!keepActionFile)
            {
                File.Delete(actionFile);
                continue;
            }

            JsonObject action = JsonNode.Parse(await File.ReadAllTextAsync(actionFile))!.AsObject();
            action["isUserCreated"] = flagActionFileUserCreated;
            await File.WriteAllTextAsync(actionFile, action.ToJsonString());
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "heimdall_twinshell_builtin_template_" + Guid.NewGuid().ToString("N"));
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
