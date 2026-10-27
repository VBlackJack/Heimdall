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
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins which of the library's buttons carries the weight, and which say they are on.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the running app on 2026-10-26: in a tab with no terminal, Send to Terminal kept the
/// filled primary style while disabled, and Copy, the only thing that could work, was a secondary
/// outline. Send stays visible on purpose (its tooltip says why it cannot run, and a gesture hidden
/// on a state read once cannot be found again), so the weight moves to Copy instead.
/// </para>
/// <para>
/// The History and Runbooks header buttons, and the favorites filter, gave no sign of being on.
/// They stay plain buttons drawing their own state, as the tab strip's broadcast marker does: a
/// ToggleButton beside a command can flip itself through UIA without the view model following.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryButtonStateTests
{
    [StaFact]
    public void WithNoTerminalCopyCarriesThePrimaryStyle()
    {
        Style? copy = null;
        Style? send = null;
        Style? primary = null;

        Render(viewModel =>
        {
            viewModel.SendCommandHandler = _ => { };
            viewModel.CanSendToTerminalProbe = () => false;
        }, (view, _) =>
        {
            copy = ((Button)view.FindName("BtnCopy")).Style;
            send = ((Button)view.FindName("BtnSend")).Style;
            primary = (Style)view.FindResource("PrimaryButtonStyle");
        });

        Assert.Same(primary, copy);
        Assert.NotSame(primary, send);
    }

    /// <summary>
    /// Positive control, and the live half: with a terminal Send is the primary again, and the
    /// weight follows the probe when it changes while the view is open.
    /// </summary>
    [StaFact]
    public void WithATerminalSendCarriesThePrimaryStyleAndFollowsTheProbe()
    {
        bool hasTerminal = true;
        Style? sendWithTerminal = null;
        Style? copyWithTerminal = null;
        Style? copyAfterTerminalGone = null;
        Style? primary = null;

        Render(viewModel =>
        {
            viewModel.SendCommandHandler = _ => { };
            viewModel.CanSendToTerminalProbe = () => hasTerminal;
        }, (view, viewModel) =>
        {
            primary = (Style)view.FindResource("PrimaryButtonStyle");
            sendWithTerminal = ((Button)view.FindName("BtnSend")).Style;
            copyWithTerminal = ((Button)view.FindName("BtnCopy")).Style;

            hasTerminal = false;
            viewModel.RefreshSendState();
            Settle(view);
            copyAfterTerminalGone = ((Button)view.FindName("BtnCopy")).Style;
        });

        Assert.Same(primary, sendWithTerminal);
        Assert.NotSame(primary, copyWithTerminal);
        Assert.Same(primary, copyAfterTerminalGone);
    }

    [StaTheory]
    [InlineData("BtnHistory", nameof(CommandLibraryViewModel.IsHistoryVisible))]
    [InlineData("BtnRunbooks", nameof(CommandLibraryViewModel.IsRunbooksVisible))]
    [InlineData("BtnFavoritesFilter", nameof(CommandLibraryViewModel.FavoritesFilterActive))]
    public void AnOnButtonSaysSoToTheEyeAndToAScreenReader(string buttonName, string property)
    {
        string? nameOff = null;
        string? nameOn = null;
        Brush? borderOff = null;
        Brush? borderOn = null;
        Brush? accent = null;

        Render(_ => { }, (view, viewModel) =>
        {
            var button = (Button)view.FindName(buttonName);
            accent = (Brush)view.FindResource("AccentBorderBrush");
            nameOff = AutomationProperties.GetName(button);
            borderOff = button.BorderBrush;

            typeof(CommandLibraryViewModel).GetProperty(property)!.SetValue(viewModel, true);
            Settle(view);
            nameOn = AutomationProperties.GetName(button);
            borderOn = button.BorderBrush;
        });

        Assert.False(string.IsNullOrEmpty(nameOff));
        Assert.NotEqual(nameOff, nameOn);
        Assert.StartsWith(nameOff!, nameOn, StringComparison.Ordinal);
        Assert.NotSame(accent, borderOff);
        Assert.Same(accent, borderOn);
    }

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
                arrange(viewModel);
                var view = new CommandLibraryView { DataContext = viewModel };
                viewModel.IsGeneratorVisible = true;
                window = new Window
                {
                    Width = 1000,
                    Height = 800,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                window.Show();
                Settle(view);
                read(view, viewModel);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    private static void Settle(FrameworkElement view)
    {
        view.UpdateLayout();
        view.Dispatcher.Invoke(DispatcherPriority.Background, new System.Action(() => { }));
        view.UpdateLayout();
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
