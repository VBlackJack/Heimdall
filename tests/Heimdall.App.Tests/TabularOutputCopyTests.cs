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
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.Tests;

/// <summary>A table copied from a tool never carries a formula a spreadsheet would run, nor a cell that spills over a row.</summary>
public sealed class TabularOutputCopyTests
{
    private static string Copy(string header, params object?[] cells)
    {
        DataTable table = new();
        table.Columns.Add(header);
        table.Rows.Add(cells[0] ?? DBNull.Value);
        return TabularOutput.ToTabSeparated(table);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("plain", "plain")]
    [InlineData("a=b", "a=b")]
    public void ACellThatStartsLikeAFormula_GetsALeadingApostrophe(string cell, string expected)
    {
        string[] lines = Copy("Name", cell).Split(Environment.NewLine);

        Assert.Equal("Name", lines[0]);
        Assert.Equal(expected, lines[1]);
    }

    [Fact]
    public void AHeaderThatStartsWithAnEqualSign_IsNeutralizedToo()
        => Assert.Equal("'=cmd", Copy("=cmd", "x").Split(Environment.NewLine)[0]);

    [Fact]
    public void ATabInsideACell_DoesNotMakeAnotherCell_AndTheFormulaItHidesIsNeutralizedWhereItStarts()
    {
        DataTable table = new();
        table.Columns.Add("A");
        table.Columns.Add("B");
        table.Rows.Add("a\t=HYPERLINK(\"http://x\")", "=2");

        string row = TabularOutput.ToTabSeparated(table).Split(Environment.NewLine)[1];

        string[] cells = row.Split('\t');
        Assert.Equal(2, cells.Length);
        Assert.Equal("a =HYPERLINK(\"http://x\")", cells[0]);
        Assert.Equal("'=2", cells[1]);
    }

    [Fact]
    public void ACellThatStartsWithATab_IsFlattenedBeforeItIsJudged()
    {
        string row = Copy("A", "\t=1+1").Split(Environment.NewLine)[1];

        Assert.Equal(" =1+1", row);
    }

    [Theory]
    [InlineData("one\r\ntwo", "one  two")]
    [InlineData("one\ntwo", "one two")]
    [InlineData("one\rtwo", "one two")]
    public void ALineBreakInsideACell_BecomesASpace_AndTheRowStaysOneLine(string cell, string expected)
    {
        string[] lines = Copy("A", cell).Split(Environment.NewLine);

        Assert.Equal(2, lines.Length - (lines[^1].Length == 0 ? 1 : 0));
        Assert.Equal(expected, lines[1]);
    }

    [Fact]
    public void ANullCell_IsAnEmptyCell()
    {
        DataTable table = new();
        table.Columns.Add("A");
        table.Columns.Add("B");
        table.Rows.Add(DBNull.Value, "x");

        Assert.Equal("\tx", TabularOutput.ToTabSeparated(table).Split(Environment.NewLine)[1]);
    }

    [Fact]
    public void TheSharedHelperAnswersTheSameForNullAndEmpty()
    {
        Assert.Equal(string.Empty, TabularOutput.SanitizeCell(null));
        Assert.Equal(string.Empty, TabularOutput.SanitizeCell(string.Empty));
    }

    [Fact]
    public void TheGridsOwnCopy_IsNeutralizedWithTheSameRule()
    {
        DataGridColumn column = new DataGridTextColumn();
        List<DataGridClipboardCellContent> row =
        [
            new(item: "row", column, "=HYPERLINK(1)"),
            new(item: "row", column, "a\tb"),
            new(item: "row", column, null),
            new(item: "row", column, -5),
        ];

        TabularGridCopy.Sanitize(row);

        Assert.Equal(["'=HYPERLINK(1)", "a b", string.Empty, "'-5"], row.Select(cell => cell.Content));
    }
}
