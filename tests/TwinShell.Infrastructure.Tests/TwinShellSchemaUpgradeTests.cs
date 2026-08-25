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
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Repositories;
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
        userVersion.Should().Be(4);

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
    public async Task UpgradeAsync_FreshDatabase_MarksTerminalSchemaVersion()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.Context.Database.EnsureCreatedAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(4);

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
        userVersion.Should().Be(4);

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
    public async Task BootstrapperInitializationPath_FreshDatabase_ReachesTerminalSchemaVersion()
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
        userVersion.Should().Be(4);
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
        userVersion.Should().Be(4);

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
        userVersion.Should().Be(4);

        string windowsAfterSecond = await ReadCommandPatternAsync(database.Context, "tpl-system-windows");
        string linuxAfterSecond = await ReadCommandPatternAsync(database.Context, "tpl-system-linux");

        windowsAfterSecond.Should().Be(windowsAfterFirst);
        windowsAfterSecond.Should().Be("Get-ADGroup -Identity {groupName} -Properties *");
        linuxAfterSecond.Should().Be(linuxAfterFirst);
        linuxAfterSecond.Should().Be("grep {pattern} {file}");
    }

    // Producer: TwinShellSchema step 3
    // ("D2 Lot B distribute quoting modes, drive-letter type, unwrapped affixes and Defender fixes to system command templates")
    // applied by SchemaUpgrader.UpgradeAsync against a database seeded at user_version = 2.
    [Fact]
    public async Task UpgradeAsync_V3_DistributesD2LotBFixesToSystemTemplatesOnly()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.SeedD2LotBMigrationFixtureAtVersionTwoAsync();
        Dictionary<string, TemplateRow> beforeRows = await ReadCommandTemplateRowsAsync(database.Context);

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(4);

        Dictionary<string, TemplateRow> afterRows = await ReadCommandTemplateRowsAsync(database.Context);
        int updatedRows = afterRows.Count(row => beforeRows[row.Key] != row.Value);
        updatedRows.Should().Be(7);

        List<TemplateParameter> class1Parameters = await ReadTemplateParametersAsync(database.Context, "tpl-class1");
        FindParameter(class1Parameters, "vmName").Quoting.Should().Be(QuotingMode.InlineInQuotes);
        FindParameter(class1Parameters, "action").Quoting.Should().BeNull();

        List<TemplateParameter> driveParameters = await ReadTemplateParametersAsync(database.Context, "tpl-driveletter");
        FindParameter(driveParameters, "driveLetter").Type.Should().Be("driveletter");
        FindParameter(driveParameters, "driveLetter").Quoting.Should().BeNull();

        string archivePattern = await ReadCommandPatternAsync(database.Context, "tpl-archive");
        archivePattern.Should().Be("tar -czf '{archiveName}.tar.gz' {sourcePath}");
        List<TemplateParameter> archiveParameters = await ReadTemplateParametersAsync(database.Context, "tpl-archive");
        FindParameter(archiveParameters, "archiveName").Quoting.Should().Be(QuotingMode.InlineInQuotes);

        string icaclsPattern = await ReadCommandPatternAsync(database.Context, "tpl-icacls");
        icaclsPattern.Should().Be("icacls {path} /grant '{user}:(OI)(CI)F' /T");
        List<TemplateParameter> icaclsParameters = await ReadTemplateParametersAsync(database.Context, "tpl-icacls");
        FindParameter(icaclsParameters, "user").Quoting.Should().Be(QuotingMode.InlineInQuotes);

        string cloudPattern = await ReadCommandPatternAsync(database.Context, "tpl-defender-cloud");
        cloudPattern.Should().Be("Set-MpPreference -MAPSReporting {level}");
        List<TemplateParameter> cloudParameters = await ReadTemplateParametersAsync(database.Context, "tpl-defender-cloud");
        FindParameter(cloudParameters, "level").Type.Should().Be("number");

        string schedulePattern = await ReadCommandPatternAsync(database.Context, "tpl-defender-schedule");
        schedulePattern.Should().Be("Set-MpPreference -ScanScheduleDay {day} -ScanScheduleTime {time}");
        List<TemplateParameter> scheduleParameters = await ReadTemplateParametersAsync(database.Context, "tpl-defender-schedule");
        FindParameter(scheduleParameters, "day").Type.Should().Be("number");
        FindParameter(scheduleParameters, "time").Type.Should().Be("string");

        string realtimePattern = await ReadCommandPatternAsync(database.Context, "tpl-defender-realtime");
        realtimePattern.Should().Be("Set-MpPreference -DisableRealtimeMonitoring ${enableDisable}");
        List<TemplateParameter> realtimeParameters = await ReadTemplateParametersAsync(database.Context, "tpl-defender-realtime");
        FindParameter(realtimeParameters, "enableDisable").DefaultValue.Should().Be("false");
        FindParameter(realtimeParameters, "enableDisable").Quoting.Should().Be(QuotingMode.InlineInQuotes);

        afterRows["tpl-user-archive"].Should().Be(beforeRows["tpl-user-archive"]);
    }

    [Fact]
    public async Task UpgradeAsync_V3_IsIdempotentAcrossRepeatedRuns()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.SeedD2LotBMigrationFixtureAtVersionTwoAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);
        Dictionary<string, TemplateRow> rowsAfterFirst = await ReadCommandTemplateRowsAsync(database.Context);

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        int userVersion = await ReadUserVersionAsync(database.Context);
        userVersion.Should().Be(4);

        Dictionary<string, TemplateRow> rowsAfterSecond = await ReadCommandTemplateRowsAsync(database.Context);
        rowsAfterSecond.Should().Equal(rowsAfterFirst);
    }

    // BL-0093. EnsureCreated only writes a schema when the file is ABSENT, so on an updated
    // installation it is a no-op and every model column added since first launch must arrive
    // through a schema step. Two did not, and the command library opened empty.
    //
    // The projection here is the one that actually threw for the user - favorites through the
    // EF stack, which selects every Action column - and not a hand-written SELECT over a
    // subset, which would have passed while the application still failed.
    [Fact]
    public async Task UpgradeAsync_DatabaseMissingPlatformExampleColumns_LetsTheFavoritesProjectionRun()
    {
        await using TempTwinShellDatabase database = new TempTwinShellDatabase();
        await database.CreateSchemaMissingPlatformExampleColumnsAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        FavoritesRepository repository = new FavoritesRepository(
            database.Context,
            NullLogger<FavoritesRepository>.Instance);
        IEnumerable<UserFavorite> favorites = await repository.GetAllAsync();

        favorites.Should().ContainSingle();
        (await TableHasColumnAsync(database.Context, "Actions", "WindowsExamplesJson")).Should().BeTrue();
        (await TableHasColumnAsync(database.Context, "Actions", "LinuxExamplesJson")).Should().BeTrue();

        // The default has to match the entity initializer: a NULL here would throw on
        // materialisation of a non-nullable string, which is the same failure one step later.
        IReadOnlyList<string> windowsExamples =
            await ReadActionColumnAsync(database.Context, "WindowsExamplesJson");
        IReadOnlyList<string> linuxExamples =
            await ReadActionColumnAsync(database.Context, "LinuxExamplesJson");
        windowsExamples.Should().OnlyContain(value => value == "[]");
        linuxExamples.Should().OnlyContain(value => value == "[]");
    }

    private static async Task<IReadOnlyList<string>> ReadActionColumnAsync(
        TwinShellDbContext context,
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
            command.CommandText = "SELECT " + columnName + " FROM Actions";

            List<string> values = new List<string>();
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
            }

            return values;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
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

    private static async Task<List<TemplateParameter>> ReadTemplateParametersAsync(
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
            command.CommandText = "SELECT ParametersJson FROM CommandTemplates WHERE Id = $id";
            DbParameter idParameter = command.CreateParameter();
            idParameter.ParameterName = "$id";
            idParameter.Value = templateId;
            command.Parameters.Add(idParameter);

            object? result = await command.ExecuteScalarAsync();
            string parametersJson = Convert.ToString(result, CultureInfo.InvariantCulture) ?? "[]";
            return JsonSerializer.Deserialize<List<TemplateParameter>>(
                parametersJson,
                JsonOptionsHelper.CompactStorage) ?? new List<TemplateParameter>();
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<Dictionary<string, TemplateRow>> ReadCommandTemplateRowsAsync(
        TwinShellDbContext context)
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
            command.CommandText = "SELECT Id, CommandPattern, ParametersJson FROM CommandTemplates ORDER BY Id";
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            Dictionary<string, TemplateRow> rows = new Dictionary<string, TemplateRow>(StringComparer.Ordinal);

            while (await reader.ReadAsync())
            {
                string id = reader.GetString(0);
                string commandPattern = reader.GetString(1);
                string parametersJson = reader.GetString(2);
                rows.Add(id, new TemplateRow(commandPattern, parametersJson));
            }

            return rows;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static TemplateParameter FindParameter(
        IEnumerable<TemplateParameter> parameters,
        string parameterName)
        => parameters.Single(parameter =>
            string.Equals(parameter.Name, parameterName, StringComparison.Ordinal));

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

    private static TemplateParameter Parameter(
        string name,
        string type = "string",
        string? defaultValue = null)
        => new TemplateParameter
        {
            Name = name,
            Label = name,
            Type = type,
            DefaultValue = defaultValue,
            Required = true,
            Description = name
        };

    private sealed record TemplateRow(
        string CommandPattern,
        string ParametersJson);

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
                        + "CommandPattern TEXT NOT NULL DEFAULT '', "
                        + "ParametersJson TEXT NOT NULL DEFAULT '[]')",
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

        // Builds the shape a user who UPDATED actually has: the schema EnsureCreated wrote at
        // their first launch, which is every column the model carried THEN. Reproduced by
        // creating today's schema and dropping the two columns added since, so the fixture
        // keeps tracking the model instead of freezing a hand-written CREATE that would drift
        // in its turn. Rows are seeded through EF before the drop, while the table still
        // matches the model.
        internal async Task CreateSchemaMissingPlatformExampleColumnsAsync()
        {
            await Context.Database.EnsureCreatedAsync();

            ActionEntity action = new()
            {
                Id = "act-legacy",
                Title = "Legacy action",
                Description = "Seeded before the platform example columns existed",
                Category = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Context.Actions.Add(action);
            Context.UserFavorites.Add(new UserFavoriteEntity
            {
                Id = "fav-legacy",
                ActionId = action.Id,
                CreatedAt = DateTime.UtcNow,
                DisplayOrder = 0
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            await ExecuteNonQueryAsync(Context, "ALTER TABLE Actions DROP COLUMN WindowsExamplesJson");
            await ExecuteNonQueryAsync(Context, "ALTER TABLE Actions DROP COLUMN LinuxExamplesJson");
            await ExecuteNonQueryAsync(Context, "PRAGMA user_version = 0");
        }

        // Seeds a realistic Actions/CommandTemplates pair at user_version = 1 so that only the
        // v2 unwrap step runs. Covers a Windows system template, a Linux system template, a
        // user-created template, and a Lot-B span that must survive the migration unchanged.
        internal async Task SeedTemplateMigrationFixtureAtVersionOneAsync()
        {
            await ExecuteNonQueryAsync(
                Context,
                "CREATE TABLE CommandTemplates (Id TEXT NOT NULL PRIMARY KEY, "
                + "CommandPattern TEXT NOT NULL, ParametersJson TEXT NOT NULL DEFAULT '[]')");
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

        // Seeds a pre-v3 database with representative D2 Lot B shapes. Only the v3 step runs.
        internal async Task SeedD2LotBMigrationFixtureAtVersionTwoAsync()
        {
            await ExecuteNonQueryAsync(
                Context,
                "CREATE TABLE CommandTemplates (Id TEXT NOT NULL PRIMARY KEY, "
                + "CommandPattern TEXT NOT NULL, ParametersJson TEXT NOT NULL DEFAULT '[]')");
            await ExecuteNonQueryAsync(
                Context,
                "CREATE TABLE Actions (Id TEXT NOT NULL PRIMARY KEY, "
                + "WindowsCommandTemplateId TEXT NULL, LinuxCommandTemplateId TEXT NULL, "
                + "IsUserCreated INTEGER NOT NULL)");

            await InsertTemplateAsync(
                "tpl-class1",
                "Get-VM -Name '{vmName}' -Action {action}",
                Parameter("vmName"),
                Parameter("action"));
            await InsertTemplateAsync(
                "tpl-driveletter",
                "Get-BitLockerVolume -MountPoint \"{driveLetter}:\"",
                Parameter("driveLetter"));
            await InsertTemplateAsync(
                "tpl-archive",
                "tar -czf \"{archiveName}.tar.gz\" {sourcePath}",
                Parameter("archiveName"),
                Parameter("sourcePath"));
            await InsertTemplateAsync(
                "tpl-icacls",
                "icacls {path} /grant \"{user}:(OI)(CI)F\" /T",
                Parameter("path"),
                Parameter("user"));
            await InsertTemplateAsync(
                "tpl-defender-cloud",
                "Set-MpPreference -MAPSReporting ${level}",
                Parameter("level", defaultValue: "2"));
            await InsertTemplateAsync(
                "tpl-defender-schedule",
                "Set-MpPreference -ScanScheduleDay ${day} -ScanScheduleTime ${time}",
                Parameter("day", defaultValue: "0"),
                Parameter("time", defaultValue: "02:00:00"));
            await InsertTemplateAsync(
                "tpl-defender-realtime",
                "Set-MpPreference -DisableRealtimeMonitoring ${enableDisable}",
                Parameter("enableDisable", defaultValue: "$false"));
            await InsertTemplateAsync(
                "tpl-user-archive",
                "tar -czf \"{archiveName}.tar.gz\" {sourcePath}",
                Parameter("archiveName"),
                Parameter("sourcePath"));

            await InsertActionAsync("act-class1", "tpl-class1", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-driveletter", "tpl-driveletter", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-archive", "tpl-archive", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-icacls", "tpl-icacls", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-defender-cloud", "tpl-defender-cloud", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-defender-schedule", "tpl-defender-schedule", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-defender-realtime", "tpl-defender-realtime", linuxTemplateId: null, isUserCreated: 0);
            await InsertActionAsync("act-user-archive", "tpl-user-archive", linuxTemplateId: null, isUserCreated: 1);

            await ExecuteNonQueryAsync(Context, "PRAGMA user_version = 2");
        }

        private async Task InsertTemplateAsync(
            string id,
            string pattern,
            params TemplateParameter[] parameters)
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
                    "INSERT INTO CommandTemplates (Id, CommandPattern, ParametersJson) "
                    + "VALUES ($id, $pattern, $parametersJson)";
                AddParameter(command, "$id", id);
                AddParameter(command, "$pattern", pattern);
                AddParameter(
                    command,
                    "$parametersJson",
                    JsonSerializer.Serialize(parameters.ToList(), JsonOptionsHelper.CompactStorage));

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
