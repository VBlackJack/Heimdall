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
using TwinShell.Infrastructure.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Every risk level survives a Git sync, and a file written against the old schema's level
/// names still imports with the level it meant.
/// </summary>
public sealed class SyncRiskLevelTests
{
    [Fact]
    public async Task ADangerousAction_RoundTripsThroughASyncFolder()
    {
        await RoundTripAsync(CriticalityLevel.Dangerous, rewrite: null, expected: CriticalityLevel.Dangerous);
    }

    [Fact]
    public async Task TheLegacyDangerSpelling_ImportsAsDangerous()
    {
        await RoundTripAsync(
            CriticalityLevel.Dangerous,
            rewrite: json => json.Replace("\"level\": \"Dangerous\"", "\"level\": \"Danger\"", StringComparison.Ordinal),
            expected: CriticalityLevel.Dangerous);
    }

    [Fact]
    public void AnUnknownLevel_StillFailsValidation()
    {
        const string json = """{ "id": "33333333-3333-4333-8333-333333333333", "title": "t", "category": "c", "level": "Critical" }""";

        JsonSchemaValidator.ValidateAction(json).IsValid.Should().BeFalse();
    }

    private static async Task RoundTripAsync(CriticalityLevel level, Func<string, string>? rewrite, CriticalityLevel expected)
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimdall_sync_level_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using TwinShellSyncFixture source = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = new()
            {
                Id = "risky",
                Title = "Risky action",
                Category = "security",
                Platform = Platform.Windows,
                Level = level,
                IsUserCreated = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            await source.Actions.AddAsync(action);
            (await source.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            if (rewrite is not null)
            {
                string file = Directory.EnumerateFiles(Path.Combine(folder, "actions"), "*.json", SearchOption.AllDirectories).Single();
                string original = await File.ReadAllTextAsync(file);
                string rewritten = rewrite(original);
                rewritten.Should().NotBe(original, "the rewrite has to reach the file to test anything");
                await File.WriteAllTextAsync(file, rewritten);
            }

            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            SyncImportResult imported = await target.SyncService.ImportDataFromYamlAsync(folder);

            imported.Warnings.Should().BeEmpty();
            imported.ActionsCreated.Should().Be(1);
            (await target.Actions.GetByPublicIdAsync(action.PublicId))!.Level.Should().Be(expected);
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
}
