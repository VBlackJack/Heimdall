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

using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace Heimdall.App.Tests;

/// <summary>
/// Horizontal scrolling is disabled inside the post-connect list, so every fixed-width column
/// sharing a row with the command field is taken straight out of the field the user types in.
/// Seven columns totalling 492 px left it a sliver at the dialog's declared width.
/// </summary>
public sealed class ServerDialogPostConnectLayoutTests
{
    /// <summary>
    /// The enabled checkbox is the only fixed column the command may share its row with.
    /// Stated as a budget rather than as a shape, so a column added later is caught too.
    /// </summary>
    private const double MaxFixedWidthOnCommandRow = 40;

    [Fact]
    public void TheCommandCellSharesItsRowWithNoFixedWidthColumn()
    {
        XElement grid = PostConnectRowGrid();

        List<double?> columnWidths =
        [
            .. grid
                .Elements()
                .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements()
                .Select(definition => ParseFixedWidth(definition.Attribute("Width")?.Value))
        ];

        Assert.True(
            columnWidths.Count >= 2,
            "The row template no longer declares columns, so this guard would measure nothing.");
        Assert.Contains(columnWidths, width => width is null);

        XElement[] children =
        [
            .. grid.Elements().Where(element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal))
        ];

        XElement commandCell = Assert.Single(
            children,
            child => child.DescendantsAndSelf().Any(IsCommandInput));

        string commandRow = RowOf(commandCell);
        double budget = children
            .Where(child => string.Equals(RowOf(child), commandRow, StringComparison.Ordinal))
            .Sum(child => FixedWidthOf(child, columnWidths));

        Assert.True(
            budget <= MaxFixedWidthOnCommandRow,
            $"The command field shares its row with {budget} px of fixed-width columns, above the "
            + $"{MaxFixedWidthOnCommandRow} px budget. Everything but the enabled checkbox belongs "
            + "on the second row, because horizontal scrolling here is disabled and nothing brings "
            + "an overflowing row back into view.");
    }

    // The premise of the budget above: with scrolling enabled a wide row would simply scroll,
    // and the star column would stop being the thing that protects the command field.
    //
    // This method used to open on HorizontalContentAlignment="Stretch" as well. That assertion
    // was dropped: the desktop measurement cleared its threshold with the attribute removed,
    // so no mutant failed it for a reason anyone can feel, and it would have broken a later
    // change that dropped the attribute for a good reason. The user-visible outcome is owned by
    // TheCommandFieldIsUsableAtTheDialogsDefaultWidth in
    // tests/Heimdall.App.UiTests/Dialogs/ServerDialogPostConnectLayoutTests.cs, which measures
    // the command field at the dialog's declared width against a 240 px floor.
    [Fact]
    public void TheStepsListRefusesHorizontalScrolling()
    {
        XElement list = PostConnectStepsList();

        Assert.Equal(
            "Disabled",
            list.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value);
    }

    private static bool IsCommandInput(XElement element)
        => element.Name.LocalName == "TextBox"
           && string.Equals(
               element.Attribute("Text")?.Value,
               "{Binding Input, UpdateSourceTrigger=PropertyChanged}",
               StringComparison.Ordinal);

    private static string RowOf(XElement element)
        => element.Attribute("Grid.Row")?.Value ?? "0";

    private static double FixedWidthOf(XElement element, IReadOnlyList<double?> columnWidths)
    {
        int column = ParseIndex(element.Attribute("Grid.Column")?.Value, 0);
        int span = ParseIndex(element.Attribute("Grid.ColumnSpan")?.Value, 1);

        double total = 0;
        for (int index = column; index < column + span && index < columnWidths.Count; index++)
        {
            total += columnWidths[index] ?? 0;
        }

        return total;
    }

    private static int ParseIndex(string? raw, int fallback)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static double? ParseFixedWidth(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private static XElement PostConnectStepsList()
    {
        XDocument document = LoadServerDialogXaml();

        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ListView"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "DlgSrv_PostConnectStepsList"));
    }

    private static XElement PostConnectRowGrid()
    {
        XElement template = Assert.Single(
            PostConnectStepsList().Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "PostConnectStepItemViewModel",
                    StringComparison.Ordinal) == true);

        return Assert.Single(template.Elements(), element => element.Name.LocalName == "Grid");
    }

    private static XDocument LoadServerDialogXaml()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml");

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
    }
}
