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
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.Views.Tools;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins the finishing points of the 2026-10-26 Command Library audit (lot D), each read from the
/// rendered view.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryFinishingTests
{
    /// <summary>
    /// The risk badge had no width of its own, so a title behind DANGER started about 22 px to
    /// the right of one behind RUN or INFO and the column of titles zigzagged.
    /// </summary>
    [StaFact]
    public void TitlesStartInOneColumnWhateverTheRiskBadge()
    {
        var entries = new[]
        {
            Entry("Show uptime", CriticalityLevel.Info),
            Entry("Restart a service", CriticalityLevel.Run),
            Entry("Wipe a disk", CriticalityLevel.Dangerous)
        };
        double[] titleLefts = [];
        Window? window = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var template = FindListBoundTo(new CommandLibraryView(), nameof(CommandLibraryViewModel.ActionsView))
                    .ItemTemplate;
                var list = new ListView { ItemsSource = entries, ItemTemplate = template, Width = 700 };
                window = Show(list, 300);

                titleLefts = entries
                    .Select(entry => (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(entry))
                    .Select(container => Descendants(container).OfType<TextBlock>()
                        .First(text => text.Text == ((CommandLibraryActionEntry)container.DataContext).Title))
                    .Select(title => title.TransformToAncestor(list).Transform(new Point(0, 0)).X)
                    .ToArray();
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }

        Assert.Equal(3, titleLefts.Length);
        Assert.All(titleLefts, left => Assert.Equal(titleLefts[0], left, precision: 0));
    }

    /// <summary>
    /// The detail panel's lists announced each row by its class name
    /// (<c>Heimdall.App.ViewModels.CommandLibrary.CommandLibraryParameterEntry</c>, measured over
    /// UIA). The name lives on the item container, where WPF reads it.
    /// </summary>
    [StaFact]
    public void TheDetailListsNameTheirRowsByWhatTheyShow()
    {
        var names = new Dictionary<string, string?>();

        Render(viewModel =>
        {
            viewModel.Parameters.Add(new CommandLibraryParameterEntry { Name = "path", Label = "Log path" });
            viewModel.SelectedActionExamples.Add(new CommandExample { Command = "tail -f /var/log/syslog", Description = "Follow syslog" });
            viewModel.SelectedActionLinks.Add(new ExternalLink { Title = "tail(1)", Url = "https://example.test/tail" });
            viewModel.IsGeneratorVisible = true;
        }, (view, viewModel) =>
        {
            names["parameters"] = FirstContainerName(view, nameof(CommandLibraryViewModel.Parameters));
            names["examples"] = FirstContainerName(view, nameof(CommandLibraryViewModel.SelectedActionExamples));
            names["links"] = FirstContainerName(view, nameof(CommandLibraryViewModel.SelectedActionLinks));
        });

        Assert.Equal("Log path", names["parameters"]);
        Assert.Equal("tail -f /var/log/syslog", names["examples"]);
        Assert.Equal("tail(1)", names["links"]);
    }

    /// <summary>
    /// The two lists the test above cannot stage (broadcast targets need open terminals, runbook
    /// variables a run) carry the same container setter.
    /// </summary>
    [StaTheory]
    [InlineData(nameof(CommandLibraryViewModel.BroadcastTargets))]
    [InlineData("Variables")]
    public void TheOtherRowListsNameTheirContainersToo(string itemsSourcePath)
    {
        bool named = false;

        WpfTestHost.Invoke(() =>
        {
            var items = Logical(new CommandLibraryView()).OfType<ItemsControl>()
                .Single(control => BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)
                    ?.Path?.Path == itemsSourcePath);
            named = items.ItemContainerStyle?.Setters.OfType<Setter>()
                .Any(setter => setter.Property == AutomationProperties.NameProperty) ?? false;
        });

        Assert.True(named, $"The {itemsSourcePath} rows have no container name and announce their class.");
    }

    /// <summary>
    /// Tab order follows what the eye reads: search, filters, list, the command bar left to right,
    /// then the header left to right. Indices are unique, so no two stops tie.
    /// </summary>
    [StaFact]
    public void TabOrderFollowsTheScreenAndNeverTies()
    {
        string[] visualOrder =
        [
            "SearchBox", "BtnFavoritesFilter", "CmbCategoryFilter", "CmbTagFilter", "CmbPlatformFilter",
            "CmbRiskFilter", "ActionList", "BtnEdit", "BtnDuplicate", "BtnDelete", "BtnCopy", "BtnInsert",
            "BtnSend", "BtnBroadcastToggle", "BtnAdd", "BtnExport", "BtnImport", "BtnCancelSync", "BtnSync",
            "BtnRunbooks", "BtnHistory", "BtnHelp"
        ];
        int[] indices = [];

        WpfTestHost.Invoke(() =>
        {
            var view = new CommandLibraryView();
            indices = visualOrder
                .Select(name => view.FindName(name) as Control
                    ?? throw new InvalidOperationException($"No control named {name}."))
                .Select(KeyboardNavigationTabIndex)
                .ToArray();
        });

        for (var i = 1; i < indices.Length; i++)
        {
            Assert.True(
                indices[i] > indices[i - 1],
                $"{visualOrder[i]} ({indices[i]}) does not come after {visualOrder[i - 1]} ({indices[i - 1]}).");
        }
    }

    private static int KeyboardNavigationTabIndex(Control control) => control.TabIndex;

    private static string? FirstContainerName(DependencyObject root, string itemsSourcePath)
    {
        var items = Descendants(root).OfType<ItemsControl>()
            .First(control => BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)
                ?.Path?.Path == itemsSourcePath);
        var container = items.ItemContainerGenerator.ContainerFromIndex(0);
        return container is null ? null : AutomationProperties.GetName(container);
    }

    private static ListView FindListBoundTo(DependencyObject root, string path)
        => Logical(root).OfType<ListView>().Single(list =>
            BindingOperations.GetBinding(list, ItemsControl.ItemsSourceProperty)?.Path?.Path == path);

    private static void Render(
        Action<CommandLibraryViewModel> arrange,
        Action<CommandLibraryView, CommandLibraryViewModel> read)
    {
        Window? window = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var viewModel = CreateViewModel();
                var view = new CommandLibraryView { DataContext = viewModel };
                arrange(viewModel);
                window = Show(view, 900);
                read(view, viewModel);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    private static Window Show(FrameworkElement content, double height)
    {
        var window = new Window
        {
            Width = 1000,
            Height = height,
            Content = content,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        content.UpdateLayout();
        content.Dispatcher.Invoke(DispatcherPriority.Background, new System.Action(() => { }));
        content.UpdateLayout();
        return window;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var descendant in Descendants(VisualTreeHelper.GetChild(root, i)))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in Logical(child))
            {
                yield return descendant;
            }
        }
    }

    private static CommandLibraryActionEntry Entry(string title, CriticalityLevel level)
    {
        var action = new ActionModel
        {
            Id = title,
            Title = title,
            Category = "Ops",
            Platform = Platform.Linux,
            Level = level,
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = title + "-t",
                Name = title,
                Platform = Platform.Linux,
                CommandPattern = "echo ok"
            }
        };

        return new CommandLibraryActionEntry(action, CreateViewModel());
    }

    private static CommandLibraryViewModel CreateViewModel()
        => new(
            serviceProvider: null!,
            configManager: null!,
            WpfTestHost.Localizer,
            dialogService: null!,
            gitSyncService: null!,
            transferService: null!);
}
