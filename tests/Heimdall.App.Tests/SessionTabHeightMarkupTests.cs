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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The session tab strip is as low as its content allows: the close button (a 24 px pointer
/// target) plus the folder bar plus a few pixels of padding. The vertical parts are named
/// resources that add up to the documented tab height, and the strip owns its item padding
/// instead of inheriting the one the server dialog's tabs keep.
/// </summary>
public sealed class SessionTabHeightMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheTabHeightIsTheSumOfItsVerticalParts()
    {
        double topBorder = TemplateBorderThickness(ThemedTabItemStyle(), "TabBorder").Top;
        Thickness item = Pad("SessionTabItemPadding");
        Thickness header = Pad("SessionTabHeaderPadding");
        double closeButton = Number(Assert.Single(
            MainWindow().Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "TabCloseButton"),
            "Height");
        double folderBar = TemplateBorderThickness(
            Assert.Single(MainWindow().Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "TabHeaderBorder"))
            .Bottom;

        double sum = topBorder + item.Top + item.Bottom + header.Top + header.Bottom + closeButton + folderBar;

        Assert.Equal(Token("SessionTabHeight"), sum);
        Assert.Equal(Token("SessionTabPointerTargetSize"), closeButton);
    }

    [Fact]
    public void ThePinnedValuesAreTheNamedOnes()
    {
        Assert.Equal(new Thickness(10, 2, 10, 2), Pad("SessionTabItemPadding"));
        Assert.Equal(new Thickness(8, 3, 8, 3), Pad("SessionTabHeaderPadding"));
        Assert.Equal(37, Token("SessionTabHeight"));
    }

    [Fact]
    public void TheSessionStripDoesNotInheritItsPaddingFromTheThemedTabItem()
    {
        XElement container = Assert.Single(
            SessionTabControl().Descendants(),
            e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "{x:Type TabItem}");

        Assert.Equal("{StaticResource ThemedTabItemStyle}", (string?)container.Attribute("BasedOn"));
        Assert.Equal("{StaticResource SessionTabItemPadding}", SetterValue(container, "Padding"));
        Assert.Equal("{StaticResource SessionTabHeight}", SetterValue(container, "MinHeight"));

        XElement header = Assert.Single(
            SessionTabControl().Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "TabHeaderBorder");
        Assert.Equal("{StaticResource SessionTabHeaderPadding}", (string?)header.Attribute("Padding"));
    }

    [Fact]
    public void TheServerDialogTabsKeepTheThemedTabItemPadding()
    {
        Assert.Equal("14,8", SetterValue(ThemedTabItemStyle(), "Padding"));
    }

    [Fact]
    public void TheGroupChipIsAPointerTargetWithoutGrowingTheTab()
    {
        XElement chip = Assert.Single(
            MainWindow().Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "Mw_SessionTabGroupChip");

        Assert.Equal("{StaticResource SessionTabPointerTargetSize}", (string?)chip.Attribute("MinHeight"));
        Assert.True(Token("SessionTabPointerTargetSize") <= Token("SessionTabHeight")
            - Pad("SessionTabItemPadding").Top - Pad("SessionTabItemPadding").Bottom);
        Assert.Equal("False", (string?)chip.Attribute("Focusable"));
    }

    private readonly record struct Thickness(double Left, double Top, double Right, double Bottom);

    private static Thickness Pad(string key) => Parse(ResourceText(key));

    private static Thickness Parse(string text)
    {
        double[] p = text.Split(',')
            .Select(part => double.Parse(part.Trim(), CultureInfo.InvariantCulture))
            .ToArray();
        return p.Length switch
        {
            1 => new Thickness(p[0], p[0], p[0], p[0]),
            2 => new Thickness(p[0], p[1], p[0], p[1]),
            _ => new Thickness(p[0], p[1], p[2], p[3]),
        };
    }

    private static Thickness TemplateBorderThickness(XElement owner, string? name = null)
    {
        XElement border = name is null
            ? owner
            : Assert.Single(owner.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == name);
        return Parse((string?)border.Attribute("BorderThickness") ?? "0");
    }

    private static double Token(string key) =>
        double.Parse(ResourceText(key), CultureInfo.InvariantCulture);

    private static double Number(XElement element, string attribute) =>
        double.Parse((string?)element.Attribute(attribute) ?? "", CultureInfo.InvariantCulture);

    private static string ResourceText(string key) =>
        Assert.Single(CommonControls().Descendants(), e => (string?)e.Attribute(Xaml + "Key") == key).Value;

    private static XElement ThemedTabItemStyle() =>
        Assert.Single(CommonControls().Descendants(), e => (string?)e.Attribute(Xaml + "Key") == "ThemedTabItemStyle");

    private static XElement SessionTabControl() =>
        Assert.Single(MainWindow().Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "SessionTabControl");

    private static string? SetterValue(XElement style, string property) =>
        (string?)Assert.Single(
            style.Elements(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
            .Attribute("Value");

    private static XDocument CommonControls() =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));

    private static XDocument MainWindow() =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
}
