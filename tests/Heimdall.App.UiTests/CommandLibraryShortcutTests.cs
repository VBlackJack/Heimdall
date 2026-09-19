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
using System.Windows.Input;
using System.Windows.Interop;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests;

/// <summary>
/// Covers the Command Library's keyboard shortcuts on the rendered view.
/// </summary>
/// <remarks>
/// <para>
/// A shortcut declared on the wrong element, or bound to a command that does not exist,
/// fails silently and looks exactly like a shortcut nobody pressed. So the unmodified one
/// is driven by raising a real key event through the view, which is the only way to learn
/// that the bindings sit where keys actually arrive.
/// </para>
/// <para>
/// <b>Why the modified ones are read rather than pressed.</b> <c>KeyGesture.Matches</c>
/// consults <c>Keyboard.Modifiers</c>, which reports the physical keyboard, and a test
/// cannot hold Control down. Raising a Ctrl+D event would therefore match nothing and the
/// test would fail for a reason that has nothing to do with the code. Those two are checked
/// by reading the gesture that was declared and then running the command it points at - so
/// a wrong key, a wrong modifier, a missing binding or a dead command all still fail. What
/// it cannot prove is that the window does not eat the chord first; the keys were chosen
/// against the window's own handlers for that reason, and it is written down in the markup.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryShortcutTests
{
    [StaFact]
    public void PressingReturnCopiesTheCommand()
    {
        var copied = new List<string>();

        CommandLibraryView? view = null;
        Window? window = null;

        WpfTestHost.Invoke(() =>
        {
            var viewModel = CreateViewModel();
            viewModel.SetClipboardText = text => { copied.Add(text); return true; };
            viewModel.ApplyExampleText("echo ready");

            view = new CommandLibraryView { DataContext = viewModel };
            window = Show(view);
            view.Focus();

            view.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(view),
                0,
                Key.Return)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
        });

        try
        {
            Assert.Equal(["echo ready"], copied);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    /// <summary>
    /// Positive control for the test above: without a command to copy, the same key press
    /// must do nothing. Otherwise "Return copies" could be satisfied by a view that copies
    /// on any key at all.
    /// </summary>
    [StaFact]
    public void PressingReturnWithNothingGeneratedCopiesNothing()
    {
        var copied = new List<string>();

        CommandLibraryView? view = null;
        Window? window = null;

        WpfTestHost.Invoke(() =>
        {
            var viewModel = CreateViewModel();
            viewModel.SetClipboardText = text => { copied.Add(text); return true; };

            view = new CommandLibraryView { DataContext = viewModel };
            window = Show(view);
            view.Focus();

            view.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(view),
                0,
                Key.Return)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
        });

        try
        {
            Assert.Empty(copied);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    /// <summary>
    /// Each shortcut has to be declared with the exact key and modifiers intended, on the
    /// view itself, and point at a command that exists.
    /// </summary>
    [StaTheory]
    [InlineData(Key.Return, ModifierKeys.None, nameof(CommandLibraryViewModel.CopyCommand))]
    [InlineData(Key.Return, ModifierKeys.Control, nameof(CommandLibraryViewModel.SendCommand))]
    [InlineData(Key.D, ModifierKeys.Control, nameof(CommandLibraryViewModel.DuplicateSelectedCommand))]
    public void EachShortcutIsDeclaredOnTheViewAndPointsAtItsCommand(
        Key key, ModifierKeys modifiers, string commandName)
    {
        Window? window = null;

        WpfTestHost.Invoke(() =>
        {
            var viewModel = CreateViewModel();
            var view = new CommandLibraryView { DataContext = viewModel };

            // Shown, not merely constructed. An InputBinding is a Freezable outside the
            // visual tree: its Command binding does not resolve until the view is in a
            // loaded window, and reading it before then returns null for every gesture -
            // measured, which is why this test opens a window it otherwise would not need.
            window = Show(view);

            var binding = view.InputBindings
                .OfType<KeyBinding>()
                .SingleOrDefault(candidate => candidate.Key == key && candidate.Modifiers == modifiers);

            Assert.True(binding is not null, $"No shortcut declared for {modifiers}+{key}.");

            var expected = typeof(CommandLibraryViewModel)
                .GetProperty(commandName)!
                .GetValue(viewModel);

            Assert.Same(expected, binding!.Command);
        });

        WpfTestHost.Invoke(() => window!.Close());
    }

    /// <summary>
    /// The three chords were picked because the window does not already take them. A
    /// fourth one added without that check would collide silently, so the set is pinned.
    /// </summary>
    [StaFact]
    public void TheViewDeclaresExactlyTheThreeIntendedShortcuts()
    {
        Window? window = null;

        WpfTestHost.Invoke(() =>
        {
            var view = new CommandLibraryView { DataContext = CreateViewModel() };
            window = Show(view);

            var declared = view.InputBindings
                .OfType<KeyBinding>()
                .Select(binding => $"{binding.Modifiers}+{binding.Key}")
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["Control+D", "Control+Return", "None+Return"], declared);
        });

        WpfTestHost.Invoke(() => window!.Close());
    }

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
