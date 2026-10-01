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
using System.Windows.Input;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// What the settings search finds, and whether it can show what it found.
/// </summary>
/// <remarks>
/// The 2026-09-30 audit and live pass: "delai" missed "Délai"; hints that exist only as tooltips
/// were not searchable; matches in controls collapsed for other reasons were counted and jumped to
/// with nothing on screen; a match inside the collapsed "Advanced timeouts" expander was jumped to
/// with the expander left shut; and Ctrl+F did nothing on the Settings tab.
/// </remarks>
public sealed class MainWindowSettingsSearchReachTests
{
    [Theory]
    [InlineData("Délai d'établissement du tunnel", "delai", true)]
    [InlineData("Délai d'établissement du tunnel", "ETABLISSEMENT", true)]
    [InlineData("Tunnel establishment delay", "delay", true)]
    [InlineData("Tunnel establishment delay", "timeout", false)]
    public void TheSearchIgnoresCaseAndAccents(string text, string query, bool expected)
        => Assert.Equal(expected, MainWindow.SettingsSearchMatches(text, query));

    [Fact]
    public void ATooltipOnlyHintIsSearchable()
    {
        RunOnStaThread(() =>
        {
            StackPanel panel = new();
            panel.Children.Add(new CheckBox { Content = "Bitmap caching", ToolTip = "Keeps drawn bitmaps between sessions" });
            panel.Children.Add(new TextBox { ToolTip = "Idle controls kept for reuse" });

            List<string> texts = Index(panel).Select(MainWindow.GetSettingsSearchEntryText).ToList();

            Assert.Contains(texts, text => text.Contains("drawn bitmaps", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("kept for reuse", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void AMatchHiddenForAnotherReasonIsNotReachable_OneInAClosedExpanderIsAndGetsOpened()
    {
        RunOnStaThread(() =>
        {
            TextBlock hidden = new() { Text = "Vault auto-lock" };
            StackPanel collapsed = new() { Visibility = Visibility.Collapsed };
            collapsed.Children.Add(hidden);

            TextBlock timeout = new() { Text = "Keep-alive interval" };
            StackPanel expanderContent = new();
            expanderContent.Children.Add(timeout);
            Expander advanced = new() { IsExpanded = false, Content = expanderContent };

            StackPanel page = new();
            page.Children.Add(collapsed);
            page.Children.Add(advanced);
            TabItem behaviour = new() { Content = page };
            TabControl tabs = new();
            tabs.Items.Add(new TabItem { Content = new TextBlock() });
            tabs.Items.Add(behaviour);

            Assert.False(MainWindow.IsSettingsSearchEntryReachable(hidden));
            Assert.True(MainWindow.IsSettingsSearchEntryReachable(timeout));

            MainWindow.RevealSettingsElement(timeout);

            Assert.True(advanced.IsExpanded);
            Assert.True(behaviour.IsSelected);
        });
    }

    [Fact]
    public void CtrlF_OnTheSettingsTab_FocusesTheSettingsSearch()
    {
        KeyboardShortcutService service = new();
        int filterFocus = 0;
        int searchFocus = 0;
        bool settingsTab = true;
        service.Register(
            Key.F,
            ModifierKeys.Control,
            () => filterFocus++,
            canExecute: () => MainWindow.CanFocusServerFilter(terminalFocused: false, filterVisible: !settingsTab));
        MainWindow.RegisterSettingsSearchShortcut(service, () => false, () => settingsTab, () => searchFocus++);

        Assert.True(service.TryHandle(Key.F, ModifierKeys.Control));
        Assert.Equal((0, 1), (filterFocus, searchFocus));

        settingsTab = false;
        Assert.True(service.TryHandle(Key.F, ModifierKeys.Control));
        Assert.Equal((1, 1), (filterFocus, searchFocus));

        // Alone, the search binding claims the key on the Settings tab only, and never from a session.
        KeyboardShortcutService alone = new();
        bool terminal = false;
        MainWindow.RegisterSettingsSearchShortcut(alone, () => terminal, () => settingsTab, () => searchFocus++);
        Assert.False(alone.TryHandle(Key.F, ModifierKeys.Control));
        settingsTab = true;
        terminal = true;
        Assert.False(alone.TryHandle(Key.F, ModifierKeys.Control));
        Assert.Equal(1, searchFocus);
    }

    private static List<MainWindow.SettingsSearchEntry> Index(DependencyObject root)
    {
        List<MainWindow.SettingsSearchEntry> entries = [];
        MainWindow.AddSettingsSearchEntries(root, new TabItem(), null, entries);
        return entries;
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
