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
/// The folders' own row sits above the session tab strip, in a row of the same host, so the strip
/// and its list button keep sharing one row and the content area keeps the rest.
/// </summary>
public sealed class SessionTabGroupRowMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheHeaderRowIsAboveTheStrip_AndTheContentIsBelowIt()
    {
        XDocument doc = MainWindow();
        XElement host = Named(doc, "SessionDragDropHost");
        List<XElement> rows = host.Elements()
            .Single(e => e.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Auto", (string?)rows[0].Attribute("Height"));
        Assert.Equal("Auto", (string?)rows[1].Attribute("Height"));
        Assert.Equal("*", (string?)rows[2].Attribute("Height"));
        Assert.Equal("0", GridRow(Named(doc, "Mw_SessionTabGroupRow")));
        Assert.Equal("1", GridRow(Named(doc, "SessionTabControl")));
        Assert.Equal("2", GridRow(Named(doc, "SessionContentHost")));
        Assert.All(
            new[] { "Mw_SessionTabGroupRow", "SessionTabControl", "SessionContentHost" },
            name => Assert.Same(host, Named(doc, name).Parent));
    }

    [Fact]
    public void TheListButtonSharesTheStripsRow()
    {
        XDocument doc = MainWindow();
        XElement button = Named(doc, "Mw_SessionTabListButton");
        XElement panel = Assert.IsType<XElement>(button.Parent);

        Assert.Same(Named(doc, "SessionDragDropHost"), panel.Parent);
        Assert.Equal(GridRow(Named(doc, "SessionTabControl")), GridRow(panel));
    }

    [Fact]
    public void TheHeaderRowIsOneTabStopWithArrowNavigation_AndIsNamed()
    {
        XElement row = Named(MainWindow(), "Mw_SessionTabGroupRow");

        Assert.Equal("False", (string?)row.Attribute("Focusable"));
        Assert.Equal("Once", (string?)row.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal("Cycle", (string?)row.Attribute("KeyboardNavigation.DirectionalNavigation"));
        Assert.False(string.IsNullOrWhiteSpace((string?)row.Attribute("AutomationProperties.Name")));
    }

    [Fact]
    public void TheHeaderButtonIsAPointerTargetBoundToTheShowCommand()
    {
        XElement button = Named(MainWindow(), "Mw_SessionTabGroupHeader");

        Assert.Equal("{StaticResource SessionTabPointerTargetSize}", (string?)button.Attribute("MinHeight"));
        Assert.Null(button.Attribute("Width"));
        Assert.Null(button.Attribute("Height"));
        Assert.Null(button.Attribute("Focusable"));
        Assert.StartsWith(
            "{Binding DataContext.Connection.ShowTabGroupCommand",
            (string?)button.Attribute("Command"),
            StringComparison.Ordinal);
        Assert.Equal("{Binding Key}", (string?)button.Attribute("CommandParameter"));
        XElement style = Assert.Single(
            button.Descendants(), e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "Button");
        XElement name = Assert.Single(
            style.Elements(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "AutomationProperties.Name");
        Assert.Equal("{Binding AccessibleName}", (string?)name.Attribute("Value"));
    }

    [Fact]
    public void TheShownHeaderHasATintedSurfaceAndABoldLabel()
    {
        XElement button = Named(MainWindow(), "Mw_SessionTabGroupHeader");

        XElement buttonTrigger = ShownTrigger(button, "Button");
        Assert.Equal("{DynamicResource AccentHoverFillBrush}", SetterValue(buttonTrigger, "Background"));
        Assert.Equal("{DynamicResource AccentBorderBrush}", SetterValue(buttonTrigger, "BorderBrush"));

        XElement labelTrigger = ShownTrigger(button, "TextBlock", "FontWeight");
        Assert.Equal("SemiBold", SetterValue(labelTrigger, "FontWeight"));
        Assert.Equal("{DynamicResource TextOnAccentBrush}", SetterValue(labelTrigger, "Foreground"));
        XElement labelStyle = Assert.IsType<XElement>(labelTrigger.Parent?.Parent);
        Assert.Equal("Normal", SetterValue(labelStyle, "FontWeight"));
        Assert.Equal(
            "{StaticResource AppFocusVisualStyle}",
            SetterValue(ShownTrigger(button, "Button").Parent!.Parent!, "FocusVisualStyle"));
    }

    [Fact]
    public void TheAttentionMarksAreTheStripsOwn_EachBoundToItsState()
    {
        XDocument doc = MainWindow();
        XElement failed = Named(doc, "Mw_SessionTabGroupHeaderFailed");
        XElement ring = Named(doc, "Mw_SessionTabGroupHeaderDisconnected");

        Assert.StartsWith("{Binding HasFailedTab", (string?)failed.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.Equal("{DynamicResource ErrorTextBrush}", (string?)failed.Attribute("Foreground"));
        Assert.StartsWith("{Binding HasDisconnectedTab", (string?)ring.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.Equal("{DynamicResource TextSecondaryBrush}", (string?)ring.Attribute("Stroke"));
        Assert.Equal(
            (string?)Named(doc, "TabStateDroppedRing").Attribute("StrokeThickness"),
            (string?)ring.Attribute("StrokeThickness"));
    }

    [Fact]
    public void TheStripClosesUpOnTheHeaderRow_OnlyWhileItIsVisible()
    {
        XDocument doc = MainWindow();
        XElement strip = Named(doc, "SessionTabControl");
        XElement style = Assert.Single(
            strip.Elements().Single(e => e.Name.LocalName == "TabControl.Style").Elements(),
            e => e.Name.LocalName == "Style");
        Assert.Equal("{StaticResource HeaderOnlyTabControlStyle}", (string?)style.Attribute("BasedOn"));
        Assert.Null(strip.Attribute("Style"));
        Assert.Equal("{StaticResource SessionTabStripPaddingUnderGroupRow}", ShowRowSetter(style, "Padding"));

        XElement panel = Assert.IsType<XElement>(Named(doc, "Mw_SessionTabListButton").Parent);
        Assert.Null(panel.Attribute("Margin"));
        XElement panelStyle = Assert.Single(
            panel.Elements().Single(e => e.Name.LocalName == "StackPanel.Style").Elements(),
            e => e.Name.LocalName == "Style");
        Assert.Equal("{StaticResource SessionTabListButtonRowMargin}", SetterValue(panelStyle, "Margin"));
        Assert.Equal("{StaticResource SessionTabListButtonRowMarginUnderGroupRow}", ShowRowSetter(panelStyle, "Margin"));

        XDocument common = XDocument.Load(
            Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));
        string Resource(string key) => Assert.Single(
            common.Descendants(), e => (string?)e.Attribute(Xaml + "Key") == key).Value;
        Assert.Equal("8,8,8,0", Resource("SessionTabStripPadding"));
        Assert.Equal("8,2,8,0", Resource("SessionTabStripPaddingUnderGroupRow"));
        Assert.Equal("0,8,1,1", Resource("SessionTabListButtonRowMargin"));
        Assert.Equal("0,2,1,1", Resource("SessionTabListButtonRowMarginUnderGroupRow"));
    }

    private static string? ShowRowSetter(XElement style, string property)
    {
        XElement trigger = Assert.Single(
            style.Descendants(),
            e => e.Name.LocalName == "DataTrigger"
                && (string?)e.Attribute("Binding") == "{Binding Connection.ShowTabGroupRow}");
        return (string?)Assert.Single(
            trigger.Elements(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
            .Attribute("Value");
    }

    private static XElement ShownTrigger(XElement button, string targetType, string? stylesProperty = null)
    {
        XElement style = Assert.Single(
            button.Descendants(),
            e => e.Name.LocalName == "Style"
                && (string?)e.Attribute("TargetType") == targetType
                && (stylesProperty is null || e.Descendants().Any(
                    s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == stylesProperty)));
        return Assert.Single(
            style.Descendants(),
            e => e.Name.LocalName == "DataTrigger" && (string?)e.Attribute("Binding") == "{Binding IsShown}");
    }

    private static string? SetterValue(XElement owner, string property) =>
        (string?)Assert.Single(
            owner.Descendants(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property
                && (e.Parent == owner || e.Parent?.Parent == owner))
            .Attribute("Value");

    [Fact]
    public void TheChipIsDrawnOnlyWhenTheTabShowsIt_AndStaysOutOfTheTabOrder()
    {
        XElement chip = Named(MainWindow(), "Mw_SessionTabGroupChip");

        Assert.StartsWith(
            "{Binding ShowsTabGroupChip",
            (string?)chip.Attribute("Visibility"),
            StringComparison.Ordinal);
        Assert.Equal("False", (string?)chip.Attribute("Focusable"));
    }

    [Fact]
    public void TheHeaderSpacingIsNamedResources()
    {
        XDocument common = XDocument.Load(
            Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));
        string[] keys =
        [
            "SessionTabGroupDotSize",
            "SessionTabGroupRowPadding",
            "SessionTabGroupHeaderMargin",
            "SessionTabGroupHeaderPadding",
            "SessionTabGroupHeaderDotMargin",
            "SessionTabGroupHeaderCountMargin",
        ];

        Assert.All(keys, key =>
            Assert.Single(common.Descendants(), e => (string?)e.Attribute(Xaml + "Key") == key));
    }

    private static string? GridRow(XElement element) => (string?)element.Attribute("Grid.Row");

    private static XElement Named(XDocument document, string name) =>
        Assert.Single(document.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == name);

    private static XDocument MainWindow() =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
}
