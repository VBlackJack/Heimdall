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
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Text that is data shows its underscores where access keys are read, and assistive technology
/// hears the real text. The menu tests that need the shell harness are in
/// <c>LiteralAccessTextMenuHarnessTests.cs</c>.
/// </summary>
/// <remarks>
/// The sites that need a live window are not covered here: the SFTP bookmark menu is built by the
/// pane's click handler, and the external tool and macro menus read files of the user's profile.
/// They call the same helper, which is what is tested. The AccessText oracle shows the key it finds
/// and, for a label without one, the text it draws; for a label with a key, WPF keeps the marker in
/// the run until the control is in a window, so a second single underscore cannot be read there.
/// </remarks>
public sealed class LiteralAccessTextTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("/etc/my_app.conf", "/etc/my__app.conf")]
    [InlineData("a_b_c", "a__b__c")]
    [InlineData("_lead", "__lead")]
    [InlineData("trail_", "trail__")]
    [InlineData("a__b", "a____b")]
    [InlineData("_", "__")]
    public void LiteralAccessText_Literal_DoublesEveryUnderscore(string text, string expected)
    {
        Assert.Equal(expected, LiteralAccessText.Literal(text));
    }

    [Fact]
    public void LiteralAccessText_Literal_ReadsNullAsEmpty()
    {
        Assert.Equal(string.Empty, LiteralAccessText.Literal(null));
    }

    [Fact]
    public void LiteralAccessText_SetLiteral_EscapesTheHeaderAndKeepsTheRealTextAsName()
    {
        RunOnStaThread(() =>
        {
            MenuItem item = new();

            LiteralAccessText.SetLiteral(item, "/etc/my_app.conf");

            Assert.Equal("/etc/my__app.conf", item.Header);
            Assert.Equal("/etc/my_app.conf", AutomationProperties.GetName(item));
        });
    }

    [Fact]
    public void LiteralAccessText_SetFormatted_EscapesTheDataAndNotTheTemplate()
    {
        RunOnStaThread(() =>
        {
            MenuItem item = new();

            LiteralAccessText.SetFormatted(item, "_Open {0} on {1}", "ssh_host", 22);

            Assert.Equal("_Open ssh__host on 22", item.Header);
            Assert.Equal("Open ssh_host on 22", AutomationProperties.GetName(item));
        });
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("_Open", "Open")]
    [InlineData("Open _{0}", "Open {0}")]
    [InlineData("a__b", "a_b")]
    [InlineData("_a__b", "a_b")]
    [InlineData("trail_", "trail_")]
    [InlineData("_", "_")]
    public void LiteralAccessText_RemoveAccessKeyMarker_ReadsALabelTheWayTheParserDoes(string label, string expected)
    {
        Assert.Equal(expected, LiteralAccessText.RemoveAccessKeyMarker(label));
    }

    [Fact]
    public void LiteralAccessText_FormatName_KeepsDataAndDropsTheTemplateKey()
    {
        Assert.Equal("Open my_app", LiteralAccessText.FormatName("_Open {0}", "my_app"));
    }

    // The oracle is WPF's own AccessText, the control the themed templates put a string in: its
    // AccessKey is empty and its displayed text is the original, for what Literal produced.
    [Theory]
    [InlineData("/etc/my_app.conf")]
    [InlineData("_lead")]
    [InlineData("trail_")]
    [InlineData("a__b")]
    [InlineData("a___b_")]
    [InlineData("_")]
    [InlineData("plain")]
    public void LiteralAccessText_Literal_IsShownByAccessTextAsTheOriginalWithNoKey(string data)
    {
        RunOnStaThread(() =>
        {
            AccessText shown = new() { Text = LiteralAccessText.Literal(data) };

            Assert.Equal(default(char), shown.AccessKey);
            Assert.Equal(data, DisplayedText(shown));
        });
    }

    // The same oracle for the name rule. A label without a key is drawn as the text the rule returns;
    // a label with one gets an access key, the character after its first single underscore, and the
    // rule returns the label without that one underscore (WPF keeps the marker in the run it draws
    // until the control is in a window, so the key is what a window-less test can read).
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a__b", "a_b")]
    [InlineData("trail_", "trail_")]
    [InlineData("_", "_")]
    public void LiteralAccessText_RemoveAccessKeyMarker_AgreesWithAccessTextForALabelWithoutAKey(string label, string shown)
    {
        RunOnStaThread(() =>
        {
            AccessText text = new() { Text = label };

            Assert.Equal(default(char), text.AccessKey);
            Assert.Equal(shown, DisplayedText(text));
            Assert.Equal(shown, LiteralAccessText.RemoveAccessKeyMarker(label));
        });
    }

    [Theory]
    [InlineData("_Open", 'O', "Open")]
    [InlineData("Open _{0}", '{', "Open {0}")]
    [InlineData("_a__b", 'a', "a_b")]
    [InlineData("_a_b", 'a', "a_b")]
    public void LiteralAccessText_RemoveAccessKeyMarker_DropsTheMarkerOfTheKeyAccessTextFinds(string label, char key, string shown)
    {
        RunOnStaThread(() =>
        {
            AccessText text = new() { Text = label };

            Assert.Equal(char.ToUpperInvariant(key), char.ToUpperInvariant(text.AccessKey));
            Assert.Equal(shown, LiteralAccessText.RemoveAccessKeyMarker(label));
        });
    }

    [Fact]
    public async Task ToolContextMenuHelper_HostActions_ShowAHostWithUnderscoresLiterally()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        RunOnStaThread(() =>
        {
            List<object> items = ToolContextMenuHelper.BuildHostActions(
                "web_01",
                null,
                [22, 80],
                localizer,
                (_, _, _) => { },
                _ => { });

            MenuItem[] menuItems = [.. items.OfType<MenuItem>()];
            SessionLaunchRequest request = SessionProtocolChoice.ForHost("web_01", [22, 80])!;
            Assert.Contains('_', request.Destination);
            string connect = string.Format(localizer["ToolCtxOpenSession"], request.Protocol, request.Destination);
            string browser = string.Format(localizer["ToolCtxOpenBrowser"], "http://web_01:80");
            MenuItem session = Assert.Single(menuItems, item => AutomationProperties.GetName(item) == connect);
            MenuItem open = Assert.Single(menuItems, item => AutomationProperties.GetName(item) == browser);
            Assert.Equal(string.Format(localizer["ToolCtxOpenSession"], request.Protocol, LiteralAccessText.Literal(request.Destination)), session.Header);
            Assert.Equal(string.Format(localizer["ToolCtxOpenBrowser"], "http://web__01:80"), open.Header);
        });
    }

    private static string DisplayedText(AccessText text)
    {
        // AccessText hosts the TextBlock that draws the text, as its one visual child.
        TextBlock host = Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(text, 0));
        return string.Concat(host.Inlines.Select(InlineText));
    }

    private static string InlineText(Inline inline)
    {
        return inline switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.Select(InlineText)),
            _ => string.Empty,
        };
    }

    // A content presenter builds access-key text only for string content that has no template. This
    // is the reasoning behind the shared template: the same string, drawn with and without it.
    [Fact]
    public void LiteralTextTemplate_ContentPresenter_DrawsTheStringAsItIsOnlyWithATemplate()
    {
        RunOnStaThread(() =>
        {
            ContentPresenter plain = Present("snake_case", template: null);
            ContentPresenter templated = Present("snake_case", TextBlockTemplate());

            Assert.NotEmpty(Descendants<AccessText>(plain));
            Assert.Empty(Descendants<AccessText>(templated));
            Assert.Equal("snake_case", Assert.Single(Descendants<TextBlock>(templated)).Text);
        });
    }

    // The settings search reads the Content of a control when it is a string: the check box of the
    // known_hosts import keeps its string content and shows it through the template.
    [Fact]
    public async Task SettingsSearch_FindsTheKnownHostsCheckBoxByItsLabel_WhateverTheTemplate()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        string label = localizer["SettingsLabelSyncKnownHostsAtStartup"];
        RunOnStaThread(() =>
        {
            StackPanel panel = new();
            panel.Children.Add(new CheckBox { Content = label, ContentTemplate = TextBlockTemplate() });
            List<MainWindow.SettingsSearchEntry> entries = [];

            MainWindow.AddSettingsSearchEntries(panel, new TabItem(), entries);

            string text = MainWindow.GetSettingsSearchEntryText(Assert.Single(entries));
            Assert.Equal(label, text);
            Assert.True(MainWindow.SettingsSearchMatches(text, "OpenSSH"));
            Assert.True(MainWindow.SettingsSearchMatches(text, "startup"));
        });
    }

    private static ContentPresenter Present(string content, DataTemplate? template)
    {
        ContentPresenter presenter = new() { Content = content, ContentTemplate = template, RecognizesAccessKey = true };
        presenter.Measure(new Size(500, 500));
        presenter.Arrange(new Rect(0, 0, 500, 500));
        presenter.UpdateLayout();
        return presenter;
    }

    private static DataTemplate TextBlockTemplate()
    {
        FrameworkElementFactory text = new(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        return new DataTemplate { VisualTree = text };
    }

    private static List<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        List<T> found = [];
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Descendants<T>(child));
        }

        return found;
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
