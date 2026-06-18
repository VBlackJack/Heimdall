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
using System.Text.RegularExpressions;
using Heimdall.Core.Logging;

namespace TwinShell.Persistence.Schema;

public static class TwinShellSchema
{
    private const string PublicIdColumnName = "PublicId";

    // Strips the surrounding double quotes only when the quoted span is exactly a single
    // placeholder ("{name}" -> {name}). Spans carrying extra literal text or nested quoting
    // (e.g. "{driveLetter}:", "*{searchTerm}*") never match and are left untouched. This is the
    // SQLite-side counterpart to the D2 Lot A seed fix for databases that predate that fix.
    private static readonly Regex DoubleQuotedPlaceholderPattern = new Regex(
        "\"\\{(\\w+)\\}\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string UuidSql =
        "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6)))";

    private static readonly string[] PublicIdTables =
    [
        "Actions",
        "CommandBatches",
        "CustomCategories",
        "CommandTemplates"
    ];

    private static readonly IReadOnlyList<SchemaStep> SchemaSteps = new List<SchemaStep>
    {
        new SchemaStep(1, "GitOps PublicId columns", ApplyPublicIdAsync),
        new SchemaStep(
            2,
            "D2 unwrap double-quoted placeholders in system command templates",
            ApplyUnwrapDoubleQuotedPlaceholdersAsync)
    }.AsReadOnly();

    public static IReadOnlyList<SchemaStep> Steps => SchemaSteps;

    private static async Task ApplyPublicIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (string tableName in PublicIdTables)
        {
            await AddPublicIdColumnIfNotExistsAsync(
                connection,
                transaction,
                tableName,
                cancellationToken);
        }
    }

    private static async Task ApplyUnwrapDoubleQuotedPlaceholdersAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        List<(string Id, string Pattern)> systemTemplates = await ReadSystemCommandTemplatesAsync(
            connection,
            transaction,
            cancellationToken);

        int updatedCount = 0;

        foreach ((string id, string pattern) in systemTemplates)
        {
            string unwrapped = DoubleQuotedPlaceholderPattern.Replace(pattern, "{$1}");

            if (string.Equals(unwrapped, pattern, StringComparison.Ordinal))
            {
                continue;
            }

            await UpdateCommandTemplatePatternAsync(
                connection,
                transaction,
                id,
                unwrapped,
                cancellationToken);
            updatedCount++;
        }

        FileLogger.Info(
            "[TwinShell] D2 migration unwrapped double-quoted placeholders in "
            + updatedCount.ToString(CultureInfo.InvariantCulture)
            + " system command template(s)");
    }

    private static async Task<List<(string Id, string Pattern)>> ReadSystemCommandTemplatesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT ct.Id, ct.CommandPattern FROM CommandTemplates ct "
            + "WHERE EXISTS (SELECT 1 FROM Actions a "
            + "WHERE (a.WindowsCommandTemplateId = ct.Id OR a.LinuxCommandTemplateId = ct.Id) "
            + "AND a.IsUserCreated = 0)";

        List<(string Id, string Pattern)> templates = new List<(string Id, string Pattern)>();

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            string id = reader.GetString(0);
            string pattern = reader.GetString(1);
            templates.Add((id, pattern));
        }

        return templates;
    }

    private static async Task UpdateCommandTemplatePatternAsync(
        DbConnection connection,
        DbTransaction transaction,
        string id,
        string pattern,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE CommandTemplates SET CommandPattern = @pattern WHERE Id = @id";

        DbParameter patternParameter = command.CreateParameter();
        patternParameter.ParameterName = "@pattern";
        patternParameter.Value = pattern;
        command.Parameters.Add(patternParameter);

        DbParameter idParameter = command.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = id;
        command.Parameters.Add(idParameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddPublicIdColumnIfNotExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        EnsureKnownPublicIdTable(tableName);

        bool exists = await PublicIdColumnExistsAsync(
            connection,
            transaction,
            tableName,
            cancellationToken);

        if (exists)
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "ALTER TABLE " + tableName + " ADD COLUMN " + PublicIdColumnName + " TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "UPDATE " + tableName + " SET " + PublicIdColumnName + " = " + UuidSql,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_" + tableName + "_" + PublicIdColumnName
            + " ON " + tableName + "(" + PublicIdColumnName + ")",
            cancellationToken);
    }

    private static void EnsureKnownPublicIdTable(string tableName)
    {
        if (!PublicIdTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid table name: " + tableName, nameof(tableName));
        }
    }

    private static async Task<bool> PublicIdColumnExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('" + tableName + "') WHERE name = '"
            + PublicIdColumnName + "'";

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        int existingColumnCount = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        return existingColumnCount > 0;
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
}
