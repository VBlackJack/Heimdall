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
/// The selected session tab is outlined on all four sides. Two things broke that on screen
/// (measured 2026-10-22 by reading the pixels of the running build): the tab item's own right
/// margin inside the TabPanel left the template border at the full slot width and clipped its
/// right edge, so the outline stopped on one side; and the selected outline used BorderBrush,
/// which is the card colour on every shipped theme, so no outline showed at all.
/// </summary>
/// <remarks>
/// The gap between tabs now belongs to the border inside the template, the item carries no
/// margin, and the selected outline is BorderStrongBrush.
/// </remarks>
public sealed class SessionTabOutlineMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheTabItemCarriesNoMarginOfItsOwn()
    {
        XElement style = TabItemStyle();

        Assert.Equal("0", SetterValue(style, "Margin"));
    }

    [Fact]
    public void TheTabGapIsTheBorderMargin()
    {
        XElement border = TabBorder();

        Assert.Equal("0,0,4,0", (string?)border.Attribute("Margin"));
    }

    [Fact]
    public void TheSelectedTabIsOutlinedWithTheStrongBorder()
    {
        XElement selected = Assert.Single(
            TabItemStyle().Descendants(),
            e => e.Name.LocalName == "Trigger"
                && (string?)e.Attribute("Property") == "IsSelected"
                && (string?)e.Attribute("Value") == "True");

        XElement brush = Assert.Single(
            selected.Elements(),
            e => e.Name.LocalName == "Setter"
                && (string?)e.Attribute("TargetName") == "TabBorder"
                && (string?)e.Attribute("Property") == "BorderBrush");

        Assert.Equal("{DynamicResource BorderStrongBrush}", (string?)brush.Attribute("Value"));
    }

    private static XElement TabItemStyle() =>
        Assert.Single(Markup().Descendants(), e => (string?)e.Attribute(Xaml + "Key") == "ThemedTabItemStyle");

    private static XElement TabBorder() =>
        Assert.Single(TabItemStyle().Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "TabBorder");

    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));

    private static string? SetterValue(XElement style, string property) =>
        (string?)Assert.Single(
            style.Elements(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
            .Attribute("Value");
}
