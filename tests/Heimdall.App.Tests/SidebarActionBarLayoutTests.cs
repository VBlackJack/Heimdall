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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class SidebarActionBarLayoutTests
{
    private const double ExpectedActionBarWidth = 243d;

    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SidebarActionBar_StructuralWidth_IncludesControlsMarginsAndBorder()
    {
        XDocument document = LoadMainWindowXaml();

        double requiredWidth = CalculateActionBarRequiredWidth(document);

        Assert.Equal(ExpectedActionBarWidth, requiredWidth);
    }

    [Fact]
    public void SidebarColumn_MinimumWidth_MatchesActionBarRequirement()
    {
        XDocument document = LoadMainWindowXaml();
        double requiredWidth = CalculateActionBarRequiredWidth(document);
        XElement sidebarColumn = FindNamedElement(document, "SessionTreeColumn");

        Assert.Equal(requiredWidth, ReadDoubleAttribute(sidebarColumn, "MinWidth"));
    }

    [Fact]
    public void SidebarState_MinimumWidth_MatchesActionBarRequirement()
    {
        XDocument document = LoadMainWindowXaml();
        double requiredWidth = CalculateActionBarRequiredWidth(document);

        Assert.Equal(WindowUIState.MinSidebarWidth, requiredWidth);
    }

    [Fact]
    public void SidebarState_WidthBelowActionBarMinimum_ClampsToRequirement()
    {
        XDocument document = LoadMainWindowXaml();
        double requiredWidth = CalculateActionBarRequiredWidth(document);
        WindowUIState state = new()
        {
            SavedSidebarWidth = requiredWidth - 1d
        };

        SidebarLayoutProjection projection = state.SidebarLayout;

        Assert.Equal(requiredWidth, projection.Width);
    }

    private static double CalculateActionBarRequiredWidth(XDocument document)
    {
        XElement filterButton = FindNamedElement(document, "FilterButton");
        XElement addButton = FindNamedElement(document, "AddButton");
        XElement overflowButton = FindNamedElement(document, "SidebarOverflowButton");
        XElement actionGrid = filterButton.Ancestors(PresentationNamespace + "Grid")
            .First(element => element.Descendants().Contains(addButton)
                && element.Descendants().Contains(overflowButton));
        XElement columnDefinitions = Assert.Single(
            actionGrid.Elements(PresentationNamespace + "Grid.ColumnDefinitions"));
        XElement filterColumn = columnDefinitions
            .Elements(PresentationNamespace + "ColumnDefinition")
            .First();
        XElement actionBarContainer = Assert.IsType<XElement>(actionGrid.Parent);
        XElement sidebarBorder = FindNamedElement(document, "TreeViewPanel");

        double filterMinimum = ReadDoubleAttribute(filterColumn, "MinWidth");
        double buttonWidths = ReadWidthWithHorizontalMargin(filterButton)
            + ReadWidthWithHorizontalMargin(addButton)
            + ReadWidthWithHorizontalMargin(overflowButton);
        double containerMargins = ReadHorizontalThickness(actionBarContainer, "Margin");
        double borderThickness = ReadHorizontalThickness(sidebarBorder, "BorderThickness");

        return filterMinimum + buttonWidths + containerMargins + borderThickness;
    }

    private static double ReadWidthWithHorizontalMargin(XElement element)
    {
        return ReadDoubleAttribute(element, "Width")
            + ReadHorizontalThickness(element, "Margin");
    }

    private static double ReadHorizontalThickness(XElement element, string attributeName)
    {
        string value = Assert.IsType<XAttribute>(element.Attribute(attributeName)).Value;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        double[] values = parts
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();

        return values.Length switch
        {
            1 => values[0] * 2d,
            2 => values[0] * 2d,
            4 => values[0] + values[2],
            _ => throw new InvalidOperationException(
                $"Unsupported thickness '{value}' on '{attributeName}'.")
        };
    }

    private static double ReadDoubleAttribute(XElement element, string attributeName)
    {
        string value = Assert.IsType<XAttribute>(element.Attribute(attributeName)).Value;
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static XElement FindNamedElement(XDocument document, string elementName)
    {
        return Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(XamlNamespace + "Name"),
                elementName,
                StringComparison.Ordinal));
    }

    private static XDocument LoadMainWindowXaml()
    {
        string repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(repositoryRoot, "src", "Heimdall.App", "MainWindow.xaml");

        Assert.True(File.Exists(path), $"Main window XAML not found: {path}");
        return XDocument.Load(path);
    }
}
