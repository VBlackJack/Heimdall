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
        Assert.All(actions, action => Assert.Empty(action.Translations));
        Assert.Equal(actions.Count, actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(PackActionCount, document.RootElement.GetProperty("totalActions").GetInt32());
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
