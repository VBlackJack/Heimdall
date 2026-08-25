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
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TwinShell.Persistence;
using TwinShell.Persistence.Schema;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Proves every column the model declares is reachable on a database created by an older
/// build.
/// </summary>
/// <remarks>
/// The class of defect is structural rather than accidental. TwinShell creates its schema with
/// <c>EnsureCreated</c>, which writes nothing when the database file already exists, so on an
/// updated installation it is a no-op. Every property added to an entity after the first launch
/// is therefore a silent time bomb: it works on every developer machine, where the database was
/// created from today's model, and throws on the first real projection for anyone who updated.
/// Nothing else in the suite notices, and the seed count reported at startup does not touch the
/// missing column, so the application even announces success first.
///
/// The fixture this reads is frozen on purpose. Regenerating it would make this test pass by
/// construction while protecting nothing.
/// </remarks>
public sealed class SchemaReachesEveryModelColumnTests
{
    [Fact]
    public async Task EveryEntity_QueryableOnADatabaseUpgradedFromTheFrozenBaseline()
    {
        await using TempDatabase database = new TempDatabase();
        await database.ApplyFrozenBaselineAsync();

        await SchemaUpgrader.UpgradeAsync(database.Context, TwinShellSchema.Steps);

        List<string> unreachable = [];

        foreach (IEntityType entityType in database.Context.Model.GetEntityTypes())
        {
            try
            {
                // Enumerating executes the real SELECT over every mapped column. An empty
                // table still runs it, which is what makes a missing column fail here rather
                // than only once a user happens to own a row.
                IQueryable queryable = CreateSetFor(database.Context, entityType.ClrType);
                foreach (object _ in queryable)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is SqliteException or TargetInvocationException)
            {
                Exception reported = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                unreachable.Add(entityType.ClrType.Name + ": " + reported.Message);
            }
        }

        unreachable.Should().BeEmpty(
            "every mapped column must be reachable through a schema step - a property added to "
            + "an entity without one is invisible until a user who updated opens the feature. "
            + "Add a step to TwinShellSchema; do NOT regenerate Fixtures/BaselineSchema.sql.");
    }

    // Non-vacuity: if the baseline ever stopped being a PRE-migration schema - regenerated from
    // the current model, say - the test above would pass without proving anything. This pins
    // what makes it meaningful: the frozen file must lack what the steps are there to add.
    [Fact]
    public void FrozenBaseline_StillPredatesTheSchemaSteps()
    {
        string baseline = File.ReadAllText(BaselinePath());

        // The header names these columns to explain why they are absent, so the assertions
        // below have to look at the SQL alone.
        string sqlOnly = string.Join(
            '\n',
            baseline
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        sqlOnly.Should().NotContain("PublicId");
        sqlOnly.Should().NotContain("WindowsExamplesJson");
        sqlOnly.Should().NotContain("LinuxExamplesJson");
        sqlOnly.Should().Contain("ExamplesJson");
        sqlOnly.Should().Contain("CREATE TABLE \"Actions\"");
        baseline.Should().Contain("NEVER REGENERATE THIS FILE");
    }

    private static IQueryable CreateSetFor(DbContext context, Type clrType)
    {
        MethodInfo setMethod = typeof(DbContext)
            .GetMethods()
            .Single(method => method.Name == nameof(DbContext.Set)
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 0)
            .MakeGenericMethod(clrType);

        return (IQueryable)setMethod.Invoke(context, null)!;
    }

    private static string BaselinePath()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "tests",
            "TwinShell.Infrastructure.Tests",
            "Fixtures",
            "BaselineSchema.sql");

        Assert.True(File.Exists(path), $"frozen baseline schema not found: {path}");
        return path;
    }

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _rootPath;

        internal TempDatabase()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_twinshell_baseline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);

            SqliteConnectionStringBuilder connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_rootPath, "twinshell.db"),
                Pooling = false
            };

            DbContextOptions<TwinShellDbContext> options =
                new DbContextOptionsBuilder<TwinShellDbContext>()
                    .UseSqlite(connectionString.ToString())
                    .Options;

            Context = new TwinShellDbContext(options);
        }

        internal TwinShellDbContext Context { get; }

        internal async Task ApplyFrozenBaselineAsync()
        {
            string script = await File.ReadAllTextAsync(BaselinePath());
            DbConnection connection = Context.Database.GetDbConnection();
            await connection.OpenAsync();

            try
            {
                foreach (string statement in script.Split(';'))
                {
                    string trimmed = StripComments(statement);
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }

                    await using DbCommand command = connection.CreateCommand();
                    command.CommandText = trimmed;
                    await command.ExecuteNonQueryAsync();
                }

                await using DbCommand versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "PRAGMA user_version = 0";
                await versionCommand.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.CloseAsync();
            }
        }

        private static string StripComments(string statement)
        {
            IEnumerable<string> lines = statement
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal));

            return string.Join('\n', lines).Trim();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
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
        }
    }
}
