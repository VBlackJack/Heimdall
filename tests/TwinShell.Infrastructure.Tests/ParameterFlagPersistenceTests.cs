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

using System.Text.Json;
using FluentAssertions;
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Infrastructure.Services;
using TwinShell.Persistence.Seeding;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// The flag a template parameter declares (<see cref="TemplateParameter.Flag"/>) reaches an
/// existing database through the seed refresh, and travels through a Git sync export and import
/// without disturbing a file that does not use it.
/// </summary>
public sealed class ParameterFlagPersistenceTests
{
    private const string SeedWithoutFlag = """
        {
          "id": "set-user", "title": "Set a user", "category": "active-directory", "platform": 0, "level": 1,
          "windowsCommandTemplateId": "set-user-cmd",
          "windowsCommandTemplate": {
            "id": "set-user-cmd", "platform": 0, "name": "Set-ADUser",
            "commandPattern": "Set-ADUser -Identity {username} -Title {title}",
            "parameters": [
              { "name": "username", "label": "Username", "type": "string", "required": true },
              { "name": "title", "label": "Title", "type": "string", "required": false }
            ]
          }
        }
        """;

    private const string SeedWithFlag = """
        {
          "id": "set-user", "title": "Set a user", "category": "active-directory", "platform": 0, "level": 1,
          "windowsCommandTemplateId": "set-user-cmd",
          "windowsCommandTemplate": {
            "id": "set-user-cmd", "platform": 0, "name": "Set-ADUser",
            "commandPattern": "Set-ADUser -Identity {username} -Title {title}",
            "parameters": [
              { "name": "username", "label": "Username", "type": "string", "required": true },
              { "name": "title", "label": "Title", "type": "string", "flag": "-Title", "required": false }
            ]
          }
        }
        """;

    [Fact]
    public async Task ASeedThatAddsAFlag_ReachesADatabaseSeededWithoutIt()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await refresher.RefreshAsync([ParseSeed(SeedWithoutFlag)], "hash-before");
        (await StoredTitleParameter(fixture)).Flag.Should().BeNull("the database starts as an older build left it");

        SeedRefreshResult result = await refresher.RefreshAsync([ParseSeed(SeedWithFlag)], "hash-after");

        result.Updated.Should().Be(1);
        (await StoredTitleParameter(fixture)).Flag.Should().Be("-Title");
    }

    [Fact]
    public async Task ATemplateWithAFlag_RoundTripsThroughASyncFolder_AndAnUnflaggedOneWritesNoFlagKey()
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimdall_sync_flag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using TwinShellSyncFixture source = await TwinShellSyncFixture.CreateAsync();
            CommandTemplate template = new()
            {
                Id = "tpl-flag",
                Name = "Flagged template",
                Platform = Platform.Windows,
                CommandPattern = "Set-ADUser -Identity {username} -Title {title} -Office {office}",
                Parameters =
                [
                    new TemplateParameter { Name = "username", Label = "Username", Required = true },
                    new TemplateParameter { Name = "title", Label = "Title", Flag = "-Title" },
                    new TemplateParameter { Name = "office", Label = "Office" }
                ]
            };
            await source.Actions.AddAsync(new ActionModel
            {
                Id = "act-flag",
                Title = "Flagged action",
                Category = CommandCategories.Development,
                Platform = Platform.Windows,
                Level = CriticalityLevel.Info,
                WindowsCommandTemplateId = template.Id,
                WindowsCommandTemplate = template,
                IsUserCreated = true
            });
            (await source.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            string written = File.ReadAllText(Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
                .Single(file => File.ReadAllText(file).Contains("Flagged template", StringComparison.Ordinal)));
            using (JsonDocument document = JsonDocument.Parse(written))
            {
                JsonElement[] parameters = document.RootElement.GetProperty("parameters").EnumerateArray().ToArray();
                parameters.Single(p => p.GetProperty("name").GetString() == "title").GetProperty("flag").GetString()
                    .Should().Be("-Title");
                parameters.Where(p => p.GetProperty("name").GetString() != "title")
                    .Select(p => p.EnumerateObject().Select(property => property.Name).ToArray())
                    .Should().OnlyContain(
                        names => !names.Contains("flag"),
                        "a parameter without a flag writes the file it wrote before the field existed");
            }

            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            SyncImportResult imported = await target.SyncService.ImportDataFromYamlAsync(folder);

            imported.Warnings.Should().BeEmpty();
            CommandTemplate stored = (await target.Templates.GetByPublicIdAsync(template.PublicId))!;
            stored.Parameters.Single(p => p.Name == "title").Flag.Should().Be("-Title");
            stored.Parameters.Single(p => p.Name == "office").Flag.Should().BeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ATemplateFileWithoutAFlag_StillPassesTheSchema_AndOneWithAFlagPassesToo()
    {
        const string without = """{ "id": "22222222-2222-4222-8222-222222222222", "name": "t", "commandPattern": "x -T {p}", "parameters": [ { "name": "p" } ] }""";
        const string with = """{ "id": "22222222-2222-4222-8222-222222222222", "name": "t", "commandPattern": "x -T {p}", "parameters": [ { "name": "p", "flag": "-T" } ] }""";

        JsonSchemaValidator.ValidateTemplate(without).IsValid.Should().BeTrue();
        JsonSchemaValidator.ValidateTemplate(with).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AFlagLongerThanTheBound_FailsTheSchema()
    {
        string tooLong = new('-', ValidationConstants.MaxParameterFlagLength + 1);
        string json = "{ \"id\": \"22222222-2222-4222-8222-222222222222\", \"name\": \"t\", \"commandPattern\": \"x {p}\", "
            + "\"parameters\": [ { \"name\": \"p\", \"flag\": \"" + tooLong + "\" } ] }";

        JsonSchemaValidator.ValidateTemplate(json).IsValid.Should().BeFalse();
    }

    private static ActionModel ParseSeed(string json) =>
        JsonSerializer.Deserialize<ActionModel>(json, JsonOptionsHelper.CaseInsensitive)!;

    private static async Task<TemplateParameter> StoredTitleParameter(TwinShellSyncFixture fixture)
    {
        fixture.DbContext.ChangeTracker.Clear();
        ActionModel action = (await fixture.Actions.GetByIdAsync("set-user"))!;
        return action.WindowsCommandTemplate!.Parameters.Single(p => p.Name == "title");
    }
}
