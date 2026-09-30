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

        List<XElement> general = Named("Mw_SettingsTabGeneral").Descendants().ToList();
        Assert.True(
            general.IndexOf(Named("Mw_SettingsAppearanceTitle")) < general.IndexOf(Named("Mw_SettingsOnboardingTitle")),
            "Appearance comes before the welcome tour on General");
        XElement cards = Named("Mw_SettingsAppearanceTitle").Ancestors().First(element => element.Name.LocalName == "Border").Parent!;
        XElement lastCard = cards.Elements().Last(element => element.Name.LocalName == "Border");
        Assert.Contains(lastCard.Descendants(), element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsOnboardingTitle");

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
