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

using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Repositories;
using TwinShell.Persistence.Schema;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Pins the two facts a history row now carries about where its command went: the host of the
/// originating session, and whether the command was sent or only copied.
/// </summary>
/// <remarks>
/// Driven through the real service, repository, mapper and SQLite file, because each of those
/// four is a place the value can be dropped without anything else noticing: the column exists,
/// the model has the property, and the row comes back with a null.
/// </remarks>
public sealed class CommandHistoryTargetHostTests
{
    private const string ActionId = "act-history-host";

    [Fact]
    public async Task AddCommand_StoresAndReturnsTheHostAndTheSentFlag()
    {
        await using TempDatabase database = await TempDatabase.CreateUpgradedAsync();

        await using (TwinShellDbContext writeContext = database.NewContext())
        {
            CommandHistoryService service = new(NewRepository(writeContext));
            await service.AddCommandAsync(
                ActionId, "uptime", new Dictionary<string, string>(), Platform.Linux,
                "Uptime", "System", "srv-01", isExecuted: true);
            await service.AddCommandAsync(
                ActionId, "hostname", new Dictionary<string, string>(), Platform.Linux,
                "Hostname", "System", null, isExecuted: false);
        }

        // A fresh context, so the values come back from the file and not from the tracker.
        await using TwinShellDbContext readContext = database.NewContext();
        List<CommandHistory> rows = (await NewRepository(readContext).GetRecentAsync(10)).ToList();

        CommandHistory sent = rows.Single(row => row.ActionTitle == "Uptime");
        sent.TargetHost.Should().Be("srv-01");
        sent.IsExecuted.Should().BeTrue();

        CommandHistory copied = rows.Single(row => row.ActionTitle == "Hostname");
        copied.TargetHost.Should().BeNull();
        copied.IsExecuted.Should().BeFalse();
    }

    // The previous terminal version is 4. A user who updated has a history table written by
    // EnsureCreated without the column, and only step 5 can add it.
    [Fact]
    public async Task UpgradeFromVersionFour_AddsTheTargetHostColumn()
    {
        await using TempDatabase database = await TempDatabase.CreateAtVersionFourWithoutTargetHostAsync();

        (await database.HistoryHasTargetHostColumnAsync()).Should().BeFalse(
            "the fixture must predate the column, or this test proves nothing");

        await using (TwinShellDbContext context = database.NewContext())
        {
            await SchemaUpgrader.UpgradeAsync(context, TwinShellSchema.Steps);
        }

        (await database.HistoryHasTargetHostColumnAsync()).Should().BeTrue();
        (await database.ReadUserVersionAsync()).Should().Be(5);
    }

    private static CommandHistoryRepository NewRepository(TwinShellDbContext context)
        => new(context, NullLogger<CommandHistoryRepository>.Instance, new ReversibleProtector());

    private sealed class ReversibleProtector : ISecretProtector
    {
        private const string Wrapper = "reversed:";

        public string Protect(string plainText) =>
            Wrapper + new string(plainText.Reverse().ToArray());

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith(Wrapper, StringComparison.Ordinal)
                ? new string(protectedValue[Wrapper.Length..].Reverse().ToArray())
                : null;
    }

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly string _connectionString;

        private TempDatabase()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_twinshell_history_host_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_rootPath, "twinshell.db"),
                Pooling = false
            }.ToString();
        }

        internal static async Task<TempDatabase> CreateUpgradedAsync()
        {
            TempDatabase database = new();
            await using TwinShellDbContext context = database.NewContext();
            await context.Database.EnsureCreatedAsync();
            await SchemaUpgrader.UpgradeAsync(context, TwinShellSchema.Steps);
            await database.SeedActionAsync(context);
            return database;
        }

        internal static async Task<TempDatabase> CreateAtVersionFourWithoutTargetHostAsync()
        {
            TempDatabase database = new();
            await using TwinShellDbContext context = database.NewContext();
            await context.Database.EnsureCreatedAsync();
            await database.ExecuteAsync("ALTER TABLE CommandHistories DROP COLUMN TargetHost");
            await database.ExecuteAsync("PRAGMA user_version = 4");
            return database;
        }

        internal TwinShellDbContext NewContext()
            => new(new DbContextOptionsBuilder<TwinShellDbContext>().UseSqlite(_connectionString).Options);

        internal async Task<bool> HistoryHasTargetHostColumnAsync()
        {
            object? count = await ScalarAsync(
                "SELECT COUNT(*) FROM pragma_table_info('CommandHistories') WHERE name = 'TargetHost'");
            return Convert.ToInt32(count, CultureInfo.InvariantCulture) > 0;
        }

        internal async Task<int> ReadUserVersionAsync()
            => Convert.ToInt32(await ScalarAsync("PRAGMA user_version"), CultureInfo.InvariantCulture);

        private async Task SeedActionAsync(TwinShellDbContext context)
        {
            context.Actions.Add(new ActionEntity
            {
                Id = ActionId,
                Title = "History host action",
                Description = "Owner of the history rows",
                Category = "System",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        private async Task ExecuteAsync(string sql)
        {
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task<object?> ScalarAsync(string sql)
        {
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
