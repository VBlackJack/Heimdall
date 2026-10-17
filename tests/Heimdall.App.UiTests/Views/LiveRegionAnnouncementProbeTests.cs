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

using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlaUI.UIA3;
using Heimdall.App.Behaviors;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using FlaUIElement = FlaUI.Core.AutomationElements.AutomationElement;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// A live region heard through UI Automation, the way a screen reader hears it, rather than read
/// off the markup.
/// </summary>
/// <remarks>
/// The markup census in <c>LiveRegionCensusGuardTests</c> can say that a region carries the
/// behavior. Only a UIA client can say that the event leaves the process: these register for
/// <c>LiveRegionChanged</c> on the real element and wait for it.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class LiveRegionAnnouncementProbeTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The local browser's status bar declared a LiveSetting and raised nothing before the
    /// census: a failed open was shown and never heard.
    /// </summary>
    [StaFact]
    public void TheLocalBrowserStatusBarAnnouncesANewStatus()
    {
        WpfTestHost.ResetLocale();
        string folder = Path.Combine(Path.GetTempPath(), "heimdall-live-region-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        LocalFileBrowserViewModel? viewModel = null;
        Window? window = null;
        IntPtr handle = IntPtr.Zero;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                LocalFileBrowserView view = new(folder, WpfTestHost.Localizer);
                viewModel = (LocalFileBrowserViewModel)view.DataContext;
                window = new Window { Content = view, Width = 700, Height = 500, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                view.UpdateLayout();
                handle = new WindowInteropHelper(window).Handle;
            });
            WpfTestHost.Invoke(DrainDispatcher);

            ConcurrentQueue<string> announcements = new();
            using UIA3Automation automation = new();
            FlaUIElement status = Assert.IsAssignableFrom<FlaUIElement>(
                automation.FromHandle(handle).FindFirstDescendant(condition => condition.ByAutomationId("StatusBarText")));
            using IDisposable handler = status.RegisterAutomationEvent(
                automation.EventLibrary.Element.LiveRegionChangedEvent,
                FlaUI.Core.Definitions.TreeScope.Element,
                (element, _) => announcements.Enqueue(element.Name));

            const string Message = "probe status";
            WpfTestHost.Invoke(() => viewModel!.StatusText = Message);
            WpfTestHost.Invoke(DrainDispatcher);

            Assert.True(WaitFor(announcements, Message), "The status bar did not raise LiveRegionChanged for its new status.");
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The same words are heard again when the region comes back: the disconnect overlay says the
    /// same thing on every disconnect, and the behavior used to drop the second one as a repeat.
    /// </summary>
    [StaFact]
    public void ARegionThatReappearsWithTheSameTextIsHeardAgain()
    {
        TextBlock? message = null;
        Window? window = null;
        IntPtr handle = IntPtr.Zero;
        WpfTestHost.Invoke(() =>
        {
            message = new TextBlock { Text = "Disconnected" };
            AutomationProperties.SetAutomationId(message, "ProbeMessage");
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);
            LiveRegionBehavior.SetAnnounceOnTargetUpdated(message, true);
            window = new Window { Content = message, Width = 300, Height = 120, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            handle = new WindowInteropHelper(window).Handle;
        });
        WpfTestHost.Invoke(DrainDispatcher);

        ConcurrentQueue<string> announcements = new();
        using UIA3Automation automation = new();
        FlaUIElement element = Assert.IsAssignableFrom<FlaUIElement>(
            automation.FromHandle(handle).FindFirstDescendant(condition => condition.ByAutomationId("ProbeMessage")));
        using IDisposable handler = element.RegisterAutomationEvent(
            automation.EventLibrary.Element.LiveRegionChangedEvent,
            FlaUI.Core.Definitions.TreeScope.Element,
            (e, _) => announcements.Enqueue(e.Name));
        try
        {
            WpfTestHost.Invoke(() => message!.Visibility = Visibility.Collapsed);
            WpfTestHost.Invoke(DrainDispatcher);
            WpfTestHost.Invoke(() => message!.Visibility = Visibility.Visible);
            WpfTestHost.Invoke(DrainDispatcher);

            Assert.True(WaitFor(announcements, "Disconnected"), "The region came back with the same words and was not heard.");
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    private static bool WaitFor(ConcurrentQueue<string> announcements, string expected)
    {
        DateTime deadline = DateTime.UtcNow + EventTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (announcements.Contains(expected, StringComparer.Ordinal))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
