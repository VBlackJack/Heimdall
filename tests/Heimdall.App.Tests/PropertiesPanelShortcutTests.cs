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
using System.Windows.Input;
using System.Xml.Linq;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The properties panel's shortcut and its Settings toggle.
/// </summary>
public sealed class PropertiesPanelShortcutTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CtrlShiftB_TogglesThePanel()
    {
        KeyboardShortcutService service = new();
        int toggles = 0;
        MainWindow.RegisterPropertiesPanelShortcut(service, () => toggles++, () => true);

        Assert.True(service.TryHandle(Key.B, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(1, toggles);
    }

    [Fact]
    public void AFocusedTerminal_KeepsTheChord()
    {
        KeyboardShortcutService service = new();
        int toggles = 0;
        MainWindow.RegisterPropertiesPanelShortcut(service, () => toggles++, () => false);

        Assert.False(service.TryHandle(Key.B, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(0, toggles);
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.None)]
    public void TheNeighbouringChords_AreNotTaken(ModifierKeys modifiers)
    {
        KeyboardShortcutService service = new();
        int toggles = 0;
        MainWindow.RegisterPropertiesPanelShortcut(service, () => toggles++, () => true);

        Assert.False(service.TryHandle(Key.B, modifiers));
        Assert.Equal(0, toggles);
    }

    /// <summary>
    /// The Settings box writes the live preference, not a copy of it: a copy saved later would
    /// undo a toggle made from the menu or the shortcut in the meantime.
    /// </summary>
    [Theory]
    [InlineData("Mw_SettingsShowPropertiesPanel", "{Binding ServerList.ShowPropertiesPanel, Mode=TwoWay}")]
    [InlineData("Mw_SettingsShowGatewayBadge", "{Binding ServerList.ShowGatewayBadge, Mode=TwoWay}")]
    [InlineData("Mw_SettingsCompactSessionTree", "{Binding ServerList.CompactSessionTree, Mode=TwoWay}")]
    public void TheSettingsToggle_IsTheLivePreference(string name, string binding)
    {
        XElement checkBox = FindNamed(name);

        Assert.Equal(binding, (string?)checkBox.Attribute("IsChecked"));
    }

    /// <summary>
    /// The menu wrote "Ctrl+Shift+B" in every language while the Settings line of the same panel
    /// said "Ctrl+Maj+B" in French: the key names come from the catalogue now.
    /// </summary>
    [Theory]
    [InlineData("en", "Ctrl+Shift+B")]
    [InlineData("fr", "Ctrl+Maj+B")]
    [InlineData("es", "Ctrl+May\u00fas+B")]
    public void TheMenu_ShowsTheShortcutInTheUsersLanguage(string language, string expected)
    {
        Assert.Equal(
            "{loc:Translate PropertiesPanelGesture}",
            (string?)FindNamed("Mw_ShowPropertiesPanelMenu").Attribute("InputGestureText"));

        using System.Text.Json.JsonDocument catalogue = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "locales", $"{language}.json")));
        Assert.Equal(expected, catalogue.RootElement.GetProperty("PropertiesPanelGesture").GetString());
    }

    /// <summary>
    /// A spoken name that does not start with the visible label fails a voice-control user who
    /// says what they see (WCAG 2.5.3): the long description is help text, not the name.
    /// </summary>
    [Fact]
    public void TheMenuItem_IsNamedByItsVisibleLabel()
    {
        XElement item = FindNamed("Mw_ShowPropertiesPanelMenu");

        Assert.Null(item.Attribute("AutomationProperties.Name"));
        Assert.Equal("{loc:Translate FilterShowPropertiesPanelHelpText}", (string?)item.Attribute("AutomationProperties.HelpText"));
    }

    /// <summary>
    /// On the Tools tab or with the sidebar hidden the panel cannot be seen: the shortcut shows it
    /// there instead of switching it off unseen.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TheShortcut_RevealsAPanelThatCannotBeSeen(bool toolsTab, bool sidebarHidden, bool expected)
    {
        Assert.Equal(expected, SessionPropertiesPanelLayout.ShortcutReveals(toolsTab, sidebarHidden));
    }

    private static XElement FindNamed(string name)
    {
        XDocument window = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        return Assert.Single(window.Descendants(), e => (string?)e.Attribute(XamlNamespace + "Name") == name);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException($"Cannot find repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }
}
