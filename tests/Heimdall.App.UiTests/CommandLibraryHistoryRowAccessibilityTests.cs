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

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Heimdall.App.Behaviors;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.CommandLibrary;

namespace Heimdall.App.UiTests;

/// <summary>
/// Reads what a screen reader receives for a command history row, the sibling of
/// <see cref="CommandLibraryRowAccessibilityTests"/> for the action rows.
/// </summary>
/// <remarks>
/// Read through a live UI Automation client, for the reason the action-row tests give: an
/// oracle on the markup alone stayed green while the rows announced their class name.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryHistoryRowAccessibilityTests
{
    [StaFact]
    public void HistoryRow_AnnouncesTitleHostSentAndTime_NotItsClassName()
    {
        var entry = new CommandLibraryHistoryEntry
        {
            ActionTitle = "Restart the print spooler",
            GeneratedCommand = "Restart-Service Spooler",
            Timestamp = "20/10/2026 12:00",
            TargetHost = "srv-01",
            IsExecuted = true,
            Localize = key => WpfTestHost.Localizer[key]
        };

        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;

        WpfTestHost.Invoke(() =>
        {
            var list = new ListView { Width = 360, Height = 240, ItemsSource = new[] { entry } };
            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(ItemContainerAccessibilityBehavior.IsEnabledProperty, true));
            list.ItemContainerStyle = itemStyle;

            window = new Window
            {
                Width = 480,
                Height = 320,
                Content = list,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            window.Show();
            list.UpdateLayout();

            var container = Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromItem(entry));
            AutomationProperties.SetAutomationId(container, "HistoryRow");
            windowHandle = new WindowInteropHelper(window).Handle;
        });

        try
        {
            var row = AutomationElement.FromHandle(windowHandle).FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "HistoryRow"));

            Assert.NotNull(row);
            var name = row!.Current.Name;

            Assert.DoesNotContain(nameof(CommandLibraryHistoryEntry), name, StringComparison.Ordinal);
            Assert.Equal(entry.AccessibleName, name);
            Assert.Contains("srv-01", name, StringComparison.Ordinal);
            Assert.Contains(WpfTestHost.Localizer["ToolCmdLibHistorySent"], name, StringComparison.Ordinal);
            Assert.Contains("20/10/2026 12:00", name, StringComparison.Ordinal);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    /// <summary>
    /// The shipped history list has to ask for the behaviour; the synthetic list above only
    /// proves that the entry and the behaviour produce a name when asked.
    /// </summary>
    [StaFact]
    public void TheShippedHistoryListEnablesTheContainerBehaviour()
    {
        WpfTestHost.Invoke(() =>
        {
            var view = new Heimdall.App.Views.Tools.CommandLibraryView();
            var list = FindHistoryList(view);

            Assert.NotNull(list);

            var enabled = list!.ItemContainerStyle?.Setters
                .OfType<Setter>()
                .Any(setter =>
                    setter.Property == ItemContainerAccessibilityBehavior.IsEnabledProperty
                    && setter.Value is true);

            Assert.True(
                enabled,
                "The history ListView's ItemContainerStyle does not enable "
                + "ItemContainerAccessibilityBehavior, so its rows announce their class name.");
        });
    }

    private static ListView? FindHistoryList(DependencyObject root)
    {
        if (root is ListView list)
        {
            var binding = System.Windows.Data.BindingOperations.GetBinding(list, ItemsControl.ItemsSourceProperty);
            if (binding?.Path?.Path == nameof(Heimdall.App.ViewModels.CommandLibraryViewModel.HistoryEntries))
            {
                return list;
            }
        }

        // The logical tree: the view is never rendered here.
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = FindHistoryList(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
