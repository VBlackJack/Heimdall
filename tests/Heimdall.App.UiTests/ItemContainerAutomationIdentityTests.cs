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
using Heimdall.App.ViewModels.CommandPalette;

namespace Heimdall.App.UiTests;

/// <summary>
/// Covers item containers that are not tree nodes, through a live UI Automation client.
/// </summary>
/// <remarks>
/// The behaviour used to accept <c>TreeViewItem</c> only, so enabling it on a list did nothing
/// and did it silently. These tests read the name a client actually receives, because the
/// source-level alternative stayed green for the entire life of the original defect: the markup
/// it looked for was present, and inert.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class ItemContainerAutomationIdentityTests
{
    [StaFact]
    public void ListBoxContainer_AnnouncesTheItemName_NotItsClassName()
    {
        var variant = new SnippetVariantDisplayItem
        {
            DisplayLabel = "Restart the print spooler",
            PreviewCommand = "Restart-Service Spooler",
            Variant = new SnippetVariant(
                SnippetVariantKind.Example,
                Platform: null,
                ExampleIndex: 0,
                Template: null,
                LiteralCommand: "Restart-Service Spooler",
                PreviewCommand: "Restart-Service Spooler")
        };

        ListBox? list = null;
        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;

        WpfTestHost.Invoke(() =>
        {
            list = new ListBox
            {
                Width = 320,
                Height = 240,
                ItemsSource = new[] { variant }
            };
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(
                ItemContainerAccessibilityBehavior.IsEnabledProperty,
                true));
            list.ItemContainerStyle = itemStyle;

            window = Show(list);
            var container = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(variant));
            AutomationProperties.SetAutomationId(container, "VariantRow");
            windowHandle = new WindowInteropHelper(window).Handle;
        });

        try
        {
            AutomationElement root = AutomationElement.FromHandle(windowHandle);
            AutomationElement? row = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "VariantRow"));

            Assert.NotNull(row);

            string name = row!.Current.Name;

            // The failure this exists for: without the behaviour the container falls back to
            // ToString() of the bound item, which is the raw view-model type name.
            Assert.DoesNotContain(nameof(SnippetVariantDisplayItem), name, StringComparison.Ordinal);

            // And the assertion above alone would pass on an empty name, which is the other way
            // this row can reach a screen reader saying nothing useful.
            Assert.Equal("Restart the print spooler", name);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    [StaFact]
    public void ListBoxContainer_WithoutTheContract_IsLeftAlone()
    {
        // An item that does not implement IAccessibleItemViewModel must not pick up the previous
        // item's identity from a recycled container. Clearing is not a fix for such an item, but
        // carrying somebody else's name would be worse than announcing a class name.
        var opaque = new object();

        ListBox? list = null;
        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;

        WpfTestHost.Invoke(() =>
        {
            list = new ListBox
            {
                Width = 320,
                Height = 240,
                ItemsSource = new[] { opaque }
            };
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(
                ItemContainerAccessibilityBehavior.IsEnabledProperty,
                true));
            list.ItemContainerStyle = itemStyle;

            window = Show(list);
            var container = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(opaque));
            AutomationProperties.SetAutomationId(container, "OpaqueRow");
            windowHandle = new WindowInteropHelper(window).Handle;
        });

        try
        {
            AutomationElement root = AutomationElement.FromHandle(windowHandle);
            AutomationElement? row = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OpaqueRow"));

            Assert.NotNull(row);
            Assert.DoesNotContain(
                "Restart the print spooler",
                row!.Current.Name,
                StringComparison.Ordinal);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    private static Window Show(UIElement content)
    {
        var window = new Window
        {
            Width = 360,
            Height = 280,
            Content = content,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        content.UpdateLayout();
        return window;
    }
}
