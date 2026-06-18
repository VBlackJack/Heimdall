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
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Persistence;
using TwinShell.Persistence.Schema;

namespace TwinShell.Infrastructure.Tests;

public sealed class TwinShellSchemaUpgradeTests
{
    private static readonly string[] PublicIdTables =
    [
        "Actions",
        "CommandBatches",
        "CustomCategories",
        "CommandTemplates"
    ];

    [Fact]
    public async Task UpgradeAsync_LegacyDatabase_AddsPublicIdsAndIndexes()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.CreateLegacySchemaAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(2);

        foreach (string tableName in PublicIdTables)
        {
            bool hasPublicIdColumn = await TableHasColumnAsync(database.Context, tableName, "PublicId");
            bool hasPublicIdIndex = await IndexExistsAsync(database.Context, "IX_" + tableName + "_PublicId");
            IReadOnlyList<string> publicIds = await ReadPublicIdsAsync(database.Context, tableName);

            hasPublicIdColumn.Should().BeTrue();
            hasPublicIdIndex.Should().BeTrue();
            publicIds.Should().HaveCount(2);
            publicIds.Should().OnlyContain(publicId => !string.IsNullOrWhiteSpace(publicId));
            publicIds.Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public async Task UpgradeAsync_FreshDatabase_MarksSchemaVersionTwo()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.Context.Database.EnsureCreatedAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(2);

        foreach (string tableName in PublicIdTables)
        {
            bool hasPublicIdColumn = await TableHasColumnAsync(database.Context, tableName, "PublicId");
            bool hasPublicIdIndex = await IndexExistsAsync(database.Context, "IX_" + tableName + "_PublicId");

            hasPublicIdColumn.Should().BeTrue();
            hasPublicIdIndex.Should().BeTrue();
        }
    }

    [Fact]
    public async Task UpgradeAsync_WhenRunTwice_LeavesVersionAndPublicIdsUnchanged()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.CreateLegacySchemaAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);
        Dictionary<string, IReadOnlyList<string>> publicIdsByTable = new Dictionary<string, IReadOnlyList<string>>();

        foreach (string tableName in PublicIdTables)
        {
            IReadOnlyList<string> publicIds = await ReadPublicIdsAsync(database.Context, tableName);
            publicIdsByTable.Add(tableName, publicIds);
        }

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(2);

        foreach (string tableName in PublicIdTables)
        {
            IReadOnlyList<string> publicIds = await ReadPublicIdsAsync(database.Context, tableName);
            publicIds.Should().Equal(publicIdsByTable[tableName]);
        }
    }

    [Fact]
    public async Task UpgradeAsync_WhenSecondTableUpdateFails_RollsBackWholePublicIdStep()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.CreateLegacySchemaAsync();

        // A trigger makes the production v1 step fail after ALTER TABLE on the second
        // table without changing the step implementation or relying on random UUIDs.
        await ExecuteNonQueryAsync(
            database.Context,
            "CREATE TRIGGER CommandBatches_ForcePublicIdFailure AFTER UPDATE ON CommandBatches "
            + "BEGIN SELECT RAISE(FAIL, 'forced publicid failure'); END");

        Func<Task> act = async () => await SchemaUpgrader.UpgradeAsync(
            database.Context,
            TwinShellSchema.Steps);

        await act.Should().ThrowAsync<Exception>().WithMessage("*forced publicid failure*");

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(0);

        foreach (string tableName in PublicIdTables)
        {
            bool hasPublicIdColumn = await TableHasColumnAsync(database.Context, tableName, "PublicId");
            bool hasPublicIdIndex = await IndexExistsAsync(database.Context, "IX_" + tableName + "_PublicId");

            hasPublicIdColumn.Should().BeFalse();
            hasPublicIdIndex.Should().BeFalse();
        }
    }

    [Fact]
    public async Task BootstrapperInitializationPath_FreshDatabase_ReachesVersionTwo()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        ServiceCollection services = new ServiceCollection();
        services.AddDbContext<TwinShellDbContext>(options =>
            options.UseSqlite(database.ConnectionString));

        // TwinShellBootstrapper owns an AppData database path; this exercises the same
        // scoped DI context plus EnsureCreated/Upgrade sequence against a temp database.
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        TwinShellDbContext context = scope.ServiceProvider.GetRequiredService<TwinShellDbContext>();

        await context.Database.EnsureCreatedAsync();
        await SchemaUpgrader.UpgradeAsync(context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(context);
        userVersion.Should().Be(2);
    }

    // Producer: TwinShellSchema step 2
    // ("D2 unwrap double-quoted placeholders in system command templates")
    // applied by SchemaUpgrader.UpgradeAsync against a database seeded at user_version = 1.
    [Fact]
    public async Task UpgradeAsync_V2_UnwrapsDoubleQuotedPlaceholdersOnSystemTemplatesOnly()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.SeedTemplateMigrationFixtureAtVersionOneAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(2);

        // Windows system template: single double-quoted placeholder is unwrapped.
        string windowsSystemPattern = await ReadCommandPatternAsync(database.Context, "tpl-system-windows");
        windowsSystemPattern.Should().Be("Get-ADGroup -Identity {groupName} -Properties *");

        // Linux system template: single double-quoted placeholder is unwrapped.
        string linuxSystemPattern = await ReadCommandPatternAsync(database.Context, "tpl-system-linux");
        linuxSystemPattern.Should().Be("grep {pattern} {file}");

        // User-created action template with the same "{x}" shape is left untouched.
        string userPattern = await ReadCommandPatternAsync(database.Context, "tpl-user-windows");
        userPattern.Should().Be("Remove-Item \"{path}\"");

        // Lot-B span ("{driveLetter}:") on a system template is left untouched.
        string lotBPattern = await ReadCommandPatternAsync(database.Context, "tpl-system-lotb");
        lotBPattern.Should().Be("Get-Volume -DriveLetter \"{driveLetter}:\"");
    }

    [Fact]
    public async Task UpgradeAsync_V2_IsIdempotentAcrossRepeatedRuns()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.SeedTemplateMigrationFixtureAtVersionOneAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        string windowsAfterFirst = await ReadCommandPatternAsync(database.Context, "tpl-system-windows");
        string linuxAfterFirst = await ReadCommandPatternAsync(database.Context, "tpl-system-linux");

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(2);

        string windowsAfterSecond = await ReadCommandPatternAsync(database.Context, "tpl-system-windows");
        string linuxAfterSecond = await ReadCommandPatternAsync(database.Context, "tpl-system-linux");

        windowsAfterSecond.Should().Be(windowsAfterFirst);
        windowsAfterSecond.Should().Be("Get-ADGroup -Identity {groupName} -Properties *");
        linuxAfterSecond.Should().Be(linuxAfterFirst);
        linuxAfterSecond.Should().Be("grep {pattern} {file}");
    }

    private static async Task<string> ReadCommandPatternAsync(
        TwinShellDbContext context,
        string templateId)
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
            command.CommandText = "SELECT CommandPattern FROM CommandTemplates WHERE Id = $id";
            DbParameter idParameter = command.CreateParameter();
            idParameter.ParameterName = "$id";
            idParameter.Value = templateId;
            command.Parameters.Add(idParameter);

            object? result = await command.ExecuteScalarAsync();
            return Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
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

    private static async Task<bool> TableHasColumnAsync(
        TwinShellDbContext context,
        string tableName,
        string columnName)
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
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('" + tableName + "') WHERE name = $columnName";
            DbParameter columnNameParameter = command.CreateParameter();
            columnNameParameter.ParameterName = "$columnName";
            columnNameParameter.Value = columnName;
            command.Parameters.Add(columnNameParameter);

            object? result = await command.ExecuteScalarAsync();
            int columnCount = Convert.ToInt32(result, CultureInfo.InvariantCulture);
            return columnCount > 0;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> IndexExistsAsync(
        TwinShellDbContext context,
        string indexName)
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
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $indexName";
            DbParameter indexNameParameter = command.CreateParameter();
            indexNameParameter.ParameterName = "$indexName";
            indexNameParameter.Value = indexName;
            command.Parameters.Add(indexNameParameter);

            object? result = await command.ExecuteScalarAsync();
            int indexCount = Convert.ToInt32(result, CultureInfo.InvariantCulture);
            return indexCount > 0;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadPublicIdsAsync(
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
            command.CommandText = "SELECT PublicId FROM " + tableName + " ORDER BY Id";
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            List<string> publicIds = new List<string>();

            while (await reader.ReadAsync())
            {
                publicIds.Add(reader.GetString(0));
            }

            return publicIds;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task ExecuteNonQueryAsync(
        TwinShellDbContext context,
        string commandText)
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
            command.CommandText = commandText;

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private sealed class TempTwinShellDatabase : IAsyncDisposable
    {
        private readonly string _rootPath;

        internal TempTwinShellDatabase()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_twinshell_schema_live_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);

            string databasePath = Path.Combine(_rootPath, "twinshell.db");
            SqliteConnectionStringBuilder connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            };

            ConnectionString = connectionString.ToString();
            DbContextOptions<TwinShellDbContext> options = new DbContextOptionsBuilder<TwinShellDbContext>()
                .UseSqlite(ConnectionString)
                .Options;

            Context = new TwinShellDbContext(options);
        }

        internal TwinShellDbContext Context { get; }

        internal string ConnectionString { get; }

        internal async Task CreateLegacySchemaAsync()
        {
            foreach (string tableName in PublicIdTables)
            {
                // Actions and CommandTemplates carry the columns the v2 migration reads so that
                // the v1 PublicId step and the v2 unwrap step both run against this fixture. The
                // seeded rows have no system-action template references, so v2 is a clean no-op.
                string createSql = tableName switch
                {
                    "Actions" =>
                        "CREATE TABLE Actions (Id TEXT NOT NULL PRIMARY KEY, "
                        + "WindowsCommandTemplateId TEXT NULL, LinuxCommandTemplateId TEXT NULL, "
                        + "IsUserCreated INTEGER NOT NULL DEFAULT 0)",
                    "CommandTemplates" =>
                        "CREATE TABLE CommandTemplates (Id TEXT NOT NULL PRIMARY KEY, "
                        + "CommandPattern TEXT NOT NULL DEFAULT '')",
                    _ => "CREATE TABLE " + tableName + " (Id TEXT NOT NULL PRIMARY KEY)"
                };

                await ExecuteNonQueryAsync(Context, createSql);
                await ExecuteNonQueryAsync(
                    Context,
                    "INSERT INTO " + tableName + " (Id) VALUES ('" + tableName + "-1')");
                await ExecuteNonQueryAsync(
                    Context,
                    "INSERT INTO " + tableName + " (Id) VALUES ('" + tableName + "-2')");
            }

            await ExecuteNonQueryAsync(Context, "PRAGMA user_version = 0");
        }

        // Seeds a realistic Actions/CommandTemplates pair at user_version = 1 so that only the
        // v2 unwrap step runs. Covers a Windows system template, a Linux system template, a
        // user-created template, and a Lot-B span that must survive the migration unchanged.
        internal async Task SeedTemplateMigrationFixtureAtVersionOneAsync()
        {
            await ExecuteNonQueryAsync(
                Context,
                "CREATE TABLE CommandTemplates (Id TEXT NOT NULL PRIMARY KEY, CommandPattern TEXT NOT NULL)");
            await ExecuteNonQueryAsync(
                Context,
                "CREATE TABLE Actions (Id TEXT NOT NULL PRIMARY KEY, "
                + "WindowsCommandTemplateId TEXT NULL, LinuxCommandTemplateId TEXT NULL, "
                + "IsUserCreated INTEGER NOT NULL)");

            await InsertTemplateAsync("tpl-system-windows", "Get-ADGroup -Identity \"{groupName}\" -Properties *");
            await InsertTemplateAsync("tpl-system-linux", "grep \"{pattern}\" {file}");
            await InsertTemplateAsync("tpl-user-windows", "Remove-Item \"{path}\"");
            await InsertTemplateAsync("tpl-system-lotb", "Get-Volume -DriveLetter \"{driveLetter}:\"");

            // System action referencing the Windows + Linux system templates (IsUserCreated = 0).
            await InsertActionAsync("act-system", "tpl-system-windows", "tpl-system-linux", isUserCreated: 0);
            // User-created action referencing only the user template (IsUserCreated = 1).
            await InsertActionAsync("act-user", "tpl-user-windows", linuxTemplateId: null, isUserCreated: 1);
            // System action referencing the Lot-B template (IsUserCreated = 0).
            await InsertActionAsync("act-system-lotb", "tpl-system-lotb", linuxTemplateId: null, isUserCreated: 0);

            await ExecuteNonQueryAsync(Context, "PRAGMA user_version = 1");
        }

        private async Task InsertTemplateAsync(string id, string pattern)
        {
            DbConnection connection = Context.Database.GetDbConnection();
            bool openedConnection = connection.State != ConnectionState.Open;

            try
            {
                if (openedConnection)
                {
                    await connection.OpenAsync();
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO CommandTemplates (Id, CommandPattern) VALUES ($id, $pattern)";
                AddParameter(command, "$id", id);
                AddParameter(command, "$pattern", pattern);

                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                if (openedConnection)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task InsertActionAsync(
            string id,
            string? windowsTemplateId,
            string? linuxTemplateId,
            int isUserCreated)
        {
            DbConnection connection = Context.Database.GetDbConnection();
            bool openedConnection = connection.State != ConnectionState.Open;

            try
            {
                if (openedConnection)
                {
                    await connection.OpenAsync();
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO Actions (Id, WindowsCommandTemplateId, LinuxCommandTemplateId, IsUserCreated) "
                    + "VALUES ($id, $windowsId, $linuxId, $isUserCreated)";
                AddParameter(command, "$id", id);
                AddParameter(command, "$windowsId", (object?)windowsTemplateId ?? DBNull.Value);
                AddParameter(command, "$linuxId", (object?)linuxTemplateId ?? DBNull.Value);
                AddParameter(command, "$isUserCreated", isUserCreated);

                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                if (openedConnection)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
