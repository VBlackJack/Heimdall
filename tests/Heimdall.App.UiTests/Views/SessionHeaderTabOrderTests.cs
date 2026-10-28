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
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The Tab key walks a session header from left to right, the way the eye reads it.
/// </summary>
/// <remarks>
/// The headers dock their controls to the right edge one by one, and a DockPanel stacks
/// right-docked children from the right: the first one declared sits rightmost, so Tab, which
/// follows declaration order, ran right to left. The RDP header hid it behind TabIndex values that
/// followed the same inverted order. These tests perform the gesture: they put every control of
/// the header in play, move the keyboard focus with the same traversal Tab uses, and require the
/// controls reached to sit further right at each step.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class SessionHeaderTabOrderTests
{
    private const double HostWindowWidth = 2400;
    private const double HostWindowHeight = 700;
    private const int ExtraStepsPastTheHeader = 3;

    private static readonly string[] WebControls =
        ["ReloadButton", "BackButton", "OpenExternallyButton", "SplitButton"];

    private static readonly string[] SshControls =
    [
        "BroadcastTargetToggle", "ElevateButton", "SplitButton", "HealthToggleButton",
        "ReconnectButton", "DisconnectButton",
    ];

    private static readonly string[] RdpControls =
    [
        "AutofillRetryButton", "AutofillDismissButton", "AntiIdleBadge", "ResolutionButton",
        "SplitButton", "SendKeysButton", "FullscreenButton", "CancelConnectButton",
        "CancelReconnectButton", "DisconnectButton", "ReconnectButton",
    ];

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void WebHeader_TabRunsLeftToRight()
        => AssertTabRunsLeftToRight(() => new EmbeddedWebView(), WebControls);

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void SshHeader_TabRunsLeftToRight()
        => AssertTabRunsLeftToRight(() => new EmbeddedSshView(), SshControls);

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void RdpHeader_TabRunsLeftToRight()
        => AssertTabRunsLeftToRight(() => new EmbeddedRdpView(), RdpControls);

    /// <summary>
    /// Positive control: a header whose controls are not in screen order must fail the walk, or
    /// the three tests above would pass whatever the markup says.
    /// </summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheWalk_NoticesAControlThatTabsOutOfScreenOrder()
    {
        WpfTestHost.Invoke(() =>
        {
            System.Windows.Controls.DockPanel strip = new() { LastChildFill = false };
            System.Windows.Controls.Button first = new() { Name = "First", Content = "First" };
            System.Windows.Controls.Button second = new() { Name = "Second", Content = "Second" };
            System.Windows.Controls.DockPanel.SetDock(first, System.Windows.Controls.Dock.Right);
            System.Windows.Controls.DockPanel.SetDock(second, System.Windows.Controls.Dock.Right);
            strip.Children.Add(first);
            strip.Children.Add(second);

            Window window = new() { Content = strip, Width = HostWindowWidth, Height = HostWindowHeight, ShowActivated = true };
            try
            {
                window.Show();
                Pump();
                window.UpdateLayout();

                List<FrameworkElement> reached = WalkTab(window, [first, second]);

                Assert.Equal(2, reached.Count);
                Assert.True(
                    LeftEdge(reached[1], window) < LeftEdge(reached[0], window),
                    "A right-docked pair declared first-then-second must tab right to left.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertTabRunsLeftToRight(Func<FrameworkElement> createView, string[] controlNames)
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            FrameworkElement view = createView();
            Window window = new() { Content = view, Width = HostWindowWidth, Height = HostWindowHeight, ShowActivated = true };
            try
            {
                List<FrameworkElement> controls = [];
                foreach (string name in controlNames)
                {
                    FrameworkElement control = Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));
                    control.Visibility = Visibility.Visible;
                    control.IsEnabled = true;
                    controls.Add(control);
                }

                window.Show();
                Pump();
                window.UpdateLayout();

                List<FrameworkElement> reached = WalkTab(window, controls);

                Assert.True(
                    reached.Count == controls.Count,
                    $"Tab reached {reached.Count} of {controls.Count} header controls: "
                    + string.Join(", ", reached.Select(NameOf)));

                double previous = double.NegativeInfinity;
                foreach (FrameworkElement control in reached)
                {
                    double left = LeftEdge(control, window);
                    Assert.True(
                        left > previous,
                        $"Tab went to {NameOf(control)} (x={left:F0}) after a control at x={previous:F0}; order: "
                        + string.Join(" > ", reached.Select(NameOf)));
                    previous = left;
                }
            }
            finally
            {
                window.Close();
                (view as IDisposable)?.Dispose();
            }
        });
    }

    /// <summary>
    /// Moves the keyboard focus forward the way the Tab key does and returns, in the order
    /// reached, the members of <paramref name="controls"/> the walk landed on.
    /// </summary>
    private static List<FrameworkElement> WalkTab(Window window, IReadOnlyCollection<FrameworkElement> controls)
    {
        List<FrameworkElement> reached = [];
        window.Activate();
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

        for (int step = 0; step < controls.Count + ExtraStepsPastTheHeader; step++)
        {
            if (Keyboard.FocusedElement is FrameworkElement focused && controls.Contains(focused) && !reached.Contains(focused))
            {
                reached.Add(focused);
            }

            if (Keyboard.FocusedElement is not UIElement current)
            {
                break;
            }

            current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        return reached;
    }

    private static double LeftEdge(FrameworkElement element, Window window)
        => element.TranslatePoint(new Point(0, 0), window).X;

    private static string NameOf(FrameworkElement element)
        => string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
}
