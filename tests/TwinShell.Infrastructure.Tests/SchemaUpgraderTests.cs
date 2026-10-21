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

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TwinShell.Persistence;
using TwinShell.Persistence.Schema;

namespace TwinShell.Infrastructure.Tests;

public sealed class SchemaUpgraderTests
{
    [Fact]
    public async Task UpgradeAsync_FreshDatabase_AppliesPendingSteps()
    {
        await using TempSchemaDatabase database = new TempSchemaDatabase();
        IReadOnlyList<SchemaStep> steps =
        [
            CreateMarkerStep(1, "create-first-marker", "MarkerOne"),
            CreateMarkerStep(2, "create-second-marker", "MarkerTwo")
        ];

        await SchemaUpgrader.UpgradeAsync(database.Context, steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        bool markerOneExists = await TableExistsAsync(database.Context, "MarkerOne");
        bool markerTwoExists = await TableExistsAsync(database.Context, "MarkerTwo");

        userVersion.Should().Be(2);
        markerOneExists.Should().BeTrue();
        markerTwoExists.Should().BeTrue();
    }

    [Fact]
    public async Task UpgradeAsync_WhenAlreadyUpToDate_DoesNotRunStepsAgain()
    {
        await using TempSchemaDatabase database = new TempSchemaDatabase();
        int applyCount = 0;
        IReadOnlyList<SchemaStep> steps =
        [
            new SchemaStep(
                1,
                "count-first-step",
                async (DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
                {
                    applyCount++;
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        "CREATE TABLE CounterMarkerOne (Id INTEGER NOT NULL)",
                        cancellationToken);
                }),
            new SchemaStep(
                2,
                "count-second-step",
                async (DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
                {
                    applyCount++;
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        "CREATE TABLE CounterMarkerTwo (Id INTEGER NOT NULL)",
                        cancellationToken);
                })
        ];

        await SchemaUpgrader.UpgradeAsync(database.Context, steps);
        await SchemaUpgrader.UpgradeAsync(database.Context, steps);

        int userVersion = await ReadUserVersionAsync(database.Context);

        applyCount.Should().Be(2);
        userVersion.Should().Be(2);
    }

    [Fact]
    public async Task UpgradeAsync_WhenStepFails_RollsBackFailingStep()
    {
        await using TempSchemaDatabase database = new TempSchemaDatabase();
        IReadOnlyList<SchemaStep> steps =
        [
            CreateMarkerStep(1, "create-success-marker", "SuccessfulMarker"),
            new SchemaStep(
                2,
                "create-failing-marker",
                async (DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        "CREATE TABLE FailingMarker (Id INTEGER NOT NULL)",
                        cancellationToken);

                    throw new InvalidOperationException("Expected failing schema step.");
                })
        ];

        Func<Task> act = async () => await SchemaUpgrader.UpgradeAsync(database.Context, steps);

        await act.Should().ThrowAsync<InvalidOperationException>();

        int userVersion = await ReadUserVersionAsync(database.Context);
        bool successfulMarkerExists = await TableExistsAsync(database.Context, "SuccessfulMarker");
        bool failingMarkerExists = await TableExistsAsync(database.Context, "FailingMarker");

        userVersion.Should().Be(1);
        successfulMarkerExists.Should().BeTrue();
        failingMarkerExists.Should().BeFalse();
    }

    [Fact]
    public async Task UpgradeAsync_WhenStepsAreOutOfOrder_AppliesAscendingVersions()
    {
        await using TempSchemaDatabase database = new TempSchemaDatabase();
        List<int> observedOrder = [];
        IReadOnlyList<SchemaStep> steps =
        [
            new SchemaStep(
                2,
                "observe-second",
                (DbConnection _, DbTransaction _, CancellationToken _) =>
                {
                    observedOrder.Add(2);
                    return Task.CompletedTask;
                }),
            new SchemaStep(
                1,
                "observe-first",
                (DbConnection _, DbTransaction _, CancellationToken _) =>
                {
                    observedOrder.Add(1);
                    return Task.CompletedTask;
                })
        ];

        await SchemaUpgrader.UpgradeAsync(database.Context, steps);

        observedOrder.Should().Equal(1, 2);
    }

    // Two steps with one version would both claim the same user_version: the second would never
    // run on a database the first had reached. The upgrader refuses the list before touching the
    // database, so neither step runs.
    [Fact]
    public async Task UpgradeAsync_WhenTwoStepsShareAVersion_RefusesAndAppliesNothing()
    {
        await using TempSchemaDatabase database = new TempSchemaDatabase();
        List<string> applied = [];
        IReadOnlyList<SchemaStep> steps =
        [
            new SchemaStep(
                1,
                "first",
                (DbConnection _, DbTransaction _, CancellationToken _) =>
                {
                    applied.Add("first");
                    return Task.CompletedTask;
                }),
            new SchemaStep(
                1,
                "same-version",
                (DbConnection _, DbTransaction _, CancellationToken _) =>
                {
                    applied.Add("same-version");
                    return Task.CompletedTask;
                })
        ];

        Func<Task> upgrade = () => SchemaUpgrader.UpgradeAsync(database.Context, steps);

        await upgrade.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Schema step versions must be unique.*");
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task UpgradeAsync_WhenCancelledBeforeFirstStep_DoesNotApplyAnything()
    {
        await using TempSchemaDatabase database = new TempSchemaDatabase();
        int applyCount = 0;
        IReadOnlyList<SchemaStep> steps =
        [
            new SchemaStep(
                1,
                "cancelled-marker",
                async (DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
                {
                    applyCount++;
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        "CREATE TABLE CancelledMarker (Id INTEGER NOT NULL)",
                        cancellationToken);
                })
        ];
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = async () => await SchemaUpgrader.UpgradeAsync(
            database.Context,
            steps,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        int userVersion = await ReadUserVersionAsync(database.Context);
        bool cancelledMarkerExists = await TableExistsAsync(database.Context, "CancelledMarker");

        applyCount.Should().Be(0);
        userVersion.Should().Be(0);
        cancelledMarkerExists.Should().BeFalse();
    }

    private static SchemaStep CreateMarkerStep(
        int version,
        string name,
        string tableName)
    {
        return new SchemaStep(
            version,
            name,
            async (DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    "CREATE TABLE " + tableName + " (Id INTEGER NOT NULL)",
                    cancellationToken);
            });
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadUserVersionAsync(TwinShellDbContext context)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool openedConnection = connection.State != ConnectionState.Open;

        try
        {
            if (openedConnection)
            {
                await connection.OpenAsync();
            }

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";

            object? result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> TableExistsAsync(
        TwinShellDbContext context,
        string tableName)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool openedConnection = connection.State != ConnectionState.Open;

        try
        {
            if (openedConnection)
            {
                await connection.OpenAsync();
            }

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "$tableName";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            object? result = await command.ExecuteScalarAsync();
            int tableCount = Convert.ToInt32(result, CultureInfo.InvariantCulture);
            return tableCount > 0;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private sealed class TempSchemaDatabase : IAsyncDisposable
    {
        private readonly string _rootPath;

        internal TempSchemaDatabase()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_twinshell_schema_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);

            string databasePath = Path.Combine(_rootPath, "schema.db");
            SqliteConnectionStringBuilder connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            };
            DbContextOptions<TwinShellDbContext> options = new DbContextOptionsBuilder<TwinShellDbContext>()
                .UseSqlite(connectionString.ToString())
                .Options;

            Context = new TwinShellDbContext(options);
        }

        internal TwinShellDbContext Context { get; }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            DeleteDirectory(_rootPath);
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            Directory.Delete(path, recursive: true);
        }
    }
}
