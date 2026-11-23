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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The CSV and JSON readers the external tool wrapper and the recipe view share. The cases are
/// the behaviour the wrapper had before the code moved, kept so that the move changes nothing.
/// </summary>
public sealed class TabularOutputTests
{
    [Fact]
    public void CsvHeadersBecomeColumnsAndQuotedFieldsKeepTheirCommasAndQuotes()
    {
        TabularOutput output = TabularOutput.FromCsv("Name,Note\r\n\"Smith, J\",\"said \"\"hi\"\"\"\r\nplain,ok\r\n");

        DataTable table = Assert.IsType<DataTable>(output.Table);
        Assert.Equal(["Name", "Note"], table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Smith, J", table.Rows[0][0]);
        Assert.Equal("said \"hi\"", table.Rows[0][1]);
        Assert.True(output.IsTable);
    }

    [Fact]
    public void CsvBlankAndDuplicateHeadersGetUniqueNamesAndBlankLinesAreSkipped()
    {
        TabularOutput output = TabularOutput.FromCsv("a,a,\r\n1,2,3\r\n\r\n4,5,6\r\n");

        DataTable table = Assert.IsType<DataTable>(output.Table);
        Assert.Equal(["a", "a_1", "Column"], table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void ACsvRowWithMoreFieldsThanHeadersIsCutAtTheHeaders()
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromCsv("a,b\r\n1,2,3,4\r\n").Table);

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("2", table.Rows[0][1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("header,only\r\n")]
    public void CsvWithoutRowsFallsBackToTheTextAsItCame(string csv)
    {
        TabularOutput output = TabularOutput.FromCsv(csv);

        Assert.Null(output.Table);
        Assert.False(output.IsTable);
        Assert.Equal(csv, output.Text);
    }

    [Fact]
    public void NullIsEmptyText()
    {
        Assert.Equal(string.Empty, TabularOutput.FromCsv(null).Text);
        Assert.Equal(string.Empty, TabularOutput.FromJson(null).Text);
    }

    [Fact]
    public void AJsonArrayOfObjectsBecomesRowsMatchedByPropertyName()
    {
        TabularOutput output = TabularOutput.FromJson("""[{"a":1,"b":"x"},{"b":"y","a":2},{"a":3}]""");

        DataTable table = Assert.IsType<DataTable>(output.Table);
        Assert.Equal(["a", "b"], table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal("2", table.Rows[1]["a"]);
        Assert.Equal("y", table.Rows[1]["b"]);
        Assert.Equal(DBNull.Value, table.Rows[2]["b"]);
    }

    [Fact]
    public void APropertyFirstSeenInALaterObjectAddsAColumn()
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromJson("""[{"a":1},{"a":2,"late":"z"}]""").Table);

        Assert.Equal(["a", "late"], table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal("z", table.Rows[1]["late"]);
    }

    [Fact]
    public void ElementsThatAreNotObjectsAreSkipped()
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromJson("""[1,{"a":"kept"},"text"]""").Table);

        Assert.Single(table.Rows);
        Assert.Equal("kept", table.Rows[0][0]);
    }

    [Fact]
    public void AJsonValueThatIsNotAnArrayIsWrittenOutWithIndentation()
    {
        TabularOutput output = TabularOutput.FromJson("""{"a":1}""");

        Assert.Null(output.Table);
        Assert.Contains("\"a\": 1", output.Text, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, output.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void JsonThatYieldsNoRowsFallsBackToTheTextAsItCame(string json)
    {
        TabularOutput output = TabularOutput.FromJson(json);

        Assert.Null(output.Table);
        Assert.Equal(json, output.Text);
    }

    [Fact]
    public void TheClipboardTextHasATabSeparatedHeaderAndRows()
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromCsv("a,b\r\n1,2\r\n").Table);

        string text = TabularOutput.ToTabSeparated(table);

        Assert.Equal("a\tb" + Environment.NewLine + "1\t2" + Environment.NewLine, text);
    }
}
