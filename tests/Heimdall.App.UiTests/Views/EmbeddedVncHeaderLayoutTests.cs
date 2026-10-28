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
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The VNC session header on the constructed view: where Tab goes, and where the controls sit
/// when the pane is too narrow for them beside the title.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedVncHeaderLayoutTests
{
    private const double PaneHeight = 400;
    private const double WidthBeyondTheMinimum = 200;
    private const double WidthShortOfTheMinimum = 60;

    /// <summary>More presses than the strip has stops, so a stop taken by the view itself cannot end the walk early.</summary>
    private const int MaxTabPresses = 12;

    // The controls are docked to the right, so the first one declared is the rightmost: Tab used
    // to run right to left, against the way the eye reads the strip (U3-3). The Tab key is
    // pressed here, not the indexes read: what counts is where the focus goes.
    // Kills: the markup back in docking order.
    [StaFact]
    public void Tab_WalksTheHeaderLeftToRight()
    {
        List<object> visited = [];
        EmbeddedVncView? view = null;

        WpfTestHost.Invoke(() =>
        {
            view = new EmbeddedVncView();

            CollapseWebView(view);
            view.ReconnectButton.Visibility = Visibility.Visible;
            HashSet<object> header =
            [
                view.SplitButton,
                view.QualitySelector,
                view.ScalingSelector,
                view.ClipboardButton,
                view.ReconnectButton,
                view.DisconnectButton,
            ];

            Window window = new()
            {
                Width = 1400,
                Height = PaneHeight,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            try
            {
                window.Show();
                view.UpdateLayout();
                window.Activate();
                Keyboard.ClearFocus();

                // From nothing focused, so the first stop is the first of the strip and not
                // wherever a previous step left the focus: Tab wraps in a window, and a walk that
                // starts inside the strip cannot tell where the strip begins. The view itself may
                // take a stop; only the strip's own are kept.
                for (int step = 0; step < MaxTabPresses && visited.Count < header.Count; step++)
                {
                    PressTab(window);
                    if (Keyboard.FocusedElement is { } focused
                        && header.Contains(focused)
                        && !visited.Contains(focused))
                    {
                        visited.Add(focused);
                    }
                }
            }
            finally
            {
                window.Close();
                view.Dispose();
            }
        });

        Assert.Equal(
            [
                view!.SplitButton,
                view.QualitySelector,
                view.ScalingSelector,
                view.ClipboardButton,
                view.ReconnectButton,
                view.DisconnectButton,
            ],
            visited);
    }

    // In a pane too narrow for the controls and the title, the controls took the strip and the
    // title and the status vanished (seen in French at 900 px).
    // Kills: the layout never moving the controls.
    [StaFact]
    public void InANarrowPane_TheControlsDropUnderTheTitle_AndComeBackWhenThePaneWidens()
    {
        bool narrowBelow = false;
        int narrowRow = -1;
        bool wideBelow = true;
        int wideRow = -1;
        bool narrowAgainBelow = false;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = new();
            CollapseWebView(view);

            Layout(view, 2000);
            double controls = view.HeaderControls.DesiredSize.Width;
            double fits = controls + EmbeddedVncView.HeaderInfoMinWidth + WidthBeyondTheMinimum;
            double cramped = controls + EmbeddedVncView.HeaderInfoMinWidth - WidthShortOfTheMinimum;

            Layout(view, cramped);
            narrowBelow = view.HeaderControlsBelow;
            narrowRow = Grid.GetRow(view.HeaderControls);

            Layout(view, fits);
            wideBelow = view.HeaderControlsBelow;
            wideRow = Grid.GetRow(view.HeaderControls);

            Layout(view, cramped);
            narrowAgainBelow = view.HeaderControlsBelow;
        });

        Assert.True(narrowBelow);
        Assert.Equal(1, narrowRow);
        Assert.False(wideBelow);
        Assert.Equal(0, wideRow);
        Assert.True(narrowAgainBelow);
    }

    /// <summary>
    /// The web view is not under test and would start a browser engine once shown. Reached by
    /// name: its type lives in an assembly this project does not reference.
    /// </summary>
    private static void CollapseWebView(EmbeddedVncView view)
    {
        ((FrameworkElement)view.FindName("VncWebView")).Visibility = Visibility.Collapsed;
    }

    private static void Layout(EmbeddedVncView view, double width)
    {
        view.Measure(new Size(width, PaneHeight));
        view.Arrange(new Rect(0, 0, width, PaneHeight));
        view.UpdateLayout();
    }

    /// <summary>Presses and releases Tab on the focused element, through the input manager.</summary>
    private static void PressTab(Window window)
    {
        PresentationSource source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The window has no presentation source.");

        KeyEventArgs down = new(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Tab)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
        InputManager.Current.ProcessInput(down);

        KeyEventArgs up = new(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Tab)
        {
            RoutedEvent = Keyboard.KeyUpEvent,
        };
        InputManager.Current.ProcessInput(up);
    }
}
