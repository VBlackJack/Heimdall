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
using Heimdall.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Views;

/// <summary>
/// WPF host for an embedded VNC session rendered through WebView2 + noVNC.
/// A local WebSocket proxy bridges the noVNC client to the VNC server's TCP socket.
/// </summary>
public partial class EmbeddedVncView : UserControl, IDisposable, ISessionPaneOwner
{
    private static readonly WebViewDocumentPolicy VncDocumentPolicy =
        new(WebSocketVncProxy.PageDocumentUrl);

    private VncSessionResult? _session;
    private SessionTabViewModel? _sessionTab;
    private WebSocketVncProxy? _proxy;
    private LocalizationManager? _localizer;
    private bool _localeChangeSubscribed;
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

    // The password the next connect sends, and what the server's answer does to it.
    private VncPasswordFlow? _passwordFlow;

    // Whether the failure overlay offers to type the password: only after the server asked for
    // one the session could not give, or refused the one it gave.
    private bool _offerPasswordEntry;

    // Why the proxy could not reach the server, in words. The page only learns that its socket
    // closed and says "connection lost" after it; this keeps the reason the proxy knew.
    private string? _targetFailureText;

    // Whether this view holds a sleep-prevention reference. Watching a remote screen involves no
    // local input, so without one the machine slept under an open VNC session - RDP and SSH
    // sessions already held one.
    private bool _sleepPreventionActive;

    /// <summary>Raised when the VNC session connects successfully. Parameter: ServerId.</summary>
    public event Action<string>? SessionConnected;

    /// <summary>Raised when the VNC session encounters an error. Parameters: ServerId, error message.</summary>
    public event Action<string, string>? SessionError;

    /// <summary>
    /// Raised when the view connects again in place, with a password the user just typed.
    /// Parameter: ServerId.
    /// </summary>
    public event Action<string>? SessionRetrying;

    /// <summary>Raised when the failure overlay's Edit profile is pressed.</summary>
    public event Action? EditProfileRequested;

    /// <summary>
    /// Asks for, keeps and drops the password of a session whose profile cannot hold one (a shared
    /// session); null for a profile of the user's own. Set before <see cref="InitializeSessionAsync"/>.
    /// </summary>
    public IVncPasswordKeeper? PasswordKeeper { get; set; }

    /// <summary>
    /// Whether the failure overlay offers Edit profile, asked each time it is shown: the pane this
    /// view lives in can change. Null offers it never.
    /// </summary>
    public Func<bool>? EditProfileAvailable { get; set; }

    /// <summary>
    /// Raised when the session ends cleanly, by the user or by the server. Parameter: ServerId.
    /// </summary>
    /// <remarks>
    /// Only the pane text changed before, so the tab and the connection state kept reading
    /// Connected for a session that had ended.
    /// </remarks>
    public event Action<string>? SessionDisconnected;

    private SessionPaneModel? _ownerPane;

    /// <summary>The pane this view lives in, wherever a split, a merge or a detach moved it.</summary>
    internal SessionPaneModel? OwningPane => _ownerPane;

    public void SetOwningPane(SessionPaneModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _ownerPane = pane;
    }

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

    /// <summary>Per-profile session-logging override. Null means inherit the live global setting.</summary>
    public bool? SessionLoggingOverride { get; set; }

    /// <summary>
    /// Where every message for the page goes. The product sends them to the live WebView2; a test
    /// puts a recorder here to read what the header sends without a browser engine behind it.
    /// </summary>
    internal Action<string> PageMessageSink { get; set; }

    /// <summary>
    /// Creates the proxy and starts it. The product opens a real one; a test puts a factory here
    /// that fails, to see what the view shows when the proxy cannot start.
    /// </summary>
    internal Func<string, int, WebSocketVncProxy> ProxyFactory { get; set; } = StartRealProxy;

    private static WebSocketVncProxy StartRealProxy(string host, int port)
    {
        WebSocketVncProxy proxy = new(host, port);
        try
        {
            proxy.Start();
        }
        catch
        {
            proxy.Dispose();
            throw;
        }

        return proxy;
    }

    public EmbeddedVncView()
    {
        PageMessageSink = PostToLivePage;

        InitializeComponent();

        // Wired here and not in the markup: both selectors carry their initial selection in XAML,
        // so a handler declared there runs inside InitializeComponent, before VncWebView exists,
        // and the view could not be constructed at all.
        ScalingSelector.SelectionChanged += OnScalingSelectionChanged;
        QualitySelector.SelectionChanged += OnQualitySelectionChanged;

        // The controls change width when Reconnect appears: the header's own size does not.
        HeaderControls.SizeChanged += (_, _) => UpdateHeaderLayout();

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
        _passwordFlow = new VncPasswordFlow(session.Password, PasswordKeeper);

        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = $"{session.Host}:{session.Port}";
        StatusTextBlock.Text = L10n("StatusVncConnecting");
        Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
        VncLoadingBar.Visibility = System.Windows.Visibility.Visible;

        if (_localizer is not null && !_localeChangeSubscribed)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
            _localeChangeSubscribed = true;
        }

        ApplyLocalization();

        // Start the WebSocket-to-TCP proxy. A proxy that cannot start (the port taken between the
        // probe and the bind, the listener refused) has no session to wait for: say so, instead of
        // leaving the tab on "Connecting" with nothing behind it.
        try
        {
            _proxy = ProxyFactory(session.Host, session.Port);
            _proxy.TargetConnectFailed += OnProxyTargetConnectFailed;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error($"VNC proxy could not start: {ex}");
            _proxy = null;
            EndAttempt(_localizer?.Format("ErrorVncProxyStartFailed", ex.Message) ?? "ErrorVncProxyStartFailed", offerEntry: false);
            return;
        }

        await InitializeWebViewAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The room the title, the endpoint and the status keep before the controls drop under them.
    /// Below it the status is a few characters and the title is cut, whatever the language.
    /// </summary>
    internal const double HeaderInfoMinWidth = 240;

    /// <summary>Whether the controls sit on their own row under the title and the status.</summary>
    internal bool HeaderControlsBelow { get; private set; }

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeaderLayout();

    /// <summary>
    /// Puts the controls beside the title when both fit, and under it when they do not: in a
    /// narrow pane (a split, or a long translation) the controls took the whole strip and the
    /// title and the status vanished.
    /// </summary>
    internal void UpdateHeaderLayout()
    {
        double available = HeaderLayout.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        bool below = HeaderControls.DesiredSize.Width + HeaderInfoMinWidth > available;
        if (below == HeaderControlsBelow)
        {
            return;
        }

        HeaderControlsBelow = below;
        Grid.SetRow(HeaderControls, below ? 1 : 0);
        Grid.SetColumn(HeaderControls, below ? 0 : 1);
        Grid.SetColumnSpan(HeaderControls, below ? 2 : 1);
        HeaderControls.HorizontalAlignment = below
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Stretch;
        HeaderControls.Margin = below ? new Thickness(0, HeaderRowGap, 0, 0) : new Thickness(0);
    }

    /// <summary>The gap between the title row and the controls row when the header wraps.</summary>
    private const double HeaderRowGap = 4;

    /// <summary>Hands the keyboard back to the session header: Disconnect while connected, else Split.</summary>
    private void ReleaseFocusToHeader()
    {
        if (DisconnectButton.Visibility == Visibility.Visible && DisconnectButton.IsEnabled)
        {
            DisconnectButton.Focus();
        }
        else
        {
            SplitButton.Focus();
        }
    }

    private string L10n(string key) => _localizer?[key] ?? key;

    private void ApplyLocalization()
    {
        // Localize static UI elements
        DisconnectButton.Content = L10n("BtnDisconnectSession");
        ReconnectButton.Content = L10n("BtnReconnectSession");
        if (ReconnectOverlay.Visibility != Visibility.Visible)
        {
            ReconnectMessageText.Text = L10n("VncDisconnectedOverlay");
        }
        OverlayReconnectButton.Content = L10n("BtnReconnectSession");
        OverlayEnterPasswordButton.Content = L10n("BtnVncEnterPassword");
        OverlayCloseButton.Content = L10n("BtnCloseOverlay");
        FallbackTitleText.Text = L10n("VncFallbackTitle");
        if (FallbackPanel.Visibility != Visibility.Visible)
        {
            FallbackMessageText.Text = L10n("VncFallbackMessage");
        }

        // Tooltips
        DisconnectButton.ToolTip = L10n("TooltipDisconnectSession");
        ReconnectButton.ToolTip = L10n("TooltipReconnectSession");
        SplitButton.ToolTip = L10n("TooltipSplitSession");

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(DisconnectButton, L10n("A11yDisconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton, L10n("A11yReconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(SplitButton, L10n("A11ySplitSession"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayReconnectButton, L10n("A11yReconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayCloseButton, L10n("A11yCloseOverlay"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayEnterPasswordButton, L10n("A11yVncEnterPassword"));
        System.Windows.Automation.AutomationProperties.SetHelpText(StatusTextBlock, L10n("A11yConnectionStatus"));
    }

    private void OnLocaleChanged(string locale)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLocaleChanged(locale));
            return;
        }

        ApplyLocalization();
        if (_eventConnectEmitted && !_eventDisconnectEmitted)
        {
            StatusTextBlock.Text = L10n("StatusVncConnected");
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!Services.WebView2Helper.IsAvailable)
            {
                var msg = L10n("ErrorWebView2NotFound");
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
            // for local content in WebView2 - avoids CORS issues and provides a stable
            // origin for WebMessage source validation).
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
            core.SetVirtualHostNameToFolderMapping(
                VncDocumentPolicy.TrustedDocument.Host, assetsPath,
                WebViewAssetAccess.Vnc);

            // Block all navigation away from the VNC virtual host
            core.NavigationStarting += OnWebViewNavigationStarting;

            // The page's words must exist before its first script runs: it shows the loading
            // label at once and reports a failure of its own library before any connect message.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(VncPageText.BuildBootstrapScript(PageTextOf));

            // Load the VNC HTML page via virtual host mapping
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vnc.html");
            if (File.Exists(htmlPath))
            {
                core.Navigate(VncDocumentPolicy.TrustedDocument.AbsoluteUri);
            }
            else
            {
                var msg = L10n("VncViewerNotFound");
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
        var activeDocument = VncWebView.CoreWebView2?.Source;
        if (!ShouldAcceptWebMessage(e.Source, activeDocument))
        {
            Core.Logging.FileLogger.Warn(
                "EmbeddedVNC rejected a WebMessage from an untrusted document.");
            return;
        }

        HandlePageMessage(e.TryGetWebMessageAsString());
    }

    /// <summary>
    /// Acts on one message from the trusted page. Separate from the WebView2 event so a test can
    /// hand the view what the page would send, as <see cref="PageMessageSink"/> records what it sends.
    /// </summary>
    internal void HandlePageMessage(string? message)
    {
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

        if (message == WebViewFocusRelease.Message)
        {
            Dispatcher.Invoke(ReleaseFocusToHeader);
            return;
        }

        if (message == "connected:")
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = L10n("StatusVncConnected");
                Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
                VncLoadingBar.Visibility = Visibility.Collapsed;
            });
            if (_session?.ServerId is not null)
            {
                SessionConnected?.Invoke(_session.ServerId);
            }
            EmitConnect();
            AcquireSleepPrevention();
            _offerPasswordEntry = false;
            if (_passwordFlow is { } connectedFlow)
            {
                ObservePasswordTask(connectedFlow.OnConnectedAsync());
            }
            return;
        }

        if (message.StartsWith("console:", StringComparison.Ordinal))
        {
            var payload = message["console:".Length..];
            var separator = payload.IndexOf(':', StringComparison.Ordinal);
            var level = separator > 0 ? payload[..separator] : string.Empty;
            var text = separator >= 0 ? payload[(separator + 1)..] : payload;

            // noVNC logs the URL it connects to, and the token is in it.
            var line = $"VNC console [{level}]: {WebSocketVncProxy.RedactToken(text, _proxy?.Token)}";
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
                $"VNC disconnect detail: {WebSocketVncProxy.RedactToken(message["disconnect-detail:".Length..], _proxy?.Token)}");
            return;
        }

        if (message.StartsWith("disconnected:", StringComparison.Ordinal))
        {
            _passwordFlow?.OnAttemptEnded();
            EmitDisconnect("remote");
            ReleaseSleepPrevention();
            if (_session?.ServerId is not null)
            {
                SessionDisconnected?.Invoke(_session.ServerId);
            }

            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = _targetFailureText ?? L10n("StatusVncDisconnected");
                Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
                ShowReconnectOverlay(_targetFailureText);
            });
            return;
        }

        if (message.StartsWith("error:", StringComparison.Ordinal))
        {
            var errorMsg = message["error:".Length..];
            Core.Logging.FileLogger.Error($"VNC error: {errorMsg}");
            _passwordFlow?.OnAttemptEnded();

            // Mid-session error ends the session; the latch makes this a no-op pre-connect.
            EmitDisconnect("remote");
            ReleaseSleepPrevention();

            // The proxy knew why the server could not be reached; the page only knows its socket
            // closed. Its words must not replace the real reason.
            string text = _targetFailureText
                ?? _localizer?.Format("ErrorVncConnectionFailed", errorMsg)
                ?? errorMsg;

            // A failure ends the attempt: the overlay offers Reconnect and the busy bar goes. The
            // status line used to be the only sign, under a bar that kept running.
            EndAttempt(text, offerEntry: false);
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
            HandleClipboardWrite(message["clipboard-write:".Length..]);
            return;
        }

        if (message.StartsWith("credentials-required:", StringComparison.Ordinal))
        {
            HandleCredentialsRequest(message["credentials-required:".Length..]);
            return;
        }

        if (message.StartsWith("security-failure:", StringComparison.Ordinal))
        {
            HandleSecurityFailure(message["security-failure:".Length..]);
            return;
        }

        if (message == "credentials-late:")
        {
            HandleLateCredentials();
        }
    }

    /// <summary>
    /// Ends the attempt when the server asks for credentials the session does not hold, and says
    /// which. A session whose profile cannot hold the password (a shared one) offers to type it;
    /// see <see cref="VncCredentialsPolicy"/>.
    /// </summary>
    private void HandleCredentialsRequest(string requestedTypesJson)
    {
        bool canAsk = _passwordFlow?.CanAsk == true;
        bool offerEntry = canAsk && VncCredentialsPolicy.AsksForPasswordOnly(requestedTypesJson);
        string key = VncCredentialsPolicy.MessageKeyFor(requestedTypesJson, canAsk);
        Core.Logging.FileLogger.Warn($"VNC server asked for credentials the session does not hold ({key}).");

        // Where the user can type the password, the server keeps waiting for it on this connection:
        // ending it would count as a wrong password on TigerVNC, which blocks the address after a few.
        if (offerEntry)
        {
            _passwordFlow!.OnServerWaiting();
        }
        else
        {
            Dispatcher.Invoke(() => PostWebMessage("disconnect:"));
        }

        EndAttempt(_localizer?[key] ?? key, offerEntry);
    }

    /// <summary>
    /// Ends the attempt the server refused at its security step. A refusal after the password was
    /// sent drops a remembered one, so the next attempt asks instead of sending it again.
    /// </summary>
    private void HandleSecurityFailure(string failureJson)
    {
        VncSecurityFailure failure = VncCredentialsPolicy.ReadSecurityFailure(failureJson);
        bool canAsk = _passwordFlow?.CanAsk == true;
        string key = VncCredentialsPolicy.MessageKeyForSecurityFailure(failure, canAsk);

        // The server's reason is logged and shown, never the password or whether one was remembered.
        Core.Logging.FileLogger.Warn($"VNC server refused the sign-in ({key}): {failure.Reason}");
        if (_passwordFlow is { } flow)
        {
            ObservePasswordTask(flow.OnSecurityFailureAsync(failure.AfterPassword));
        }

        string text = key == "ErrorVncSecurityRefusedReason"
            ? _localizer?.Format(key, failure.Reason ?? string.Empty) ?? key
            : _localizer?[key] ?? key;
        EndAttempt(text, offerEntry: failure.AfterPassword && canAsk);
    }

    /// <summary>Shows why the attempt ended, on the header and over the pane.</summary>
    private void EndAttempt(string text, bool offerEntry)
    {
        Dispatcher.Invoke(() =>
        {
            _offerPasswordEntry = offerEntry;
            StatusTextBlock.Text = text;
            Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
            ShowReconnectOverlay(text);
        });

        if (_session?.ServerId is not null)
        {
            SessionError?.Invoke(_session.ServerId, text);
        }
    }

    /// <summary>Logs a failure to keep or drop a password; the session itself goes on.</summary>
    private static void ObservePasswordTask(Task task)
    {
        _ = task.ContinueWith(
            t => Core.Logging.FileLogger.Warn($"VNC password could not be kept or dropped: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void OnWebViewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs navArgs)
    {
        if (VncDocumentPolicy.IsTrustedDocument(navArgs.Uri))
        {
            _webViewReady = false;
            return;
        }

        navArgs.Cancel = true;
        Core.Logging.FileLogger.Warn("EmbeddedVNC blocked navigation to an untrusted document.");
    }

    // ---- Display controls, carried by the header ----

    /// <summary>The scaling modes the page accepts, in the order the header offers them.</summary>
    internal static readonly string[] ScalingModes = ["remote", "scale", "none"];

    /// <summary>
    /// One option of the header's quality selector, and the two noVNC levels it sets together.
    /// </summary>
    /// <remarks>
    /// The selector used to set the compression level alone. That level is zlib's, lossless: it
    /// trades bandwidth against CPU and never changes the picture, so "Best quality" looked like
    /// every other option. The JPEG quality level is what changes the image.
    /// </remarks>
    /// <param name="Name">The Tag the header option carries.</param>
    /// <param name="QualityLevel">noVNC's JPEG quality, from 0 (smallest) to 9 (sharpest).</param>
    /// <param name="CompressionLevel">noVNC's zlib compression, from 0 (none) to 9 (most).</param>
    internal sealed record QualityPreset(string Name, int QualityLevel, int CompressionLevel);

    /// <summary>The JPEG quality noVNC uses when nothing sets it, kept by the default preset.</summary>
    internal const int NoVncDefaultQualityLevel = 6;

    /// <summary>The presets the header offers, best picture first.</summary>
    internal static readonly QualityPreset[] QualityPresets =
    [
        new("best", QualityLevel: 9, CompressionLevel: 0),
        new("balanced", QualityLevel: 8, CompressionLevel: 3),
        new("performance", QualityLevel: NoVncDefaultQualityLevel, CompressionLevel: 6),
        new("low-bandwidth", QualityLevel: 3, CompressionLevel: 9),
    ];

    /// <summary>What a session starts on when the header's selection says nothing.</summary>
    internal const string DefaultScalingMode = "scale";

    /// <summary>The preset a session starts on when the header's selection says nothing.</summary>
    internal const string DefaultQualityPresetName = "performance";

    /// <summary>The message that carries a scaling mode to the page.</summary>
    internal static string ScalingMessage(string mode) => $"scaling:{mode}";

    /// <summary>The message that carries a preset's two levels to the page.</summary>
    internal static string QualityMessage(QualityPreset preset) =>
        "quality:" + JsonSerializer.Serialize(new
        {
            qualityLevel = preset.QualityLevel,
            compressionLevel = preset.CompressionLevel,
        });

    /// <summary>The preset named <paramref name="name"/>, or the default one for an unknown name.</summary>
    internal static QualityPreset QualityPresetNamed(string? name) =>
        QualityPresets.FirstOrDefault(preset => preset.Name == name)
        ?? QualityPresets.First(preset => preset.Name == DefaultQualityPresetName);

    /// <summary>The scaling mode the header currently shows.</summary>
    private string SelectedScalingMode() =>
        (ScalingSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? DefaultScalingMode;

    /// <summary>The quality preset the header currently shows.</summary>
    private QualityPreset SelectedQualityPreset() =>
        QualityPresetNamed((QualitySelector.SelectedItem as ComboBoxItem)?.Tag as string);

    private void SendConnectCommand()
    {
        if (_proxy is null || !_webViewReady)
        {
            return;
        }

        // A new attempt starts without the last one's reason.
        _targetFailureText = null;

        var connectParams = new
        {
            wsUrl = _proxy.WebSocketUrl,
            password = _passwordFlow?.Password,
            viewOnly = _session?.ViewOnly ?? false,

            // The page shows and reports these; it used to carry its own English ones.
            text = VncPageText.Build(PageTextOf),
        };

        var json = JsonSerializer.Serialize(connectParams);
        PostWebMessage($"connect:{json}");

        // The header carries the display controls, so the session opens on what it shows rather
        // than on the page's own defaults. The page applies them in the order they arrive and
        // keeps them until the client exists.
        PostWebMessage(ScalingMessage(SelectedScalingMode()));
        PostWebMessage(QualityMessage(SelectedQualityPreset()));
    }

    private string? PageTextOf(string key) => _localizer?[key];

    /// <summary>The locale key of the message that names <paramref name="failure"/>.</summary>
    internal static string TargetFailureMessageKey(VncTargetFailure failure) => failure switch
    {
        VncTargetFailure.Refused => "ErrorVncTargetRefused",
        VncTargetFailure.TimedOut => "ErrorVncTargetTimedOut",
        _ => "ErrorVncTargetUnreachable",
    };

    private void OnProxyTargetConnectFailed(VncTargetFailure failure)
    {
        // Raised on the proxy's thread.
        Dispatcher.BeginInvoke(() => HandleTargetConnectFailure(failure));
    }

    /// <summary>
    /// Ends the attempt with the reason the proxy gave for not reaching the server: each reason
    /// has its own words, since the fix differs (start the server, wait, or check the address).
    /// </summary>
    internal void HandleTargetConnectFailure(VncTargetFailure failure)
    {
        if (_disposed || _session is null)
        {
            return;
        }

        string key = TargetFailureMessageKey(failure);
        string text = _localizer?.Format(
                key, _session.Host, _session.Port, WebSocketVncProxy.TargetConnectTimeoutSeconds)
            ?? key;

        _targetFailureText = text;
        _passwordFlow?.OnAttemptEnded();
        EndAttempt(text, offerEntry: false);
    }

    private void PostWebMessage(string message) => PageMessageSink(message);

    private void PostToLivePage(string message)
    {
        if (VncWebView.CoreWebView2 is { } core
            && VncDocumentPolicy.CanExchangeMessages(_webViewReady, core.Source))
        {
            core.PostWebMessageAsString(message);
        }
    }

    internal static bool ShouldAcceptWebMessage(string? source, string? activeDocument)
    {
        return VncDocumentPolicy.ShouldAcceptMessage(source, activeDocument);
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
    {
        bool globalEnabled = SessionLoggingEnabledProvider?.Invoke() ?? false;
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(SessionLoggingOverride, globalEnabled);
        return SessionEventLog is not null && SessionEventGatePolicy.ShouldLog(enabled, "VNC");
    }

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
        ReleaseSleepPrevention();
        PostWebMessage("disconnect:");
        SessionDisconnected?.Invoke(_session?.ServerId ?? string.Empty);

        // Mirror the SSH disconnect contract: the view stays alive in the
        // Disconnected state so the overlay's Reconnect/Close actions keep
        // working. The noVNC "disconnected:" round-trip also shows the overlay,
        // but set it directly so a wedged WebView cannot leave the user stuck.
        StatusTextBlock.Text = L10n("StatusVncDisconnected");
        Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
        ShowReconnectOverlay();
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        ReconnectOverlay.Visibility = Visibility.Collapsed;
        ReconnectButton.Visibility = Visibility.Collapsed;
        VncWebView.Visibility = Visibility.Visible;
        RequestReconnect?.Invoke(_session?.ServerId ?? "");
    }

    /// <summary>
    /// Asks for the password the server wants and hands it to the connection still waiting for it,
    /// or connects again in this view with it when that connection is gone. A reconnect from the
    /// pane rebuilds the session without asking anything, so it could never get there.
    /// </summary>
    private async void OnOverlayEnterPasswordClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _passwordFlow is null)
        {
            return;
        }

        bool answered;
        try
        {
            answered = await _passwordFlow.AskAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"VNC password prompt failed: {ex.Message}");
            return;
        }

        if (!answered || _disposed)
        {
            return;
        }

        ShowSigningInWithPassword();
        if (_passwordFlow.TakeWaitingServer())
        {
            PostWebMessage("credentials:" + JsonSerializer.Serialize(new { password = _passwordFlow.Password }));
        }
        else
        {
            SendConnectCommand();
        }
    }

    /// <summary>
    /// The page had no connection waiting when the answer reached it (the server ended the sign-in
    /// in the meantime): the answer goes into a new connect instead.
    /// </summary>
    private void HandleLateCredentials()
    {
        if (_disposed || _passwordFlow?.Password is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            ShowSigningInWithPassword();
            SendConnectCommand();
        });
    }

    /// <summary>Takes the overlay down while the typed password signs in.</summary>
    private void ShowSigningInWithPassword()
    {
        // The words that asked for the password would outlive it: an attempt that then fails for
        // another reason shows the overlay again without the entry, and must not still say to use it.
        _offerPasswordEntry = false;
        ReconnectMessageText.Text = L10n("VncDisconnectedOverlay");
        ReconnectOverlay.Visibility = Visibility.Collapsed;
        ReconnectButton.Visibility = Visibility.Collapsed;
        VncWebView.Visibility = Visibility.Visible;
        StatusTextBlock.Text = L10n("StatusVncConnecting");
        Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);
        VncLoadingBar.Visibility = Visibility.Visible;
        if (_session?.ServerId is not null)
        {
            SessionRetrying?.Invoke(_session.ServerId);
        }
    }

    private void OnOverlayEditProfileClick(object sender, RoutedEventArgs e)
    {
        EditProfileRequested?.Invoke();
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

    /// <summary>
    /// Hands the local clipboard to the session, which is what the header's Clipboard button
    /// promises: the remote pastes what was copied here.
    /// </summary>
    private void OnClipboardClick(object sender, RoutedEventArgs e)
    {
        SendLocalClipboardToSession();
    }

    /// <summary>
    /// Sends the local clipboard text to the page, which pastes it into the session. The request
    /// used to arrive from the page, from its own toolbar button; the header carries that button
    /// now, so it is made here and the page only receives the paste.
    /// </summary>
    private void SendLocalClipboardToSession()
    {
        string? text = VncClipboardAccess.TryGetText(Clipboard.ContainsText, Clipboard.GetText, LogClipboardFailure);
        if (text is null) return;

        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        PostWebMessage($"clipboard-paste:{b64}");
    }

    /// <summary>
    /// Copies the text the session placed on its clipboard to the local one. Invalid base64 is ignored.
    /// </summary>
    private void HandleClipboardWrite(string base64)
    {
        string? text = VncClipboardAccess.DecodePayload(base64);
        if (text is null) return;

        Dispatcher.Invoke(() => VncClipboardAccess.TrySetText(Clipboard.SetText, text, LogClipboardFailure));
    }

    /// <summary>A clipboard held by another process is logged and nothing more: no dialog.</summary>
    private static void LogClipboardFailure(Exception ex)
    {
        Core.Logging.FileLogger.Warn($"EmbeddedVNC clipboard access failed: {ex.Message}");
    }

    /// <summary>Sends the scaling mode the user picked to the page.</summary>
    private void OnScalingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PostWebMessage(ScalingMessage(SelectedScalingMode()));
    }

    /// <summary>Sends the quality preset the user picked to the page.</summary>
    private void OnQualitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PostWebMessage(QualityMessage(SelectedQualityPreset()));
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

            // Whatever ended the attempt, nothing is loading any more.
            VncLoadingBar.Visibility = Visibility.Collapsed;
            OverlayEnterPasswordButton.Visibility = _offerPasswordEntry ? Visibility.Visible : Visibility.Collapsed;
            OverlayEditProfileButton.Visibility = EditProfileAvailable?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
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

    private void AcquireSleepPrevention()
    {
        if (!_sleepPreventionActive && !_disposed)
        {
            _sleepPreventionActive = true;
            SleepPrevention.SessionStarted();
        }
    }

    private void ReleaseSleepPrevention()
    {
        if (_sleepPreventionActive)
        {
            _sleepPreventionActive = false;
            SleepPrevention.SessionEnded();
        }
    }

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
        ReleaseSleepPrevention();

        _disposed = true;
        _webViewReady = false;

        if (_localeChangeSubscribed && _localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localeChangeSubscribed = false;
        }

        if (VncWebView.CoreWebView2 is not null)
        {
            VncWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            // Detach to prevent handler leak identified by audit-2026-04-22 (PERF-01).
            VncWebView.CoreWebView2.NavigationStarting -= OnWebViewNavigationStarting;
        }

        if (_proxy is not null)
        {
            _proxy.TargetConnectFailed -= OnProxyTargetConnectFailed;
            _proxy.Dispose();
            _proxy = null;
        }

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
