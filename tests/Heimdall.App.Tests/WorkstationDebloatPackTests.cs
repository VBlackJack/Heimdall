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
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using TwinShell.Core.Models;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The workstation debloat actions live in an optional pack, not in the default catalogue.
/// </summary>
/// <remarks>
/// They remove store apps, Edge and OneDrive and tune a personal machine: nothing an operator
/// connecting to servers expects to find among the shipped actions.
/// </remarks>
public sealed class WorkstationDebloatPackTests
{
    private const int PackActionCount = 72;
    private const string PackFileName = "windows-workstation-debloat.json";

    [Fact]
    public void NoSeedFile_IsInTheDebloatCategory()
    {
        var offenders = Directory.EnumerateFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .Where(path =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.GetProperty("category").GetString() == CommandCategories.WindowsDebloat;
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Seed files in the debloat category: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ThePack_ParsesThroughTheImportDoor_AndHoldsEveryDebloatAction()
    {
        var json = File.ReadAllText(PackPath());

        var actions = CommandLibraryTransferService.ParseImportJson(json);

        Assert.NotNull(actions);
        Assert.Equal(PackActionCount, actions!.Count);
        Assert.All(actions, action => Assert.Equal(CommandCategories.WindowsDebloat, action.Category));

        // The base text is English and the import door carries both translation blocks, as it
        // does for an export of the shipped catalogue.
        Assert.All(actions, action => Assert.Equal(["fr", "es"], action.Translations.Select(t => t.CultureCode)));
        Assert.All(actions, action => Assert.All(
            action.Translations,
            translation =>
            {
                Assert.False(string.IsNullOrWhiteSpace(translation.Title), $"{action.Id}/{translation.CultureCode} title");
                Assert.False(string.IsNullOrWhiteSpace(translation.Description), $"{action.Id}/{translation.CultureCode} description");
                Assert.False(string.IsNullOrWhiteSpace(translation.Notes), $"{action.Id}/{translation.CultureCode} notes");
            }));
        Assert.All(actions, action => Assert.NotEqual(action.Title, action.Translations[0].Title));

        Assert.Equal(actions.Count, actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(PackActionCount, document.RootElement.GetProperty("totalActions").GetInt32());
    }

    /// <summary>
    /// Nine of the pack's scripts were longer than the generator's old 1024-character bound
    /// and opened on a length error. Every template has to generate within the bound now,
    /// with its shipped defaults and a plain value for a required field left empty.
    /// </summary>
    [Fact]
    public void EveryPackTemplate_GeneratesWithinTheCommandBound()
    {
        var actions = CommandLibraryTransferService.ParseImportJson(File.ReadAllText(PackPath()))!;
        var generator = new CommandGeneratorService(new FakeTwinShellLocalizationService());
        var failures = new List<string>();

        foreach (var template in actions.SelectMany(action => new[] { action.WindowsCommandTemplate, action.LinuxCommandTemplate }))
        {
            if (template is null)
            {
                continue;
            }

            var values = template.Parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => !string.IsNullOrWhiteSpace(parameter.DefaultValue) || !parameter.Required
                    ? parameter.DefaultValue ?? string.Empty
                    : parameter.IsChoice ? parameter.AllowedValues![0] : "1",
                StringComparer.Ordinal);

            try
            {
                generator.GenerateCommand(template, values);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{template.Id} ({template.CommandPattern.Length} characters): {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task ADebloatFileLeftByAnOlderInstall_IsNotReadAsASeed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "heimdall_seed_leftover_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var leftover = Path.Combine(folder, "win-debloat-001.json");
            var kept = Path.Combine(folder, "service-restart.json");
            await File.WriteAllTextAsync(leftover, """{ "id": "win-debloat-001", "title": "Remove apps", "category": "🧹 Windows Debloat" }""");
            await File.WriteAllTextAsync(kept, """{ "id": "service-restart", "title": "Restart a service", "category": "⚙️ Services & Automation" }""");

            var actions = await TwinShellBootstrapper.ReadSeedActionsAsync([leftover, kept]);

            var action = Assert.Single(actions);
            Assert.Equal("service-restart", action.Id);
            Assert.Equal(CommandCategories.ServicesAutomation, action.Category);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string PackPath()
    {
        var seedActions = new DirectoryInfo(CommandGeneratorServiceTests.FindSeedActionsDirectory());
        var path = Path.Combine(seedActions.Parent!.Parent!.FullName, "packs", PackFileName);
        Assert.True(File.Exists(path), $"pack not found: {path}");
        return path;
    }
}
