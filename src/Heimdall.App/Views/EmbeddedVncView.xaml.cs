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

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Views;

/// <summary>
/// WPF host for an embedded VNC session rendered through WebView2 + noVNC.
/// A local WebSocket proxy bridges the noVNC client to the VNC server's TCP socket.
/// </summary>
public partial class EmbeddedVncView : UserControl, IDisposable
{
    /// <summary>
    /// Virtual host name for mapping local VNC assets into WebView2.
    /// Provides a stable HTTPS origin instead of file:// for security.
    /// </summary>
    private const string VncVirtualHost = "heimdall-vnc.local";

    private VncSessionResult? _session;
    private SessionTabViewModel? _sessionTab;
    private WebSocketVncProxy? _proxy;
    private LocalizationManager? _localizer;
    private bool _webViewReady;
    private bool _disposed;

    // Server-supplied desktop name, captured from the "desktop-name:" web message; used as the
    // event-log title when present, falling back to the host otherwise.
    private string? _desktopName;

    // Session-event state: at most one Connected and one Disconnected per view instance (VNC
    // reconnect is a full teardown + recreate, so there is no in-place bounce to track).
    private DateTime _eventConnectedAtUtc;
    private bool _eventConnectEmitted;
    private bool _eventDisconnectEmitted;

    /// <summary>Raised when the VNC session connects successfully. Parameter: ServerId.</summary>
    public event Action<string>? SessionConnected;

    /// <summary>Raised when the VNC session encounters an error. Parameters: ServerId, error message.</summary>
    public event Action<string, string>? SessionError;

    /// <summary>
    /// Shared sink for graphical-protocol connect/disconnect events. Injected by
    /// <c>EmbeddedSessionManager</c>, mirroring the RDP wiring.
    /// </summary>
    public ISessionEventLog? SessionEventLog { get; set; }

    /// <summary>
    /// Provider for the LIVE global session-logging toggle, read at each emit so the setting takes
    /// effect without a restart. The view never snapshots it.
    /// </summary>
    public Func<bool>? SessionLoggingEnabledProvider { get; set; }

    public EmbeddedVncView()
    {
        InitializeComponent();

        // Set WebView2 background from theme to avoid flash of wrong color
        if (TryFindResource("BackgroundColor") is System.Windows.Media.Color themeColor)
        {
            VncWebView.DefaultBackgroundColor =
                System.Drawing.Color.FromArgb(themeColor.A, themeColor.R, themeColor.G, themeColor.B);
        }
    }

    /// <summary>
    /// Initializes the VNC view with session parameters and starts the WebSocket proxy.
    /// </summary>
    public async Task InitializeSessionAsync(
        VncSessionResult session,
        SessionTabViewModel sessionTab,
        string displayName,
        LocalizationManager? localizer)
    {
        _session = session;
        _sessionTab = sessionTab;
        _localizer = localizer;

        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = $"{session.Host}:{session.Port}";
        StatusTextBlock.Text = localizer?["StatusVncConnecting"] ?? "Connecting...";
        VncLoadingBar.Visibility = System.Windows.Visibility.Visible;

        // Localize static UI elements
        DisconnectButton.Content = localizer?["BtnDisconnectSession"] ?? "Disconnect";
        ReconnectButton.Content = localizer?["BtnReconnectSession"] ?? "Reconnect";
        ReconnectMessageText.Text = localizer?["VncDisconnectedOverlay"] ?? "VNC session disconnected";
        OverlayReconnectButton.Content = localizer?["BtnReconnectSession"] ?? "Reconnect";
        OverlayCloseButton.Content = localizer?["BtnCloseOverlay"] ?? "Close";
        FallbackTitleText.Text = localizer?["VncFallbackTitle"] ?? "Embedded VNC viewer unavailable";
        FallbackMessageText.Text = localizer?["VncFallbackMessage"] ?? "WebView2 could not be initialized on this machine.";

        // Tooltips
        DisconnectButton.ToolTip = localizer?["TooltipDisconnectSession"] ?? "Disconnect session";
        ReconnectButton.ToolTip = localizer?["TooltipReconnectSession"] ?? "Reconnect session";
        SplitButton.ToolTip = localizer?["TooltipSplitSession"] ?? "Split session";

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(DisconnectButton, localizer?["A11yDisconnectSession"] ?? "Disconnect session");
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton, localizer?["A11yReconnectSession"] ?? "Reconnect session");
        System.Windows.Automation.AutomationProperties.SetName(SplitButton, localizer?["A11ySplitSession"] ?? "Split session view");
        System.Windows.Automation.AutomationProperties.SetName(OverlayReconnectButton, localizer?["A11yReconnectSession"] ?? "Reconnect session");
        System.Windows.Automation.AutomationProperties.SetName(OverlayCloseButton, localizer?["A11yCloseOverlay"] ?? "Close overlay");
        System.Windows.Automation.AutomationProperties.SetName(StatusTextBlock, localizer?["A11yConnectionStatus"] ?? "Connection status");

        // Start the WebSocket-to-TCP proxy
        _proxy = new WebSocketVncProxy(session.Host, session.Port);
        _proxy.Start();

        await InitializeWebViewAsync().ConfigureAwait(false);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!Services.WebView2Helper.IsAvailable)
            {
                var msg = _localizer?["ErrorWebView2NotFound"]
                    ?? "WebView2 Runtime not found. Place a Fixed Version Runtime in runtimes/webview2/ or install the Evergreen Runtime.";
                ShowFallback(msg);
                if (_session?.ServerId is not null)
                {
                    SessionError?.Invoke(_session.ServerId, msg);
                }
                return;
            }

            var env = await Services.WebView2Helper.CreateEnvironmentAsync("VNC");

            await VncWebView.EnsureCoreWebView2Async(env);

            var core = VncWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;

            core.WebMessageReceived += OnWebMessageReceived;

            // Map local Assets folder to a virtual HTTPS host so noVNC runs
            // under a proper origin instead of file:// (Microsoft recommended pattern
            // for local content in WebView2 — avoids CORS issues and provides a stable
            // origin for WebMessage source validation).
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
            core.SetVirtualHostNameToFolderMapping(
                VncVirtualHost, assetsPath,
                CoreWebView2HostResourceAccessKind.Allow);

            // Block all navigation away from the VNC virtual host
            core.NavigationStarting += OnWebViewNavigationStarting;

            // Load the VNC HTML page via virtual host mapping
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vnc.html");
            if (File.Exists(htmlPath))
            {
                core.Navigate($"https://{VncVirtualHost}/vnc.html");
            }
            else
            {
                var msg = _localizer?["VncViewerNotFound"] ?? "VNC viewer HTML not found.";
                ShowFallback(msg);
                if (_session?.ServerId is not null)
                {
                    SessionError?.Invoke(_session.ServerId, msg);
                }
            }
        }
        catch (Exception ex)
        {
            ShowFallback(ex.Message);
            if (_session?.ServerId is not null)
            {
                SessionError?.Invoke(_session.ServerId, ex.Message);
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Validate message source — only accept messages from our virtual VNC host
        var source = e.Source;
        if (!string.IsNullOrEmpty(source)
            && !source.StartsWith($"https://{VncVirtualHost}", StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedVNC rejected WebMessage from unexpected source: {source}");
            return;
        }

        var message = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        if (message == "ready:")
        {
            _webViewReady = true;
            SendConnectCommand();
            return;
        }

        if (message == "connected:")
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = _localizer?["StatusVncConnected"] ?? "Connected";
                VncLoadingBar.Visibility = Visibility.Collapsed;
            });
            if (_session?.ServerId is not null)
            {
                SessionConnected?.Invoke(_session.ServerId);
            }
            EmitConnect();
            return;
        }

        if (message.StartsWith("console:", StringComparison.Ordinal))
        {
            var payload = message["console:".Length..];
            var separator = payload.IndexOf(':', StringComparison.Ordinal);
            var level = separator > 0 ? payload[..separator] : string.Empty;
            var text = separator >= 0 ? payload[(separator + 1)..] : payload;
            var line = $"VNC console [{level}]: {text}";
            switch (level)
            {
                case "error":
                    Core.Logging.FileLogger.Error(line);
                    break;
                case "warn":
                    Core.Logging.FileLogger.Warn(line);
                    break;
                default:
                    Core.Logging.FileLogger.Info(line);
                    break;
            }
            return;
        }

        if (message.StartsWith("disconnect-detail:", StringComparison.Ordinal))
        {
            Core.Logging.FileLogger.Warn(
                $"VNC disconnect detail: {message["disconnect-detail:".Length..]}");
            return;
        }

        if (message.StartsWith("disconnected:", StringComparison.Ordinal))
        {
            EmitDisconnect("remote");
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = _localizer?["StatusVncDisconnected"] ?? "Disconnected";
                ShowReconnectOverlay();
            });
            return;
        }

        if (message.StartsWith("error:", StringComparison.Ordinal))
        {
            var errorMsg = message["error:".Length..];
            Core.Logging.FileLogger.Error($"VNC error: {errorMsg}");

            // Mid-session error ends the session; the latch makes this a no-op pre-connect.
            EmitDisconnect("remote");

            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = _localizer?.Format("ErrorVncConnectionFailed", errorMsg)
                    ?? $"VNC error: {errorMsg}";
            });

            if (_session?.ServerId is not null)
            {
                SessionError?.Invoke(_session.ServerId, errorMsg);
            }
            return;
        }

        if (message.StartsWith("desktop-name:", StringComparison.Ordinal))
        {
            var desktopName = message["desktop-name:".Length..];
            if (!string.IsNullOrWhiteSpace(desktopName))
            {
                _desktopName = desktopName;
            }
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(desktopName))
                {
                    SessionTitleText.Text = desktopName;
                }
            });
            return;
        }

        if (message.StartsWith("clipboard-write:", StringComparison.Ordinal))
        {
            var b64 = message["clipboard-write:".Length..];
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(b64));
                Dispatcher.Invoke(() => Clipboard.SetText(text));
            }
            catch (FormatException)
            {
                // Invalid base64; ignore.
            }
            return;
        }

        if (message == "clipboard-read:")
        {
            Dispatcher.Invoke(() =>
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    var b64 = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(text));
                    PostWebMessage($"clipboard-paste:{b64}");
                }
            });
            return;
        }

        if (message == "credentials-required:")
        {
            // If we have a password, re-send the connect command with it
            if (_session?.Password is not null)
            {
                SendConnectCommand();
            }
        }
    }

    private void OnWebViewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs navArgs)
    {
        if (navArgs.Uri is not null
            && !navArgs.Uri.StartsWith($"https://{VncVirtualHost}", StringComparison.OrdinalIgnoreCase)
            && !navArgs.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            && !navArgs.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            navArgs.Cancel = true;
            Core.Logging.FileLogger.Warn(
                $"EmbeddedVNC blocked navigation to: {navArgs.Uri}");
        }
    }

    private void SendConnectCommand()
    {
        if (_proxy is null || !_webViewReady)
        {
            return;
        }

        var connectParams = new
        {
            wsUrl = $"ws://127.0.0.1:{_proxy.ListenPort}",
            password = _session?.Password,
            viewOnly = _session?.ViewOnly ?? false
        };

        var json = JsonSerializer.Serialize(connectParams);
        PostWebMessage($"connect:{json}");
    }

    private void PostWebMessage(string message)
    {
        if (VncWebView.CoreWebView2 is not null)
        {
            VncWebView.CoreWebView2.PostWebMessageAsString(message);
        }
    }

    private void ShowFallback(string message)
    {
        Dispatcher.Invoke(() =>
        {
            FallbackMessageText.Text = message;
            FallbackPanel.Visibility = Visibility.Visible;
            VncWebView.Visibility = Visibility.Collapsed;
        });
    }

    // Live gate at the seam: the sink is a dumb writer, so the view decides per-emit against the
    // LIVE global toggle and the VNC event eligibility (the policy stays the single source of truth).
    private bool ShouldLogSessionEvents()
        => SessionEventLog is not null
            && SessionEventGatePolicy.ShouldLog(SessionLoggingEnabledProvider?.Invoke() ?? false, "VNC");

    // Event-log title: the server-supplied desktop name when received, else the host.
    private string? ResolveEventTitle()
        => string.IsNullOrWhiteSpace(_desktopName) ? _session?.Host : _desktopName;

    // Emits a Connected event and opens the (single) connected segment for this view instance.
    private void EmitConnect()
    {
        if (!ShouldLogSessionEvents() || _session is null)
        {
            return;
        }

        _eventConnectedAtUtc = DateTime.UtcNow;
        _eventConnectEmitted = true;
        _eventDisconnectEmitted = false;

        try
        {
            SessionEventLog!.LogEvent(
                VncSessionEventFactory.BuildConnected(_session.Host, ResolveEventTitle()));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedVNC session-event connect emit failed: {ex.Message}");
        }
    }

    // Single idempotent disconnect funnel. The three native sinks and the Dispose backstop all call
    // this with the honest trigger; the latch guarantees at most one Disconnected per session and
    // suppresses a pre-connect failure (no Connected was ever emitted).
    private void EmitDisconnect(string endTrigger)
    {
        if (!ShouldLogSessionEvents() || !_eventConnectEmitted || _eventDisconnectEmitted || _session is null)
        {
            return;
        }

        _eventDisconnectEmitted = true;

        try
        {
            long? durationMs = GraphicalSessionEventHelpers.ResolveDurationMs(_eventConnectedAtUtc, DateTime.UtcNow);
            SessionEventLog!.LogEvent(
                VncSessionEventFactory.BuildDisconnected(_session.Host, ResolveEventTitle(), durationMs, endTrigger));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedVNC session-event disconnect emit failed: {ex.Message}");
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("EmbeddedVNC Disconnect requested by user");
        EmitDisconnect("user");
        PostWebMessage("disconnect:");

        // Mirror the SSH disconnect contract: the view stays alive in the
        // Disconnected state so the overlay's Reconnect/Close actions keep
        // working. The noVNC "disconnected:" round-trip also shows the overlay,
        // but set it directly so a wedged WebView cannot leave the user stuck.
        StatusTextBlock.Text = _localizer?["StatusVncDisconnected"] ?? "Disconnected";
        ShowReconnectOverlay();
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        ReconnectOverlay.Visibility = Visibility.Collapsed;
        ReconnectButton.Visibility = Visibility.Collapsed;
        VncWebView.Visibility = Visibility.Visible;
        RequestReconnect?.Invoke(_session?.ServerId ?? "");
    }

    private void OnOverlayReconnectClick(object sender, RoutedEventArgs e)
    {
        ReconnectOverlay.Visibility = Visibility.Collapsed;
        ReconnectButton.Visibility = Visibility.Collapsed;
        VncWebView.Visibility = Visibility.Visible;
        RequestReconnect?.Invoke(_session?.ServerId ?? "");
    }

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e)
    {
        ReconnectOverlay.Visibility = Visibility.Collapsed;
        // Route through the shared pane lifecycle so the tab closes via the
        // normal teardown path instead of disposing the view under a live tab.
        RequestClose?.Invoke(_session?.ServerId ?? "");
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        RequestSplit?.Invoke(_sessionTab);
    }

    /// <summary>Shows the reconnect overlay and header button after an unexpected disconnect.</summary>
    public void ShowReconnectOverlay(string? message = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(message))
            {
                ReconnectMessageText.Text = message;
            }

            ReconnectOverlay.Visibility = Visibility.Visible;
            ReconnectButton.Visibility = Visibility.Visible;
            // Airspace rule: the WebView2 HWND occludes WPF siblings regardless
            // of ZIndex, so the surface must be collapsed for the overlay to
            // render. Mirrors ShowFallback().
            VncWebView.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Raised to request a reconnect. Parameter: ServerId.</summary>
    public event Action<string>? RequestReconnect;

    /// <summary>Raised to request a split pane. Parameter: SessionTab.</summary>
    public event Action<SessionTabViewModel?>? RequestSplit;

    /// <summary>Raised to request closing the session tab. Parameter: ServerId.</summary>
    public event Action<string>? RequestClose;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Backstop: emit a teardown disconnect before any teardown, but only if no native signal
        // (remote/user) already did. The event sink is an app singleton, not disposed per-view, so
        // enqueuing here is safe. "teardown" can overstate duration if the remote died earlier and
        // the user left a dead tab open; endTrigger makes that explicit in the log.
        EmitDisconnect("teardown");

        _disposed = true;

        if (VncWebView.CoreWebView2 is not null)
        {
            VncWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            // Detach to prevent handler leak identified by audit-2026-04-22 (PERF-01).
            VncWebView.CoreWebView2.NavigationStarting -= OnWebViewNavigationStarting;
        }

        _proxy?.Dispose();
        _proxy = null;

        try
        {
            VncWebView.Dispose();
        }
        catch (InvalidOperationException)
        {
            // WebView2 may already be disposed.
        }
    }
}
