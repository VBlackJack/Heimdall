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

using FluentAssertions;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Infrastructure.Services;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// A template keeps every parameter type the generator understands, and the fixed values and
/// quoting of its parameters, through a Git sync export and import.
/// </summary>
public sealed class SyncParameterTypeTests
{
    [Fact]
    public async Task ATemplateWithChoiceNumberAndDriveLetter_RoundTripsThroughASyncFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimdall_sync_types_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using TwinShellSyncFixture source = await TwinShellSyncFixture.CreateAsync();
            CommandTemplate template = new()
            {
                Id = "tpl-types",
                Name = "Typed template",
                Platform = Platform.Windows,
                CommandPattern = "Do-It -Mode {mode} -Count {count} -Drive {drive}:",
                Parameters =
                [
                    new TemplateParameter
                    {
                        Name = "mode",
                        Label = "Mode",
                        Type = TemplateParameter.ChoiceTypeName,
                        AllowedValues = ["Fast", "Full"],
                        DefaultValue = "Fast",
                        Required = true,
                        Quoting = QuotingMode.InlineInQuotes
                    },
                    new TemplateParameter { Name = "count", Label = "Count", Type = "number", DefaultValue = "3" },
                    new TemplateParameter { Name = "drive", Label = "Drive", Type = "driveletter", DefaultValue = "C" }
                ]
            };
            // A template travels with the user action that uses it.
            await source.Actions.AddAsync(new TwinShell.Core.Models.Action
            {
                Id = "act-types",
                Title = "Typed action",
                Category = CommandCategories.Development,
                Platform = Platform.Windows,
                Level = CriticalityLevel.Info,
                WindowsCommandTemplateId = template.Id,
                WindowsCommandTemplate = template,
                IsUserCreated = true
            });
            (await source.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            SyncImportResult imported = await target.SyncService.ImportDataFromYamlAsync(folder);

            imported.Warnings.Should().BeEmpty();
            imported.TemplatesCreated.Should().Be(1);
            CommandTemplate stored = (await target.Templates.GetByPublicIdAsync(template.PublicId))!;
            TemplateParameter mode = stored.Parameters.Single(p => p.Name == "mode");
            mode.IsChoice.Should().BeTrue("a choice must not come back as free text");
            mode.AllowedValues.Should().Equal("Fast", "Full");
            mode.Quoting.Should().Be(QuotingMode.InlineInQuotes);
            stored.Parameters.Single(p => p.Name == "count").Type.Should().Be("number");
            stored.Parameters.Single(p => p.Name == "drive").Type.Should().Be("driveletter");
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

    [Theory]
    [InlineData("int")]
    [InlineData("integer")]
    [InlineData("bool")]
    [InlineData("boolean")]
    [InlineData("choice")]
    [InlineData("number")]
    [InlineData("driveletter")]
    public void EveryTypeTheGeneratorUnderstands_PassesTheTemplateSchema(string type)
    {
        string json = "{ \"id\": \"22222222-2222-4222-8222-222222222222\", \"name\": \"t\", \"commandPattern\": \"x {p}\", "
            + "\"parameters\": [ { \"name\": \"p\", \"type\": \"" + type + "\" } ] }";

        JsonSchemaValidator.ValidateTemplate(json).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AnUnknownType_StillFailsTheTemplateSchema()
    {
        const string json = """{ "id": "22222222-2222-4222-8222-222222222222", "name": "t", "commandPattern": "x {p}", "parameters": [ { "name": "p", "type": "script" } ] }""";

        JsonSchemaValidator.ValidateTemplate(json).IsValid.Should().BeFalse();
    }
}
