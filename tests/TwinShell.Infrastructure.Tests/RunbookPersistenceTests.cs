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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using TwinShell.Persistence;
using TwinShell.Persistence.Repositories;
using TwinShell.Persistence.Schema;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Pins what a runbook keeps across a save and a reload: its variables, each step's parameter
/// values, the order the operator gave the steps, and the steps the operator removed.
/// </summary>
/// <remarks>
/// Driven through the real service, repository, mapper and SQLite file, each read from a fresh
/// context, so a value comes back from the file and not from the change tracker.
/// </remarks>
public sealed class RunbookPersistenceTests
{
    [Fact]
    public async Task VariablesAndStepParameters_ComeBackFromTheFile()
    {
        await using TempDatabase database = await TempDatabase.CreateAsync();

        string id;
        await using (TwinShellDbContext context = database.NewContext())
        {
            CommandBatch created = await NewService(context).CreateBatchAsync(Runbook("status", "logs"));
            id = created.Id;
        }

        await using TwinShellDbContext readContext = database.NewContext();
        CommandBatch? reloaded = await NewService(readContext).GetBatchByIdAsync(id);

        reloaded.Should().NotBeNull();
        reloaded!.Variables.Should().Equal(new Dictionary<string, string> { ["host"] = "srv-01", ["unit"] = "nginx" });
        reloaded.Commands[0].Parameters.Should().Equal(new Dictionary<string, string> { ["service"] = "{{unit}}" });
    }

    [Fact]
    public async Task Update_KeepsTheVariablesItWasGiven()
    {
        await using TempDatabase database = await TempDatabase.CreateAsync();

        string id;
        await using (TwinShellDbContext context = database.NewContext())
        {
            id = (await NewService(context).CreateBatchAsync(Runbook("status"))).Id;
        }

        await using (TwinShellDbContext context = database.NewContext())
        {
            BatchService service = NewService(context);
            CommandBatch loaded = (await service.GetBatchByIdAsync(id))!;
            loaded.Variables["unit"] = "sshd";
            await service.UpdateBatchAsync(loaded);
        }

        await using TwinShellDbContext readContext = database.NewContext();
        CommandBatch reloaded = (await NewService(readContext).GetBatchByIdAsync(id))!;
        reloaded.Variables["unit"].Should().Be("sshd");
    }

    [Fact]
    public async Task Reorder_SurvivesASaveAndAReload()
    {
        await using TempDatabase database = await TempDatabase.CreateAsync();

        string id;
        await using (TwinShellDbContext context = database.NewContext())
        {
            id = (await NewService(context).CreateBatchAsync(Runbook("status", "logs", "restart"))).Id;
        }

        await using (TwinShellDbContext context = database.NewContext())
        {
            BatchService service = NewService(context);
            CommandBatch loaded = (await service.GetBatchByIdAsync(id))!;

            // Move "restart" above "logs", the way the editor's Move up does: the list order
            // changes and each step keeps the Order it was loaded with.
            BatchCommandItem restart = loaded.Commands[2];
            loaded.Commands.RemoveAt(2);
            loaded.Commands.Insert(1, restart);
            await service.UpdateBatchAsync(loaded);
        }

        await using TwinShellDbContext readContext = database.NewContext();
        CommandBatch reloaded = (await NewService(readContext).GetBatchByIdAsync(id))!;
        reloaded.Commands.Select(command => command.ActionTitle).Should().Equal("status", "restart", "logs");
    }

    [Fact]
    public async Task RemovedStep_IsGoneAfterASaveAndAReload()
    {
        await using TempDatabase database = await TempDatabase.CreateAsync();

        string id;
        await using (TwinShellDbContext context = database.NewContext())
        {
            id = (await NewService(context).CreateBatchAsync(Runbook("status", "logs", "restart"))).Id;
        }

        await using (TwinShellDbContext context = database.NewContext())
        {
            BatchService service = NewService(context);
            CommandBatch loaded = (await service.GetBatchByIdAsync(id))!;
            loaded.Commands.RemoveAt(1);
            await service.UpdateBatchAsync(loaded);
        }

        await using TwinShellDbContext readContext = database.NewContext();
        CommandBatch reloaded = (await NewService(readContext).GetBatchByIdAsync(id))!;
        reloaded.Commands.Select(command => command.ActionTitle).Should().Equal("status", "restart");
    }

    [Fact]
    public async Task DeletedRunbook_IsGoneWithItsSteps()
    {
        await using TempDatabase database = await TempDatabase.CreateAsync();

        string id;
        await using (TwinShellDbContext context = database.NewContext())
        {
            id = (await NewService(context).CreateBatchAsync(Runbook("status"))).Id;
        }

        await using (TwinShellDbContext context = database.NewContext())
        {
            await NewService(context).DeleteBatchAsync(id);
        }

        await using TwinShellDbContext readContext = database.NewContext();
        (await NewService(readContext).GetAllBatchesAsync()).Should().BeEmpty();
    }

    private static BatchService NewService(TwinShellDbContext context) => new(new BatchRepository(context));

    private static CommandBatch Runbook(params string[] stepTitles) => new()
    {
        Name = "Restart a unit",
        ExecutionMode = BatchExecutionMode.StopOnError,
        Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = "srv-01", ["unit"] = "nginx" },
        Commands = stepTitles
            .Select(title => new BatchCommandItem
            {
                ActionId = "act-" + title,
                ActionTitle = title,
                Command = "systemctl " + title,
                Platform = Platform.Linux,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["service"] = "{{unit}}" }
            })
            .ToList()
    };

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly string _connectionString;

        private TempDatabase()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_twinshell_runbook_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_rootPath, "twinshell.db"),
                Pooling = false
            }.ToString();
        }

        internal static async Task<TempDatabase> CreateAsync()
        {
            TempDatabase database = new();
            await using TwinShellDbContext context = database.NewContext();
            await context.Database.EnsureCreatedAsync();
            await SchemaUpgrader.UpgradeAsync(context, TwinShellSchema.Steps);
            return database;
        }

        internal TwinShellDbContext NewContext()
            => new(new DbContextOptionsBuilder<TwinShellDbContext>().UseSqlite(_connectionString).Options);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // A handle still open on a CI agent leaves a temp folder behind; nothing to fix.
            }

            return ValueTask.CompletedTask;
        }
    }
}
