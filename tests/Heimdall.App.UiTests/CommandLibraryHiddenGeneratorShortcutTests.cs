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
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.Views.Tools;
using TwinShell.Core.Models;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins that the generator's shortcuts act only while the generator is on screen.
/// </summary>
/// <remarks>
/// <para>
/// Enter, Ctrl+Enter and Shift+Enter are declared on the whole view and point at the generator's
/// Copy, Send and Insert. A single-line text box does not handle Enter, so the key bubbles up to
/// them from anywhere in the view. With a runbook run open, the generator is hidden but keeps the
/// command of the action last selected: Enter in a run variable copied that command and wrote it to
/// the history, and Ctrl+Enter sent it to the terminal, a command the operator could not see.
/// </para>
/// <para>
/// Enter is pressed for real, through the variable's own text box. The modified gestures are run
/// through the binding the view declares, for the reason <see cref="CommandLibraryShortcutTests"/>
/// gives: a test cannot hold Control or Shift down.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryHiddenGeneratorShortcutTests
{
    private const string GeneratorCommand = "echo generator";

    [StaFact]
    public void EnterInARunVariableCopiesNothing()
    {
        var copied = new List<string>();

        Render(copied, [], [], generatorVisible: false, (view, _) =>
        {
            var variable = Descendants(view.FindName("RunbookRunPanel") as DependencyObject
                    ?? throw new InvalidOperationException("No run panel."))
                .OfType<TextBox>()
                .Single(box => !box.IsReadOnly);
            Assert.True(variable.IsVisible, "The run variable is not on screen.");

            PressReturn(variable);
        });

        Assert.Empty(copied);
    }

    [StaFact]
    public void TheModifiedGesturesReachNothingWhileTheGeneratorIsHidden()
    {
        var sent = new List<string>();
        var inserted = new List<string>();

        Render([], sent, inserted, generatorVisible: false, (view, _) =>
        {
            Gesture(view, ModifierKeys.Control).Execute(null);
            Gesture(view, ModifierKeys.Shift).Execute(null);
        });

        Assert.Empty(sent);
        Assert.Empty(inserted);
    }

    /// <summary>
    /// Positive control on the same harness: with the generator on screen, the same press copies
    /// and the same gestures send and insert, so the tests above fail on the rule and not on a
    /// harness that delivers nothing.
    /// </summary>
    [StaFact]
    public void WithTheGeneratorOnScreenTheShortcutsAct()
    {
        var copied = new List<string>();
        var sent = new List<string>();
        var inserted = new List<string>();

        Render(copied, sent, inserted, generatorVisible: true, (view, _) =>
        {
            var variable = Descendants(view.FindName("RunbookRunPanel") as DependencyObject
                    ?? throw new InvalidOperationException("No run panel."))
                .OfType<TextBox>()
                .Single(box => !box.IsReadOnly);

            PressReturn(variable);
            Gesture(view, ModifierKeys.Control).Execute(null);
            Gesture(view, ModifierKeys.Shift).Execute(null);
        });

        Assert.Equal([GeneratorCommand], copied);
        Assert.Equal([GeneratorCommand], sent);
        Assert.Equal([GeneratorCommand], inserted);
    }

    private static void Render(
        List<string> copied,
        List<string> sent,
        List<string> inserted,
        bool generatorVisible,
        Action<CommandLibraryView, CommandLibraryViewModel> act)
    {
        Window? window = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var viewModel = new CommandLibraryViewModel(
                    serviceProvider: null!,
                    configManager: null!,
                    WpfTestHost.Localizer,
                    dialogService: null!,
                    gitSyncService: null!,
                    transferService: null!)
                {
                    SetClipboardText = text => { copied.Add(text); return true; },
                    SendCommandHandler = sent.Add,
                    InsertTextHandler = inserted.Add,
                    CanSendToTerminalProbe = () => true
                };

                // The state a run leaves behind: the action last selected still fills the
                // generator, which the Runbooks panel has hidden.
                viewModel.GeneratedCommand = GeneratorCommand;
                viewModel.IsCommandValid = true;
                viewModel.ActiveRun = new RunbookRunViewModel(Runbook(), new RunbookRunContext
                {
                    FindAction = _ => null,
                    // A literal step: the run never asks the generator for it.
                    Generator = null!,
                    DialogService = null!,
                    Localize = WpfTestHost.Translate
                });
                viewModel.IsRunbooksVisible = true;
                viewModel.IsGeneratorVisible = generatorVisible;

                var view = new CommandLibraryView { DataContext = viewModel };
                window = new Window
                {
                    Width = 1000,
                    Height = 900,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                window.Show();
                view.UpdateLayout();
                view.Dispatcher.Invoke(DispatcherPriority.Background, new System.Action(() => { }));
                view.UpdateLayout();

                act(view, viewModel);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    private static CommandBatch Runbook()
        => new()
        {
            Id = "rb-1",
            Name = "Restart a unit",
            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "nginx" },
            Commands =
            [
                new BatchCommandItem
                {
                    Order = 0,
                    Command = "systemctl restart {{unit}}"
                }
            ]
        };

    private static void PressReturn(UIElement target)
    {
        target.Focus();
        target.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(target),
            0,
            Key.Return)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static ICommand Gesture(CommandLibraryView view, ModifierKeys modifiers)
        => view.InputBindings
            .OfType<KeyBinding>()
            .Single(binding => binding.Key == Key.Return && binding.Modifiers == modifiers)
            .Command;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
