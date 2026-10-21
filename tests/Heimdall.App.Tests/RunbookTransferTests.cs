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
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the runbooks in the transfer envelope: an export carries them whole, an import merges
/// them by public id, and the export choice "My actions" includes the operator's runbooks.
/// </summary>
public sealed class RunbookTransferTests
{
    [Fact]
    public async Task TheEnvelope_RoundTripsARunbook_WithItsVariablesAndStepValues()
    {
        var runbook = Runbook();
        var target = new FakeBatchService();

        var result = await RoundTripAsync(runbook, target);

        Assert.Equal(CommandLibraryImportOutcome.Success, result.Outcome);
        Assert.Equal(1, result.RunbooksImported);
        var stored = Assert.Single(target.Stored);
        Assert.Equal(runbook.PublicId, stored.PublicId);
        Assert.Equal("Restart a unit", stored.Name);
        Assert.Equal(BatchExecutionMode.ContinueOnError, stored.ExecutionMode);
        Assert.Equal(runbook.Variables, stored.Variables);
        Assert.Equal(["status", null], stored.Commands.Select(step => step.ActionId));
        Assert.Equal("{{unit}}", stored.Commands[0].Parameters["unit"]);
        Assert.Equal("uptime", stored.Commands[1].Command);
        Assert.Equal(Platform.Linux, stored.Commands[1].Platform);
    }

    [Fact]
    public async Task ImportingTheSameRunbookTwice_UpdatesItInsteadOfAddingASecond()
    {
        var runbook = Runbook();
        var target = new FakeBatchService();
        await RoundTripAsync(runbook, target);

        runbook.Variables["unit"] = "sshd";
        var second = await RoundTripAsync(runbook, target);

        Assert.Equal(0, second.RunbooksImported);
        Assert.Equal(1, second.RunbooksUpdated);
        Assert.Equal("sshd", Assert.Single(target.Stored).Variables["unit"]);
    }

    // A file written before runbooks existed has no such array, and an export without runbooks
    // must still read the same, so an older Heimdall keeps importing it.
    [Fact]
    public async Task AnExportWithoutRunbooks_WritesNoRunbooksArray()
    {
        var path = Path.GetTempFileName();
        try
        {
            await new CommandLibraryTransferService().ExportAsync([], [], path);

            Assert.DoesNotContain("\"runbooks\"", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportMyActions_IncludesTheOperatorsRunbooks_AndCurrentFilterDoesNot()
    {
        var batches = new FakeBatchService();
        batches.Seed(Runbook());
        using var library = await RunbookTestHelpers.CreateLibraryAsync(
            batches, new CollectingHistoryService(), new SilentDialogService(), "srv-01", "SSH");

        var mine = await library.SelectExportRunbooksAsync(CommandLibraryExportScope.Mine);
        var filtered = await library.SelectExportRunbooksAsync(CommandLibraryExportScope.CurrentFilter);

        Assert.Equal("Restart a unit", Assert.Single(mine).Name);
        Assert.Empty(filtered);
    }

    private static async Task<CommandLibraryImportResult> RoundTripAsync(CommandBatch runbook, FakeBatchService target)
    {
        var service = new CommandLibraryTransferService();
        var path = Path.GetTempFileName();
        try
        {
            await service.ExportAsync([], [runbook], path);
            return await service.ImportAsync(new FakeActionService([]), target, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CommandBatch Runbook() => new()
    {
        Id = "rb-local",
        PublicId = Guid.Parse("6f1b7c1e-3a54-4d9e-9a5e-2f3b4c5d6e7f"),
        Name = "Restart a unit",
        ExecutionMode = BatchExecutionMode.ContinueOnError,
        IsUserCreated = true,
        Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = "", ["unit"] = "nginx" },
        Commands =
        [
            new BatchCommandItem
            {
                Order = 0,
                ActionId = "status",
                ActionTitle = "Service status",
                Command = "systemctl status 'nginx'",
                Platform = Platform.Linux,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "{{unit}}" }
            },
            RunbookTestHelpers.LiteralStep("uptime")
        ]
    };
}
