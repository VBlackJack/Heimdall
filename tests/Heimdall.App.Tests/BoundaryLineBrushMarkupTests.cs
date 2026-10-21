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
/// A line that carries a boundary is drawn with BorderStrongBrush, the border colour the theme
/// service derives to reach 3 to 1 on the background and on a card (WCAG 1.4.11). The theme's own
/// BorderBrush is the card colour on every shipped theme: on Parchment there was no visible divider
/// between the session sidebar and the content, nor under a grid header.
/// </summary>
/// <remarks>
/// The markup is parsed and the BorderBrush of each named site is compared whole, so a reference
/// to BorderBrush, or none at all, fails.
/// </remarks>
public sealed class BoundaryLineBrushMarkupTests
{
    private const string StrongBorder = "{DynamicResource BorderStrongBrush}";

    /// <summary>Tool header and footer borders found under src/Heimdall.App on 2026-10-21: 104. A floor for the walk.</summary>
    private const int MinimumToolHeaderAndFooterCount = 100;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The session sidebar's right edge, the divider between the sidebar and the content.</summary>
    [Fact]
    public void TheSidebarDividerIsAStrongBorder()
    {
        XElement sidebar = Named(Markup("MainWindow.xaml"), "TreeViewPanel");

        Assert.Equal(StrongBorder, (string?)sidebar.Attribute("BorderBrush"));
    }

    /// <summary>The top rule of the properties panel shown under the session tree, the divider between the tree and the panel.</summary>
    [Fact]
    public void TheSidebarPropertiesPanelDividerIsAStrongBorder()
    {
        XElement panel = Named(Markup("MainWindow.xaml"), "PropertiesPanel");

        Assert.Equal(StrongBorder, (string?)panel.Attribute("BorderBrush"));
    }

    /// <summary>The separator under a DataGrid column header and under a ListView column header.</summary>
    [Fact]
    public void GridColumnHeaderSeparatorsAreStrongBorders()
    {
        XDocument common = Markup("Themes", "CommonControls.xaml");
        XElement dataGrid = Keyed(common, "ThemedDataGridColumnHeaderStyle");
        XElement gridView = common.Descendants()
            .Single(e => e.Name.LocalName == "Style"
                && e.Attribute(Xaml + "Key") is null
                && (string?)e.Attribute("TargetType") == "{x:Type GridViewColumnHeader}");

        Assert.Equal(StrongBorder, SetterValue(dataGrid, "BorderBrush"));
        Assert.Equal(StrongBorder, SetterValue(gridView, "BorderBrush"));
    }

    /// <summary>A dialog section card and the properties card of the session detail pane.</summary>
    [Fact]
    public void SectionCardsHaveStrongBorders()
    {
        XElement dialogCard = Keyed(Markup("Themes", "DialogCommonStyles.xaml"), "DialogSectionCardStyle");
        XElement propertiesCard = Named(Markup("Controls", "SessionPropertiesView.xaml"), "Spv_PropertiesCard");

        Assert.Equal(StrongBorder, SetterValue(dialogCard, "BorderBrush"));
        Assert.Equal(StrongBorder, (string?)propertiesCard.Attribute("BorderBrush"));
    }

    /// <summary>The rule under every tool header and above every tool footer.</summary>
    [Fact]
    public void ToolHeaderAndFooterRulesAreStrongBorders()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
        List<string> found = [];
        List<string> faint = [];

        foreach (string path in SourceFileEnumeration.EnumerateFiles(root, "*.xaml"))
        {
            foreach (XElement border in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants().Where(e => e.Name.LocalName == "Border"))
            {
                string? padding = (string?)border.Attribute("Padding");
                if (padding is not ("{StaticResource ToolHeaderPadding}" or "{StaticResource ToolFooterPadding}"))
                {
                    continue;
                }

                string site = $"{Path.GetFileName(path)}:{((System.Xml.IXmlLineInfo)border).LineNumber}";
                found.Add(site);
                string? brush = (string?)border.Attribute("BorderBrush");
                if (brush != StrongBorder)
                {
                    faint.Add($"{site} BorderBrush={brush ?? "(none)"}");
                }
            }
        }

        Assert.True(
            found.Count >= MinimumToolHeaderAndFooterCount,
            $"the walk found {found.Count} tool header and footer borders, under the floor of {MinimumToolHeaderAndFooterCount}");
        Assert.True(faint.Count == 0, string.Join(Environment.NewLine, faint));
    }

    private static XDocument Markup(params string[] relative) =>
        XDocument.Load(Path.Combine([ViewSource.RepoRoot(), "src", "Heimdall.App", .. relative]), LoadOptions.SetLineInfo);

    private static XElement Named(XDocument markup, string name) =>
        Assert.Single(markup.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == name);

    private static XElement Keyed(XDocument markup, string key) =>
        Assert.Single(markup.Descendants(), e => (string?)e.Attribute(Xaml + "Key") == key);

    private static string? SetterValue(XElement style, string property) =>
        (string?)Assert.Single(
            style.Elements(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
            .Attribute("Value");
}
