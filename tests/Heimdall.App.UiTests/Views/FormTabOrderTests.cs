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
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The Tab key walks a form in reading order: row by row from the top, left to right inside a row.
/// </summary>
/// <remarks>
/// These forms mixed controls that carried a <c>TabIndex</c> with controls that did not. WPF sorts
/// the unnumbered ones after every numbered one among siblings, so the Generate button of a key or
/// certificate form took the focus before the fields above it, and the combo box of the bulk
/// gateway dialog came after the radio buttons that follow it. The tests perform the Tab gesture
/// on the shown form and compare the controls reached with their place on the screen.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class FormTabOrderTests
{
    private const double HostWindowWidth = 1100;
    private const double HostWindowHeight = 2000;
    private const int MinimumControlsWalked = 3;

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void SshKeyGenerator_TabFollowsTheForm()
        => AssertReadingOrder(() => new SshKeyGeneratorView());

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void CertificateGenerator_TabFollowsTheForm()
        => AssertReadingOrder(() => new CertificateGeneratorView());

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void CertInspector_TabFollowsTheForm()
        => AssertReadingOrder(() => new CertInspectorView());

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void SshKeyAudit_TabFollowsTheForm()
        => AssertReadingOrder(() => new SshKeyAuditView());

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void BulkGatewayDialog_TabFollowsTheForm()
        => AssertReadingOrder(() => new ServerBulkEditGatewayDialog());

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void GatewayDialog_TabFollowsTheForm()
        => AssertReadingOrder(() => new GatewayDialog());

    /// <summary>
    /// Positive control: a form whose numbered control sits before an unnumbered one that is
    /// above it on the screen must fail the comparison, or every test above would pass whatever
    /// the markup says.
    /// </summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheComparison_NoticesAButtonThatTabsBeforeTheFieldAboveIt()
    {
        WpfTestHost.Invoke(() =>
        {
            System.Windows.Controls.StackPanel form = new();
            System.Windows.Controls.TextBox field = new() { Name = "Field" };
            System.Windows.Controls.Button submit = new() { Name = "Submit", Content = "Go" };
            System.Windows.Controls.Primitives.ToggleButton lower = new() { Name = "Lower" };
            form.Children.Add(new System.Windows.Controls.StackPanel { Children = { field } });
            form.Children.Add(submit);
            form.Children.Add(lower);
            submit.TabIndex = 1;

            Window window = new() { Content = form, Width = HostWindowWidth, Height = HostWindowHeight, ShowActivated = true };
            try
            {
                window.Show();
                Pump();
                window.UpdateLayout();

                List<FrameworkElement> walked = TabWalk.Run(window, form);
                List<FrameworkElement> expected = TabWalk.InReadingOrder(walked, window);

                Assert.NotEqual(expected, walked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertReadingOrder(Func<FrameworkElement> create)
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            FrameworkElement form = create();
            Window window = form as Window
                ?? new Window { Content = form };

            // Tall enough that nothing scrolls out of view: a footer pinned under a scrolled form
            // would otherwise sit above the fields it follows on the screen.
            window.SizeToContent = SizeToContent.Manual;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = HostWindowWidth;
            window.Height = HostWindowHeight;
            window.Top = 0;
            window.ShowActivated = true;

            try
            {
                window.Show();
                Pump();
                window.UpdateLayout();

                FrameworkElement root = form is Window ? (FrameworkElement)window.Content : form;

                // The commit row of a dialog (OK, Cancel) is pinned under a scrolling form, so its
                // place on the screen says nothing about where it belongs in the walk.
                List<FrameworkElement> walked = TabWalk.Run(window, root)
                    .Where(c => c is not System.Windows.Controls.Button { IsDefault: true } and not System.Windows.Controls.Button { IsCancel: true })
                    .ToList();
                Assert.True(
                    walked.Count >= MinimumControlsWalked,
                    $"The walk reached only {walked.Count} controls: " + string.Join(", ", walked.Select(TabWalk.NameOf)));

                List<FrameworkElement> expected = TabWalk.InReadingOrder(walked, window);
                Assert.True(
                    walked.SequenceEqual(expected),
                    "Tab order: " + string.Join(" > ", walked.Select(TabWalk.NameOf))
                    + Environment.NewLine + "Reading order: " + string.Join(" > ", expected.Select(TabWalk.NameOf)));
            }
            finally
            {
                window.Close();
                (form as IDisposable)?.Dispose();
            }
        });
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
}
