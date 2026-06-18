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
            ApplyUnwrapDoubleQuotedPlaceholdersAsync),
        new SchemaStep(
            3,
            "D2 Lot B distribute quoting modes, drive-letter type, unwrapped affixes and Defender fixes to system command templates",
            ApplyD2LotBAsync)
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
            if (parameter.Quoting == null && IsPlaceholderInsideSingleQuotedSpan(commandPattern, parameter.Name))
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

    private static bool IsPlaceholderInsideSingleQuotedSpan(string commandPattern, string parameterName)
    {
        string placeholder = "{" + parameterName + "}";
        int placeholderIndex = commandPattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            return false;
        }

        int quoteCount = 0;
        for (int index = 0; index < placeholderIndex; index++)
        {
            if (commandPattern[index] == '\'')
            {
                quoteCount++;
            }
        }

        return quoteCount % 2 == 1;
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

    private sealed record SystemCommandTemplateRow(
        string Id,
        string CommandPattern,
        string? ParametersJson);
}
