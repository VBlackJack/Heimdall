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

using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The transient toast of the RDP pane lives in a popup so the Win32 child window cannot paint
/// over it; these drive the real pane to check when that popup opens and closes.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class EmbeddedRdpTransientToastPopupTests
{
    private const string ToastMessage = "probe toast";
    private const int WindowWidth = 600;
    private const int WindowHeight = 400;
    private const uint GwHwndPrev = 3;
    private const int MaxZOrderWalk = 4096;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [StaFact]
    public void TheToastOpensItsPopupWhenThePaneIsVisible()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window window, EmbeddedRdpView view) = Show();
            try
            {
                Raise(view, ToastMessage);

                Assert.True(ToastPopup(view).IsOpen);
            }
            finally
            {
                view.Dispose();
                window.Close();
            }
        });
    }

    [StaFact]
    public void TheToastClosesItsPopupWhenItsTimerTicks()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window window, EmbeddedRdpView view) = Show();
            try
            {
                Raise(view, ToastMessage);
                Assert.True(ToastPopup(view).IsOpen);

                Invoke(view, "OnTransientToastTick", null, EventArgs.Empty);

                Assert.False(ToastPopup(view).IsOpen);
            }
            finally
            {
                view.Dispose();
                window.Close();
            }
        });
    }

    [StaFact]
    public void TheToastClosesItsPopupWhenThePaneUnloads()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window window, EmbeddedRdpView view) = Show();
            try
            {
                Raise(view, ToastMessage);
                Assert.True(ToastPopup(view).IsOpen);

                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

                Assert.False(ToastPopup(view).IsOpen);
            }
            finally
            {
                view.Dispose();
                window.Close();
            }
        });
    }

    [StaFact]
    public void TheToastDoesNotOpenItsPopupWhenThePaneIsCollapsedButStillAnnounces()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window window, EmbeddedRdpView view) = Show();
            try
            {
                view.Visibility = Visibility.Collapsed;
                view.UpdateLayout();

                Raise(view, ToastMessage);

                Assert.False(ToastPopup(view).IsOpen);

                // The announcement is raised whether or not the popup opened, and raising it
                // creates the peer a screen reader listens on.
                Assert.NotNull(UIElementAutomationPeer.FromElement(Toast(view)));
            }
            finally
            {
                view.Dispose();
                window.Close();
            }
        });
    }

    [StaFact]
    public void TheToastInsideItsPopupIsStillAnnouncedThroughItsLiveRegionPeer()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window window, EmbeddedRdpView view) = Show();
            try
            {
                Raise(view, ToastMessage);

                Assert.True(ToastPopup(view).IsOpen);
                Assert.NotNull(UIElementAutomationPeer.FromElement(Toast(view)));
            }
            finally
            {
                view.Dispose();
                window.Close();
            }
        });
    }

    [StaFact]
    public void TheToastClosesItsPopupWhenItsSurfaceIsHiddenWithoutUnloading()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window window, EmbeddedRdpView view) = Show();
            try
            {
                Raise(view, ToastMessage);
                Assert.True(ToastPopup(view).IsOpen);

                view.Visibility = Visibility.Collapsed;
                view.UpdateLayout();

                Assert.False(ToastPopup(view).IsOpen);
            }
            finally
            {
                view.Dispose();
                window.Close();
            }
        });
    }

    [StaFact]
    public void TheToastOfAnInactiveOwnerStaysBelowTheWindowInFrontOfIt()
    {
        WpfTestHost.Invoke(() =>
        {
            (Window owner, EmbeddedRdpView view) = Show();
            Window front = new()
            {
                Width = WindowWidth,
                Height = WindowHeight,
                ShowInTaskbar = false,
            };
            try
            {
                front.Show();
                _ = front.Activate();
                DrainDispatcher();
                Assert.False(owner.IsActive, "The owner must be inactive for this to measure anything.");

                Raise(view, ToastMessage);

                Assert.True(ToastPopup(view).IsOpen);
                IntPtr popup = ((HwndSource)PresentationSource.FromVisual((System.Windows.Media.Visual)Toast(view))).Handle;
                IntPtr frontHandle = new WindowInteropHelper(front).Handle;
                IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;

                // Walking up from the toast, the foreground window has to be met before the top
                // of the z-order. A toast put at the top of the non-topmost band would not meet it.
                Assert.True(
                    IsAbove(frontHandle, popup),
                    "The toast was placed above the window the user is working in.");
                Assert.True(
                    IsAbove(popup, ownerHandle),
                    "The toast was not kept above its own owner.");
            }
            finally
            {
                view.Dispose();
                front.Close();
                owner.Close();
            }
        });
    }

    /// <summary>True when <paramref name="upper"/> is above <paramref name="lower"/> in the z-order.</summary>
    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        IntPtr current = lower;
        for (int step = 0; step < MaxZOrderWalk && current != IntPtr.Zero; step++)
        {
            current = GetWindow(current, GwHwndPrev);
            if (current == upper)
            {
                return true;
            }
        }

        return false;
    }

    private static (Window Window, EmbeddedRdpView View) Show()
    {
        WpfTestHost.ResetLocale();
        EmbeddedRdpView view = new();
        Window window = new()
        {
            Content = view,
            Width = WindowWidth,
            Height = WindowHeight,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        _ = window.Activate();
        view.UpdateLayout();
        return (window, view);
    }

    private static void Raise(EmbeddedRdpView view, string message)
    {
        Invoke(view, "ShowTransientToast", message);
        DrainDispatcher();
    }

    private static void Invoke(EmbeddedRdpView view, string method, params object?[] arguments)
    {
        MethodInfo target = typeof(EmbeddedRdpView).GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{method}' was not found.");
        _ = target.Invoke(view, arguments);
    }

    private static Popup ToastPopup(EmbeddedRdpView view) => (Popup)view.FindName("TransientToastPopup");

    private static UIElement Toast(EmbeddedRdpView view) => (UIElement)view.FindName("TransientToast");

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
