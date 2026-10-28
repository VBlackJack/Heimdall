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
/// Item controls whose rows were announced by the type name of the item, and the way each now
/// names its containers.
/// </summary>
/// <remarks>
/// <para>
/// UI Automation reads the generated container, never the visual tree a data template builds
/// inside it: a name set in the template lands on an element with no automation peer, and the
/// container falls back to <c>ToString()</c> of the bound item. Every control below was announced
/// that way (the notes tree as <c>Heimdall.App.Services.NoteTreeNode</c>). The census is explicit
/// rather than measured because "this list shows items" cannot be told from the markup alone; it
/// is the place to add the next one, and a control removed or renamed is a red test instead of a
/// silent gap.
/// </para>
/// <para>
/// The mechanism is checked on the container style: <c>ItemContainerAccessibilityBehavior</c> for
/// an item that implements <c>IAccessibleItemViewModel</c>, a localized name converter for an item
/// that belongs to a storage model with no localizer, or a direct name setter where the item
/// already holds its display name. What this cannot show is the name a client receives; the
/// <c>RequiresDesktop</c> tests read that through a live UI Automation client.
/// </para>
/// </remarks>
public sealed class ItemContainerNamingCensusTests
{
    private const string Behavior = "behaviors:ItemContainerAccessibilityBehavior.IsEnabled";
    private const string Converter = "AccessibleRowNameConverter";
    private const string NameSetter = "AutomationProperties.Name";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>(file, x:Name of the control, what its container style must contain)</summary>
    private static readonly (string File, string Control, string Marker)[] Census =
    [
        ("MainWindow.xaml", "PaletteResultsList", Behavior),
        ("MainWindow.xaml", "PaletteVariantsList", Behavior),
        ("MainWindow.xaml", "SidebarToolsTreeView", Behavior),
        ("Views/Tools/NotesToolView.xaml", "NotesTreeView", Converter),
        ("Views/Tools/NotesToolView.xaml", "NotesTreeView", NameSetter),
        ("Views/Tools/RegexTesterView.xaml", "MatchesList", Behavior),
        ("Views/Dialogs/ServerDialog.xaml", "DlgSrv_PostConnectStepsList", Behavior),
    ];

    [Fact]
    public void EveryCensusedControl_NamesItsContainers()
    {
        List<string> missing = [];
        foreach ((string file, string control, string marker) in Census)
        {
            XElement? element = Find(file, control);
            if (element is null)
            {
                missing.Add($"{file}: no control named {control}");
                continue;
            }

            if (!ContainerStyleOf(element).Contains(marker, StringComparison.Ordinal))
            {
                missing.Add($"{file}: {control} has no {marker} on its item container style");
            }
        }

        Assert.True(
            missing.Count == 0,
            "These lists leave their rows to announce the type of the item:" + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The gateway list of the new tunnel dialog is a combo box over a Core record: the container
    /// carries the gateway's name, and typing in the closed box searches on it.
    /// </summary>
    [Fact]
    public void TheNewTunnelGatewayCombo_NamesItsItems_AndSearchesOnTheName()
    {
        XElement combo = Assert.Single(
            Load("Views/Dialogs/NewTunnelDialog.xaml").Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && ((string?)element.Attribute("ItemsSource"))?.Contains("Gateways", StringComparison.Ordinal) == true);

        Assert.Equal("Name", (string?)combo.Attribute("TextSearch.TextPath"));

        string containerStyle = ContainerStyleOf(combo);
        Assert.Contains(NameSetter, containerStyle, StringComparison.Ordinal);
        Assert.Contains("{Binding Name}", containerStyle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control: a list whose container style names nothing is reported, and a control
    /// the census lists that is not in the file is reported too, instead of being skipped.
    /// </summary>
    [Fact]
    public void TheCensus_ReportsAContainerStyleWithoutAMarker()
    {
        XElement list = XElement.Parse(
            """
            <ListBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <ListBox.ItemContainerStyle>
                <Style TargetType="ListBoxItem"><Setter Property="Padding" Value="0"/></Style>
              </ListBox.ItemContainerStyle>
            </ListBox>
            """);

        Assert.DoesNotContain(Behavior, ContainerStyleOf(list), StringComparison.Ordinal);
        Assert.Null(Find("Views/Tools/RegexTesterView.xaml", "NoSuchList"));
    }

    [Fact]
    public void TheCensus_ReachesTheFilesItNames()
    {
        foreach (string file in Census.Select(entry => entry.File).Distinct(StringComparer.Ordinal))
        {
            Assert.True(File.Exists(Path.Combine(AppRoot(), file)), file);
        }
    }

    private static XElement? Find(string file, string name) =>
        Load(file).Descendants().FirstOrDefault(element => (string?)element.Attribute(Xaml + "Name") == name);

    /// <summary>The markup of the control's <c>*.ItemContainerStyle</c> property element, or empty.</summary>
    private static string ContainerStyleOf(XElement control) =>
        string.Concat(control.Elements()
            .Where(child => child.Name.LocalName.EndsWith(".ItemContainerStyle", StringComparison.Ordinal))
            .Select(child => child.ToString()));

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppRoot(), file));

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
}
