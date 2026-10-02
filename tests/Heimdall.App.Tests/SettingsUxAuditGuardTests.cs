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
using Heimdall.App.ViewModels.Shell;

namespace Heimdall.App.Tests;

/// <summary>
/// Guards for the findings of the Settings UI/UX audit that a markup read can prove.
/// </summary>
public sealed class SettingsUxAuditGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppFile(params string[] parts) =>
        Path.Combine([SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", .. parts]);

    private static XDocument LoadMainWindow() => XDocument.Load(AppFile("MainWindow.xaml"));

    private static XElement Named(XDocument markup, string name) =>
        markup.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

    /// <summary>The settings card (a Border) that holds an element.</summary>
    private static XElement CardOf(XElement element) =>
        element.Ancestors().First(ancestor => ancestor.Name.LocalName == "Border"
            && ancestor.Attribute("Padding")?.Value == "{StaticResource SpacingLg}");

    private static bool Holds(XElement card, string name) =>
        card.Descendants().Any(element => element.Attribute(Xaml + "Name")?.Value == name);

    /// <summary>
    /// P1-1: the Git token is a secret written to the vault, so it is committed when the field is
    /// left, never on each keystroke, and a write cannot overtake an earlier one.
    /// </summary>
    [Fact]
    public void TheGitTokenIsCommittedWhenTheFieldIsLeftNotOnEachKeystroke()
    {
        XElement box = Named(LoadMainWindow(), "Mw_SettingsCmdLibSyncToken");
        Assert.Equal("OnCmdLibSyncTokenLostFocus", box.Attribute("LostFocus")?.Value);

        // The keystroke handler only notes that the field was edited: an absence assertion, so
        // putting the vault write back into it is what fails.
        string source = File.ReadAllText(AppFile("MainWindow.xaml.cs"));
        string changed = MethodBody(source, "private void OnCmdLibSyncTokenChanged");

        Assert.DoesNotContain("TrySaveTokenAsync", changed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token typed and then left by a gesture that moves no focus is still committed: Ctrl+S is
    /// a keystroke, leaving the Settings tab by shortcut keeps the field focused, and WPF raises no
    /// LostFocus when the window itself is closed. Each of them commits the pending edit, so the
    /// field never keeps a token the vault does not have.
    /// </summary>
    [Fact]
    public void EveryWayOutOfTheTokenFieldCommitsThePendingToken()
    {
        string code = ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(AppFile("MainWindow.xaml.cs")));

        // One call per constant: the source-assertion guard ties each read to the anchor it names.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(ViewSource.HandlerBody(code, TokenLostFocusMember), TokenLostFocusCommit),
            "leaving the token field does not commit it");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(ViewSource.HandlerBody(code, SaveShortcutMember), SaveShortcutCommit),
            "Ctrl+S does not commit the pending token");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(ViewSource.HandlerBody(code, SwitchTabMember), SwitchTabCommit),
            "a tab switch does not commit the pending token");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(ViewSource.HandlerBody(code, WindowClosingMember), WindowClosingCommit),
            "closing the window does not commit the pending token");
    }

    /// <summary>Only a switch away from the Settings tab commits the token.</summary>
    [Theory]
    [InlineData(ShellTab.Settings, ShellTab.Sessions, true)]
    [InlineData(ShellTab.Settings, ShellTab.Settings, false)]
    [InlineData(ShellTab.Sessions, ShellTab.Settings, false)]
    [InlineData(ShellTab.Sessions, ShellTab.Tunnels, false)]
    public void OnlyLeavingTheSettingsTabCommitsTheToken(string current, string target, bool expected)
    {
        Assert.Equal(expected, MainWindow.LeavesSettingsTab(current, target));
    }

    private const string TokenLostFocusMember = "private async void OnCmdLibSyncTokenLostFocus(object sender, RoutedEventArgs e)";
    private const string TokenLostFocusCommit = "await CommitPendingCmdLibTokenAsync().ConfigureAwait(true);";
    private const string SaveShortcutMember = "private void SaveSettingsFromShortcut()";
    private const string SaveShortcutCommit = "_ = CommitPendingCmdLibTokenAsync();";
    private const string SwitchTabMember = "private void SwitchToTab(string tabName)";
    private const string SwitchTabCommit = "_ = CommitPendingCmdLibTokenOnTabSwitchAsync(vm.SelectedTab, tabName);";
    private const string WindowClosingMember = "protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)";
    private const string WindowClosingCommit = "await CommitPendingCmdLibTokenAsync().ConfigureAwait(true);";

    /// <summary>
    /// P2-2 and P2-4: every card has a title, and Credential Guard and the Windows Hello
    /// requirement sit in their own card, not under the external credential provider.
    /// </summary>
    [Theory]
    [InlineData("Mw_SettingsSshSessionTitle")]
    [InlineData("Mw_SettingsSftpTitle")]
    [InlineData("Mw_SettingsX11Title")]
    [InlineData("Mw_SettingsBehaviorTitle")]
    [InlineData("Mw_SettingsConnectionChecksTitle")]
    public void ACardStartsWithItsTitle(string title)
    {
        XElement heading = Named(LoadMainWindow(), title);

        Assert.Equal("TextBlock", heading.Name.LocalName);
        Assert.Same(heading, heading.Parent!.Elements().First());
        Assert.Same(CardOf(heading), heading.Parent.Parent);
    }

    [Theory]
    [InlineData("Mw_SettingsCredGuard")]
    [InlineData("Mw_SettingsRequireWindowsHello")]
    [InlineData("Mw_SettingsWindowsHelloGraceLabel")]
    public void CredentialGuardAndHelloAreNotFiledUnderTheCredentialProvider(string control)
    {
        XDocument markup = LoadMainWindow();
        XElement card = CardOf(Named(markup, control));

        Assert.True(Holds(card, "Mw_SettingsConnectionChecksTitle"));
        Assert.False(Holds(card, "Mw_SettingsCredProviderTitle"));
    }

    [Theory]
    [InlineData("Mw_SettingsMaxSessionsLabel")]
    [InlineData("Mw_SettingsPreventSleep")]
    [InlineData("Mw_SettingsCollapseTunnelsPanelByDefault")]
    public void BehaviourSettingsAreNotFiledUnderAppearance(string control)
    {
        XDocument markup = LoadMainWindow();
        XElement card = CardOf(Named(markup, control));

        Assert.True(Holds(card, "Mw_SettingsBehaviorTitle"));
        Assert.False(Holds(card, "Mw_SettingsAppearanceTitle"));
    }

    [Fact]
    public void SftpAndX11AreSeparateCards()
    {
        XDocument markup = LoadMainWindow();

        Assert.NotSame(
            CardOf(Named(markup, "Mw_SettingsSftpBrowserEnabled")),
            CardOf(Named(markup, "Mw_SettingsX11PathLabel")));
    }

    /// <summary>P2-5: the Git sync fields wait for the switch above them, and the author fields are labelled.</summary>
    [Fact]
    public void GitSyncFieldsFollowTheEnableSwitchAndTheAuthorFieldsAreLabelled()
    {
        XDocument markup = LoadMainWindow();
        XElement fields = Named(markup, "Mw_SettingsCmdLibSyncLblUrl").Parent!.Parent!;

        Assert.Equal("{Binding Settings.CmdLibGitSyncEnabled}", fields.Attribute("IsEnabled")?.Value);
        Assert.Contains(Named(markup, "Mw_SettingsCmdLibSyncLblToken"), fields.Descendants());
        Assert.Contains(Named(markup, "Mw_SettingsCmdLibSyncLblAuthorName"), fields.Descendants());
        Assert.Contains(Named(markup, "Mw_SettingsCmdLibSyncLblAuthorEmail"), fields.Descendants());
    }

    /// <summary>P2-10: the smallest controls keep a pointer target of 24 pixels.</summary>
    [Theory]
    [InlineData("Controls/SettingDefaultMarker.xaml")]
    [InlineData("Controls/SecurityPostureLineView.xaml")]
    [InlineData("MainWindow.xaml")]
    public void NoSettingsControlOptsOutOfTheMinimumTarget(string file)
    {
        string text = File.ReadAllText(AppFile(file.Split('/')));
        string scope = file == "MainWindow.xaml" ? SettingsScope(text) : text;

        Assert.DoesNotContain("MinHeight=\"0\"", scope, StringComparison.Ordinal);
    }

    /// <summary>P3-5: the shortcuts that exist are announced on the controls they belong to.</summary>
    [Theory]
    [InlineData("Mw_SettingsSaveBtn")]
    [InlineData("Mw_SettingsSearchBox")]
    public void ShortcutsAreAnnouncedOnTheirControls(string name)
    {
        XElement element = Named(LoadMainWindow(), name);

        Assert.NotNull(element.Attribute("ToolTip"));
        Assert.StartsWith(
            "{loc:Translate SettingsShortcut",
            element.Attribute("AutomationProperties.AcceleratorKey")?.Value);
    }

    /// <summary>P3-2: the gateway and external tool lists answer the keyboard like the trust grids do.</summary>
    [Theory]
    [InlineData("Settings.Gateways", "Enter", "Settings.EditGatewayCommand")]
    [InlineData("Settings.Gateways", "Delete", "Settings.DeleteGatewayCommand")]
    [InlineData("Settings.ExternalTools", "Delete", "Settings.RemoveExternalToolCommand")]
    public void SettingsListsAnswerTheKeyboard(string items, string key, string command)
    {
        XElement list = LoadMainWindow().Descendants()
            .Single(element => element.Name.LocalName == "ListBox"
                && element.Attribute("ItemsSource")?.Value == "{Binding " + items + "}");

        XElement bindings = list.Elements().Single(element => element.Name.LocalName == "ListBox.InputBindings");

        Assert.Contains(
            bindings.Elements(),
            binding => binding.Name.LocalName == "KeyBinding"
                && binding.Attribute("Key")?.Value == key
                && binding.Attribute("Command")?.Value == "{Binding " + command + "}");
    }

    [Fact]
    public void AGatewayOpensOnDoubleClickAndItsRowIsNamedForAssistiveTechnology()
    {
        XElement list = LoadMainWindow().Descendants()
            .Single(element => element.Name.LocalName == "ListBox"
                && element.Attribute("ItemsSource")?.Value == "{Binding Settings.Gateways}");

        Assert.Contains(
            list.Element(list.Name.Namespace + "ListBox.InputBindings")!.Elements(),
            binding => binding.Name.LocalName == "MouseBinding"
                && binding.Attribute("MouseAction")?.Value == "LeftDoubleClick");
        Assert.Contains(
            list.Descendants(),
            setter => setter.Attribute("Property")?.Value == "AutomationProperties.Name");
    }

    /// <summary>P3-1: the settings markup spells its sizes and fonts through tokens.</summary>
    [Theory]
    [InlineData("MaxWidth=\"900\"")]
    [InlineData("FontFamily=\"Segoe MDL2 Assets\"")]
    [InlineData("FontFamily=\"Consolas\"")]
    public void TheSettingsMarkupHoldsNoLiteralWhereATokenExists(string literal)
    {
        string scope = SettingsScope(File.ReadAllText(AppFile("MainWindow.xaml")));

        Assert.DoesNotContain(literal, scope, StringComparison.Ordinal);
    }

    /// <summary>
    /// P2-7: a field in error is marked by a glyph as well as a colour, and its border keeps its
    /// thickness, so the box neither relies on red alone nor grows when the error appears.
    /// </summary>
    [Fact]
    public void AFieldInErrorCarriesAGlyphAndKeepsItsBorderThickness()
    {
        XDocument themes = XDocument.Load(AppFile("Themes", "CommonControls.xaml"));
        XElement template = themes.Descendants()
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "ValidationErrorTemplate");

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "\uE783");

        XElement style = themes.Descendants()
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "ThemedTextBoxStyle");
        XElement errorTrigger = style.Descendants()
            .Single(element => element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "Validation.HasError");

        Assert.DoesNotContain(
            errorTrigger.Elements(),
            setter => setter.Attribute("Property")?.Value == "BorderThickness");
    }

    /// <summary>
    /// A token that the markup names but no dictionary defines only fails when the window is
    /// first shown, so every static resource of the settings markup must exist as a key.
    /// </summary>
    [Fact]
    public void EveryStaticResourceOfTheSettingsMarkupIsDefined()
    {
        string root = AppFile();
        HashSet<string> keys = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            foreach (XAttribute key in XDocument.Load(file).Descendants().Attributes(Xaml + "Key"))
            {
                keys.Add(key.Value);
            }
        }

        XDocument markup = LoadMainWindow();
        XElement settings = Named(markup, "Mw_SettingsRoot");
        const string Reference = "{StaticResource ";
        HashSet<string> used = [];
        foreach (XAttribute attribute in settings.DescendantsAndSelf().Attributes())
        {
            string value = attribute.Value;
            for (int at = value.IndexOf(Reference, StringComparison.Ordinal); at >= 0;
                at = value.IndexOf(Reference, at + 1, StringComparison.Ordinal))
            {
                int close = value.IndexOf('}', at);
                string name = value[(at + Reference.Length)..close].Trim();

                // {StaticResource {x:Type TextBox}} names an implicit style, not a key.
                if (!name.StartsWith('{'))
                {
                    used.Add(name);
                }
            }
        }

        Assert.True(used.Count > 20, "the scan found almost no static resources, so it reads nothing");
        Assert.All(used, name => Assert.Contains(name, keys));
        Assert.Contains("SettingsNumericFieldWidth", used);
    }

    private static string SettingsScope(string mainWindow)
    {
        int start = mainWindow.IndexOf("x:Name=\"Mw_SettingsRoot\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the settings root was not found");
        int end = mainWindow.IndexOf("<!-- ABOUT TAB", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of the settings markup was not found");
        return mainWindow[start..end];
    }

    /// <summary>The text of a method, from its signature to the next method of the same indentation.</summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " was not found");
        int end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
