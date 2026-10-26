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
using Heimdall.App.Converters;

namespace Heimdall.App.Tests;

/// <summary>
/// The settings are one rail of sections grouped under the names of their groups.
/// </summary>
/// <remarks>
/// They were six top tabs with a rail of sections inside each, two levels to cross before a
/// setting, with an error badge on both. The top tabs are gone: their names are headings on the
/// one rail, and their automation ids moved onto those headings so a group can still be found.
/// </remarks>
public sealed class SettingsSingleRailMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly XNamespace Controls = "clr-namespace:Heimdall.App.Controls";

    private const string HeadingStyle = "{StaticResource SettingsRailGroupHeaderStyle}";

    private const string SectionStyle = "{StaticResource SettingsRailTabItemStyle}";

    /// <summary>The groups in rail order, each the suffix of its heading's automation id.</summary>
    private static readonly string[] Groups = ["General", "Terminal", "Ssh", "Rdp", "Security", "Advanced"];

    [Fact]
    public void EverySectionCarriesTheGroupOfTheHeadingAboveIt()
    {
        List<XElement> items = RailItems();
        List<string> problems = [];
        string? current = null;
        int sections = 0;

        foreach (XElement item in items)
        {
            string name = item.Attribute(Xaml + "Name")?.Value ?? "(unnamed)";
            string? group = item.Attribute(Controls + "SettingsRail.Group")?.Value;
            string? style = item.Attribute("Style")?.Value;
            if (style == HeadingStyle)
            {
                current = group;
                continue;
            }

            sections++;
            if (style != SectionStyle)
            {
                problems.Add($"{name} is neither a group heading nor a section");
            }

            if (group is null)
            {
                problems.Add($"{name} carries no group");
            }
            else if (group != current)
            {
                problems.Add($"{name} says it is in {group} but sits under the {current ?? "(no)"} heading");
            }
        }

        Assert.True(sections >= 25, $"only {sections} sections found on the rail; the scan is not reading the settings");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheRailShowsOneHeadingPerGroupInOrder()
    {
        List<XElement> headings = RailItems()
            .Where(item => item.Attribute("Style")?.Value == HeadingStyle)
            .ToList();

        Assert.Equal(Groups, headings.Select(heading => heading.Attribute(Controls + "SettingsRail.Group")?.Value));

        // The six former top tabs keep their automation ids, on their headings.
        Assert.Equal(
            Groups.Select(group => "Mw_SettingsTab" + group),
            headings.Select(heading => heading.Attribute(Xaml + "Name")?.Value));
        Assert.All(headings, heading => Assert.StartsWith("{loc:Translate SettingsTab", heading.Attribute("Header")?.Value, StringComparison.Ordinal));

        // A heading holds nothing: the rail never opens one.
        Assert.All(headings, heading => Assert.Empty(heading.Nodes().OfType<XElement>()));
    }

    [Fact]
    public void TheSettingsHaveNoTopTabs()
    {
        XElement root = Named(Window(), "Mw_SettingsRoot");
        List<XElement> tabControls = root.Descendants()
            .Where(element => element.Name.LocalName == "TabControl")
            .ToList();

        XElement rail = Assert.Single(tabControls);
        Assert.Equal("Mw_SettingsSubTabControl", rail.Attribute(Xaml + "Name")?.Value);
        Assert.Equal("{StaticResource SettingsRailTabControlStyle}", rail.Attribute("Style")?.Value);
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Attribute("Style")?.Value == "{StaticResource ThemedTabItemStyle}");

        // The rail opens on a section, never on the heading that comes first.
        XElement selected = Assert.Single(RailItems(), item => item.Attribute("IsSelected")?.Value == "True");
        Assert.Equal("Mw_SettingsGeneralSubTabAppearance", selected.Attribute(Xaml + "Name")?.Value);
    }

    /// <summary>
    /// The arrow keys, Ctrl+Tab, Home and End walk the sections and pass over the headings.
    /// </summary>
    /// <remarks>
    /// The rail's directional navigation skips what cannot take focus, and the tab control's own
    /// keys skip a disabled item; a heading is both.
    /// </remarks>
    [Fact]
    public void AGroupHeadingIsNeverFocused()
    {
        XElement style = Named(Theme(), "SettingsRailGroupHeaderStyle", Xaml + "Key");
        Dictionary<string, string?> setters = style.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(setter => setter.Attribute("Property")!.Value, setter => setter.Attribute("Value")?.Value);

        Assert.Equal("False", setters["IsEnabled"]);
        Assert.Equal("False", setters["Focusable"]);
        Assert.Equal("False", setters["IsTabStop"]);
    }

    /// <summary>
    /// A group name reads as the heading of the sections under it, as in Heimdall-rs: upper case,
    /// semi-bold, muted, well more room above it than between two sections, and the sections
    /// indented under it.
    /// </summary>
    /// <remarks>
    /// It was the section names' own case, 14 above it against 2 between sections, and at the
    /// left edge of the section names, so on Magellan the groups did not read as groups.
    /// </remarks>
    [Fact]
    public void AGroupHeadingStandsOutFromItsSections()
    {
        XDocument theme = Theme();
        XElement heading = Named(theme, "SettingsRailGroupHeaderStyle", Xaml + "Key");
        Dictionary<string, string?> setters = Setters(heading);
        Assert.Equal("{DynamicResource TextTertiaryBrush}", setters["Foreground"]);
        Assert.Equal("SemiBold", setters["FontWeight"]);
        Assert.Equal("{StaticResource SettingsRailGroupHeaderMargin}", setters["Margin"]);

        // The name is shown through the upper case converter, not as written.
        XElement text = heading.Descendants().Single(element => element.Name.LocalName == "TextBlock");
        Assert.Contains("Converter={StaticResource SettingsRailGroupHeaderCaseConverter}", text.Attribute("Text")?.Value, StringComparison.Ordinal);
        Assert.Equal(nameof(UpperCaseTextConverter), Named(theme, "SettingsRailGroupHeaderCaseConverter", Xaml + "Key").Name.LocalName);
        Assert.DoesNotContain(heading.Descendants(), element => element.Name.LocalName == "ContentPresenter");

        double[] headingMargin = Thickness(theme, "SettingsRailGroupHeaderMargin");
        Dictionary<string, string?> section = Setters(Named(theme, "SettingsRailTabItemStyle", Xaml + "Key"));
        Assert.Equal("{StaticResource SettingsRailItemMargin}", section["Margin"]);
        double[] sectionMargin = Thickness(theme, "SettingsRailItemMargin");
        double sectionTextLeft = sectionMargin[0] + double.Parse(section["Padding"]!.Split(',')[0], CultureInfo.InvariantCulture);

        Assert.True(headingMargin[1] >= 5 * sectionMargin[3], $"{headingMargin[1]} above a group is not well more than the {sectionMargin[3]} between sections");
        Assert.True(sectionMargin[0] >= 8, $"the sections are indented by {sectionMargin[0]} only");
        Assert.True(sectionTextLeft - headingMargin[0] >= 16, $"a section name starts at {sectionTextLeft}, a group name at {headingMargin[0]}: they line up");
    }

    /// <summary>
    /// The upper case follows the interface language, not the binding's culture, which is the
    /// element language and en-US unless set.
    /// </summary>
    [Fact]
    public void TheUpperCaseFollowsTheInterfaceLanguage()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            object shown = new UpperCaseTextConverter().Convert("bilgi", typeof(string), null!, CultureInfo.GetCultureInfo("en-US"));
            // Turkish upper-cases a dotted i to a dotted capital I, U+0130.
            string dottedCapitalI = char.ConvertFromUtf32(0x0130);
            Assert.Equal("B" + dottedCapitalI + "LG" + dottedCapitalI, shown);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// Reset RDP defaults sat under the RDP rail; with one rail it shows under an RDP section only.
    /// </summary>
    [Fact]
    public void TheRdpResetShowsUnderAnRdpSectionOnly()
    {
        XElement rail = Named(Window(), "Mw_SettingsSubTabControl");
        XElement footer = rail.Elements().Single(element => element.Name == Controls + "SettingsRail.ContentFooter");
        XElement button = footer.Elements().Single();

        Assert.Equal("Mw_SettingsResetRdpDefaults", button.Attribute(Xaml + "Name")?.Value);
        XElement trigger = button.Descendants().Single(element => element.Name.LocalName == "DataTrigger");
        Assert.Equal("Rdp", trigger.Attribute("Value")?.Value);
        Assert.Contains("SelectedItem.(controls:SettingsRail.Group)", trigger.Attribute("Binding")?.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rail and the open section sit on the window background; the section cards are the only
    /// surface.
    /// </summary>
    /// <remarks>
    /// The settings sat on a rounded card surface, the open section on a rounded background
    /// surface inside it, and the section cards on the card colour again: a card in a card in a
    /// card. A divider is all that parts the rail from the section now.
    /// </remarks>
    [Fact]
    public void NoCardSitsInsideACard()
    {
        XElement style = Named(Theme(), "SettingsRailTabControlStyle", Xaml + "Key");
        XElement template = style.Descendants().Single(element => element.Name.LocalName == "ControlTemplate");

        Assert.DoesNotContain(template.DescendantsAndSelf(), element => element.Attribute("Background") is not null);
        Assert.DoesNotContain(template.DescendantsAndSelf(), element => element.Attribute("CornerRadius") is not null);

        Dictionary<string, string?> setters = style.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(setter => setter.Attribute("Property")!.Value, setter => setter.Attribute("Value")?.Value);
        Assert.Equal("Transparent", setters["Background"]);
        Assert.Equal("{DynamicResource BorderStrongBrush}", setters["BorderBrush"]);
        XElement divider = template.Descendants().Single(element => element.Attribute("BorderThickness")?.Value == "0,0,1,0");
        Assert.Equal("{TemplateBinding BorderBrush}", divider.Attribute("BorderBrush")?.Value);

        // The rail reaches the window edge on the right, so the section scroll bar sits there.
        XElement rail = Named(Window(), "Mw_SettingsSubTabControl");
        Assert.Equal("{StaticResource SettingsRailHostMargin}", rail.Attribute("Margin")?.Value);
        XElement margin = Named(Theme(), "SettingsRailHostMargin", Xaml + "Key");
        Assert.EndsWith(",0,0", margin.Value, StringComparison.Ordinal);

        // The section cards keep the card colour and its outline.
        List<XElement> cards = Named(Window(), "Mw_SettingsSubTabControl").Descendants()
            .Where(element => element.Name.LocalName == "Border"
                && element.Attribute("Background")?.Value == "{DynamicResource CardBrush}")
            .ToList();
        Assert.True(cards.Count >= 25, $"only {cards.Count} section cards found; the scan is not reading the settings");
        Assert.All(cards, card => Assert.Equal("{DynamicResource BorderBrush}", card.Attribute("BorderBrush")?.Value));
    }

    /// <summary>The glyph each apply text is shown with; the two "after Save" texts share one.</summary>
    private static readonly Dictionary<string, string> GlyphForKey = new(StringComparer.Ordinal)
    {
        ["SettingsAppliesImmediately"] = "SettingsApplyGlyphImmediate",
        ["SettingsSavedImmediately"] = "SettingsApplyGlyphSavedAtOnce",
        ["SettingsAppliesToNewSessions"] = "SettingsApplyGlyphNextConnection",
        ["SettingsAppliesToNewConnections"] = "SettingsApplyGlyphNextConnection",
    };

    /// <summary>
    /// The apply badge under a section title is gone; a glyph beside the title replaces it.
    /// </summary>
    [Fact]
    public void NoApplyBadgeRemains()
    {
        string root = SettingsNumericFields.FindRepoRoot();
        foreach (string file in new[] { Path.Combine("src", "Heimdall.App", "MainWindow.xaml"), Path.Combine("src", "Heimdall.App", "Themes", "CommonControls.xaml") })
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain("SettingsApplyBadge", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every apply glyph sits beside a section title and carries the old badge text as its tooltip
    /// and as the name a screen reader hears.
    /// </summary>
    [Fact]
    public void EveryApplyGlyphSaysItsTextToTheMouseAndToAScreenReader()
    {
        List<XElement> glyphs = Named(Window(), "Mw_SettingsRoot").Descendants()
            .Where(element => element.Attribute("Style")?.Value == "{StaticResource SettingsApplyGlyphStyle}")
            .ToList();

        Assert.True(glyphs.Count >= 15, $"only {glyphs.Count} apply glyphs found; the scan is not reading the settings");
        Assert.All(glyphs, glyph =>
        {
            string name = glyph.Attribute(Xaml + "Name")?.Value ?? "(unnamed)";
            string? tooltip = glyph.Attribute("ToolTip")?.Value;
            Assert.Equal(tooltip, glyph.Attribute("AutomationProperties.Name")?.Value);
            string key = Assert.Single(GlyphForKey.Keys, key => tooltip == "{loc:Translate " + key + "}");
            Assert.Equal("{StaticResource " + GlyphForKey[key] + "}", glyph.Attribute("Text")?.Value);

            XElement title = Assert.IsType<XElement>(glyph.ElementsBeforeSelf().LastOrDefault());
            Assert.Equal(name, title.Attribute(Xaml + "Name")?.Value + "Applies");
            Assert.Equal("Horizontal", glyph.Parent?.Attribute("Orientation")?.Value);
        });
    }

    /// <summary>
    /// The foot of the rail says what each glyph means, in the words the glyphs carry.
    /// </summary>
    [Fact]
    public void TheRailLegendExplainsEveryGlyph()
    {
        XElement rail = Named(Window(), "Mw_SettingsSubTabControl");
        XElement footer = rail.Elements().Single(element => element.Name == Controls + "SettingsRail.Footer");
        XElement legend = footer.Elements().Single();
        Assert.Equal("Mw_SettingsApplyLegend", legend.Attribute(Xaml + "Name")?.Value);

        List<XElement> lines = legend.Elements().Where(element => element.Name.LocalName == "TextBlock").ToList();
        Assert.Equal(3, lines.Count);
        HashSet<string> explained = new(StringComparer.Ordinal);
        foreach (XElement line in lines)
        {
            List<XElement> runs = line.Elements().Where(element => element.Name.LocalName == "Run").ToList();
            Assert.Equal(2, runs.Count);
            string text = runs[1].Attribute("Text")!.Value;
            string key = Assert.Single(GlyphForKey.Keys, key => text == "{loc:Translate " + key + "}");

            // The line is named by its text alone, so the glyph is not read out.
            Assert.Equal(text, line.Attribute("AutomationProperties.Name")?.Value);
            Assert.Equal("{StaticResource " + GlyphForKey[key] + "}", runs[0].Attribute("Text")?.Value);
            explained.Add(GlyphForKey[key]);
        }

        Assert.Equal(GlyphForKey.Values.ToHashSet(StringComparer.Ordinal), explained);

        // The rail template shows its footer under the list of sections.
        XElement style = Named(Theme(), "SettingsRailTabControlStyle", Xaml + "Key");
        Assert.Contains(
            style.Descendants(),
            element => element.Name.LocalName == "ContentPresenter"
                && element.Attribute("Content")?.Value == "{TemplateBinding controls:SettingsRail.Footer}");
    }

    private static Dictionary<string, string?> Setters(XElement style) =>
        style.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(setter => setter.Attribute("Property")!.Value, setter => setter.Attribute("Value")?.Value);

    private static double[] Thickness(XDocument theme, string key) =>
        Named(theme, key, Xaml + "Key").Value.Split(',')
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();

    private static List<XElement> RailItems() =>
        Named(Window(), "Mw_SettingsSubTabControl").Elements()
            .Where(element => element.Name.LocalName == "TabItem")
            .ToList();

    private static XElement Named(XDocument document, string name, XName? attribute = null) =>
        document.Descendants().Single(element => element.Attribute(attribute ?? Xaml + "Name")?.Value == name);

    private static XDocument Window() =>
        XDocument.Load(Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));

    private static XDocument Theme() =>
        XDocument.Load(Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));
}
