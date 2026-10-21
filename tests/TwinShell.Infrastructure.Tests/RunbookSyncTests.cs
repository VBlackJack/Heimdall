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

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Git sync carries runbooks through its batch files: a runbook's variables and each step's
/// parameter values survive an export to the folder and an import from it.
/// </summary>
public sealed class RunbookSyncTests
{
    [Fact]
    public async Task ExportThenImport_KeepsTheVariablesAndTheStepParameterValues()
    {
        string exportPath = Path.Combine(Path.GetTempPath(), "heimdall_runbook_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportPath);

        try
        {
            await using TwinShellSyncFixture exportFixture = await TwinShellSyncFixture.CreateAsync();
            CommandBatch runbook = new()
            {
                Id = "rb-sync",
                PublicId = Guid.Parse("7a2c8d2f-4b65-4e0f-8b6f-3a4c5d6e7f80"),
                Name = "Restart a unit",
                ExecutionMode = BatchExecutionMode.StopOnError,
                Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "nginx" },
                Commands =
                [
                    new BatchCommandItem
                    {
                        Id = "rb-sync-step",
                        Order = 0,
                        ActionId = "status",
                        ActionTitle = "Service status",
                        Command = "systemctl status 'nginx'",
                        Platform = Platform.Linux,
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "{{unit}}" }
                    }
                ],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsUserCreated = true
            };
            await exportFixture.Batches.AddAsync(runbook);

            SyncExportResult exported = await exportFixture.SyncService.ExportDataToYamlAsync(exportPath);
            exported.Success.Should().BeTrue();

            await using TwinShellSyncFixture importFixture = await TwinShellSyncFixture.CreateAsync();
            SyncImportResult imported = await importFixture.SyncService.ImportDataFromYamlAsync(exportPath);

            imported.Warnings.Should().BeEmpty();
            imported.BatchesCreated.Should().Be(1);
            CommandBatch? reloaded = await importFixture.Batches.GetByPublicIdAsync(runbook.PublicId);
            reloaded.Should().NotBeNull();
            reloaded!.Variables.Should().Equal(new Dictionary<string, string> { ["unit"] = "nginx" });
            reloaded.Commands.Should().ContainSingle()
                .Which.Parameters.Should().Equal(new Dictionary<string, string> { ["unit"] = "{{unit}}" });
        }
        finally
        {
            try
            {
                Directory.Delete(exportPath, recursive: true);
            }
            catch (IOException)
            {
                // A handle still open on a CI agent leaves a temp folder behind; nothing to fix.
            }
        }
    }
}
