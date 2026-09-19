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
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests;

/// <summary>
/// Reads what each history row's double-click gesture would actually copy.
/// </summary>
/// <remarks>
/// <para>
/// The history rows carry a double-click-to-copy gesture whose <c>CommandParameter</c> is
/// bound inside an <c>InputBindings</c> collection. An <c>InputBinding</c> is a
/// <c>Freezable</c> that sits outside the visual and logical trees, so it does not inherit
/// the per-container <c>DataContext</c> the way an element in the template does. The same
/// defect was already found and removed from this view's example list, where the fix note
/// records that the parameter "only resolved CommandParameter for the first realized
/// container".
/// </para>
/// <para>
/// This reads the second row specifically. A one-row list cannot distinguish a parameter
/// that resolves per container from one that resolved once against whichever item came
/// first, so it would pass in both worlds.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryHistoryRowCopyTests
{
    [StaFact]
    public void EachHistoryRowCopiesItsOwnCommand()
    {
        var copied = new List<string>();

        CommandLibraryView? view = null;
        Window? window = null;
        List<ListViewItem> containers = [];

        WpfTestHost.Invoke(() =>
        {
            var viewModel = CreateViewModel();
            viewModel.SetClipboardText = text =>
            {
                copied.Add(text);
                return true;
            };

            viewModel.HistoryEntries.Add(NewEntry("First action", "echo first"));
            viewModel.HistoryEntries.Add(NewEntry("Second action", "echo second"));
            viewModel.IsHistoryVisible = true;

            view = new CommandLibraryView { DataContext = viewModel };
            window = Show(view);

            var list = FindHistoryList(view)
                ?? throw new InvalidOperationException("History ListView not found in the view.");
            list.UpdateLayout();

            containers = viewModel.HistoryEntries
                .Select(entry => list.ItemContainerGenerator.ContainerFromItem(entry))
                .OfType<ListViewItem>()
                .ToList();
        });

        try
        {
            Assert.Equal(2, containers.Count);

            WpfTestHost.Invoke(() =>
            {
                foreach (var container in containers)
                {
                    InvokeDoubleClickGesture(container);
                }
            });

            // Each row must have copied its own command, in row order.
            Assert.Equal(["echo first", "echo second"], copied);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    /// <summary>
    /// Every feedback target the view model can emit must resolve to a control here.
    /// </summary>
    /// <remarks>
    /// Copying an example shipped with no acknowledgement at all because the view model
    /// emitted a target the view's switch did not list, and an unlisted target did
    /// nothing and said nothing. The theory enumerates the declared constants so adding
    /// a fourth without wiring it fails here rather than in the user's hands.
    /// </remarks>
    [StaTheory]
    [InlineData(CommandLibraryViewModel.CopyTarget)]
    [InlineData(CommandLibraryViewModel.SendTarget)]
    [InlineData(CommandLibraryViewModel.CopyExampleTarget)]
    public void EveryDeclaredFeedbackTargetResolvesToAControl(string target)
    {
        WpfTestHost.Invoke(() =>
        {
            var view = new CommandLibraryView();

            Assert.NotNull(view.ResolveFeedbackButton(target));
        });
    }

    [StaFact]
    public void AnUnknownFeedbackTargetResolvesToNothing()
    {
        // Positive control: the test above would pass on a mapping that returned the
        // same button for anything at all, which would hide a target going astray.
        WpfTestHost.Invoke(() =>
        {
            var view = new CommandLibraryView();

            Assert.Null(view.ResolveFeedbackButton("no-such-target"));
        });
    }

    /// <summary>
    /// Fires the double-click gesture the way the user's mouse would: through whatever
    /// input binding the row actually carries, reading its command and parameter at
    /// invoke time rather than trusting the markup.
    /// </summary>
    private static void InvokeDoubleClickGesture(DependencyObject container)
    {
        foreach (var element in Descendants(container).OfType<FrameworkElement>())
        {
            foreach (var binding in element.InputBindings.OfType<MouseBinding>())
            {
                if (binding.MouseAction != MouseAction.LeftDoubleClick)
                {
                    continue;
                }

                var command = binding.Command
                    ?? throw new InvalidOperationException("The row's double-click binding has no command.");
                command.Execute(binding.CommandParameter);
                return;
            }
        }

        throw new InvalidOperationException(
            "No left-double-click gesture found on the history row. "
            + "If the gesture moved, move this test with it rather than deleting it.");
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

    private static ListView? FindHistoryList(DependencyObject root)
    {
        foreach (var list in Descendants(root).OfType<ListView>())
        {
            var binding = System.Windows.Data.BindingOperations.GetBinding(
                list, ItemsControl.ItemsSourceProperty);
            if (binding?.Path?.Path == nameof(CommandLibraryViewModel.HistoryEntries))
            {
                return list;
            }
        }

        return null;
    }

    private static CommandLibraryHistoryEntry NewEntry(string title, string command)
        => new()
        {
            ActionTitle = title,
            GeneratedCommand = command,
            Timestamp = "now"
        };

    private static CommandLibraryViewModel CreateViewModel()
        => new(
            serviceProvider: null!,
            configManager: null!,
            WpfTestHost.Localizer,
            dialogService: null!,
            gitSyncService: null!,
            transferService: null!);

    private static Window Show(UIElement content)
    {
        var window = new Window
        {
            Width = 640,
            Height = 700,
            Content = content,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        content.UpdateLayout();
        return window;
    }
}
