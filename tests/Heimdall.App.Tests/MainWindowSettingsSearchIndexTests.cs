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

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes what the settings search index holds, and what a match jumps to.
/// </summary>
/// <remarks>
/// The index held TextBlocks and CheckBoxes only, so every theme name, every accent name and
/// every button on the settings tabs was missing from it: a box labelled "Search settings"
/// answered "No matching settings" for words the user was reading on screen. Combo choices
/// carry the extra rule that the item itself is in a closed popup and cannot be the thing the
/// jump scrolls to.
/// </remarks>
public sealed class MainWindowSettingsSearchIndexTests
{
    [Fact]
    public void ComboChoicesAndButtonLabels_AreIndexed()
    {
        RunOnStaThread(() =>
        {
            StackPanel panel = new();
            panel.Children.Add(new TextBlock { Text = "Theme" });
            panel.Children.Add(new CheckBox { Content = "Start minimized" });

            ComboBox themes = new();
            themes.Items.Add(new ComboBoxItem { Content = "Dracula" });
            themes.Items.Add(new ComboBoxItem { Content = "Parchment" });
            panel.Children.Add(themes);
            panel.Children.Add(new Button { Content = "Check now" });

            IReadOnlyList<string> texts = IndexTexts(panel);

            Assert.Contains("Dracula", texts);
            Assert.Contains("Parchment", texts);
            Assert.Contains("Check now", texts);
            Assert.Contains("Theme", texts);
            Assert.Contains("Start minimized", texts);
        });
    }

    [Fact]
    public void RadioChoices_AreIndexed()
    {
        RunOnStaThread(() =>
        {
            StackPanel panel = new();
            panel.Children.Add(new RadioButton { Content = "Every launch" });

            Assert.Contains("Every launch", IndexTexts(panel));
        });
    }

    // A button whose Content is a nested TextBlock was already reachable through that
    // TextBlock. Indexing the button instead would replace a label the search can read with a
    // container it cannot, and would stop the walk before the label.
    [Fact]
    public void ButtonWrappingATextBlock_StaysIndexedThroughTheTextBlock()
    {
        RunOnStaThread(() =>
        {
            TextBlock label = new() { Text = "Reset defaults" };
            StackPanel panel = new();
            panel.Children.Add(new Button { Content = label });

            List<MainWindow.SettingsSearchEntry> entries = Index(panel);

            Assert.Contains("Reset defaults", entries.Select(MainWindow.GetSettingsSearchEntryText));
            Assert.Same(label, Assert.Single(entries).Target);
        });
    }

    // The item lives in a popup that is closed when the jump happens: it has no rendered size
    // to bring into view and no visible background to highlight.
    [Fact]
    public void ComboChoice_JumpsToItsComboBox()
    {
        RunOnStaThread(() =>
        {
            ComboBoxItem choice = new() { Content = "Dracula" };
            ComboBox themes = new();
            themes.Items.Add(choice);

            Assert.Same(themes, MainWindow.ResolveSettingsSearchJumpTarget(choice));
        });
    }

    [Fact]
    public void AnythingElse_JumpsToItself()
    {
        RunOnStaThread(() =>
        {
            Button checkNow = new() { Content = "Check now" };

            Assert.Same(checkNow, MainWindow.ResolveSettingsSearchJumpTarget(checkNow));
        });
    }

    private static List<MainWindow.SettingsSearchEntry> Index(DependencyObject root)
    {
        List<MainWindow.SettingsSearchEntry> entries = [];
        MainWindow.AddSettingsSearchEntries(root, new TabItem(), null, entries);
        return entries;
    }

    private static IReadOnlyList<string> IndexTexts(DependencyObject root) =>
        Index(root).Select(MainWindow.GetSettingsSearchEntryText).ToList();

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
