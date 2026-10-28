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
/// What the command library sync writes to a shared folder, what it deletes from it, and what
/// an import keeps of the local copy.
/// </summary>
public sealed class CommandLibrarySyncDataSafetyTests
{
    private static readonly DateTime Earlier = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // P1-03: the shipped catalogue never travels. Its public ids are drawn per install, so each
    // teammate's copy landed as a second catalogue on everybody else's.
    [Fact]
    public async Task Export_WritesTheUserActionAndItsTemplate_AndNoShippedOne()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            ActionModel shipped = CreateAction("seed-shipped", "Shipped action", isUserCreated: false);
            ActionModel mine = CreateAction("act-mine", "My action", isUserCreated: true);
            await fixture.Actions.AddAsync(shipped);
            await fixture.Actions.AddAsync(mine);

            SyncExportResult result = await fixture.SyncService.ExportDataToYamlAsync(folder);

            result.Success.Should().BeTrue();
            ReadIds(Path.Combine(folder, "actions"), recursive: true).Should().Equal(mine.PublicId);
            ReadIds(Path.Combine(folder, "templates"), recursive: false)
                .Should().Equal(mine.LinuxCommandTemplate!.PublicId);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_IgnoresTheShippedActionAndTemplateAnOlderBuildWrote()
    {
        string folder = CreateTempDirectory();
        try
        {
            Guid shippedTemplateId = Guid.NewGuid();
            WriteShippedPair(folder, Guid.NewGuid(), shippedTemplateId);
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.ActionsCreated.Should().Be(0);
            result.TemplatesCreated.Should().Be(0);
            (await fixture.Actions.GetAllAsync()).Should().BeEmpty();
            (await fixture.Templates.GetByPublicIdAsync(shippedTemplateId)).Should().BeNull();
            result.Warnings.Should().Contain(w => w.Contains("shipped action", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    // The explicit exception to "never delete a file this export did not write": an action
    // file declaring isUserCreated false is a shipped entry an older build wrote. A template
    // file declares nothing about itself, so it stays and is reported.
    [Fact]
    public async Task Export_DeletesTheShippedActionFileAnOlderBuildWrote_AndKeepsItsTemplateFile()
    {
        string folder = CreateTempDirectory();
        try
        {
            (string actionFile, string templateFile) = WriteShippedPair(folder, Guid.NewGuid(), Guid.NewGuid());
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();

            SyncExportResult result = await fixture.SyncService.ExportDataToYamlAsync(folder);

            result.Success.Should().BeTrue();
            File.Exists(actionFile).Should().BeFalse("every install ships its own copy of a shipped action");
            File.Exists(templateFile).Should().BeTrue("the export never deletes a file it did not write but this one exception");
            result.Warnings.Should().Contain(w => w.Contains(Path.GetFileName(templateFile), StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    // P1-02: the exact scenario of the audit. A newer teammate's file carries a field this
    // build's schema refuses, so the import skips it; the export that follows used to delete
    // every managed file it had not written, and pushed the loss to everybody.
    [Fact]
    public async Task Export_KeepsAFileTheSchemaRejected_AndReportsIt()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            ActionModel mine = CreateAction("act-mine", "My action", isUserCreated: true);
            await fixture.Actions.AddAsync(mine);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            string newerFile = Path.Combine(folder, "actions", "Development", "Newer-" + Guid.NewGuid().ToString("N") + ".json");
            JsonObject newer = new()
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["title"] = "Written by a newer build",
                ["category"] = "Development",
                ["isUserCreated"] = true,
                ["futureField"] = "this build does not know it"
            };
            await File.WriteAllTextAsync(newerFile, newer.ToJsonString());

            SyncImportResult imported = await fixture.SyncService.ImportDataFromYamlAsync(folder);
            imported.Success.Should().BeTrue();
            imported.Warnings.Should().Contain(w => w.Contains("Schema validation failed", StringComparison.Ordinal));

            SyncExportResult exported = await fixture.SyncService.ExportDataToYamlAsync(folder);

            exported.Success.Should().BeTrue();
            File.Exists(newerFile).Should().BeTrue("the import skipped it, so this install never held its entry");
            exported.Warnings.Should().Contain(w => w.Contains(Path.GetFileName(newerFile), StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Export_KeepsAFileWhoseIdCannotBeRead()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            string batches = Path.Combine(folder, "batches");
            Directory.CreateDirectory(batches);
            string unreadable = Path.Combine(batches, "half-written.json");
            await File.WriteAllTextAsync(unreadable, "{ \"id\": ");

            SyncExportResult result = await fixture.SyncService.ExportDataToYamlAsync(folder);

            result.Success.Should().BeTrue();
            File.Exists(unreadable).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    // P1-02: a delete reaches the folder, and the other installs, only through a tombstone.
    [Fact]
    public async Task Export_AfterALocalDelete_RemovesTheFileAndRecordsTheTombstone()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            ActionModel deleted = CreateAction("act-deleted", "Deleted later", isUserCreated: true);
            await fixture.Actions.AddAsync(deleted);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            string file = Directory.GetFiles(Path.Combine(folder, "actions"), "*.json", SearchOption.AllDirectories).Single();

            await fixture.Actions.DeleteAsync(deleted.Id);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            File.Exists(file).Should().BeFalse();
            ReadTombstoneIds(folder).Should().Contain([deleted.PublicId, deleted.LinuxCommandTemplate!.PublicId]);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_OfATombstoneNewerThanTheLocalEntry_DeletesIt()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture author = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = CreateAction("act-shared", "Shared then deleted", isUserCreated: true);
            await author.Actions.AddAsync(action);
            (await author.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            await using TwinShellSyncFixture teammate = await TwinShellSyncFixture.CreateAsync();
            (await teammate.SyncService.ImportDataFromYamlAsync(folder)).ActionsCreated.Should().Be(1);

            await author.Actions.DeleteAsync(action.Id);
            (await author.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            SyncImportResult result = await teammate.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.EntriesDeleted.Should().BeGreaterThan(0);
            (await teammate.Actions.GetByPublicIdAsync(action.PublicId)).Should().BeNull();
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task ALocalEditAfterTheDelete_Wins_AndTheNextExportDropsTheTombstone()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture author = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = CreateAction("act-contested", "Deleted there, edited here", isUserCreated: true);
            await author.Actions.AddAsync(action);
            (await author.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            await using TwinShellSyncFixture teammate = await TwinShellSyncFixture.CreateAsync();
            (await teammate.SyncService.ImportDataFromYamlAsync(folder)).Success.Should().BeTrue();
            await author.Actions.DeleteAsync(action.Id);
            (await author.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            ActionModel edited = (await teammate.Actions.GetByPublicIdAsync(action.PublicId))!;
            edited.Title = "Edited after the delete";
            edited.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
            await teammate.Actions.UpdateAsync(edited);
            (await teammate.SyncService.ImportDataFromYamlAsync(folder)).Success.Should().BeTrue();
            (await teammate.Actions.GetByPublicIdAsync(action.PublicId)).Should().NotBeNull();

            (await teammate.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            ReadTombstoneIds(folder).Should().NotContain(action.PublicId);
            ReadIds(Path.Combine(folder, "actions"), recursive: true).Should().Contain(action.PublicId);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    // D-01, categories: a pull overwrote the local category unconditionally.
    [Fact]
    public async Task Import_OfAnOlderCategory_KeepsTheLocalEdit()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            CustomCategory category = new()
            {
                Id = "cat-edited",
                Name = "Before",
                IconKey = "folder",
                ColorHex = "#336699",
                CreatedAt = Earlier,
                ModifiedAt = Earlier
            };
            await fixture.Categories.CreateAsync(category);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            CustomCategory stored = (await fixture.Categories.GetByIdAsync(category.Id))!;
            stored.Name = "Edited here";
            stored.ModifiedAt = Earlier.AddDays(1);
            await fixture.Categories.UpdateAsync(stored);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Conflicts.Should().ContainSingle(c => c.EntityType == "Category");
            (await fixture.Categories.GetByIdAsync(category.Id))!.Name.Should().Be("Edited here");
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    // D-01: a pull used to overwrite the template of an action whose local copy it kept, so a
    // local edit of the command was reverted while the action said "kept local".
    [Fact]
    public async Task Import_WhenTheLocalActionIsNewer_KeepsItsTemplateLocalToo()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = CreateAction("act-edited", "Edited here", isUserCreated: true);
            await fixture.Actions.AddAsync(action);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            RewriteTemplatePattern(folder, "echo remote");

            CommandTemplate local = (await fixture.Templates.GetByPublicIdAsync(action.LinuxCommandTemplate!.PublicId))!;
            local.CommandPattern = "echo local edit";
            await fixture.Templates.UpdateAsync(local);
            ActionModel stored = (await fixture.Actions.GetByIdAsync(action.Id))!;
            stored.UpdatedAt = Earlier.AddDays(1);
            await fixture.Actions.UpdateAsync(stored);

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.Conflicts.Should().ContainSingle(c => c.EntityId == action.PublicId);
            result.TemplatesUpdated.Should().Be(0);
            (await fixture.Templates.GetByPublicIdAsync(local.PublicId))!.CommandPattern.Should().Be("echo local edit");
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task Import_WhenTheRemoteActionIsNewer_TakesItsTemplate()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = CreateAction("act-remote", "Edited there", isUserCreated: true);
            await fixture.Actions.AddAsync(action);
            (await fixture.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();
            RewriteTemplatePattern(folder, "echo remote");

            SyncImportResult result = await fixture.SyncService.ImportDataFromYamlAsync(folder);

            result.Success.Should().BeTrue();
            result.TemplatesUpdated.Should().Be(1);
            (await fixture.Templates.GetByPublicIdAsync(action.LinuxCommandTemplate!.PublicId))!
                .CommandPattern.Should().Be("echo remote");
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    // D-03: a step used to carry only the local id of its action, which no other install holds.
    [Fact]
    public async Task ARunbookStep_TravelsByPublicId_AndResolvesToTheLocalIdOfTheOtherInstall()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture source = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = CreateAction("act-source-local", "Restart unit", isUserCreated: true);
            await source.Actions.AddAsync(action);
            await source.Batches.AddAsync(CreateRunbook(Guid.NewGuid(), "act-source-local"));
            (await source.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            string batchFile = Directory.GetFiles(Path.Combine(folder, "batches"), "*.json").Single();
            JsonNode step = JsonNode.Parse(await File.ReadAllTextAsync(batchFile))!["commands"]![0]!;
            step["actionId"]!.GetValue<string>().Should().Be("act-source-local");
            step["actionPublicId"]!.GetValue<Guid>().Should().Be(action.PublicId);

            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            (await target.SyncService.ImportDataFromYamlAsync(folder)).Success.Should().BeTrue();

            ActionModel imported = (await target.Actions.GetByPublicIdAsync(action.PublicId))!;
            imported.Id.Should().NotBe("act-source-local");
            CommandBatch runbook = (await target.Batches.GetAllAsync()).Single();
            runbook.Commands.Single().ActionId.Should().Be(imported.Id);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task AnOlderRunbookFile_ResolvesAShippedActionByItsLocalId_AndKeepsAnUnknownReference()
    {
        string folder = CreateTempDirectory();
        try
        {
            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            await target.Actions.AddAsync(CreateAction("seed-status", "Service status", isUserCreated: false));
            string batches = Path.Combine(folder, "batches");
            Directory.CreateDirectory(batches);
            JsonObject runbook = new()
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["name"] = "Older runbook",
                ["isUserCreated"] = true,
                ["commands"] = new JsonArray(
                    new JsonObject { ["order"] = 0, ["actionId"] = "seed-status", ["command"] = "systemctl status" },
                    new JsonObject { ["order"] = 1, ["actionId"] = "other-install-local-id", ["command"] = "uptime" })
            };
            await File.WriteAllTextAsync(Path.Combine(batches, "older.json"), runbook.ToJsonString());

            (await target.SyncService.ImportDataFromYamlAsync(folder)).Success.Should().BeTrue();

            CommandBatch stored = (await target.Batches.GetAllAsync()).Single();
            stored.Commands.OrderBy(c => c.Order).Select(c => c.ActionId)
                .Should().Equal("seed-status", "other-install-local-id");
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    private static (string ActionFile, string TemplateFile) WriteShippedPair(string folder, Guid actionId, Guid templateId)
    {
        string actions = Path.Combine(folder, "actions", "Development");
        string templates = Path.Combine(folder, "templates");
        Directory.CreateDirectory(actions);
        Directory.CreateDirectory(templates);

        string actionFile = Path.Combine(actions, "Shipped-" + actionId.ToString("N") + ".json");
        File.WriteAllText(actionFile, new JsonObject
        {
            ["id"] = actionId.ToString(),
            ["title"] = "Shipped action",
            ["category"] = "Development",
            ["platform"] = "Linux",
            ["linuxTemplateId"] = templateId.ToString(),
            ["isUserCreated"] = false
        }.ToJsonString());

        string templateFile = Path.Combine(templates, "Shipped-" + templateId.ToString("N") + ".json");
        File.WriteAllText(templateFile, new JsonObject
        {
            ["id"] = templateId.ToString(),
            ["name"] = "Shipped template",
            ["platform"] = "Linux",
            ["commandPattern"] = "echo shipped"
        }.ToJsonString());

        return (actionFile, templateFile);
    }

    // The root fix of P1-03: a shipped action is the same entry on every install.
    [Fact]
    public void ASeedPublicId_IsTheRfc4122Version5OfTheSeedId()
    {
        // The RFC 4122 test vector: uuid5(NAMESPACE_DNS, "www.example.com").
        SeedPublicIds.CreateVersion5(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com")
            .Should().Be(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
        SeedPublicIds.ForAction("ad-get-user").Should().Be(SeedPublicIds.ForAction("ad-get-user"));
        SeedPublicIds.ForAction("x").Should().NotBe(SeedPublicIds.ForTemplate("x"));
    }

    [Fact]
    public async Task TheSchemaStep_GivesAnExistingSystemActionAndItsTemplateTheirSeedPublicIds()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        ActionModel shipped = CreateAction("seed-legacy", "Seeded before the fix", isUserCreated: false);
        ActionModel mine = CreateAction("act-mine", "Mine", isUserCreated: true);
        await fixture.Actions.AddRangeAsync([shipped, mine]);
        fixture.DbContext.ChangeTracker.Clear();

        await TwinShell.Persistence.Schema.SchemaUpgrader.UpgradeAsync(fixture.DbContext, TwinShell.Persistence.Schema.TwinShellSchema.Steps);

        (await fixture.Actions.GetByIdAsync("seed-legacy"))!.PublicId.Should().Be(SeedPublicIds.ForAction("seed-legacy"));
        (await fixture.Templates.GetByIdAsync("seed-legacy-lin"))!.PublicId.Should().Be(SeedPublicIds.ForTemplate("seed-legacy-lin"));
        (await fixture.Actions.GetByIdAsync("act-mine"))!.PublicId.Should().Be(mine.PublicId, "a user action keeps the id the folder knows");
    }

    private static List<Guid> ReadTombstoneIds(string folder)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "_deleted.json")));
        return document.RootElement.GetProperty("tombstones").EnumerateArray()
            .Select(tombstone => tombstone.GetProperty("id").GetGuid())
            .ToList();
    }

    private static void RewriteTemplatePattern(string folder, string pattern)
    {
        string templateFile = Directory.GetFiles(Path.Combine(folder, "templates"), "*.json").Single();
        JsonNode template = JsonNode.Parse(File.ReadAllText(templateFile))!;
        template["commandPattern"] = pattern;
        File.WriteAllText(templateFile, template.ToJsonString());
    }

    private static CommandBatch CreateRunbook(Guid publicId, string actionId) => new()
    {
        Id = "rb-" + publicId.ToString("N"),
        PublicId = publicId,
        Name = "Restart",
        Commands =
        [
            new BatchCommandItem
            {
                Id = "rb-step",
                Order = 0,
                ActionId = actionId,
                ActionTitle = "Restart unit",
                Command = "systemctl restart nginx",
                Platform = Platform.Linux
            }
        ],
        CreatedAt = Earlier,
        UpdatedAt = Earlier,
        IsUserCreated = true
    };

    private static ActionModel CreateAction(string id, string title, bool isUserCreated)
    {
        CommandTemplate template = new()
        {
            Id = id + "-lin",
            Name = title + " template",
            Platform = Platform.Linux,
            CommandPattern = "echo " + id
        };

        return new ActionModel
        {
            Id = id,
            Title = title,
            Description = title,
            Category = CommandCategories.Development,
            Platform = Platform.Linux,
            Level = CriticalityLevel.Info,
            LinuxCommandTemplateId = template.Id,
            LinuxCommandTemplate = template,
            CreatedAt = Earlier,
            UpdatedAt = Earlier,
            IsUserCreated = isUserCreated
        };
    }

    private static List<Guid> ReadIds(string folder, bool recursive)
        => Directory.GetFiles(folder, "*.json", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(file =>
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                return document.RootElement.GetProperty("id").GetGuid();
            })
            .ToList();

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "heimdall_cmdlib_sync_safety_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
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
