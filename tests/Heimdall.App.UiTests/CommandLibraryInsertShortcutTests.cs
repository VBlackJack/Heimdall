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
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins the Insert shortcut on the rendered view.
/// </summary>
/// <remarks>
/// Read rather than pressed, for the reason <see cref="CommandLibraryShortcutTests"/> gives:
/// <c>KeyGesture.Matches</c> consults the physical keyboard's modifiers, and a test cannot hold
/// Shift down. The declared gesture is read from the loaded view and its command compared with
/// the view model's, so a wrong key, a wrong modifier, a missing binding or a binding to Send
/// all fail. What this does not prove is that the key reaches the view; that is the same limit
/// the existing modified shortcuts live with.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryInsertShortcutTests
{
    [StaFact]
    public void ShiftReturn_IsDeclaredOnTheView_AndPointsAtInsert()
    {
        Window? window = null;

        WpfTestHost.Invoke(() =>
        {
            var viewModel = new CommandLibraryViewModel(
                serviceProvider: null!,
                configManager: null!,
                WpfTestHost.Localizer,
                dialogService: null!,
                gitSyncService: null!,
                transferService: null!);
            var view = new CommandLibraryView { DataContext = viewModel };

            // Shown, not merely constructed: an InputBinding's Command binding resolves only once
            // the view is in a loaded window.
            window = new Window
            {
                Width = 640,
                Height = 700,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            window.Show();
            view.UpdateLayout();

            var binding = view.InputBindings
                .OfType<KeyBinding>()
                .SingleOrDefault(candidate => candidate.Key == Key.Return && candidate.Modifiers == ModifierKeys.Shift);

            Assert.True(binding is not null, "No shortcut declared for Shift+Return.");
            Assert.Same(viewModel.InsertCommand, binding!.Command);
        });

        WpfTestHost.Invoke(() => window!.Close());
    }

    /// <summary>
    /// Ctrl+Enter reaches Send through the declared binding, so the command it resolves to must
    /// refuse an invalid command; with a valid one and a terminal it must accept.
    /// </summary>
    [StaFact]
    public void CtrlReturn_ResolvesToACommandThatRefusesAnInvalidCommand()
    {
        Window? window = null;
        bool invalidCanExecute = true;
        bool validCanExecute = false;

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
                SendCommandHandler = _ => { },
                CanSendToTerminalProbe = () => true
            };
            var view = new CommandLibraryView { DataContext = viewModel };
            window = new Window
            {
                Width = 640,
                Height = 700,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            window.Show();
            view.UpdateLayout();

            var binding = view.InputBindings
                .OfType<KeyBinding>()
                .Single(candidate => candidate.Key == Key.Return && candidate.Modifiers == ModifierKeys.Control);

            // Nothing generated yet: the command is not valid.
            invalidCanExecute = binding.Command.CanExecute(null);

            viewModel.ApplyExampleText("echo ready");
            validCanExecute = binding.Command.CanExecute(null);
        });

        try
        {
            Assert.False(invalidCanExecute);
            Assert.True(validCanExecute);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }
}
