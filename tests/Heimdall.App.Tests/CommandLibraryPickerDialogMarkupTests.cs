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

using System.IO;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the layout that keeps the picker's result rows inside the dialog.
/// </summary>
/// <remarks>
/// The list let its rows scroll sideways, so it measured them at an infinite width: the star
/// column then sized to its content like an Auto column, and a long description pushed the
/// category and the badges past the window behind a horizontal scrollbar. The fix is in the
/// markup alone, so this file reads the markup. It does not render the dialog.
/// </remarks>
public sealed class CommandLibraryPickerDialogMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private const string ResultsListName = "{loc:Translate LabelCommandLibraryPickerResults}";

    private const string AccessibilityBehaviorProperty = "behaviors:ItemContainerAccessibilityBehavior.IsEnabled";

    [Fact]
    public void TheResultsList_DisablesHorizontalScrolling()
    {
        XElement list = ResultsList();

        Assert.Equal("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
    }

    /// <summary>
    /// Without a stretched container the row is arranged at its own desired width, and the
    /// badges no longer sit at the right edge of the list.
    /// </summary>
    [Fact]
    public void TheResultsList_StretchesItsRows()
    {
        XElement list = ResultsList();

        Assert.Equal("Stretch", (string?)list.Attribute("HorizontalContentAlignment"));
    }

    /// <summary>
    /// The title and description take what the category and badges leave; every other column
    /// sizes to its content.
    /// </summary>
    [Fact]
    public void TheRow_GivesTheDescriptionTheOnlyStarColumn()
    {
        List<string?> widths = RowColumns()
            .Select(column => (string?)column.Attribute("Width"))
            .ToList();

        Assert.Equal(["*", "Auto", "Auto"], widths);
    }

    [Fact]
    public void TheRow_TrimsTheTitleAndTheDescription()
    {
        XElement row = RowGrid();

        List<string?> trimming = row
            .Element(Presentation + "StackPanel")!
            .Elements(Presentation + "TextBlock")
            .Select(text => (string?)text.Attribute("TextTrimming"))
            .ToList();

        Assert.Equal(["CharacterEllipsis", "CharacterEllipsis"], trimming);
    }

    /// <summary>
    /// UI Automation reads the generated container, never the template, so the row's name is
    /// applied there by the behaviour from the item's own accessible name.
    /// </summary>
    [Fact]
    public void TheRows_AreNamedOnTheirContainer()
    {
        XElement style = Assert.Single(
            ResultsList().Element(Presentation + "ListView.ItemContainerStyle")!.Elements(Presentation + "Style"));

        XElement setter = Assert.Single(
            style.Elements(Presentation + "Setter"),
            candidate => (string?)candidate.Attribute("Property") == AccessibilityBehaviorProperty);

        Assert.Equal("True", (string?)setter.Attribute("Value"));
    }

    /// <summary>
    /// A name on the template's grid is inert, and leaving it there reads as though the row
    /// were named.
    /// </summary>
    [Fact]
    public void TheRowGrid_CarriesNoInertName()
    {
        Assert.Null(RowGrid().Attribute("AutomationProperties.Name"));
    }

    /// <summary>
    /// Positive control for the tests above: a renamed list would make them fail for the wrong
    /// reason, and a missing template would read as a repaired layout.
    /// </summary>
    [Fact]
    public void TheResultsList_IsAListViewWithARowGrid()
    {
        Assert.Equal("ListView", ResultsList().Name.LocalName);
        Assert.Equal(3, RowColumns().Count);
    }

    private static List<XElement> RowColumns()
        => RowGrid()
            .Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .ToList();

    private static XElement RowGrid()
    {
        XElement template = Assert.Single(ResultsList().Elements(Presentation + "ListView.ItemTemplate"));
        XElement dataTemplate = Assert.Single(template.Elements(Presentation + "DataTemplate"));
        return Assert.Single(dataTemplate.Elements(Presentation + "Grid"));
    }

    private static XElement ResultsList()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(),
            "src", "Heimdall.App", "Views", "Dialogs", "CommandLibraryPickerDialog.xaml");

        Assert.True(File.Exists(path), $"View not found: {path}");

        List<XElement> matches = XDocument.Load(path)
            .Descendants()
            .Where(element => string.Equals(
                (string?)element.Attribute("AutomationProperties.Name"), ResultsListName, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(matches);
    }
}
