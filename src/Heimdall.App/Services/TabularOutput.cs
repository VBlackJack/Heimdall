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
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Recipes;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// The output of a program read as a table when it is CSV or a JSON array of objects, and as text
/// otherwise. Shared by every view that shows program output in a grid, so the two readers exist
/// once. A text that does not parse falls back to text and never throws.
/// </summary>
public sealed class TabularOutput
{
    private const string ColumnFallbackName = "Column";

    private const char CommaSeparator = ',';

    private const char SemicolonSeparator = ';';

    private const char Quote = '"';

    private const char CellSeparator = '\t';

    private TabularOutput(DataTable? table, string text)
    {
        Table = table;
        Text = text;
    }

    /// <summary>The table, or null when the output is to be shown as text.</summary>
    public DataTable? Table { get; }

    /// <summary>
    /// The text to show when <see cref="Table"/> is null: the output as it came, or a JSON value
    /// that is not an array written out with indentation. Not trimmed.
    /// </summary>
    public string Text { get; }

    /// <summary>True when the output reads as a table with at least one row.</summary>
    public bool IsTable => Table is not null;

    /// <summary>Reads separated values: the first row names the columns.</summary>
    /// <param name="csv">The output text.</param>
    /// <param name="separator">The character between two cells; the same quoting rules apply to each.</param>
    public static TabularOutput FromCsv(string? csv, RecipeCsvSeparator separator = RecipeCsvSeparator.Comma)
    {
        string text = csv ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return AsText(text);
        }

        try
        {
            DataTable table = ParseCsv(text, SeparatorChar(separator));
            return table.Rows.Count == 0 ? AsText(text) : new TabularOutput(table, text);
        }
        catch (Exception ex) when (ex is DataException or ArgumentException or IOException)
        {
            // Fallback to text if CSV parsing fails
            return AsText(text);
        }
    }

    /// <summary>
    /// Reads a JSON array of objects: a property is matched by its name, not by its position, so
    /// a missing or reordered field does not shift a cell. Any other JSON value is shown as text.
    /// </summary>
    /// <param name="json">The output text.</param>
    public static TabularOutput FromJson(string? json)
    {
        string text = json ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return AsText(text);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                // Pretty-print non-array JSON
                string pretty = JsonSerializer.Serialize(
                    doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                return AsText(pretty);
            }

            DataTable table = BuildJsonTable(doc.RootElement);
            return table.Rows.Count == 0 ? AsText(text) : new TabularOutput(table, text);
        }
        catch (Exception ex) when (ex is JsonException or DataException or ArgumentException or InvalidOperationException)
        {
            return AsText(text);
        }
    }

    /// <summary>
    /// One cell as it is copied: a tab, a carriage return or a line feed inside it becomes a single space, so it
    /// stays one cell of one row, and a value that a spreadsheet would read as a formula gets a leading apostrophe.
    /// </summary>
    /// <param name="value">The text of a header or a cell, or null.</param>
    public static string SanitizeCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string flat = value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        // A paste into a spreadsheet may read a leading double quote as a text qualifier, strip it and
        // evaluate what is left. The shared CSV rule is not changed: CSV exporters quote their own fields.
        return flat[0] == '"' ? "'" + flat : InputValidator.SanitizeCsvCell(flat);
    }

    /// <summary>The table as tab separated text with a header line, for the clipboard.</summary>
    /// <param name="table">The table to write out.</param>
    public static string ToTabSeparated(DataTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        StringBuilder sb = new();

        // Headers
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(CellSeparator);
            }

            sb.Append(SanitizeCell(table.Columns[i].ColumnName));
        }

        sb.AppendLine();

        // Rows
        foreach (DataRow row in table.Rows)
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(CellSeparator);
                }

                sb.Append(SanitizeCell(row[i] is DBNull ? null : Convert.ToString(row[i], CultureInfo.CurrentCulture)));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static TabularOutput AsText(string text) => new(null, text);

    private static DataTable BuildJsonTable(JsonElement array)
    {
        DataTable table = new();
        bool firstRow = true;

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (firstRow)
            {
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    string colName = prop.Name;
                    string uniqueName = colName;
                    int suffix = 1;
                    while (table.Columns.Contains(uniqueName))
                    {
                        uniqueName = $"{colName}_{suffix++}";
                    }

                    table.Columns.Add(uniqueName);
                }

                firstRow = false;
            }

            DataRow row = table.NewRow();
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                // Match by property name, not position - handles missing/reordered fields
                if (table.Columns.Contains(prop.Name))
                {
                    row[prop.Name] = prop.Value.ToString();
                }
                else if (!firstRow)
                {
                    // Late-discovered column: add it and backfill with empty
                    table.Columns.Add(prop.Name);
                    row[prop.Name] = prop.Value.ToString();
                }
            }

            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>
    /// Simple RFC 4180-compatible CSV parser. Handles quoted fields with embedded commas and
    /// double-quotes. First row = headers, which become the column names.
    /// </summary>
    private static DataTable ParseCsv(string csv, char separator)
    {
        DataTable table = new();
        using StringReader reader = new(csv);
        string? headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            return table;
        }

        foreach (string header in ParseCsvLine(headerLine, separator))
        {
            string colName = header.Trim();

            // DataTable requires unique column names
            string baseName = string.IsNullOrWhiteSpace(colName) ? ColumnFallbackName : colName;
            string uniqueName = baseName;
            int suffix = 1;
            while (table.Columns.Contains(uniqueName))
            {
                uniqueName = $"{baseName}_{suffix++}";
            }

            table.Columns.Add(uniqueName);
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            List<string> fields = ParseCsvLine(line, separator);
            DataRow row = table.NewRow();
            for (int i = 0; i < Math.Min(fields.Count, table.Columns.Count); i++)
            {
                row[i] = fields[i];
            }

            table.Rows.Add(row);
        }

        return table;
    }

    private static char SeparatorChar(RecipeCsvSeparator separator) => separator switch
    {
        RecipeCsvSeparator.Comma => CommaSeparator,
        RecipeCsvSeparator.Semicolon => SemicolonSeparator,
        RecipeCsvSeparator.Tab => CellSeparator,
        _ => throw new ArgumentOutOfRangeException(nameof(separator), separator, "A separator without a character."),
    };

    private static List<string> ParseCsvLine(string line, char separator)
    {
        List<string> fields = [];
        StringBuilder current = new();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == Quote)
                {
                    if (i + 1 < line.Length && line[i + 1] == Quote)
                    {
                        current.Append(Quote);
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == Quote)
            {
                inQuotes = true;
            }
            else if (c == separator)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
