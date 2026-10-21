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
using System.Text.Json.Nodes;
using FluentAssertions;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// An action's translations travel with it: through the seed and export JSON shape, into the
/// database and back, through an update, and through a Git sync folder.
/// </summary>
public sealed class ActionTranslationPersistenceTests
{
    // The contract lot 2b writes the seeds against, verbatim in shape.
    private const string ContractExample = """
        {
          "fr": {
            "title": "Redémarrer un service",
            "description": "Redémarre un service Windows ou Linux.",
            "notes": "Windows : droits administrateur requis. Linux : sudo requis.",
            "parameters": {
              "windows": { "serviceName": { "label": "Nom du service", "description": "Nom du service à redémarrer" } },
              "linux":   { "serviceName": { "label": "Nom du service", "description": "Nom du service à redémarrer" } }
            },
            "examples": {
              "Restart-Service -Name Spooler -Force": "Redémarre le service d'impression",
              "sudo systemctl restart nginx": "Redémarre nginx"
            }
          }
        }
        """;

    [Fact]
    public void TheContractExample_ReadsAndWritesBackToTheSameObject()
    {
        string actionJson = "{\"title\":\"Restart a service\",\"category\":\"services-automation\",\"translations\":"
            + ContractExample + "}";

        ActionModel action = JsonSerializer.Deserialize<ActionModel>(actionJson, JsonOptionsHelper.CaseInsensitive)!;
        string written = JsonSerializer.Serialize(action, JsonOptionsHelper.CamelCaseForExport);

        JsonNode? writtenTranslations = JsonNode.Parse(written)!["translations"];
        JsonNode expected = JsonNode.Parse(ContractExample)!;
        JsonNode.DeepEquals(writtenTranslations, expected).Should().BeTrue(
            "the translations object must survive a read and a write unchanged, got " + writtenTranslations?.ToJsonString());

        ActionTranslation french = action.Translations.Should().ContainSingle().Subject;
        french.CultureCode.Should().Be("fr");
        french.WindowsParameters["serviceName"].Label.Should().Be("Nom du service");
        french.ExampleDescriptions["sudo systemctl restart nginx"].Should().Be("Redémarre nginx");
    }

    [Fact]
    public void AnActionWithoutTranslations_WritesAnEmptyObject_AndReadsNullAsNone()
    {
        string written = JsonSerializer.Serialize(new ActionModel { Title = "t" }, JsonOptionsHelper.CamelCaseForExport);
        JsonNode.Parse(written)!["translations"]!.ToJsonString().Should().Be("{}");

        ActionModel read = JsonSerializer.Deserialize<ActionModel>(
            "{\"title\":\"t\",\"translations\":null}",
            JsonOptionsHelper.CaseInsensitive)!;
        read.Translations.Should().BeEmpty();
    }

    [Fact]
    public async Task TheRepository_StoresTranslations_AndReadsThemBackOnEveryLoad()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        ActionModel action = CreateTranslatedAction();

        await fixture.Actions.AddAsync(action);

        ActionModel loaded = (await fixture.Actions.GetAllAsync()).Single();
        AssertFrench(loaded);
        AssertFrench((await fixture.Actions.GetByIdAsync(action.Id))!);
        AssertFrench((await fixture.Actions.GetByPublicIdAsync(action.PublicId))!);
        AssertFrench((await fixture.Actions.GetAllWithTemplatesAsync()).Single());
    }

    [Fact]
    public async Task AnUpdate_ReplacesTheSetOfTranslations()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        ActionModel action = CreateTranslatedAction();
        action.Translations.Add(new ActionTranslation { CultureCode = "es", Title = "Reiniciar un servicio" });
        await fixture.Actions.AddAsync(action);

        ActionModel stored = (await fixture.Actions.GetByIdAsync(action.Id))!;
        stored.Translations.RemoveAll(translation => translation.CultureCode == "es");
        stored.Translations.Single().Title = "Relancer un service";
        await fixture.Actions.UpdateAsync(stored);

        ActionModel reloaded = (await fixture.Actions.GetByIdAsync(action.Id))!;
        ActionTranslation french = reloaded.Translations.Should().ContainSingle(
            "a culture dropped from the action must leave the database too").Subject;
        french.Title.Should().Be("Relancer un service");
    }

    [Fact]
    public async Task AGitSyncFolder_CarriesTranslationsAcross()
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimdall_translation_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using TwinShellSyncFixture source = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = CreateTranslatedAction();
            action.IsUserCreated = true;
            await source.Actions.AddAsync(action);

            SyncExportResult exported = await source.SyncService.ExportDataToYamlAsync(folder);
            exported.Success.Should().BeTrue();

            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            SyncImportResult imported = await target.SyncService.ImportDataFromYamlAsync(folder);

            imported.Warnings.Should().BeEmpty();
            imported.ActionsCreated.Should().Be(1);
            AssertFrench((await target.Actions.GetByPublicIdAsync(action.PublicId))!);
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

    private static ActionModel CreateTranslatedAction()
    {
        DateTime timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var template = new CommandTemplate
        {
            Id = "tpl-restart-win",
            Name = "Restart-Service",
            Platform = Platform.Windows,
            CommandPattern = "Restart-Service -Name {serviceName} -Force",
            Parameters = [new TemplateParameter { Name = "serviceName", Label = "Service name", Required = true }]
        };

        return new ActionModel
        {
            Id = "act-restart",
            Title = "Restart a service",
            Description = "Restarts a Windows service.",
            Category = "services-automation",
            Platform = Platform.Windows,
            Level = CriticalityLevel.Info,
            WindowsCommandTemplateId = template.Id,
            WindowsCommandTemplate = template,
            WindowsExamples =
            [
                new CommandExample { Command = "Restart-Service -Name Spooler -Force", Description = "Restarts the print spooler", Platform = Platform.Windows }
            ],
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            Translations =
            [
                new ActionTranslation
                {
                    CultureCode = "fr",
                    Title = "Redémarrer un service",
                    Description = "Redémarre un service Windows.",
                    WindowsParameters = new() { ["serviceName"] = new ParameterTranslation("Nom du service", null) },
                    ExampleDescriptions = new() { ["Restart-Service -Name Spooler -Force"] = "Redémarre le spouleur" }
                }
            ]
        };
    }

    private static void AssertFrench(ActionModel action)
    {
        ActionTranslation french = action.Translations.Should().ContainSingle().Subject;
        french.CultureCode.Should().Be("fr");
        french.Title.Should().Be("Redémarrer un service");
        french.WindowsParameters["serviceName"].Should().Be(new ParameterTranslation("Nom du service", null));
        french.ExampleDescriptions["Restart-Service -Name Spooler -Force"].Should().Be("Redémarre le spouleur");
    }
}
