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
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Views;

internal readonly record struct ReconnectRequestContext(
    bool IsAutomatic,
    int Attempt,
    int MaxAttempts)
{
    internal static ReconnectRequestContext Manual { get; } = new(false, 0, 0);

    internal static ReconnectRequestContext Automatic(int attempt, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, attempt);
        return new ReconnectRequestContext(true, attempt, maxAttempts);
    }
}

internal sealed class AutoReconnectTickScheduler
{
    private long _generation;

    internal TimerCallback CreateTimerCallback(
        Action<Action> queueAction,
        Action tickAction)
    {
        ArgumentNullException.ThrowIfNull(queueAction);
        ArgumentNullException.ThrowIfNull(tickAction);

        long generation = Interlocked.Increment(ref _generation);
        return _ => queueAction(() =>
        {
            if (Volatile.Read(ref _generation) == generation)
            {
                tickAction();
            }
        });
    }

    internal void Invalidate()
    {
        Interlocked.Increment(ref _generation);
    }
}

public partial class EmbeddedSshView
{
    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        StopAutoReconnectTimer();
        Core.Logging.FileLogger.Info("EmbeddedSSH Reconnect requested by user");
        RaiseReconnectRequested(ReconnectRequestContext.Manual);
    }

    private void OnOverlayReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        StopAutoReconnectTimer();
        HideReconnectOverlay();
        Core.Logging.FileLogger.Info("EmbeddedSSH Reconnect requested via overlay");
        RaiseReconnectRequested(ReconnectRequestContext.Manual);
    }

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        StopAutoReconnectTimer();
        HideReconnectOverlay();
        Core.Logging.FileLogger.Info("EmbeddedSSH Close requested via overlay");
        CloseRequested?.Invoke();
    }

    private void OnOverlayEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        StopAutoReconnectTimer();
        EditProfileRequested?.Invoke();
    }

    private void OnAutoReconnectCancelClick(object sender, RoutedEventArgs e)
    {
        StopAutoReconnectTimer();
        SetOverlayVisibility(AutoReconnectOverlay, Visibility.Collapsed);
        Core.Logging.FileLogger.Info("EmbeddedSSH auto-reconnect cancelled by user");
        ShowReconnectOverlay();
    }

    /// <summary>
    /// What the terminal surface does while the view is in a given state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal is a WebView2, a native window hosted in WPF, and a native window covers
    /// every WPF sibling drawn over it whatever their order: the end-of-session message, the
    /// reconnect countdown and the connecting veil were all set visible and none of them was
    /// ever seen (measured on v2026.100201: a clean exit and a server-side kill both showed
    /// the bare terminal). The surface steps aside while any of them is up, as the VNC view's
    /// does.
    /// </para>
    /// <para>
    /// Hidden rather than collapsed: a hidden surface keeps its layout size, so the terminal
    /// does not refit to nothing and send the server a resize while a veil is shown.
    /// Collapsed is kept for the fallback panel, where the surface is gone for good.
    /// </para>
    /// </remarks>
    internal static Visibility ResolveTerminalSurfaceVisibility(bool webViewUnavailable, bool overlayShown)
        => webViewUnavailable ? Visibility.Collapsed
            : overlayShown ? Visibility.Hidden
            : Visibility.Visible;

    /// <summary>
    /// Shows or hides one of the three veils, and moves the terminal surface out of their way.
    /// </summary>
    private void SetOverlayVisibility(UIElement overlay, Visibility visibility)
    {
        overlay.Visibility = visibility;
        SyncTerminalSurfaceVisibility();
    }

    private void SyncTerminalSurfaceVisibility()
    {
        bool overlayShown = ReconnectOverlay.Visibility == Visibility.Visible
            || AutoReconnectOverlay.Visibility == Visibility.Visible
            || ConnectingOverlay.Visibility == Visibility.Visible;
        TerminalWebView.Visibility = ResolveTerminalSurfaceVisibility(_webViewUnavailable, overlayShown);
        AnnounceSurfaceVisibilityToPage();
    }

    /// <summary>
    /// Tells the page what the host knows and the page cannot see: that its surface was hidden (the
    /// box it is to lay itself out in) or shown (measure yourself). Only once the page is ready, so
    /// that <c>ready:</c> answers with the state of the moment rather than a queue of old ones.
    /// </summary>
    private void AnnounceSurfaceVisibilityToPage()
    {
        Visibility now = TerminalWebView.Visibility;
        Visibility previous = _lastSurfaceVisibility;
        _lastSurfaceVisibility = now;
        if (!_terminalReady || previous == now)
        {
            return;
        }

        if (now == Visibility.Visible)
        {
            PostSurfaceShown();
        }
        else if (now == Visibility.Hidden)
        {
            SendViewportIfHidden();
        }
    }

    /// <summary>
    /// While the surface is hidden, tells the page the box the pane has: the native window behind
    /// the control is not positioned while it is hidden, so the page cannot measure it. Nothing is
    /// sent before layout has happened; the next size change sends it.
    /// </summary>
    private void SendViewportIfHidden()
    {
        if (!_terminalReady || TerminalWebView.Visibility != Visibility.Hidden)
        {
            return;
        }

        SendViewportBox();
    }

    /// <summary>
    /// Tells the page its surface is shown. When layout is valid the box goes first, always, so the
    /// page waits for its real bounds the same way whether or not it was ever laid out hidden: the
    /// shown message arrives before WPF's next layout pass has positioned the native window.
    /// </summary>
    private void PostSurfaceShown()
    {
        SendViewportBox();
        PostTerminalMessage(TerminalViewportMessage.Shown);
    }

    /// <summary>
    /// Posts <c>viewport:W,H</c> for the box the native window will get: the element's bounds
    /// relative to the window root, in device pixels with each edge rounded, as the host window is
    /// sized. Nothing is sent before layout.
    /// </summary>
    private void SendViewportBox()
    {
        double width = TerminalWebView.ActualWidth;
        double height = TerminalWebView.ActualHeight;
        Rect bounds = new(0, 0, width, height);
        try
        {
            if (PresentationSource.FromVisual(TerminalWebView)?.RootVisual is Visual root)
            {
                bounds = TerminalWebView.TransformToAncestor(root).TransformBounds(bounds);
            }
        }
        catch (InvalidOperationException)
        {
            // Not connected to the visual tree: the size alone is what there is.
            bounds = new Rect(0, 0, width, height);
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(TerminalWebView);
        double zoom = TerminalWebView.ZoomFactor > 0 ? TerminalWebView.ZoomFactor : 1;
        if (TerminalViewportMessage.TryBuild(
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                dpi.DpiScaleX,
                dpi.DpiScaleY,
                surfaceHidden: true,
                out string message,
                zoom))
        {
            PostTerminalMessage(message);
        }
    }

    private void ShowReconnectOverlay()
    {
        SetOverlayVisibility(AutoReconnectOverlay, Visibility.Collapsed);
        SetOverlayVisibility(ReconnectOverlay, Visibility.Visible);
        BeginInvokeIfAvailable(
            () =>
            {
                if (_disposed || ReconnectOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                // The overlay itself takes the focus, not a button: a Space or Enter typed out
                // of habit as the session drops must not trigger Reconnect, which closes the tab.
                ReconnectOverlay.Focus();
            },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HideReconnectOverlay()
    {
        SetOverlayVisibility(ReconnectOverlay, Visibility.Collapsed);
    }

    private void SetReconnectDetail(string? detail)
    {
        bool hasDetail = !string.IsNullOrWhiteSpace(detail);
        ReconnectDetailText.Text = hasDetail ? detail : string.Empty;
        ReconnectDetailText.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Dismisses the veil so the output left in the terminal can be read and copied.</summary>
    private void OnOverlayViewOutputClick(object sender, RoutedEventArgs e)
    {
        HideReconnectOverlay();
        FocusTerminal();
    }

    internal static int ComputeAutoReconnectDelaySeconds(AppSettings? settings, int attempt)
    {
        return attempt switch
        {
            1 => settings?.SshAutoReconnectFirstDelaySeconds ?? AppSettings.DefaultSshAutoReconnectFirstDelaySeconds,
            2 => settings?.SshAutoReconnectSecondDelaySeconds ?? AppSettings.DefaultSshAutoReconnectSecondDelaySeconds,
            _ => settings?.SshAutoReconnectSubsequentDelaySeconds ?? AppSettings.DefaultSshAutoReconnectSubsequentDelaySeconds,
        };
    }

    private int ResolveMaxAutoReconnectAttempts()
    {
        return Math.Clamp(
            TerminalSettings?.SshAutoReconnectAttempts ?? AppSettings.DefaultSshAutoReconnectAttempts,
            AppSettings.MinSshAutoReconnectAttempts,
            AppSettings.MaxSshAutoReconnectAttempts);
    }

    internal int AutoReconnectAttempt => _autoReconnectAttempt;

    internal void SeedAutoReconnectAttempt(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        _autoReconnectAttempt = attempt;
    }

    private void RaiseReconnectRequested(ReconnectRequestContext context)
    {
        ReconnectContextRequested?.Invoke(context);
        ReconnectRequested?.Invoke();
    }

    private void StartAutoReconnectCountdown(int delaySeconds, int attempt, int maxAttempts)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() =>
                StartAutoReconnectCountdown(delaySeconds, attempt, maxAttempts));
            return;
        }

        StopAutoReconnectTimer();
        HideReconnectOverlay();
        HideConnectingOverlay();

        _autoReconnectSecondsRemaining = delaySeconds;
        _autoReconnectMaxAttempts = maxAttempts;
        AutoReconnectMessageText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L("SshAutoReconnectMessage"),
            attempt,
            maxAttempts);
        UpdateAutoReconnectCountdownText();
        SetOverlayVisibility(AutoReconnectOverlay, Visibility.Visible);
        BeginInvokeIfAvailable(
            () =>
            {
                if (_disposed || AutoReconnectOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                // Same reasoning as the reconnect veil: focus the container, not the action.
                AutoReconnectOverlay.Focus();
            },
            System.Windows.Threading.DispatcherPriority.Input);

        TimerCallback timerCallback = _autoReconnectTickScheduler.CreateTimerCallback(
            action => BeginInvokeIfAvailable(action),
            OnAutoReconnectTick);
        _autoReconnectTimer = new System.Threading.Timer(
            timerCallback,
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private void OnAutoReconnectTick()
    {
        if (_disposed)
        {
            StopAutoReconnectTimer();
            return;
        }

        _autoReconnectSecondsRemaining--;
        if (_autoReconnectSecondsRemaining <= 0)
        {
            StopAutoReconnectTimer();
            SetOverlayVisibility(AutoReconnectOverlay, Visibility.Collapsed);
            Core.Logging.FileLogger.Info(
                $"EmbeddedSSH auto-reconnect attempt {_autoReconnectAttempt} firing");
            RaiseReconnectRequested(ReconnectRequestContext.Automatic(
                _autoReconnectAttempt,
                _autoReconnectMaxAttempts));
            return;
        }

        UpdateAutoReconnectCountdownText();
    }

    private void UpdateAutoReconnectCountdownText()
    {
        AutoReconnectCountdownText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L("SshAutoReconnectCountdown"),
            _autoReconnectSecondsRemaining);
    }

    private void StopAutoReconnectTimer()
    {
        StopAutoReconnectTimer(_autoReconnectTickScheduler, ref _autoReconnectTimer);
    }

    internal static void StopAutoReconnectTimer(
        AutoReconnectTickScheduler scheduler,
        ref System.Threading.Timer? timer)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.Invalidate();
        System.Threading.Timer? stoppedTimer = Interlocked.Exchange(ref timer, null);
        stoppedTimer?.Dispose();
    }

    /// <summary>
    /// The connecting veil stays up until the session is attached AND the page is ready:
    /// the page is ready long before the SSH handshake, and a blank terminal with no sign of
    /// progress is what the veil is there to prevent.
    /// </summary>
    internal static bool ShouldHideConnectingOverlay(bool terminalReady, bool sessionAttached)
        => terminalReady && sessionAttached;

    private void HideConnectingOverlayWhenEstablished()
    {
        if (ShouldHideConnectingOverlay(_terminalReady, _session is not null || _terminalSession is not null))
        {
            HideConnectingOverlay();
        }
    }

    private void HideConnectingOverlay()
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(HideConnectingOverlay);
            return;
        }

        SetOverlayVisibility(ConnectingOverlay, Visibility.Collapsed);
    }

    private void UpdateConnectingOverlay(string displayName, string? endpoint)
    {
        if (_disposed) return;

        ConnectingTitleText.Text = string.IsNullOrWhiteSpace(displayName)
            ? L("SshConnectingTitle")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                L("SshConnectingTitleWithName"), displayName);

        ConnectingEndpointText.Text = endpoint ?? string.Empty;
    }
}
