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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.Views.Tools;
using TwinShell.Core.Models;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins how the bottom of the library shares its height with the action list.
/// </summary>
/// <remarks>
/// <para>
/// The generator, history and runbook panels sat in auto-sized rows under a star-sized list.
/// An auto row takes what its content asks for first, so a generator with notes and examples
/// took up to its cap of 450 px whatever the window had left. Measured on the running app at
/// 1024x768: the list was gone and the Copy / Send bar was clipped below the tool, reachable
/// only by keyboard.
/// </para>
/// <para>
/// Both the list height and the bar position are read from the rendered view, because the
/// defect lives in how the grid distributes height, which no view-model test can see.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryBottomPanelLayoutTests
{
    /// <summary>A view height in the range a 1366x768 laptop leaves the tool.</summary>
    private const double ShortViewHeight = 520;

    /// <summary>A view height where every panel fits under its own cap.</summary>
    private const double TallViewHeight = 1400;

    private const double ViewWidth = 900;

    /// <summary>Enough examples to push the generator past any height a short view has.</summary>
    private const int ExampleCount = 12;

    [StaFact]
    public void AShortViewKeepsTheActionListOnScreen()
    {
        double listHeight = 0;
        double listMinHeight = 0;

        RenderGenerator(ShortViewHeight, (view, _) =>
        {
            var list = FindActionList(view);
            listHeight = list.ActualHeight;
            listMinHeight = (double)view.FindResource("CmdLibActionListMinHeight");
        });

        Assert.True(listMinHeight > 0, "The list's minimum height resource resolved to nothing.");
        Assert.True(
            listHeight >= listMinHeight - 1,
            $"The action list got {listHeight:F0} px, below its minimum of {listMinHeight:F0} px.");
    }

    [StaFact]
    public void AShortViewKeepsTheCommandBarInsideTheTool()
    {
        double barBottom = 0;
        double viewHeight = 0;

        RenderGenerator(ShortViewHeight, (view, _) =>
        {
            var copy = (Button)view.FindName("BtnCopy");
            barBottom = copy.TransformToAncestor(view)
                .Transform(new Point(0, copy.ActualHeight)).Y;
            viewHeight = view.ActualHeight;
        });

        Assert.True(barBottom > 0, "Copy has no height: it was not laid out.");
        Assert.True(
            barBottom <= viewHeight + 1,
            $"Copy ends at {barBottom:F0} px in a view {viewHeight:F0} px high.");
    }

    /// <summary>
    /// Positive control for the two above: with room to spare the generator keeps its own
    /// cap, so the fix shares the height rather than shrinking the panel everywhere.
    /// </summary>
    [StaFact]
    public void ATallViewLetsTheGeneratorReachItsCap()
    {
        double generatorHeight = 0;
        double cap = 0;

        RenderGenerator(TallViewHeight, (view, _) =>
        {
            generatorHeight = FindGeneratorPanel(view).ActualHeight;
            cap = (double)view.FindResource("CmdLibGeneratorMaxHeight");
        });

        Assert.True(
            generatorHeight >= cap - 1,
            $"The generator stopped at {generatorHeight:F0} px under a cap of {cap:F0} px.");
    }

    /// <summary>
    /// The history list was docked left inside its panel because an empty-state text, not the
    /// list, was the panel's last child. Rows took the width of their content and the
    /// timestamp, Copy and Replay ended around the middle of the panel.
    /// </summary>
    [StaFact]
    public void HistoryRowsSpanThePanel()
    {
        double rowWidth = 0;
        double panelWidth = 0;

        Render(ShortViewHeight, viewModel =>
        {
            viewModel.HistoryEntries.Add(new CommandLibraryHistoryEntry
            {
                ActionTitle = "Short",
                GeneratedCommand = "ls",
                Timestamp = "now",
                IsReadable = true
            });
            viewModel.IsHistoryVisible = true;
        }, (view, viewModel) =>
        {
            var list = FindListBoundTo(view, nameof(CommandLibraryViewModel.HistoryEntries));
            var row = (FrameworkElement)list.ItemContainerGenerator
                .ContainerFromItem(viewModel.HistoryEntries[0]);
            rowWidth = row.ActualWidth;
            panelWidth = ((FrameworkElement)VisualTreeHelper.GetParent(list)).ActualWidth;
        });

        Assert.True(panelWidth > 0, "The history panel was not laid out.");
        Assert.True(
            rowWidth >= panelWidth * 0.95,
            $"A history row is {rowWidth:F0} px wide in a panel of {panelWidth:F0} px.");
    }

    private static void RenderGenerator(
        double height, Action<CommandLibraryView, CommandLibraryViewModel> read)
        => Render(height, viewModel =>
        {
            viewModel.SelectedActionNotes = "Notes long enough to wrap onto a second line once "
                + "the panel narrows, the way the shipped notes do.";
            for (var i = 0; i < ExampleCount; i++)
            {
                viewModel.SelectedActionExamples.Add(new CommandExample
                {
                    Command = $"echo example-{i}",
                    Description = $"Example {i}"
                });
            }

            viewModel.IsGeneratorVisible = true;
        }, read);

    private static void Render(
        double height,
        Action<CommandLibraryViewModel> arrange,
        Action<CommandLibraryView, CommandLibraryViewModel> read)
    {
        Window? window = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var viewModel = CreateViewModel();
                arrange(viewModel);
                var view = new CommandLibraryView { DataContext = viewModel };
                window = new Window
                {
                    Width = ViewWidth,
                    Height = height,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize
                };
                window.Show();
                view.UpdateLayout();
                // The panels' caps follow the body's ActualHeight, which is only known once a
                // layout pass has run; let the bindings it fed settle before reading.
                view.Dispatcher.Invoke(DispatcherPriority.Background, new System.Action(() => { }));
                view.UpdateLayout();
                read(view, viewModel);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    private static ListView FindActionList(DependencyObject root)
        => FindListBoundTo(root, nameof(CommandLibraryViewModel.ActionsView));

    private static Border FindGeneratorPanel(DependencyObject root)
        => Descendants(root).OfType<Border>().Single(border =>
            System.Windows.Data.BindingOperations.GetBinding(border, UIElement.VisibilityProperty)
                ?.Path?.Path == nameof(CommandLibraryViewModel.IsGeneratorVisible));

    private static ListView FindListBoundTo(DependencyObject root, string path)
        => Descendants(root).OfType<ListView>().Single(list =>
            System.Windows.Data.BindingOperations.GetBinding(list, ItemsControl.ItemsSourceProperty)
                ?.Path?.Path == path);

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

    private static CommandLibraryViewModel CreateViewModel()
        => new(
            serviceProvider: null!,
            configManager: null!,
            WpfTestHost.Localizer,
            dialogService: null!,
            gitSyncService: null!,
            transferService: null!);
}
