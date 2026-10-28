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
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.Core.Logging;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using TwinShell.Persistence.Configurations;

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

    private const string ActionsTableName = "Actions";

    private const string ActionTranslationsTableName = "ActionTranslations";

    private const string CommandHistoriesTableName = "CommandHistories";

    private const string CommandTemplatesTableName = "CommandTemplates";

    private const string TargetHostColumnName = "TargetHost";

    private const string CommandBatchesTableName = "CommandBatches";

    private const string VariablesColumnName = "VariablesJson";

    private const string IsRetiredColumnName = "IsRetired";

    // The translated parameter labels and example descriptions, added when the shipped
    // catalogue started carrying its French beside an English pivot. Default '{}' matches the
    // entity initializers, so a translation row that predates them still materialises.
    private static readonly string[] TranslationTextColumns =
    [
        "ParametersJson",
        "ExamplesJson"
    ];

    private static readonly string[] PublicIdTables =
    [
        ActionsTableName,
        "CommandBatches",
        "CustomCategories",
        "CommandTemplates"
    ];

    // Added to ActionEntity without a schema step, so updated databases never got them:
    // EnsureCreated writes a schema only when the file is absent. Default '[]' matches the
    // entity initializers, so a row that predates them materialises instead of throwing on a
    // NULL for a non-nullable string.
    private static readonly string[] PlatformExampleColumns =
    [
        "WindowsExamplesJson",
        "LinuxExamplesJson"
    ];

    // Every identifier this file concatenates into SQL, and nothing else. pragma_table_info
    // takes no parameter for the table name, so the allow-list is the guard.
    private static readonly IReadOnlyDictionary<string, string[]> KnownColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [ActionsTableName] = [PublicIdColumnName, "WindowsExamplesJson", "LinuxExamplesJson", IsRetiredColumnName],
            [ActionTranslationsTableName] = ["ParametersJson", "ExamplesJson"],
            [CommandHistoriesTableName] = [TargetHostColumnName],
            [CommandBatchesTableName] = [PublicIdColumnName, VariablesColumnName],
            ["CustomCategories"] = [PublicIdColumnName],
            ["CommandTemplates"] = [PublicIdColumnName]
        };

    private static readonly IReadOnlyList<SchemaStep> SchemaSteps = new List<SchemaStep>
    {
        new SchemaStep(1, "GitOps PublicId columns", ApplyPublicIdAsync),
        new SchemaStep(
            2,
            "D2 unwrap double-quoted placeholders in system command templates",
            ApplyUnwrapDoubleQuotedPlaceholdersAsync),
        new SchemaStep(
            3,
            "D2 Lot B distribute quoting modes, drive-letter type, unwrapped affixes and Defender fixes to system command templates",
            ApplyD2LotBAsync),
        new SchemaStep(
            4,
            "Per-platform example columns on Actions",
            ApplyPlatformExampleColumnsAsync),
        new SchemaStep(
            5,
            "Target host column on CommandHistories",
            ApplyHistoryTargetHostColumnAsync),
        new SchemaStep(
            6,
            "Translated parameter and example columns on ActionTranslations",
            ApplyTranslationTextColumnsAsync),
        new SchemaStep(
            7,
            "SeedState table recording the seed last applied",
            ApplySeedStateTableAsync),
        new SchemaStep(
            8,
            "Runbook variables column on CommandBatches",
            ApplyBatchVariablesColumnAsync),
        new SchemaStep(
            9,
            "Retired flag on Actions",
            ApplyActionRetiredColumnAsync),
        new SchemaStep(
            10,
            "SyncTombstones table recording the shared entries deleted here",
            ApplySyncTombstonesTableAsync),
        new SchemaStep(
            11,
            "Deterministic public ids for system actions and their templates",
            ApplySeedPublicIdsAsync),
        new SchemaStep(
            12,
            "Upper-case public ids, the form EF writes and compares",
            ApplyUpperCasePublicIdsAsync)
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

    /// <summary>
    /// Adds the per-platform example columns to <c>Actions</c> when they are missing.
    /// </summary>
    /// <remarks>
    /// Idempotent by existence test rather than by catching a duplicate-column error, so a
    /// second run leaves both the schema and <c>user_version</c> untouched. The column is
    /// added, never backfilled: the DEFAULT covers existing rows, and the aggregated
    /// <c>ExamplesJson</c> that predates the split is left where it is - splitting it by
    /// platform would be a guess about content, which a schema step has no business making.
    /// </remarks>
    private static async Task ApplyPlatformExampleColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (string columnName in PlatformExampleColumns)
        {
            bool exists = await ColumnExistsAsync(
                connection,
                transaction,
                ActionsTableName,
                columnName,
                cancellationToken);

            if (exists)
            {
                continue;
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "ALTER TABLE " + ActionsTableName + " ADD COLUMN " + columnName
                + " TEXT NOT NULL DEFAULT '[]'",
                cancellationToken);
        }
    }

    /// <summary>
    /// Adds the nullable <c>TargetHost</c> column to <c>CommandHistories</c> when it is missing.
    /// </summary>
    /// <remarks>
    /// Nullable with no default: a row written before the column existed does not know where
    /// its command went, and a made-up value would put it under a host's filter it never
    /// reached. Idempotent by existence test, like the platform example columns. A database with
    /// no history table at all is left alone: the table is created from the model, column
    /// included, and there is nothing to add to.
    /// </remarks>
    private static async Task ApplyHistoryTargetHostColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, CommandHistoriesTableName, cancellationToken))
        {
            return;
        }

        bool exists = await ColumnExistsAsync(
            connection,
            transaction,
            CommandHistoriesTableName,
            TargetHostColumnName,
            cancellationToken);

        if (exists)
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "ALTER TABLE " + CommandHistoriesTableName + " ADD COLUMN " + TargetHostColumnName + " TEXT NULL",
            cancellationToken);
    }

    /// <summary>
    /// Adds the translated parameter and example columns to <c>ActionTranslations</c> when
    /// they are missing.
    /// </summary>
    /// <remarks>
    /// Idempotent by existence test, like <see cref="ApplyPlatformExampleColumnsAsync"/>. No
    /// row is backfilled: a translation that predates the columns had no parameter or example
    /// text to carry, and the DEFAULT says exactly that. A database without the table at all
    /// is left alone: <c>EnsureCreated</c> writes the table whole, columns included, the
    /// first time it creates it.
    /// </remarks>
    private static async Task ApplyTranslationTextColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, ActionTranslationsTableName, cancellationToken))
        {
            return;
        }

        foreach (string columnName in TranslationTextColumns)
        {
            bool exists = await ColumnExistsAsync(
                connection,
                transaction,
                ActionTranslationsTableName,
                columnName,
                cancellationToken);

            if (exists)
            {
                continue;
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "ALTER TABLE " + ActionTranslationsTableName + " ADD COLUMN " + columnName
                + " TEXT NOT NULL DEFAULT '{}'",
                cancellationToken);
        }
    }

    /// <summary>
    /// Creates the <c>SeedState</c> key-value table that records the content hash of the seed
    /// last applied, so a changed seed reaches a database seeded by an older build.
    /// </summary>
    /// <remarks>
    /// Idempotent through <c>IF NOT EXISTS</c>: on a database <c>EnsureCreated</c> has just
    /// written from today's model, the table is already there. No row is written: a missing
    /// hash on a populated database is what tells the refresh to run once.
    /// </remarks>
    /// <summary>
    /// Adds the runbook <c>VariablesJson</c> column to <c>CommandBatches</c> when it is missing.
    /// </summary>
    /// <remarks>
    /// Idempotent by existence test, like the other column steps. The DEFAULT '{}' says what a
    /// batch written before runbooks had: no variables. A step's parameter values need no
    /// column of their own: steps live in <c>CommandsJson</c>, and a step stored before it had
    /// parameters reads back with none. A database without the table is left alone:
    /// <c>EnsureCreated</c> writes it whole, column included.
    /// </remarks>
    private static async Task ApplyBatchVariablesColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, CommandBatchesTableName, cancellationToken))
        {
            return;
        }

        bool exists = await ColumnExistsAsync(
            connection,
            transaction,
            CommandBatchesTableName,
            VariablesColumnName,
            cancellationToken);

        if (exists)
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "ALTER TABLE " + CommandBatchesTableName + " ADD COLUMN " + VariablesColumnName
            + " TEXT NOT NULL DEFAULT '{}'",
            cancellationToken);
    }

    /// <summary>
    /// Adds the <c>IsRetired</c> flag to <c>Actions</c> when it is missing.
    /// </summary>
    /// <remarks>
    /// Idempotent by existence test, like the other column steps. The DEFAULT 0 says what every
    /// row written before the flag was: live. Retiring a shipped action used to delete its row,
    /// and its history went with it through the cascade; the seed refresh now marks it instead.
    /// A database without the table is left alone: <c>EnsureCreated</c> writes it whole.
    /// </remarks>
    private static async Task ApplyActionRetiredColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, ActionsTableName, cancellationToken))
        {
            return;
        }

        bool exists = await ColumnExistsAsync(
            connection,
            transaction,
            ActionsTableName,
            IsRetiredColumnName,
            cancellationToken);

        if (exists)
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "ALTER TABLE " + ActionsTableName + " ADD COLUMN " + IsRetiredColumnName
            + " INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
    }

    /// <summary>
    /// Creates the <c>SyncTombstones</c> table: the user actions, templates, runbooks and
    /// categories deleted here, so the next sync export removes their files from the shared
    /// folder and records the delete in its <c>_deleted.json</c>.
    /// </summary>
    /// <remarks>
    /// Idempotent through <c>IF NOT EXISTS</c>, like the <c>SeedState</c> step. No row is
    /// written: a delete made before this version left nothing to record.
    /// </remarks>
    private static Task ApplySyncTombstonesTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        => ExecuteNonQueryAsync(
            connection,
            transaction,
            "CREATE TABLE IF NOT EXISTS \"" + SyncTombstoneConfiguration.TableName + "\" ("
            + "\"PublicId\" TEXT NOT NULL CONSTRAINT \"PK_" + SyncTombstoneConfiguration.TableName + "\" PRIMARY KEY, "
            + "\"DeletedAt\" TEXT NOT NULL)",
            cancellationToken);

    /// <summary>
    /// Gives every system action, and every template only system actions use, the public id
    /// <see cref="SeedPublicIds"/> derives from its seed id.
    /// </summary>
    /// <remarks>
    /// The ids used to be drawn at random on each install, so a teammate's copy of a shipped
    /// action was a different entry from everybody else's. Idempotent: a second run writes
    /// the same values. A template a user action also uses keeps its id, because the sync
    /// folder knows it by that id through the user action. EF stores a Guid as upper-case
    /// text, the form written here.
    /// </remarks>
    private static async Task ApplySeedPublicIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        // A database whose tables never got the column has no public id to correct.
        if (!await TableExistsAsync(connection, transaction, ActionsTableName, cancellationToken)
            || !await TableExistsAsync(connection, transaction, CommandTemplatesTableName, cancellationToken)
            || !await ColumnExistsAsync(connection, transaction, ActionsTableName, PublicIdColumnName, cancellationToken)
            || !await ColumnExistsAsync(connection, transaction, CommandTemplatesTableName, PublicIdColumnName, cancellationToken))
        {
            return;
        }

        List<string> actionIds = await ReadIdsAsync(
            connection,
            transaction,
            "SELECT Id FROM Actions WHERE IsUserCreated = 0",
            cancellationToken);
        foreach (string actionId in actionIds)
        {
            await UpdatePublicIdAsync(connection, transaction, ActionsTableName, actionId, SeedPublicIds.ForAction(actionId), cancellationToken);
        }

        List<string> templateIds = await ReadIdsAsync(
            connection,
            transaction,
            "SELECT ct.Id FROM CommandTemplates ct "
            + "WHERE EXISTS (SELECT 1 FROM Actions a "
            + "WHERE (a.WindowsCommandTemplateId = ct.Id OR a.LinuxCommandTemplateId = ct.Id) AND a.IsUserCreated = 0) "
            + "AND NOT EXISTS (SELECT 1 FROM Actions a "
            + "WHERE (a.WindowsCommandTemplateId = ct.Id OR a.LinuxCommandTemplateId = ct.Id) AND a.IsUserCreated = 1)",
            cancellationToken);
        foreach (string templateId in templateIds)
        {
            await UpdatePublicIdAsync(connection, transaction, CommandTemplatesTableName, templateId, SeedPublicIds.ForTemplate(templateId), cancellationToken);
        }

        FileLogger.Info(
            "[TwinShell] Seed public ids written for "
            + actionIds.Count.ToString(CultureInfo.InvariantCulture) + " system action(s) and "
            + templateIds.Count.ToString(CultureInfo.InvariantCulture) + " template(s)");
    }

    /// <summary>
    /// Rewrites every public id in upper case, the text EF Core writes for a Guid and binds
    /// when it looks one up.
    /// </summary>
    /// <remarks>
    /// Step 1 filled the column with lower-case ids for every row that existed before it, and
    /// SQLite compares text case-sensitively: a lookup by public id missed each of those rows,
    /// so an import created a second copy of the entry and a tombstone never reached it.
    /// <para>
    /// That second copy is the twin this step meets: the lower-case original and an upper-case
    /// row with the same id, which the unique index allows because it compares the raw text.
    /// Upper-casing the original would break the index and roll the upgrade back, so the
    /// original gets a new random id instead. Nothing is merged or deleted: both rows stay
    /// visible as before, and the upper-case one, the copy the folder's file matches, keeps the
    /// sync identity.
    /// </para>
    /// <para>
    /// Idempotent: only a row not already in upper case is written. A table or column that
    /// does not exist is left alone.
    /// </para>
    /// </remarks>
    private static async Task ApplyUpperCasePublicIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (string tableName in PublicIdTables)
        {
            if (!await TableExistsAsync(connection, transaction, tableName, cancellationToken)
                || !await ColumnExistsAsync(connection, transaction, tableName, PublicIdColumnName, cancellationToken))
            {
                continue;
            }

            EnsureKnownPublicIdTable(tableName);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "UPDATE " + tableName + " SET " + PublicIdColumnName + " = upper(" + UuidSql + ") "
                + "WHERE " + PublicIdColumnName + " <> upper(" + PublicIdColumnName + ") "
                + "AND EXISTS (SELECT 1 FROM " + tableName + " twin "
                + "WHERE twin." + PublicIdColumnName + " = upper(" + tableName + "." + PublicIdColumnName + "))",
                cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "UPDATE " + tableName + " SET " + PublicIdColumnName + " = upper(" + PublicIdColumnName + ") "
                + "WHERE " + PublicIdColumnName + " <> upper(" + PublicIdColumnName + ")",
                cancellationToken);
        }
    }

    private static async Task<List<string>> ReadIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;

        List<string> ids = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids;
    }

    private static async Task UpdatePublicIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string id,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        EnsureKnownPublicIdTable(tableName);

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE " + tableName + " SET " + PublicIdColumnName + " = @publicId WHERE Id = @id";

        DbParameter publicIdParameter = command.CreateParameter();
        publicIdParameter.ParameterName = "@publicId";
        publicIdParameter.Value = publicId.ToString().ToUpperInvariant();
        command.Parameters.Add(publicIdParameter);

        DbParameter idParameter = command.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = id;
        command.Parameters.Add(idParameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task ApplySeedStateTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        => ExecuteNonQueryAsync(
            connection,
            transaction,
            "CREATE TABLE IF NOT EXISTS \"" + SeedStateConfiguration.TableName + "\" ("
            + "\"Key\" TEXT NOT NULL CONSTRAINT \"PK_" + SeedStateConfiguration.TableName + "\" PRIMARY KEY, "
            + "\"Value\" TEXT NOT NULL)",
            cancellationToken);

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

    private static async Task ApplyD2LotBAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        List<SystemCommandTemplateRow> systemTemplates = await ReadSystemCommandTemplatesForD2LotBAsync(
            connection,
            transaction,
            cancellationToken);

        int updatedCount = 0;

        foreach (SystemCommandTemplateRow template in systemTemplates)
        {
            string commandPattern = template.CommandPattern;
            bool patternChanged = false;
            bool parametersChanged = false;
            List<TemplateParameter>? parameters = DeserializeParameters(template.ParametersJson);

            if (parameters is { Count: > 0 })
            {
                parametersChanged = ApplyD2LotBGenericParameterFixes(commandPattern, parameters);
            }

            patternChanged = ApplyD2LotBTargetedFixes(
                ref commandPattern,
                parameters,
                ref parametersChanged);

            if (!patternChanged && !parametersChanged)
            {
                continue;
            }

            string? parametersJson = parametersChanged && parameters is { Count: > 0 }
                ? JsonSerializer.Serialize(parameters, JsonOptionsHelper.CompactStorage)
                : null;

            await UpdateCommandTemplateAsync(
                connection,
                transaction,
                template.Id,
                patternChanged ? commandPattern : null,
                parametersJson,
                cancellationToken);
            updatedCount++;
        }

        FileLogger.Info(
            "[TwinShell] D2 Lot B migration updated "
            + updatedCount.ToString(CultureInfo.InvariantCulture)
            + " system command template(s)");
    }

    private static bool ApplyD2LotBGenericParameterFixes(
        string commandPattern,
        List<TemplateParameter> parameters)
    {
        bool changed = false;

        foreach (TemplateParameter parameter in parameters)
        {
            // The row carries no platform, so the step asks the lexer under both shells'
            // rules and marks the parameter inline only where they agree it is literal.
            if (parameter.Quoting == null
                && CommandPatternQuoting.IsPlaceholderOnlyInSingleQuotedSpans(commandPattern, parameter.Name, powerShell: false)
                && CommandPatternQuoting.IsPlaceholderOnlyInSingleQuotedSpans(commandPattern, parameter.Name, powerShell: true))
            {
                parameter.Quoting = QuotingMode.InlineInQuotes;
                changed = true;
            }

            if (string.Equals(parameter.Name, "driveLetter", StringComparison.Ordinal)
                && string.Equals(parameter.Type, "string", StringComparison.OrdinalIgnoreCase))
            {
                parameter.Type = "driveletter";
                changed = true;
            }
        }

        return changed;
    }

    private static bool ApplyD2LotBTargetedFixes(
        ref string commandPattern,
        List<TemplateParameter>? parameters,
        ref bool parametersChanged)
    {
        bool patternChanged = false;

        if (string.Equals(
            commandPattern,
            "tar -czf \"{archiveName}.tar.gz\" {sourcePath}",
            StringComparison.Ordinal))
        {
            commandPattern = "tar -czf '{archiveName}.tar.gz' {sourcePath}";
            patternChanged = true;
            parametersChanged |= SetParameterQuoting(parameters, "archiveName");
        }

        if (string.Equals(
            commandPattern,
            "icacls {path} /grant \"{user}:(OI)(CI)F\" /T",
            StringComparison.Ordinal))
        {
            commandPattern = "icacls {path} /grant '{user}:(OI)(CI)F' /T";
            patternChanged = true;
            parametersChanged |= SetParameterQuoting(parameters, "user");
        }

        if (string.Equals(
            commandPattern,
            "Set-MpPreference -MAPSReporting ${level}",
            StringComparison.Ordinal))
        {
            commandPattern = "Set-MpPreference -MAPSReporting {level}";
            patternChanged = true;
            parametersChanged |= SetParameterType(parameters, "level", "string", "number");
        }

        if (string.Equals(
            commandPattern,
            "Set-MpPreference -ScanScheduleDay ${day} -ScanScheduleTime ${time}",
            StringComparison.Ordinal))
        {
            commandPattern = "Set-MpPreference -ScanScheduleDay {day} -ScanScheduleTime {time}";
            patternChanged = true;
            parametersChanged |= SetParameterType(parameters, "day", "string", "number");
        }

        if (string.Equals(
            commandPattern,
            "Set-MpPreference -DisableRealtimeMonitoring ${enableDisable}",
            StringComparison.Ordinal))
        {
            parametersChanged |= SetParameterDefaultValue(parameters, "enableDisable", "$false", "false");
            parametersChanged |= SetParameterQuoting(parameters, "enableDisable");
        }

        return patternChanged;
    }

    private static bool SetParameterQuoting(List<TemplateParameter>? parameters, string parameterName)
    {
        TemplateParameter? parameter = FindParameter(parameters, parameterName);
        if (parameter == null || parameter.Quoting != null)
        {
            return false;
        }

        parameter.Quoting = QuotingMode.InlineInQuotes;
        return true;
    }

    private static bool SetParameterType(
        List<TemplateParameter>? parameters,
        string parameterName,
        string currentType,
        string replacementType)
    {
        TemplateParameter? parameter = FindParameter(parameters, parameterName);
        if (parameter == null
            || !string.Equals(parameter.Type, currentType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        parameter.Type = replacementType;
        return true;
    }

    private static bool SetParameterDefaultValue(
        List<TemplateParameter>? parameters,
        string parameterName,
        string currentDefaultValue,
        string replacementDefaultValue)
    {
        TemplateParameter? parameter = FindParameter(parameters, parameterName);
        if (parameter == null
            || !string.Equals(parameter.DefaultValue, currentDefaultValue, StringComparison.Ordinal))
        {
            return false;
        }

        parameter.DefaultValue = replacementDefaultValue;
        return true;
    }

    private static TemplateParameter? FindParameter(
        List<TemplateParameter>? parameters,
        string parameterName)
        => parameters?.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, parameterName, StringComparison.Ordinal));

    private static List<TemplateParameter>? DeserializeParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return null;
        }

        List<TemplateParameter>? parameters = JsonSerializer.Deserialize<List<TemplateParameter>>(
            parametersJson,
            JsonOptionsHelper.CompactStorage);

        return parameters is { Count: > 0 } ? parameters : null;
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

    private static async Task<List<SystemCommandTemplateRow>> ReadSystemCommandTemplatesForD2LotBAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT ct.Id, ct.CommandPattern, ct.ParametersJson FROM CommandTemplates ct "
            + "WHERE EXISTS (SELECT 1 FROM Actions a "
            + "WHERE (a.WindowsCommandTemplateId = ct.Id OR a.LinuxCommandTemplateId = ct.Id) "
            + "AND a.IsUserCreated = 0)";

        List<SystemCommandTemplateRow> templates = new List<SystemCommandTemplateRow>();

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            string id = reader.GetString(0);
            string pattern = reader.GetString(1);
            string? parametersJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            templates.Add(new SystemCommandTemplateRow(id, pattern, parametersJson));
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

    private static async Task UpdateCommandTemplateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string id,
        string? commandPattern,
        string? parametersJson,
        CancellationToken cancellationToken)
    {
        List<string> assignments = new List<string>();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        if (commandPattern != null)
        {
            assignments.Add("CommandPattern = @pattern");
            DbParameter patternParameter = command.CreateParameter();
            patternParameter.ParameterName = "@pattern";
            patternParameter.Value = commandPattern;
            command.Parameters.Add(patternParameter);
        }

        if (parametersJson != null)
        {
            assignments.Add("ParametersJson = @parametersJson");
            DbParameter parametersJsonParameter = command.CreateParameter();
            parametersJsonParameter.ParameterName = "@parametersJson";
            parametersJsonParameter.Value = parametersJson;
            command.Parameters.Add(parametersJsonParameter);
        }

        if (assignments.Count == 0)
        {
            return;
        }

        command.CommandText =
            "UPDATE CommandTemplates SET " + string.Join(", ", assignments) + " WHERE Id = @id";

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

        bool exists = await ColumnExistsAsync(
            connection,
            transaction,
            tableName,
            PublicIdColumnName,
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

    /// <summary>
    /// Reports whether a column already exists, so a step can be re-run without harm.
    /// </summary>
    /// <remarks>
    /// Generalised from the PublicId-only check when the per-platform example columns needed
    /// the same test: two copies of an existence probe is how one of them ends up guarding a
    /// different table than it names. Both identifiers are checked against
    /// <see cref="KnownColumns"/> before reaching the command text, because
    /// <c>pragma_table_info</c> accepts no parameter for them.
    /// </remarks>
    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        EnsureKnownColumn(tableName, columnName);

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('" + tableName + "') WHERE name = '"
            + columnName + "'";

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        int existingColumnCount = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        return existingColumnCount > 0;
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";

        DbParameter nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "@name";
        nameParameter.Value = tableName;
        command.Parameters.Add(nameParameter);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private static void EnsureKnownColumn(string tableName, string columnName)
    {
        if (!KnownColumns.TryGetValue(tableName, out string[]? columns)
            || !columns.Contains(columnName, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Invalid schema identifier: " + tableName + "." + columnName,
                nameof(columnName));
        }
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

    private sealed record SystemCommandTemplateRow(
        string Id,
        string CommandPattern,
        string? ParametersJson);
}
