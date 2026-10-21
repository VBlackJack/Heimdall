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

namespace Heimdall.App.Tests;

/// <summary>
/// The Settings tab is traversed in reading order and its labels are not clipped.
/// </summary>
/// <remarks>
/// <para>G-15: a path field and its Browse button sat in a DockPanel with the button declared first
/// and docked right, so Tab reached "Browse" before the field it fills - on nine rows. The Plink and
/// PuTTY rows papered over it with TabIndex 1 to 4, which order against the whole window, so Tab from
/// anywhere on the Settings tab went there first.</para>
/// <para>G-23: three label columns were fixed at 130 and 100 pixels, which cut French and Spanish
/// labels ("Chemin Sysinternals", "Directorio de trabajo").</para>
/// </remarks>
public sealed class SettingsLayoutOrderGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly XNamespace Rail = "clr-namespace:Heimdall.App.Controls";

    [Fact]
    public void SettingsFieldsComeBeforeTheirButtonsAndLabelsAreNotClipped()
    {
        XDocument markup = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"),
            LoadOptions.SetLineInfo);
        XElement root = markup.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsRoot");

        List<string> problems = Inspect(root);
        int browseButtons = root.Descendants()
            .Count(element => element.Name.LocalName == "Button"
                && (element.Attribute(Xaml + "Name")?.Value ?? string.Empty).Contains("Browse", StringComparison.Ordinal));

        Assert.True(browseButtons >= 12, $"only {browseButtons} Browse buttons found; the scan is not reading the settings");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Cards sit where the user looks for them.
    /// </summary>
    /// <remarks>
    /// L-7: General opened on the welcome tour, above Appearance. G-09: NLA and strict server
    /// authentication sat under RDP > Performance, away from the certificate trust they govern.
    /// I-08: auto-lock and disconnect on lock were hidden while the vault is off, so nothing said
    /// they exist or what turns them on.
    /// </remarks>
    [Fact]
    public void CardsSitWhereTheUserLooksForThem()
    {
        XDocument markup = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement Named(string name) => markup.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

        List<XElement> settings = Named("Mw_SettingsSubTabControl").Descendants().ToList();
        Assert.True(
            settings.IndexOf(Named("Mw_SettingsAppearanceTitle")) < settings.IndexOf(Named("Mw_SettingsOnboardingTitle")),
            "Appearance comes before the welcome tour on General");
        // The rail opens on the first General section, so Appearance leads the group and the tour
        // closes it.
        List<XElement> sections = Named("Mw_SettingsSubTabControl").Elements()
            .Where(element => element.Name.LocalName == "TabItem"
                && element.Attribute(Rail + "SettingsRail.Group")?.Value == "General"
                && element.Attribute("Style")?.Value == "{StaticResource SettingsRailTabItemStyle}")
            .ToList();
        Assert.Contains(sections[0].Descendants(), element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsAppearanceTitle");
        Assert.Contains(sections[^1].Descendants(), element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsOnboardingTitle");

        foreach (string security in new[] { "Mw_SettingsRdpNla", "Mw_SettingsRdpStrictServerAuth" })
        {
            string? tab = Named(security).Ancestors()
                .FirstOrDefault(element => element.Name.LocalName == "TabItem")?.Attribute(Xaml + "Name")?.Value;
            Assert.Equal("Mw_SettingsRdpSubTabCertificates", tab);
        }

        XElement autoLock = Named("Mw_SettingsAutoLockPanel");
        Assert.Null(autoLock.Attribute("Visibility"));
        Assert.Equal("{Binding Settings.IsVaultEnabled}", autoLock.Attribute("IsEnabled")?.Value);
    }

    /// <summary>
    /// The section rail is one stop for Tab, like a tab strip.
    /// </summary>
    /// <remarks>
    /// Its first build hosted the sections in a plain StackPanel, so Tab walked through every
    /// section name before it reached a setting. A TabPanel is a single stop and the arrow keys
    /// move inside it; the rail has to say the same thing itself.
    /// </remarks>
    [Fact]
    public void TheSectionRailIsASingleTabStop()
    {
        XDocument theme = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));
        XElement style = theme.Descendants().Single(element => element.Attribute(Xaml + "Key")?.Value == "SettingsRailTabControlStyle");
        XElement host = style.Descendants().Single(element => element.Attribute("IsItemsHost")?.Value == "True");

        Assert.Equal("Once", host.Attribute("KeyboardNavigation.TabNavigation")?.Value);
        Assert.Equal("Cycle", host.Attribute("KeyboardNavigation.DirectionalNavigation")?.Value);
    }

    /// <summary>
    /// The sections that do not wait for Save say when they apply.
    /// </summary>
    /// <remarks>
    /// I-03. The PIN, the vault and the two trust lists are written the moment they change, so
    /// Discard cannot undo them and Save has nothing to do with them. Nothing on screen said so.
    /// A badge under the title said it; a glyph beside the title says it now, with the same text
    /// as its tooltip and as the name a screen reader hears.
    /// </remarks>
    [Theory]
    [InlineData("Mw_SettingsPinTitle", "SettingsSavedImmediately", "SettingsApplyGlyphSavedAtOnce")]
    [InlineData("Mw_SettingsVaultTitle", "SettingsSavedImmediately", "SettingsApplyGlyphSavedAtOnce")]
    [InlineData("Mw_SettingsTrustedHostKeysTitle", "SettingsSavedImmediately", "SettingsApplyGlyphSavedAtOnce")]
    [InlineData("Mw_SettingsTrustedRdpCertificatesTitle", "SettingsSavedImmediately", "SettingsApplyGlyphSavedAtOnce")]
    [InlineData("Mw_SettingsAppearanceTitle", "SettingsAppliesImmediately", "SettingsApplyGlyphImmediate")]
    [InlineData("Mw_SettingsRdpDefaultsTitle", "SettingsAppliesToNewConnections", "SettingsApplyGlyphNextConnection")]
    [InlineData("Mw_SettingsTerminalTitle", "SettingsAppliesToNewSessions", "SettingsApplyGlyphNextConnection")]
    public void ASectionThatDoesNotWaitForSaveSaysWhenItApplies(string title, string key, string glyph)
    {
        XDocument markup = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement heading = markup.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == title);

        XElement? sign = heading.ElementsAfterSelf().FirstOrDefault();

        Assert.NotNull(sign);
        Assert.Equal("TextBlock", sign!.Name.LocalName);
        Assert.Equal("{StaticResource SettingsApplyGlyphStyle}", sign.Attribute("Style")?.Value);
        Assert.Equal("{StaticResource " + glyph + "}", sign.Attribute("Text")?.Value);
        Assert.Equal("{loc:Translate " + key + "}", sign.Attribute("ToolTip")?.Value);
        Assert.Equal("{loc:Translate " + key + "}", sign.Attribute("AutomationProperties.Name")?.Value);

        // Beside the title, not under it.
        Assert.Equal("Horizontal", heading.Parent?.Attribute("Orientation")?.Value);
    }

    /// <summary>
    /// The buttons beside a section heading keep their own height.
    /// </summary>
    /// <remarks>
    /// A stack docked right in a DockPanel stretches to the height of the text docked beside it.
    /// Next to a heading, a badge and a hint, Import, Export and Refresh grew to 68 pixels and the
    /// hint ran into them, on the host keys and both certificate lists.
    /// </remarks>
    [Fact]
    public void ButtonsBesideAHeadingDoNotStretchToItsHeight()
    {
        XElement root = SettingsRoot();
        List<XElement> stacks = root.Descendants()
            .Where(element => element.Name.LocalName == "StackPanel"
                && element.Attribute("DockPanel.Dock")?.Value == "Right"
                && element.Attribute("Orientation")?.Value == "Horizontal")
            .ToList();

        Assert.True(stacks.Count >= 7, $"only {stacks.Count} right-docked button stacks found; the scan is not reading the settings");
        Assert.All(stacks, stack => Assert.NotNull(stack.Attribute("VerticalAlignment")));
    }

    /// <summary>
    /// A trust list filter says in the box what it filters.
    /// </summary>
    /// <remarks>
    /// The three filters were empty boxes above their lists, explained only by a tooltip, so
    /// nothing on screen said they were a search.
    /// </remarks>
    [Fact]
    public void ATrustListFilterShowsWhatItFilters()
    {
        List<XElement> filters = SettingsRoot().Descendants()
            .Where(element => element.Name.LocalName == "TextBox"
                && (element.Attribute("Text")?.Value ?? string.Empty).StartsWith("{Binding Settings.Trusted", StringComparison.Ordinal)
                && (element.Attribute("Text")?.Value ?? string.Empty).Contains(".SearchText", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, filters.Count);
        Assert.All(filters, filter =>
        {
            Assert.Equal("{StaticResource WatermarkTextBoxStyle}", filter.Attribute("Style")?.Value);
            Assert.Equal(filter.Attribute("ToolTip")?.Value, filter.Attribute("Tag")?.Value);
        });
    }

    /// <summary>
    /// A block that only applies while a switch is on dims as a whole when it is off.
    /// </summary>
    /// <remarks>
    /// The inputs dimmed but the labels did not, and an empty dimmed box on a card of its own
    /// colour looked like any other, so the credential provider and Git sync read as available
    /// with their switch off.
    /// </remarks>
    [Fact]
    public void ABlockDisabledByASwitchDimsAsAWhole()
    {
        List<XElement> blocks = SettingsRoot().Descendants()
            .Where(element => element.Name.LocalName == "StackPanel"
                && (element.Attribute("IsEnabled")?.Value ?? string.Empty).StartsWith("{Binding Settings.", StringComparison.Ordinal))
            .ToList();

        Assert.True(blocks.Count >= 15, $"only {blocks.Count} switch-bound blocks found; the scan is not reading the settings");
        Assert.All(blocks, block => Assert.Equal("{StaticResource SettingsDependentPanelStyle}", block.Attribute("Style")?.Value));
    }

    /// <summary>
    /// The command library Git sync has its own section, apart from the external tools.
    /// </summary>
    [Fact]
    public void TheCommandLibrarySyncHasItsOwnSection()
    {
        XElement title = SettingsRoot().Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsCmdLibSyncTitle");
        string? section = title.Ancestors()
            .FirstOrDefault(element => element.Name.LocalName == "TabItem")?.Attribute(Xaml + "Name")?.Value;

        Assert.Equal("Mw_SettingsAdvancedSubTabCmdLibSync", section);
    }

    /// <summary>
    /// Every section of the rail takes the same width, whatever it holds.
    /// </summary>
    /// <remarks>
    /// A section sized itself to its content: a scroll viewer that may scroll sideways measures
    /// its content at infinite width, and the rail item style left-aligned the open section, so
    /// Devices was a narrow card and Tools a wide one. A section neither scrolls sideways nor
    /// inherits a left alignment from its rail entry.
    /// </remarks>
    [Fact]
    public void EveryRailSectionTakesTheSameWidth()
    {
        List<XElement> sections = SettingsRoot().Descendants()
            .Where(element => element.Name.LocalName == "TabItem"
                && element.Attribute("Style")?.Value == "{StaticResource SettingsRailTabItemStyle}")
            .ToList();

        Assert.True(sections.Count >= 25, $"only {sections.Count} rail sections found; the scan is not reading the settings");
        Assert.All(sections, section =>
        {
            XElement viewer = section.Elements().Single(element => element.Name.LocalName == "ScrollViewer");
            Assert.Equal("Disabled", viewer.Attribute("HorizontalScrollBarVisibility")?.Value);

            // The viewer fills the column so its scroll bar sits at the window edge; the panel
            // inside caps the section width and keeps it against the rail instead of centring.
            XElement panel = viewer.Elements().Single();
            Assert.Equal("{StaticResource SettingsContentMaxWidth}", panel.Attribute("MaxWidth")?.Value);
            Assert.Equal("Left", panel.Attribute("HorizontalAlignment")?.Value);
        });

        XDocument theme = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));
        XElement itemStyle = theme.Descendants().Single(element => element.Attribute(Xaml + "Key")?.Value == "SettingsRailTabItemStyle");
        Assert.DoesNotContain(
            itemStyle.Elements().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "HorizontalContentAlignment");

        XElement controlStyle = theme.Descendants().Single(element => element.Attribute(Xaml + "Key")?.Value == "SettingsRailTabControlStyle");
        Assert.DoesNotContain(
            controlStyle.Descendants().Where(element => element.Name.LocalName == "ColumnDefinition"),
            column => column.Attribute("MaxWidth") is not null);
    }

    private static XElement SettingsRoot()
    {
        XDocument markup = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        return markup.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsRoot");
    }

    [Fact]
    public void TheCheckRefusesEachShape()
    {
        XElement sample = XElement.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <DockPanel>
                    <Button x:Name="BrowseFirst" DockPanel.Dock="Right"/>
                    <TextBox/>
                </DockPanel>
                <TextBox TabIndex="1"/>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="130"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                </Grid>
            </Grid>
            """);

        Assert.Equal(3, Inspect(sample).Count);
    }

    private static List<string> Inspect(XElement root)
    {
        List<string> problems = [];
        foreach (XElement element in root.Descendants())
        {
            int line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            if (element.Name.LocalName == "DockPanel")
            {
                List<XElement> children = element.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
                // Anything docked right and declared before the filling field is reached by Tab first
                // while it is drawn after it: a Browse or Clear button, or a second field.
                int docked = children.FindIndex(child => child.Attribute("DockPanel.Dock")?.Value == "Right"
                    && child.Name.LocalName is not "TextBlock");
                int input = children.FindIndex(child => child.Name.LocalName is "TextBox" or "PasswordBox" or "ComboBox"
                    && child.Attribute("DockPanel.Dock") is null);
                if (docked >= 0 && input > docked)
                {
                    problems.Add($"line {line}: a control docked right is declared before the field it serves, so Tab reaches it first");
                }
            }

            if (element.Attribute("TabIndex") is not null)
            {
                problems.Add($"line {line}: TabIndex orders against the whole window; use document order");
            }

            if (element.Name.LocalName == "ColumnDefinition"
                && int.TryParse(element.Attribute("Width")?.Value, out _))
            {
                problems.Add($"line {line}: a fixed-width column clips longer translations; use Auto or *");
            }
        }

        return problems;
    }
}
