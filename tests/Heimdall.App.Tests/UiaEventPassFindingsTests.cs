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
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Controls;
using System.Xml.Linq;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// What a screen reader received from the running app on 2026-10-18, recorded through UI
/// Automation focus and live-region events: the tools filter box carried the name of the button
/// beside it, Tab from a tool's first field went out to the sidebar and back, a tool's error was
/// shown and never announced, and after a lookup focus was left on an unnamed scroll viewer.
/// </summary>
public sealed class UiaEventPassFindingsTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string NameAttribute = "AutomationProperties.Name";
    private const string TabNavigation = "KeyboardNavigation.TabNavigation";

    [Fact]
    public void TheToolsFilterBoxIsNotNamedAfterTheToolsTabButton()
    {
        XDocument window = Markup("MainWindow.xaml");
        string filterName = Attribute(Named(window, "Mw_SidebarToolsFilter"), NameAttribute);
        string tabName = Attribute(Named(window, "SidebarTabTools"), NameAttribute);

        Assert.NotEqual(tabName, filterName);

        string key = TranslateKey(filterName);
        foreach (string locale in new[] { "en", "fr", "es" })
        {
            using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "locales", locale + ".json")));
            Assert.True(strings.RootElement.TryGetProperty(key, out JsonElement value) && value.GetString()!.Length > 0, $"{key} has no {locale} text");
        }
    }

    [Fact]
    public void AHostedViewNumbersItsTabOrderInsideThePane()
    {
        XElement host = Named(Markup(@"Views\SessionPaneControl.xaml"), "HostPresenter");

        Assert.Equal("Local", Attribute(host, TabNavigation));
    }

    [Fact]
    public void TheSidebarSwitchNumbersItsTabOrderInsideItself()
    {
        XElement tools = Named(Markup("MainWindow.xaml"), "SidebarTabTools");
        XElement group = Assert.IsType<XElement>(tools.Parent);

        Assert.Equal("Local", Attribute(group, TabNavigation));
    }

    [Fact]
    public void AToolErrorIsALiveRegionThatIsRaised()
    {
        XElement style = Assert.Single(
            Markup(@"Themes\CommonControls.xaml").Descendants(),
            e => e.Name.LocalName == "Style" && (string?)e.Attribute(X + "Key") == "ToolInlineErrorTextStyle");

        Assert.Equal("Assertive", SetterValue(style, "AutomationProperties.LiveSetting"));
        Assert.Equal("True", SetterValue(style, "behaviors:LiveRegionBehavior.AnnounceOnTargetUpdated"));
    }

    [Fact]
    public void AToolErrorWrittenWhileShownIsAnnouncedAgain()
    {
        string logic = ControllerMethod("private void WriteError(");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, "_errorText.Text = message;"), "WriteError does not write the error");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, "LiveRegionBehavior.Announce(_errorText);"), "WriteError does not announce what it wrote");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ControllerMethod("public void ShowError("), "WriteError(message);"), "ShowError does not go through WriteError");
    }

    [Fact]
    public void TheRunGivesFocusBackToTheControlThatHeldIt()
    {
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ControllerMethod("public void Begin("),
                "_focusedBeforeRun ??= Array.Find(_controlsToDisable, control => control.IsKeyboardFocusWithin);"),
            "Begin does not remember the focused control, or a repeated Begin forgets it");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ControllerMethod("public void End("), "RestoreFocusAfterRun();"), "End does not restore focus");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ControllerMethod("private void RestoreFocusAfterRun("), "FocusWhenEnabled(previous);"), "the restore never focuses");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ControllerMethod("private static void FocusWhenEnabled("), "FocusIfItFellBack(previous);"), "an enabled control is not focused");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ControllerMethod("private static void FocusIfItFellBack("), "previous.Focus();"), "the restore never focuses");
    }

    [Fact]
    public void FocusComesBackOnlyWhenItFellOntoAContainer()
    {
        RunOnStaThread(() =>
        {
            Button lookup = new();
            StackPanel row = new();
            row.Children.Add(lookup);
            Border pane = new() { Child = row };
            TextBox elsewhere = new();
            StackPanel window = new();
            window.Children.Add(pane);
            window.Children.Add(elsewhere);

            Assert.True(ToolAsyncStateController.FocusFellBackFrom(null, lookup));
            Assert.True(ToolAsyncStateController.FocusFellBackFrom(pane, lookup));
            Assert.True(ToolAsyncStateController.FocusFellBackFrom(row, lookup));
            Assert.False(ToolAsyncStateController.FocusFellBackFrom(lookup, lookup));
            Assert.False(ToolAsyncStateController.FocusFellBackFrom(elsewhere, lookup));
        });
    }

    /// <summary>
    /// The Server box shares its grid row with the port column, which the WinRM SSL boxes make
    /// taller: stretched, the one-line box stood 84 px high beside a 31 px Display name box.
    /// </summary>
    [Fact]
    public void TheServerBoxKeepsItsOwnHeightBesideATallerPortColumn()
    {
        XElement server = Named(Markup(@"Views\Dialogs\ServerDialog.xaml"), "DlgSrv_RemoteServerBox");

        Assert.Equal("Top", Attribute(server, "VerticalAlignment"));
    }

    private static string ControllerMethod(string signature)
    {
        string source = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Services", "ToolAsyncStateController.cs"));
        return ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), signature);
    }

    private static XDocument Markup(string relative) =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", relative));

    private static XElement Named(XDocument markup, string name) =>
        Assert.Single(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == name);

    private static string Attribute(XElement element, string name) =>
        Assert.IsType<string>((string?)element.Attribute(name));

    private static string? SetterValue(XElement style, string property) =>
        (string?)style.Elements().SingleOrDefault(s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == property)?.Attribute("Value");

    private static string TranslateKey(string markupExtension)
    {
        const string Prefix = "{loc:Translate ";
        Assert.StartsWith(Prefix, markupExtension, StringComparison.Ordinal);
        return markupExtension[Prefix.Length..^1];
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
