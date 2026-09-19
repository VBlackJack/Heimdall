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
using TwinShell.Core.Enums;
using ActionModel = TwinShell.Core.Models.Action;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;

namespace Heimdall.App.UiTests;

/// <summary>
/// Reads what a screen reader actually receives for a command library action row.
/// </summary>
/// <remarks>
/// The row's data template writes its text into <c>TextBlock</c>s inside a
/// <c>DockPanel</c>, none of which the container's automation peer reports as its name, so
/// the row fell back to <c>ToString()</c> of the bound entry. That is the identical defect
/// already measured for the session tree and the command palette, which is why this is
/// read through a live UI Automation client rather than asserted against the markup: the
/// markup-level oracle stayed green for the whole life of the original defect.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryRowAccessibilityTests
{
    [StaFact]
    public void ActionRow_AnnouncesTitleRiskAndPlatform_NotItsClassName()
    {
        var entry = CreateEntry("Restart the print spooler", CriticalityLevel.Dangerous, Platform.Windows);

        ListView? list = null;
        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;

        WpfTestHost.Invoke(() =>
        {
            list = BuildList(entry);
            window = Show(list);

            var container = Assert.IsType<ListViewItem>(
                list.ItemContainerGenerator.ContainerFromItem(entry));
            AutomationProperties.SetAutomationId(container, "ActionRow");
            windowHandle = new WindowInteropHelper(window).Handle;
        });

        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            var row = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ActionRow"));

            Assert.NotNull(row);
            var name = row!.Current.Name;

            // The failure this exists for.
            Assert.DoesNotContain(nameof(CommandLibraryActionEntry), name, StringComparison.Ordinal);

            // And the assertion above alone would pass on an empty name, which is the
            // other way a row reaches a screen reader saying nothing useful.
            Assert.Equal(entry.AccessibleName, name);

            // The badges carry the two facts the user most needs before running a command,
            // and neither is spoken by the title alone.
            Assert.Contains(entry.RiskLabel, name, StringComparison.Ordinal);
            Assert.Contains(entry.PlatformLabel, name, StringComparison.Ordinal);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    /// <summary>
    /// Two rows differing only in risk must not announce the same thing, which is what an
    /// implementation returning the title alone would produce.
    /// </summary>
    [StaFact]
    public void RowsDifferingOnlyInRisk_AnnounceDifferentNames()
    {
        var info = CreateEntry("Show uptime", CriticalityLevel.Info, Platform.Linux);
        var dangerous = CreateEntry("Show uptime", CriticalityLevel.Dangerous, Platform.Linux);

        WpfTestHost.Invoke(() =>
            Assert.NotEqual(info.AccessibleName, dangerous.AccessibleName));
    }

    private static ListView BuildList(CommandLibraryActionEntry entry)
    {
        var list = new ListView
        {
            Width = 360,
            Height = 240,
            ItemsSource = new[] { entry }
        };

        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(ItemContainerAccessibilityBehavior.IsEnabledProperty, true));
        list.ItemContainerStyle = itemStyle;

        return list;
    }

    private static CommandLibraryActionEntry CreateEntry(
        string title, CriticalityLevel level, Platform platform)
    {
        var action = new ActionModel
        {
            Id = $"{title}-{level}",
            Title = title,
            Category = "Ops",
            Platform = platform,
            Level = level,
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = "t",
                Name = title,
                Platform = Platform.Linux,
                CommandPattern = "echo ok"
            }
        };

        // Only the localizer is on the path the entry's display properties take; the
        // services behind the other constructor parameters are never reached.
        var viewModel = new Heimdall.App.ViewModels.CommandLibraryViewModel(
            serviceProvider: null!,
            configManager: null!,
            WpfTestHost.Localizer,
            dialogService: null!,
            gitSyncService: null!,
            transferService: null!);

        return new CommandLibraryActionEntry(action, viewModel);
    }

    /// <summary>
    /// The synthetic list above proves the contract and the behaviour produce a usable
    /// name; it does not prove the shipped view asks for them. This reads the real
    /// <c>CommandLibraryView</c> markup, because enabling the behaviour in one place and
    /// testing it in another is how an inert accessibility fix ships looking done.
    /// </summary>
    [StaFact]
    public void TheShippedActionListEnablesTheContainerBehaviour()
    {
        WpfTestHost.Invoke(() =>
        {
            var view = new Heimdall.App.Views.Tools.CommandLibraryView();
            var list = FindActionList(view);

            Assert.NotNull(list);

            var enabled = list!.ItemContainerStyle?.Setters
                .OfType<Setter>()
                .Any(setter =>
                    setter.Property == ItemContainerAccessibilityBehavior.IsEnabledProperty
                    && setter.Value is true);

            Assert.True(
                enabled,
                "The action ListView's ItemContainerStyle does not enable "
                + "ItemContainerAccessibilityBehavior, so its rows announce their class name.");
        });
    }

    /// <summary>
    /// Finds the action list: the one whose <c>ItemsSource</c> binding names the view
    /// model's filtered view, so the history list cannot be mistaken for it.
    /// </summary>
    private static ListView? FindActionList(DependencyObject root)
    {
        if (root is ListView list)
        {
            var binding = System.Windows.Data.BindingOperations.GetBinding(
                list, ItemsControl.ItemsSourceProperty);
            if (binding?.Path?.Path == nameof(Heimdall.App.ViewModels.CommandLibraryViewModel.ActionsView))
            {
                return list;
            }
        }

        // The logical tree, not the visual one: the view is never rendered here, so no
        // template has been applied and the visual tree is empty. XAML-declared content
        // is in the logical tree from the moment InitializeComponent returns.
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = FindActionList(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static Window Show(UIElement content)
    {
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = content,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        content.UpdateLayout();
        return window;
    }
}
