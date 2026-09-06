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

using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.Services.Macros;
using Heimdall.App.Services.WinRm;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Ssh;
using Heimdall.Terminal.Logging;
using Heimdall.Terminal.Macros;
using Microsoft.Web.WebView2.Core;
using AppDialogs = Heimdall.App.Views.Dialogs;
using AppDialogViewModels = Heimdall.App.ViewModels.Dialogs;

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

/// <summary>
/// WPF host for an interactive SSH shell session rendered through WebView2 + xterm.js.
/// The browser surface handles VT parsing, ANSI colors, cursor movement, and scrollback.
/// </summary>
public partial class EmbeddedSshView : UserControl, IDisposable, ITerminalCommandSink, ISessionPaneOwner
{
    private static readonly (string Tag, Func<string> ContentFactory, string WrapperStart, string WrapperEnd)[] InlineAssets =
    [
        ("<link rel=\"stylesheet\" href=\"./Terminal/xterm.min.css\" />", static () => TerminalAssetsLoader.XtermCss, "<style>", "</style>"),
        ("<script src=\"./Terminal/xterm.min.js\"></script>", static () => TerminalAssetsLoader.XtermJs, "<script>", "</script>"),
        ("<script src=\"./Terminal/addon-fit.min.js\"></script>", static () => TerminalAssetsLoader.AddonFitJs, "<script>", "</script>"),
        ("<script src=\"./Terminal/addon-webgl.min.js\"></script>", static () => TerminalAssetsLoader.AddonWebglJs, "<script>", "</script>")
    ];

    private static readonly byte[] KeepAliveCr = [0x0D];

    private const string MsgOpenUrl = "open-url:";
    private const string MsgReady = "ready:";
    private const string MsgResize = "resize:";
    private const string MsgInput = "input:";
    private const string MsgCwd = "cwd:";
    private const string MsgClipboardWrite = "clipboard-write:";
    private const string MsgClipboardRead = "clipboard-read:";
    private const string TerminalPageMessageSource = "about:blank";
    private const int LoggedWebViewTextLimit = 256;
    internal const int MaxPendingTerminalMessageBytes = 4 * 1024 * 1024;
    internal const int MaxInboundWebMessageDecodedBytes = 1 * 1024 * 1024;
    internal const int MaxInboundWebMessageBase64Length = ((MaxInboundWebMessageDecodedBytes + 2) / 3) * 4;
    private const int MaxResizeColumns = 999;
    private const int MaxResizeRows = 999;

    /// <summary>Outbound message: sets the xterm.js convertEol option at runtime.</summary>
    private const string MsgSetConvertEol = "set-convert-eol:";

    /// <summary>
    /// Outbound message: clipboard text the host has confirmed, base64 encoded. The page hands it
    /// to xterm's own paste path so bracketed paste and newline folding apply.
    /// </summary>
    private const string MsgClipboardPaste = "clipboard-paste:";

    /// <summary>
    /// Maps color scheme names to xterm.js theme JSON object literals.
    /// Keys must match the values stored in <see cref="AppSettings.TerminalColorScheme"/>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ColorSchemes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dracula"] = """
                {
                    background: '#282A36',
                    foreground: '#F8F8F2',
                    cursor: '#BD93F9',
                    cursorAccent: '#282A36',
                    selectionBackground: 'rgba(98, 114, 164, 0.5)',
                    selectionForeground: '#F8F8F2',
                    black: '#21222C',
                    red: '#FF5555',
                    green: '#50FA7B',
                    yellow: '#F1FA8C',
                    blue: '#BD93F9',
                    magenta: '#FF79C6',
                    cyan: '#8BE9FD',
                    white: '#F8F8F2',
                    brightBlack: '#6272A4',
                    brightRed: '#FF6E6E',
                    brightGreen: '#69FF94',
                    brightYellow: '#FFFFA5',
                    brightBlue: '#D6ACFF',
                    brightMagenta: '#FF92DF',
                    brightCyan: '#A4FFFF',
                    brightWhite: '#FFFFFF'
                }
                """,
            ["Solarized Dark"] = """
                {
                    background: '#002B36',
                    foreground: '#839496',
                    cursor: '#93A1A1',
                    cursorAccent: '#002B36',
                    selectionBackground: 'rgba(38, 139, 210, 0.3)',
                    selectionForeground: '#93A1A1',
                    black: '#073642',
                    red: '#DC322F',
                    green: '#859900',
                    yellow: '#B58900',
                    blue: '#268BD2',
                    magenta: '#D33682',
                    cyan: '#2AA198',
                    white: '#EEE8D5',
                    brightBlack: '#586E75',
                    brightRed: '#CB4B16',
                    brightGreen: '#586E75',
                    brightYellow: '#657B83',
                    brightBlue: '#839496',
                    brightMagenta: '#6C71C4',
                    brightCyan: '#93A1A1',
                    brightWhite: '#FDF6E3'
                }
                """,
            ["Monokai"] = """
                {
                    background: '#272822',
                    foreground: '#F8F8F2',
                    cursor: '#F8F8F0',
                    cursorAccent: '#272822',
                    selectionBackground: 'rgba(73, 72, 62, 0.7)',
                    selectionForeground: '#F8F8F2',
                    black: '#272822',
                    red: '#F92672',
                    green: '#A6E22E',
                    yellow: '#F4BF75',
                    blue: '#66D9EF',
                    magenta: '#AE81FF',
                    cyan: '#A1EFE4',
                    white: '#F8F8F2',
                    brightBlack: '#75715E',
                    brightRed: '#F92672',
                    brightGreen: '#A6E22E',
                    brightYellow: '#F4BF75',
                    brightBlue: '#66D9EF',
                    brightMagenta: '#AE81FF',
                    brightCyan: '#A1EFE4',
                    brightWhite: '#F9F8F5'
                }
                """,
            ["Nord"] = """
                {
                    background: '#2E3440',
                    foreground: '#D8DEE9',
                    cursor: '#D8DEE9',
                    cursorAccent: '#2E3440',
                    selectionBackground: 'rgba(67, 76, 94, 0.7)',
                    selectionForeground: '#ECEFF4',
                    black: '#3B4252',
                    red: '#BF616A',
                    green: '#A3BE8C',
                    yellow: '#EBCB8B',
                    blue: '#81A1C1',
                    magenta: '#B48EAD',
                    cyan: '#88C0D0',
                    white: '#E5E9F0',
                    brightBlack: '#4C566A',
                    brightRed: '#BF616A',
                    brightGreen: '#A3BE8C',
                    brightYellow: '#EBCB8B',
                    brightBlue: '#81A1C1',
                    brightMagenta: '#B48EAD',
                    brightCyan: '#8FBCBB',
                    brightWhite: '#ECEFF4'
                }
                """,
            ["Default"] = "{}"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly PendingTerminalMessageBuffer _pendingTerminalMessages =
        new(MaxPendingTerminalMessageBytes, static () =>
            Core.Logging.FileLogger.Debug(
                "EmbeddedSSH pending terminal output exceeded the pre-ready buffer limit; oldest buffered output was dropped."));
    private readonly ResizeFailureLogThrottle _resizeLogThrottle = new ResizeFailureLogThrottle();
    private readonly object _macroExpectSync = new();

    private string? _sessionLogKey;
    private string _sessionLogDisplayName = string.Empty;
    private string _sessionLogEndpoint = string.Empty;
    private SshShellSession? _session;
    private Heimdall.Terminal.ITerminalSession? _terminalSession;
    private SessionTabViewModel? _sessionTab;
    private SessionPaneModel? _ownerPane;
    private System.Threading.Timer? _keepAliveTimer;
    private System.Threading.Timer? _autoReconnectTimer;
    private readonly AutoReconnectTickScheduler _autoReconnectTickScheduler = new();
    private Action<ReadOnlyMemory<byte>>? _terminalDataHandler;
    private Action<int>? _terminalExitHandler;
    private Core.Localization.LocalizationManager? _localizer;
    private ServerHealthMonitor? _healthMonitor;
    private WinRmEarlyOutputDiagnostic? _winRmEarlyOutputDiagnostic;
    private MacroExpectObserver? _activeMacroExpectObserver;
    private readonly List<MacroEntry> _macroEntries = [];
    private readonly Stopwatch _macroStopwatch = new();

    private bool _healthPanelVisible;
    private bool _disposed;
    private bool _webViewInitializationStarted;
    private bool _webViewInitialized;
    private bool _terminalPageNavigationPending;
    private bool _terminalPageLoaded;
    private bool _terminalReady;
    private bool _webViewUnavailable;
    private bool _initialTerminalFocusApplied;
    private bool _sleepPreventionActive;
    private bool _userInitiatedDisconnect;
    private bool _isRecording;
    private bool _localeChangeSubscribed;
    private bool _autoReconnectOnProcessExit = true;
    private bool _terminalSessionHasInput;
    private string? _pendingSecurityDisconnectMessage;
    private DateTimeOffset? _terminalSessionAttachedAtUtc;
    private int _autoReconnectAttempt;
    private int _autoReconnectMaxAttempts;
    private int _autoReconnectSecondsRemaining;

    /// <summary>Localizer for translating user-facing strings. Set by EmbeddedSessionManager.</summary>
    public Core.Localization.LocalizationManager? Localizer
    {
        get => _localizer;
        set => _localizer = value;
    }

    /// <summary>Terminal appearance settings (font, color scheme). Set by EmbeddedSessionManager before Loaded fires.</summary>
    public AppSettings? TerminalSettings { get; set; }

    /// <summary>Per-profile session-logging override. Null means inherit the live global setting.</summary>
    public bool? SessionLoggingOverride { get; set; }

    /// <summary>
    /// Session transcript writer. Set by EmbeddedSessionManager. When null, transcript recording
    /// (auto and manual) is unavailable for this view.
    /// </summary>
    public ISessionLogService? SessionLogService { get; set; }

    private bool IsSessionConnected =>
        (_session?.IsConnected ?? false) || (_terminalSession?.IsRunning ?? false);

    /// <summary>
    /// Raised when the user clicks the Split button in the header strip.
    /// The subscriber (EmbeddedSessionManager) shows the split picker context menu.
    /// </summary>
    public event Action? SplitRequested;

    /// <summary>
    /// Raised when the user types input and broadcast mode may be active.
    /// The subscriber (MainViewModel) decides whether to relay to other terminals.
    /// </summary>
    public event Action<byte[]>? BroadcastInput;

    public event Action<string>? CurrentDirectoryChanged;

    /// <summary>
    /// Raised when the user clicks the Reconnect button after a disconnection.
    /// The subscriber (EmbeddedSessionManager) re-establishes the connection
    /// using the original server parameters.
    /// </summary>
    public event Action? ReconnectRequested;

    internal event Action<ReconnectRequestContext>? ReconnectContextRequested;

    /// <summary>
    /// Raised when the user clicks the Close button on the disconnect overlay.
    /// The subscriber (EmbeddedSessionManager) closes the owning session tab
    /// through the shared <c>ConnectionViewModel.CloseSessionAsync</c> path.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Raised synchronously when the hosted terminal process exits.
    /// </summary>
    internal event Action? TerminalProcessExited;

    internal SessionPaneModel? OwningPane => _ownerPane;

    public void SetOwningPane(SessionPaneModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _ownerPane = pane;
    }

    public EmbeddedSshView()
    {
        InitializeComponent();

        // Set WebView2 background from theme to avoid flash of wrong color
        if (TryFindResource("BackgroundColor") is System.Windows.Media.Color themeColor)
        {
            TerminalWebView.DefaultBackgroundColor =
                System.Drawing.Color.FromArgb(themeColor.A, themeColor.R, themeColor.G, themeColor.B);
        }

        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    /// <summary>
    /// Wires the view to a connected SSH shell session. Must be called
    /// exactly once, immediately after construction.
    /// </summary>
    public void InitializeSession(
        SshShellSession session,
        SessionTabViewModel sessionTab,
        string displayName,
        string endpoint,
        int keepAliveIntervalSeconds = AppSettings.DefaultSshTmoutResetIntervalSeconds)
    {
        InitializeConnecting(sessionTab, displayName, endpoint);
        AttachSession(session, keepAliveIntervalSeconds);
    }

    /// <summary>
    /// Initializes the view with a terminal session backed by Plink/ConPTY.
    /// </summary>
    public void InitializeTerminalSession(
        Heimdall.Terminal.ITerminalSession terminalSession,
        SessionTabViewModel sessionTab,
        string displayName,
        int keepAliveIntervalSeconds = AppSettings.DefaultSshTmoutResetIntervalSeconds,
        string? endpoint = null,
        string connectedStatus = "Connected",
        bool autoReconnectOnProcessExit = true)
    {
        var endpointLabel = string.IsNullOrWhiteSpace(endpoint)
            ? L("SshEndpointViaPlink")
            : endpoint;

        InitializeConnecting(sessionTab, displayName, endpointLabel);
        AttachTerminalSession(
            terminalSession,
            keepAliveIntervalSeconds,
            connectedStatus,
            autoReconnectOnProcessExit);
    }

    /// <summary>
    /// Phase 1: prepares the view to display a "Connecting..." placeholder
    /// without an active session. Sets the tab, header, endpoint, and overlay
    /// text. Localizes the toolbar and transitions tab status to "Connecting".
    /// Must be called exactly once, before AttachSession or AttachTerminalSession.
    /// Keepalive and sleep prevention start only after a session is attached.
    /// </summary>
    public void InitializeConnecting(
        SessionTabViewModel sessionTab,
        string displayName,
        string endpoint)
    {
        ArgumentNullException.ThrowIfNull(sessionTab);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EmbeddedSshView));
        }

        if (_sessionTab is not null)
        {
            throw new InvalidOperationException("EmbeddedSshView already initialized.");
        }

        _sessionTab = sessionTab;
        _sessionLogDisplayName = displayName;
        _sessionLogEndpoint = endpoint;

        LocalizeButtons();
        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = endpoint;
        UpdateConnectingOverlay(displayName, endpoint);
        UpdateStatus("Connecting");
    }

    /// <summary>
    /// Phase 2: wires a freshly-connected SSH shell session to the view.
    /// Hooks session events, starts keepalive, acquires sleep prevention, and
    /// transitions tab status to "Connected".
    /// </summary>
    public void AttachSession(
        SshShellSession session,
        int keepAliveIntervalSeconds = AppSettings.DefaultSshTmoutResetIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EmbeddedSshView));
        }

        if (_sessionTab is null)
        {
            throw new InvalidOperationException("InitializeConnecting must be called first.");
        }

        if (_session is not null || _terminalSession is not null)
        {
            throw new InvalidOperationException("Session already attached.");
        }

        _session = session;
        _winRmEarlyOutputDiagnostic = null;
        ShowHealthButton(true);

        _session.DataReceived += OnDataReceived;
        _session.Disconnected += OnDisconnected;
        _session.SecurityEventOccurred += OnSessionSecurityEvent;

        // A panel left open across a reconnect is watching a monitor the
        // disconnect stopped; point it at the new session's client.
        if (_healthPanelVisible)
        {
            StartHealthMonitor();
        }

        UpdateStatus("Connected");
        StartKeepAliveTimer(keepAliveIntervalSeconds);
        AcquireSleepPrevention();
        // A successful attach ends the coordinator-owned reconnect chain.
        _autoReconnectAttempt = 0;

        TryAutoStartSessionLog();
    }

    /// <summary>
    /// Phase 2 variant: wires a freshly-connected terminal session to the view.
    /// Hooks terminal events, starts keepalive, acquires sleep prevention, and
    /// transitions tab status to the supplied connected-status key.
    /// </summary>
    public void AttachTerminalSession(
        Heimdall.Terminal.ITerminalSession terminalSession,
        int keepAliveIntervalSeconds = AppSettings.DefaultSshTmoutResetIntervalSeconds,
        string connectedStatus = "Connected",
        bool autoReconnectOnProcessExit = true)
    {
        ArgumentNullException.ThrowIfNull(terminalSession);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EmbeddedSshView));
        }

        if (_sessionTab is null)
        {
            throw new InvalidOperationException("InitializeConnecting must be called first.");
        }

        if (_session is not null || _terminalSession is not null)
        {
            throw new InvalidOperationException("Session already attached.");
        }

        _terminalSession = terminalSession;
        _autoReconnectOnProcessExit = autoReconnectOnProcessExit;
        ShowHealthButton(false);
        _terminalSessionHasInput = false;
        _terminalSessionAttachedAtUtc = DateTimeOffset.UtcNow;
        _winRmEarlyOutputDiagnostic = IsWinRmSessionTab() ? new WinRmEarlyOutputDiagnostic() : null;
        _terminalDataHandler = OnTerminalDataReceived;
        _terminalExitHandler = OnTerminalProcessExited;

        _terminalSession.DataReceived += _terminalDataHandler;
        _terminalSession.ProcessExited += _terminalExitHandler;

        // The xterm.js convertEol option is baked at terminal construction time
        // (GetTerminalHtml). For SSH the view is mounted in a "Connecting" state
        // before the session exists, so that early value is always false. Push the
        // correct value now that the real session type is known: pipe-mode (plink)
        // output mixes bare-LF content (e.g. the SSH pre-auth banner) that xterm
        // must render as CR+LF.
        PostTerminalMessage(MsgSetConvertEol
            + (terminalSession is Heimdall.Terminal.PipeModeSession ? "true" : "false"));

        UpdateStatus(connectedStatus);
        StartKeepAliveTimer(keepAliveIntervalSeconds);
        AcquireSleepPrevention();
        // A successful attach ends the coordinator-owned reconnect chain.
        _autoReconnectAttempt = 0;

        TryAutoStartSessionLog();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopHealthMonitor();
        StopTranscript();
        StopKeepAliveTimer();
        StopAutoReconnectTimer();
        ReleaseSleepPrevention();

        Loaded -= OnLoaded;
        IsVisibleChanged -= OnVisibilityChanged;

        _terminalReady = false;

        if (_localeChangeSubscribed && _localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localeChangeSubscribed = false;
        }

        if (_webViewInitialized && TryGetCoreWebView2(out CoreWebView2? core, allowDuringDispose: true))
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnWebViewProcessFailed;
            // Detach to prevent handler leak identified by audit-2026-04-22 (PERF-01).
            core.NavigationStarting -= OnWebViewNavigationStarting;
            core.NavigationCompleted -= OnWebViewNavigationCompleted;
        }

        if (_session is not null)
        {
            _session.DataReceived -= OnDataReceived;
            _session.Disconnected -= OnDisconnected;
            _session.SecurityEventOccurred -= OnSessionSecurityEvent;

            try
            {
                _session.Disconnect();
            }
            catch (ObjectDisposedException)
            {
                // Already cleaned up.
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"EmbeddedSSH Disconnect during dispose failed: {ex.Message}");
            }

            try
            {
                _session.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by Disconnect path.
            }

            _session = null;
        }

        if (_terminalSession is not null)
        {
            if (_terminalDataHandler is not null)
            {
                _terminalSession.DataReceived -= _terminalDataHandler;
            }

            if (_terminalExitHandler is not null)
            {
                _terminalSession.ProcessExited -= _terminalExitHandler;
            }

            try { _terminalSession.Kill(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedSshView] terminal kill: {ex.Message}"); }
            try { _terminalSession.Dispose(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedSshView] terminal dispose: {ex.Message}"); }
            _terminalSession = null;
        }

        _winRmEarlyOutputDiagnostic = null;
        _terminalDataHandler = null;
        _terminalExitHandler = null;

        if (TerminalWebView is IDisposable disposableWebView)
        {
            try { disposableWebView.Dispose(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedSshView] WebView dispose: {ex.Message}"); }
        }

        _pendingTerminalMessages.Clear();

        Core.Logging.FileLogger.Info("EmbeddedSSH Dispose completed");
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _terminalReady && !_disposed)
        {
            // Re-focus terminal when tab becomes visible (tab switch),
            // but only if no other focusable control currently has keyboard focus
            BeginInvokeIfAvailable(() =>
            {
                if (_disposed || !_terminalReady) return;

                IInputElement? currentFocus = System.Windows.Input.Keyboard.FocusedElement;
                bool focusIsElsewhere = currentFocus is not null
                    && currentFocus is not System.Windows.Window
                    && !IsKeyboardFocusWithin;

                if (!focusIsElsewhere)
                {
                    TerminalWebView.Focus();
                    PostTerminalMessage("focus:");
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebView2Async();
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _userInitiatedDisconnect = true;
            Core.Logging.FileLogger.Info("EmbeddedSSH Disconnect requested by user");
            UpdateStatus("Disconnected");

            if (_session is not null)
            {
                _session.Disconnect();
            }
            else
            {
                _terminalSession?.Kill();
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSSH manual disconnect failed: {ex.Message}");
            UpdateStatus("Error");
        }
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        SplitRequested?.Invoke();
    }

    private void OnHealthToggleClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _healthPanelVisible = !_healthPanelVisible;

        if (_healthPanelVisible)
        {
            ShowHealthPanel();
        }
        else
        {
            HideHealthPanel();
        }
    }

    private void ShowHealthPanel()
    {
        HealthPanel.Visibility = Visibility.Visible;
        HealthColumnDef.Width = new GridLength(180);
        LocalizeHealthLabels();
        StartHealthMonitor();
    }

    private void StartHealthMonitor()
    {
        var client = _session?.Client;
        if (client is null || !client.IsConnected)
        {
            Core.Logging.FileLogger.Warn(
                "ServerHealthMonitor: no connected SSH client available for health monitoring");
            return;
        }

        StopHealthMonitor();

        _healthMonitor = new ServerHealthMonitor();
        _healthMonitor.HealthUpdated += OnHealthDataReceived;
        _ = _healthMonitor.StartAsync(client);

        Core.Logging.FileLogger.Info("ServerHealthMonitor started");
    }

    private void HideHealthPanel()
    {
        HealthPanel.Visibility = Visibility.Collapsed;
        HealthColumnDef.Width = new GridLength(0);
        StopHealthMonitor();
    }

    private void ShowHealthButton(bool visible)
    {
        HealthToggleButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && _healthPanelVisible)
        {
            _healthPanelVisible = false;
            HideHealthPanel();
        }
    }

    private void StopHealthMonitor()
    {
        if (_healthMonitor is not null)
        {
            _healthMonitor.HealthUpdated -= OnHealthDataReceived;
            _healthMonitor.Dispose();
            _healthMonitor = null;
            Core.Logging.FileLogger.Info("ServerHealthMonitor stopped");
        }
    }

    private void OnHealthDataReceived(ServerHealthData data)
    {
        BeginInvokeIfAvailable(() =>
        {
            if (_disposed || !_healthPanelVisible)
            {
                return;
            }

            if (!data.IsSupported)
            {
                SetHealthProgressEnabled(false);
                string unsupported = L("HealthPanelUnsupported");
                CpuProgressBar.Value = 0;
                CpuPercentText.Text = unsupported;
                RamProgressBar.Value = 0;
                RamDetailText.Text = unsupported;
                DiskProgressBar.Value = 0;
                DiskDetailText.Text = unsupported;
                return;
            }

            SetHealthProgressEnabled(true);
            CpuProgressBar.Value = data.CpuPercent;
            CpuPercentText.Text = $"{data.CpuPercent:F1}%";

            double ramPercent = data.MemTotalMb > 0
                ? (double)data.MemUsedMb / data.MemTotalMb * 100.0
                : 0;
            RamProgressBar.Value = ramPercent;
            RamDetailText.Text = $"{data.MemUsedMb} / {data.MemTotalMb} MB";

            DiskProgressBar.Value = data.DiskPercent;
            DiskDetailText.Text = $"{data.DiskUsed} / {data.DiskTotal}";
        });
    }

    private void SetHealthProgressEnabled(bool isEnabled)
    {
        CpuProgressBar.IsEnabled = isEnabled;
        RamProgressBar.IsEnabled = isEnabled;
        DiskProgressBar.IsEnabled = isEnabled;
    }

    private void LocalizeHealthLabels()
    {
        HealthCpuLabel.Text = L("HealthPanelCpu");
        HealthRamLabel.Text = L("HealthPanelRam");
        HealthDiskLabel.Text = L("HealthPanelDisk");
        HealthToggleButton.ToolTip = L("HealthPanelToggle");
    }

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

    private void OnAutoReconnectCancelClick(object sender, RoutedEventArgs e)
    {
        StopAutoReconnectTimer();
        AutoReconnectOverlay.Visibility = Visibility.Collapsed;
        Core.Logging.FileLogger.Info("EmbeddedSSH auto-reconnect cancelled by user");
        ShowReconnectOverlay();
    }

    private void ShowReconnectOverlay()
    {
        AutoReconnectOverlay.Visibility = Visibility.Collapsed;
        ReconnectOverlay.Visibility = Visibility.Visible;
        BeginInvokeIfAvailable(
            () =>
            {
                if (_disposed || ReconnectOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                OverlayReconnectButton.Focus();
            },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HideReconnectOverlay()
    {
        ReconnectOverlay.Visibility = Visibility.Collapsed;
    }

    internal static int ComputeAutoReconnectDelaySeconds(AppSettings? settings, int attempt)
    {
        return attempt switch
        {
            1 => settings?.SshAutoReconnectFirstDelaySeconds ?? 2,
            2 => settings?.SshAutoReconnectSecondDelaySeconds ?? 5,
            _ => settings?.SshAutoReconnectSubsequentDelaySeconds ?? 15,
        };
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
        AutoReconnectOverlay.Visibility = Visibility.Visible;
        BeginInvokeIfAvailable(
            () =>
            {
                if (_disposed || AutoReconnectOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                AutoReconnectCancelButton.Focus();
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
            AutoReconnectOverlay.Visibility = Visibility.Collapsed;
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

    private void HideConnectingOverlay()
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(HideConnectingOverlay);
            return;
        }

        ConnectingOverlay.Visibility = Visibility.Collapsed;
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

    private async Task InitializeWebView2Async()
    {
        if (_disposed || _webViewInitializationStarted)
        {
            return;
        }

        _webViewInitializationStarted = true;

        try
        {
            if (!Services.WebView2Helper.IsAvailable)
            {
                ShowWebViewUnavailable(
                    _localizer?["ErrorWebView2NotFound"]
                    ?? "ErrorWebView2NotFound");
                return;
            }

            Core.Logging.FileLogger.Info("EmbeddedSSH initializing WebView2 terminal surface");

            CoreWebView2Environment env = await Services.WebView2Helper.CreateEnvironmentAsync("SSH");
            await TerminalWebView.EnsureCoreWebView2Async(env);
            if (_disposed || !TryGetCoreWebView2(out CoreWebView2? core))
            {
                return;
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnWebViewProcessFailed;

            // Block all navigation away from the inline terminal page
            core.NavigationStarting += OnWebViewNavigationStarting;
            core.NavigationCompleted += OnWebViewNavigationCompleted;

            _terminalPageLoaded = false;
            _terminalPageNavigationPending = true;
            core.NavigateToString(GetTerminalHtml());
            _webViewInitialized = true;

            Core.Logging.FileLogger.Info("EmbeddedSSH WebView2 NavigateToString issued");
        }
        catch (Exception ex)
        {
            ShowWebViewUnavailable(
                _localizer?["ErrorWebView2InitUnavailable"]
                ?? "ErrorWebView2InitUnavailable");
            Core.Logging.FileLogger.Warn($"EmbeddedSSH WebView2 initialization failed: {ex.Message}");
        }
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Core.Logging.FileLogger.Warn($"EmbeddedSSH WebView2 process failed: {e.ProcessFailedKind}");
        ShowWebViewUnavailable(_localizer?["ErrorTerminalRendererCrashed"] ?? "ErrorTerminalRendererCrashed");
    }

    private void OnWebViewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs navArgs)
    {
        if (_terminalPageNavigationPending && !_terminalPageLoaded)
        {
            _terminalPageNavigationPending = false;
            return;
        }

        navArgs.Cancel = true;
        Core.Logging.FileLogger.Warn(
            $"EmbeddedSSH blocked terminal WebView navigation to: {DescribeWebViewText(navArgs.Uri)}");
    }

    private void OnWebViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs navArgs)
    {
        _terminalPageNavigationPending = false;

        if (!navArgs.IsSuccess)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSSH terminal page navigation completed with status: {navArgs.WebErrorStatus}");
            return;
        }

        _terminalPageLoaded = true;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string source = args.Source;
        if (!IsTrustedTerminalMessageSource(source))
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSSH rejected WebMessage from unexpected source: {DescribeWebViewText(source)}");
            return;
        }

        string message;

        try
        {
            message = args.TryGetWebMessageAsString();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedSSH invalid WebView2 message: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Open URL: terminal requests to open a link in the default browser
        if (message.StartsWith(MsgOpenUrl, StringComparison.Ordinal))
        {
            string url = message[MsgOpenUrl.Length..];
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = uri.AbsoluteUri,
                        UseShellExecute = true
                    })?.Dispose();
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"EmbeddedSSH open-url failed: {ex.Message}");
                }
            }
            return;
        }

        if (message.StartsWith(MsgReady, StringComparison.Ordinal))
        {
            _terminalReady = true;
            Core.Logging.FileLogger.Info($"EmbeddedSSH terminal ready: {message}");

            if (TryParseSize(message.AsSpan(MsgReady.Length), out int readyCols, out int readyRows))
            {
                ResizeSession(readyCols, readyRows);
            }

            FlushPendingTerminalMessages();
            ApplyInitialTerminalFocus();
            HideConnectingOverlay();
            return;
        }

        if (message.StartsWith(MsgResize, StringComparison.Ordinal))
        {
            if (TryParseSize(message.AsSpan(MsgResize.Length), out int cols, out int rows))
            {
                ResizeSession(cols, rows);
            }
            return;
        }

        if (message.StartsWith(MsgInput, StringComparison.Ordinal))
        {
            string base64 = message[MsgInput.Length..];
            if (!TryAcceptInboundBase64Payload("input", base64))
            {
                return;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                WriteToSession(bytes);

                // Broadcast to other terminals when broadcast mode is active
                try
                {
                    BroadcastInput?.Invoke(bytes);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"EmbeddedSSH BroadcastInput subscriber failed: {ex.Message}");
                }
            }
            catch (FormatException ex)
            {
                Core.Logging.FileLogger.Warn($"EmbeddedSSH invalid input payload: {ex.Message}");
            }
            return;
        }

        if (message.StartsWith(MsgCwd, StringComparison.Ordinal))
        {
            string base64 = message[MsgCwd.Length..];
            if (!TryAcceptInboundBase64Payload("cwd", base64))
            {
                return;
            }

            if (TryDecodeCurrentDirectoryPayload(base64, out string? path))
            {
                CurrentDirectoryChanged?.Invoke(path);
            }

            return;
        }

        // Clipboard write: terminal wants to copy text to system clipboard
        if (message.StartsWith(MsgClipboardWrite, StringComparison.Ordinal))
        {
            string base64 = message[MsgClipboardWrite.Length..];
            if (!TryAcceptInboundBase64Payload("clipboard-write", base64))
            {
                return;
            }

            try
            {
                // Safe: the JS host posts a single complete UTF-8 message in one chunk;
                // boundaries cannot fall mid-character. No stateful decoder needed.
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"EmbeddedSSH clipboard-write failed: {ex.Message}");
            }
            return;
        }

        // Clipboard read: terminal requests paste from system clipboard
        // Protected by SmartPasteGuard to prevent dangerous multi-line or destructive pastes.
        if (message.StartsWith(MsgClipboardRead, StringComparison.Ordinal))
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        Heimdall.Terminal.SmartPasteGuard.PasteRisk risk =
                            Heimdall.Terminal.SmartPasteGuard.Evaluate(text);

                        if (risk == Heimdall.Terminal.SmartPasteGuard.PasteRisk.Dangerous)
                        {
                            if (!ConfirmPaste(
                                text,
                                AppDialogViewModels.PasteRisk.Dangerous))
                            {
                                return;
                            }
                        }
                        else if (risk == Heimdall.Terminal.SmartPasteGuard.PasteRisk.MultiLine)
                        {
                            if (!ConfirmPaste(
                                text,
                                AppDialogViewModels.PasteRisk.MultiLine))
                            {
                                return;
                            }
                        }

                        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                        PostTerminalMessage(MsgClipboardPaste + base64);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"EmbeddedSSH clipboard-read failed: {ex.Message}");
            }
            return;
        }

        Core.Logging.FileLogger.Warn(
            $"EmbeddedSSH dropped unknown WebView2 terminal message: {DescribeWebViewText(message)}");
    }

    internal static bool TryDecodeCurrentDirectoryPayload(
        string base64,
        [NotNullWhen(true)] out string? path)
    {
        path = null;

        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        if (!IsInboundWebMessageBase64PayloadWithinLimit(base64))
        {
            return false;
        }

        try
        {
            string decodedPath = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            if (string.IsNullOrWhiteSpace(decodedPath))
            {
                return false;
            }

            path = decodedPath;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool IsInboundWebMessageBase64PayloadWithinLimit(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        return base64.Length <= MaxInboundWebMessageBase64Length;
    }

    private static bool TryAcceptInboundBase64Payload(string messageKind, string base64)
    {
        if (IsInboundWebMessageBase64PayloadWithinLimit(base64))
        {
            return true;
        }

        Core.Logging.FileLogger.Debug(
            $"EmbeddedSSH {messageKind} payload ignored because it exceeds the inbound WebView message limit.");
        return false;
    }

    private static bool IsTrustedTerminalMessageSource(string? source)
    {
        return string.Equals(source, TerminalPageMessageSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeWebViewText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return "data:";
        }

        if (text.Length <= LoggedWebViewTextLimit)
        {
            return text;
        }

        return text[..LoggedWebViewTextLimit] + "...";
    }

    private bool ConfirmPaste(string text, AppDialogViewModels.PasteRisk risk)
    {
        if (_localizer is null)
        {
            // Themed PasteConfirmDialog needs a localizer; if the field is not set
            // (DI not wired for this instance), fall back to the generic confirm
            // dialog routed through IDialogService. Use locale keys via the
            // application-level LocalizationManager when accessible, otherwise
            // refuse the paste to avoid an unthemed native dialog.
            return ConfirmPasteViaDialogService(text, risk);
        }

        var viewModel = new AppDialogViewModels.PasteConfirmDialogViewModel(
            risk,
            text,
            _localizer);
        var dialog = new AppDialogs.PasteConfirmDialog
        {
            Owner = Window.GetWindow(this),
            DataContext = viewModel,
        };

        return dialog.ShowDialog() == true;
    }

    private static bool ConfirmPasteViaDialogService(
        string text,
        AppDialogViewModels.PasteRisk risk)
    {
        var services = (Application.Current as App)?.Services;
        var dialogService = services?.GetService(typeof(IDialogService)) as IDialogService;

        if (dialogService is null)
        {
            Core.Logging.FileLogger.Warn(
                "EmbeddedSSH paste blocked: no dialog service available.");
            return false;
        }

        string titleKey = risk == AppDialogViewModels.PasteRisk.Dangerous
            ? "PasteConfirmDangerousTitle"
            : "PasteConfirmMultiLineTitle";

        var localizer = services?.GetService(typeof(Core.Localization.LocalizationManager))
            as Core.Localization.LocalizationManager;
        string title = localizer is not null ? localizer[titleKey] : titleKey;

        // ShowConfirmAsync runs synchronously to completion inside WpfDialogService
        // (the dialog is modal); GetResult is therefore non-blocking on a real Task.
        return dialogService.ShowConfirmAsync(title, text, "warning")
            .GetAwaiter()
            .GetResult();
    }

    private void OnDataReceived(byte[] data)
    {
        QueueOutput(data);
    }

    private void OnTerminalDataReceived(ReadOnlyMemory<byte> data)
    {
        QueueOutput(data.Span);
    }

    private void QueueOutput(ReadOnlySpan<byte> data)
    {
        if (_disposed || _webViewUnavailable || data.IsEmpty)
        {
            return;
        }

        ForwardToActiveMacroExpect(data);

        if (SessionLogService is not null && _sessionLogKey is not null)
        {
            // Forward raw bytes; the service owns decoding + ANSI stripping. No-op until a
            // transcript is active for this key, so replayed bootstrap bytes that arrive before
            // StartSession are not logged, and live bytes after it are logged exactly once.
            SessionLogService.WriteOutput(_sessionLogKey, data);
        }

        ObserveWinRmEarlyOutput(data);

        var message = "data:" + Convert.ToBase64String(data);
        PostTerminalMessage(message);
    }

    private void ObserveWinRmEarlyOutput(ReadOnlySpan<byte> data)
    {
        WinRmEarlyOutputDiagnostic? diagnostic = _winRmEarlyOutputDiagnostic;
        if (diagnostic is null || !diagnostic.IsActive)
        {
            return;
        }

        string? localizationKey = diagnostic.Observe(data);
        if (!diagnostic.IsActive)
        {
            _winRmEarlyOutputDiagnostic = null;
        }

        if (string.IsNullOrWhiteSpace(localizationKey))
        {
            return;
        }

        BeginInvokeIfAvailable(() =>
        {
            if (_disposed)
            {
                return;
            }

            UpdateStatus(
                "RemoteSessionHandedOff",
                displayTextOverride: L(localizationKey),
                forceErrorBrush: true);
        });
    }

    private void OnDisconnected(SshSessionDisconnectInfo disconnectInfo)
    {
        ArgumentNullException.ThrowIfNull(disconnectInfo);

        BeginInvokeIfAvailable(() =>
        {
            if (_disposed)
            {
                return;
            }

            // The session disposes its SSH client on disconnect; a monitor
            // left running would poll that disposed client until the tab
            // closes. Whatever branch below is taken, there is nothing to poll.
            StopHealthMonitor();

            string? errorMessage = disconnectInfo.Message;
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                string template = L("SshTerminalDisconnectMarker");
                string marker = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    template, errorMessage);
                string disconnectText = $"\r\n\x1b[90m{marker}\x1b[0m\r\n";
                QueueOutput(Encoding.UTF8.GetBytes(disconnectText));
            }

            PostTerminalMessage("session-ended:");

            string? securityDisconnectMessage = _pendingSecurityDisconnectMessage;
            _pendingSecurityDisconnectMessage = null;
            if (!string.IsNullOrWhiteSpace(securityDisconnectMessage))
            {
                ReconnectMessageText.Text = securityDisconnectMessage;
                UpdateStatus("Error");
                ShowReconnectOverlay();
                return;
            }

            UpdateStatus("Disconnected");

            if (_userInitiatedDisconnect)
            {
                return;
            }

            if (!SshReconnectPolicy.AllowsAutoReconnect(disconnectInfo))
            {
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSSH auto-reconnect skipped for disconnect: " +
                    $"code={disconnectInfo.Failure?.Code.ToString() ?? "none"} clean={disconnectInfo.IsClean}");
                ShowReconnectOverlay();
                return;
            }

            int maxAttempts = Math.Clamp(TerminalSettings?.SshAutoReconnectAttempts ?? 3, 1, 10);
            if (TerminalSettings?.SshAutoReconnect == true
                && _autoReconnectAttempt < maxAttempts)
            {
                _autoReconnectAttempt++;
                int delay = ComputeAutoReconnectDelaySeconds(TerminalSettings, _autoReconnectAttempt);
                StartAutoReconnectCountdown(delay, _autoReconnectAttempt, maxAttempts);
                return;
            }

            ShowReconnectOverlay();
        });
    }

    private void OnSessionSecurityEvent(SshSessionSecurityEvent evt)
    {
        if (evt.Code != SshFailureCode.HostKeyMismatch)
        {
            return;
        }

        string message = FormatHostKeyMismatchMidSession(evt);
        _pendingSecurityDisconnectMessage = message;

        BeginInvokeIfAvailable(() =>
        {
            if (_disposed)
            {
                return;
            }

            ReconnectMessageText.Text = message;
            string securityText = $"\r\n\x1b[91m{message}\x1b[0m\r\n";
            QueueOutput(Encoding.UTF8.GetBytes(securityText));
        });
    }

    private void OnTerminalProcessExited(int exitCode)
    {
        TerminalProcessExited?.Invoke();

        TimeSpan runtime = _terminalSessionAttachedAtUtc is { } attachedAt
            ? DateTimeOffset.UtcNow - attachedAt
            : TimeSpan.Zero;
        bool suppressAutoReconnect = TerminalReconnectPolicy.SuppressesConnectTimeProcessExit(
            _sessionTab?.ConnectionType,
            _terminalSession is Heimdall.Terminal.PipeModeSession,
            _terminalSessionHasInput,
            runtime,
            TerminalReconnectPolicy.ResolveConnectTimeExitWindow(
                TerminalSettings?.SshConnectTimeExitWindowSeconds));
        SshSessionDisconnectInfo disconnectInfo =
            TerminalReconnectPolicy.ClassifyProcessExit(
                exitCode,
                _autoReconnectOnProcessExit,
                suppressAutoReconnect);
        OnDisconnected(disconnectInfo);
    }

    private void ResizeSession(int columns, int rows)
    {
        if (_disposed || columns <= 0 || rows <= 0)
        {
            return;
        }

        int clampedColumns = Math.Clamp(columns, 1, MaxResizeColumns);
        int clampedRows = Math.Clamp(rows, 1, MaxResizeRows);

        try
        {
            if (_session is not null)
            {
                _session.Resize(clampedColumns, clampedRows);
            }
            else
            {
                _terminalSession?.Resize(clampedColumns, clampedRows);
            }
        }
        catch (Exception ex)
        {
            ResizeFailureLogDecision decision = _resizeLogThrottle.RecordFailure(ex);
            switch (decision.Action)
            {
                case ResizeFailureLogAction.Skip:
                    break;
                case ResizeFailureLogAction.LogCurrent:
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSSH resize failed for {clampedColumns}x{clampedRows}: {ex.Message}");
                    break;
                case ResizeFailureLogAction.LogRepeatSummaryThenCurrent:
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSSH previous resize failure repeated {decision.PreviousRepeatCount} times before changing.");
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSSH resize failed for {clampedColumns}x{clampedRows}: {ex.Message}");
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a write raised the transport going away rather than a defect in the caller.
    /// </summary>
    /// <remarks>
    /// Named types rather than a blanket catch: a session that has been disposed or has lost its
    /// stream is a fact about the connection, while an argument or reference fault is a bug and
    /// must keep travelling. The SSH session raises the first two of these by name, and the rest
    /// come from the socket underneath it once the connection has actually dropped.
    /// <para><see cref="ObjectDisposedException"/> is already an
    /// <see cref="InvalidOperationException"/>, so naming it changes nothing this list does. It is
    /// kept because the session raises it by name and the reader should not have to know the
    /// hierarchy to see that; a test pins the subsumption so the redundancy stays a choice rather
    /// than becoming an assumption.</para>
    /// </remarks>
    internal static bool IsTransportWriteFailure(Exception exception) => exception is
        ObjectDisposedException
        or InvalidOperationException
        or IOException
        or System.Net.Sockets.SocketException
        or Renci.SshNet.Common.SshException;

    /// <summary>
    /// Writes through a transport, dropping the input if the transport has gone.
    /// </summary>
    /// <returns><see langword="true"/> when the write was delivered.</returns>
    /// <remarks>
    /// <para>Typing into a terminal whose session has just dropped used to throw out of the
    /// WebView2 message handler, where the only catch was for a malformed payload. The local
    /// terminal already tolerated a dead process and the resize path already tolerated a dead
    /// session; the SSH write was the one that did not.</para>
    /// <para>Neither the payload nor its length is logged. Input is exactly where a password may
    /// be, and its length is as much of it as anyone should be told.</para>
    /// </remarks>
    internal static bool TryTransportWrite(Action write, string transport)
    {
        ArgumentNullException.ThrowIfNull(write);

        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (IsTransportWriteFailure(ex))
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSSH {transport} input dropped, transport unavailable: {ex.GetType().Name}");
            return false;
        }
    }

    private void WriteToSession(byte[] data, bool marksTerminalInput = true)
    {
        if (_disposed || data.Length == 0)
        {
            return;
        }

        if (_isRecording)
        {
            var delayMs = (int)_macroStopwatch.ElapsedMilliseconds;
            _macroStopwatch.Restart();

            _macroEntries.Add(new MacroEntry
            {
                // Safe: data is a single complete xterm.js onData message posted in one chunk;
                // boundaries cannot fall mid-character. No stateful decoder needed.
                Input = Encoding.UTF8.GetString(data),
                DelayMs = delayMs
            });
        }

        if (_session is not null)
        {
            SshShellSession session = _session;
            TryTransportWrite(() => session.Write(data), "ssh");
        }
        else
        {
            if (marksTerminalInput && _terminalSession is not null)
            {
                MarkTerminalInput();
            }

            _terminalSession?.Write(data);
        }
    }

    /// <summary>
    /// Sends a command string followed by a newline to the active session.
    /// Intended for external callers (e.g. SFTP "Open in Terminal").
    /// </summary>
    public void WriteCommand(string command)
    {
        WriteToSession(command + "\n");
    }

    /// <summary>
    /// Sends raw bytes to the active session without triggering broadcast.
    /// Used by the broadcast relay to avoid infinite loops.
    /// </summary>
    public void WriteBytes(byte[] data) => WriteToSession(data);

    /// <summary>
    /// Shows or hides the broadcast mode indicator badge in the header strip.
    /// </summary>
    public void SetBroadcastIndicator(bool active)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => SetBroadcastIndicator(active));
            return;
        }

        BroadcastBadge.Visibility = active
            ? Visibility.Visible
            : Visibility.Collapsed;

        BroadcastBorder.BorderThickness = active
            ? new Thickness(2)
            : new Thickness(0);
    }

    /// <summary>
    /// Invoked when the user clicks this pane's broadcast target toggle. The
    /// coordinator owns the canonical per-pane state
    /// (<c>SessionPaneModel.IsBroadcastTarget</c>); the view only requests the flip
    /// and then reflects the new value via <see cref="SetBroadcastTargetState"/>.
    /// </summary>
    public Action? BroadcastTargetToggleRequested;

    private void OnBroadcastTargetToggleClick(object sender, RoutedEventArgs e)
    {
        BroadcastTargetToggleRequested?.Invoke();
    }

    /// <summary>
    /// Shows or hides the per-pane broadcast target toggle. Visible only while
    /// broadcast mode is active and the scope is SelectedPanes; otherwise the pane
    /// chrome stays uncluttered.
    /// </summary>
    public void SetBroadcastSelectionMode(bool selectionActive)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => SetBroadcastSelectionMode(selectionActive));
            return;
        }

        BroadcastTargetToggle.Visibility = selectionActive
            ? Visibility.Visible
            : Visibility.Collapsed;

        // One-shot diagnostic so a missing per-pane toggle can be confirmed from the
        // log (does selection mode actually drive the toggle to Visible?).
        if (!_broadcastSelectionDiagnosticLogged)
        {
            _broadcastSelectionDiagnosticLogged = true;
            Core.Logging.FileLogger.Info(
                $"[Broadcast] SetBroadcastSelectionMode: selectionActive={selectionActive}, " +
                $"toggleVisibility={BroadcastTargetToggle.Visibility}");
        }

        if (selectionActive)
        {
            // In selection mode the badge/border reflect target membership and are
            // driven by SetBroadcastTargetState; start hidden until told otherwise.
            BroadcastBadge.Visibility = Visibility.Collapsed;
            BroadcastBorder.BorderThickness = new Thickness(0);
        }
    }

    private bool _broadcastSelectionDiagnosticLogged;

    /// <summary>
    /// Reflects whether this pane is currently a broadcast target: checked toggle
    /// plus a visible badge/border highlight. Only meaningful in SelectedPanes mode.
    /// </summary>
    public void SetBroadcastTargetState(bool isTarget)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => SetBroadcastTargetState(isTarget));
            return;
        }

        BroadcastTargetToggle.IsChecked = isTarget;
        BroadcastBadge.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
        BroadcastBorder.BorderThickness = isTarget ? new Thickness(2) : new Thickness(0);
    }

    /// <summary>Whether a transcript recording is currently active for this session.</summary>
    public bool IsTranscriptActive =>
        SessionLogService is not null
        && _sessionLogKey is not null
        && SessionLogService.IsSessionActive(_sessionLogKey);

    /// <summary>
    /// Starts (or restarts) transcript recording for this session through the shared
    /// <see cref="ISessionLogService"/>, bypassing the auto-start gate. Used by the manual
    /// Start Transcript menu action.
    /// </summary>
    /// <returns>The log file path, or <c>null</c> if recording could not start.</returns>
    public string? StartTranscript() => StartSessionLog();

    /// <summary>Stops the active transcript recording for this session, if any.</summary>
    public void StopTranscript()
    {
        if (SessionLogService is not null && _sessionLogKey is not null)
        {
            SessionLogService.StopSession(_sessionLogKey);
        }
    }

    /// <summary>
    /// Auto-starts transcript recording when the gate policy allows it for this session's
    /// protocol and the global logging toggle is on. No-op otherwise.
    /// </summary>
    private void TryAutoStartSessionLog()
    {
        bool globalEnabled = TerminalSettings?.SessionLoggingEnabled ?? false;
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(SessionLoggingOverride, globalEnabled);
        if (SessionLogGatePolicy.ShouldAutoStart(enabled, _sessionTab?.ConnectionType))
        {
            StartSessionLog();
        }
    }

    /// <summary>
    /// Builds the session-log context and starts recording through the service. The session key
    /// is generated once and reused for the tap, manual start/stop, and teardown.
    /// </summary>
    private string? StartSessionLog()
    {
        if (SessionLogService is null || _sessionTab is null)
        {
            return null;
        }

        _sessionLogKey ??= Guid.NewGuid().ToString("N");

        string protocol = string.IsNullOrWhiteSpace(_sessionTab.ConnectionType)
            ? "SESSION"
            : _sessionTab.ConnectionType;
        string host = string.IsNullOrWhiteSpace(_sessionLogEndpoint)
            ? _sessionLogDisplayName
            : _sessionLogEndpoint;

        SessionLogContext context = new SessionLogContext(
            _sessionLogKey,
            protocol,
            host,
            _sessionLogDisplayName,
            DateTime.UtcNow);

        return SessionLogService.StartSession(context);
    }

    private void WriteToSession(string text, bool marksTerminalInput = true)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_session is not null)
        {
            SshShellSession session = _session;
            TryTransportWrite(() => session.Write(text), "ssh");
        }
        else
        {
            if (marksTerminalInput && _terminalSession is not null)
            {
                MarkTerminalInput();
            }

            _terminalSession?.Write(text);
        }
    }

    private void MarkTerminalInput()
    {
        _terminalSessionHasInput = true;
        _winRmEarlyOutputDiagnostic?.MarkUserInput();
        _winRmEarlyOutputDiagnostic = null;
    }

    /// <summary>Whether a macro recording is currently in progress.</summary>
    public bool IsRecordingMacro => _isRecording;

    /// <summary>Clears any previous recording state and begins capturing terminal input.</summary>
    public void StartRecording()
    {
        _macroEntries.Clear();
        _macroStopwatch.Restart();
        _isRecording = true;
        Core.Logging.FileLogger.Info("Macro recording started");
    }

    /// <summary>
    /// Stops recording and returns the captured entries.
    /// Returns an empty list if recording was not active.
    /// </summary>
    public List<MacroEntry> StopRecording()
    {
        _isRecording = false;
        _macroStopwatch.Stop();
        Core.Logging.FileLogger.Info($"Macro recording stopped ({_macroEntries.Count} entries)");
        return new List<MacroEntry>(_macroEntries);
    }

    /// <summary>
    /// Replays a previously recorded macro by sending each entry to the terminal
    /// with the recorded inter-input delays.
    /// </summary>
    public async Task PlayMacro(TerminalMacro macro, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(macro);

        Core.Logging.FileLogger.Info($"Macro playback started: {macro.Name} ({macro.Entries.Count} entries)");

        var result = await MacroPlaybackExecutor.RunAsync(
            macro.Entries,
            WaitForMacroExpectAsync,
            static (delayMs, token) => Task.Delay(delayMs, token),
            bytes => WriteToSession(bytes),
            OnMacroExpectTimedOut,
            ct);

        if (result.WasStoppedByExpectTimeout)
        {
            Core.Logging.FileLogger.Warn(
                $"Macro playback stopped after expect timeout ({result.ExpectTimeoutMs ?? 0} ms): {macro.Name}");
            return;
        }

        Core.Logging.FileLogger.Info($"Macro playback completed: {macro.Name}");
    }

    private async Task<MacroExpectWaitResult> WaitForMacroExpectAsync(MacroEntry entry, CancellationToken ct)
    {
        if (entry.ExpectPattern is null)
        {
            return MacroExpectWaitResult.NotRequired;
        }

        var timeoutMs = entry.GetEffectiveExpectTimeoutMs();
        var observer = new MacroExpectObserver(entry.ExpectPattern, entry.ExpectIsRegex);
        SetActiveMacroExpectObserver(observer);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);
            var completedTask = await Task.WhenAny(observer.Completion, timeoutTask);
            if (completedTask == observer.Completion)
            {
                timeoutCts.Cancel();
                return await observer.Completion;
            }

            ct.ThrowIfCancellationRequested();
            return MacroExpectWaitResult.TimedOut(timeoutMs);
        }
        finally
        {
            ClearActiveMacroExpectObserver(observer);
        }
    }

    private void OnMacroExpectTimedOut(MacroEntry entry, MacroExpectWaitResult result)
    {
        Core.Logging.FileLogger.Warn(
            $"Macro expect timed out after {result.TimeoutMs} ms; action={entry.ExpectOnTimeout}.");

        if (entry.ExpectOnTimeout != ExpectTimeoutAction.Abort)
        {
            return;
        }

        BeginInvokeIfAvailable(() =>
        {
            if (_disposed)
            {
                return;
            }

            UpdateStatus(
                "MacroExpectTimeout",
                displayTextOverride: $"Macro expect timed out after {result.TimeoutMs} ms",
                forceErrorBrush: true);
        });
    }

    private void ForwardToActiveMacroExpect(ReadOnlySpan<byte> data)
    {
        MacroExpectObserver? observer;
        lock (_macroExpectSync)
        {
            observer = _activeMacroExpectObserver;
        }

        observer?.Observe(data);
    }

    private void SetActiveMacroExpectObserver(MacroExpectObserver observer)
    {
        lock (_macroExpectSync)
        {
            if (_activeMacroExpectObserver is not null)
            {
                throw new InvalidOperationException("A macro expect wait is already active.");
            }

            _activeMacroExpectObserver = observer;
        }
    }

    private void ClearActiveMacroExpectObserver(MacroExpectObserver observer)
    {
        lock (_macroExpectSync)
        {
            if (ReferenceEquals(_activeMacroExpectObserver, observer))
            {
                _activeMacroExpectObserver = null;
            }
        }
    }

    private sealed class MacroExpectObserver
    {
        private readonly object _sync = new();
        private readonly ExpectMatcher _matcher = new();
        private readonly string _pattern;
        private readonly bool _isRegex;
        private readonly TaskCompletionSource<MacroExpectWaitResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public MacroExpectObserver(string pattern, bool isRegex)
        {
            _pattern = pattern;
            _isRegex = isRegex;
        }

        public Task<MacroExpectWaitResult> Completion => _completion.Task;

        public void Observe(ReadOnlySpan<byte> data)
        {
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            lock (_sync)
            {
                if (_completion.Task.IsCompleted)
                {
                    return;
                }

                _matcher.Feed(data);
                if (_matcher.TryMatch(_pattern, _isRegex))
                {
                    _completion.TrySetResult(MacroExpectWaitResult.Matched);
                }
            }
        }
    }

    /// <summary>Resolves a locale key, falling back to the key name if no localizer is set.</summary>
    private string L(string key) => _localizer?[key] ?? key;

    private string FormatHostKeyMismatchMidSession(SshSessionSecurityEvent evt)
    {
        var template = L("SftpHostKeyMismatchMidSession");
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            template,
            evt.Host,
            evt.Port,
            evt.PresentedFingerprint ?? "?",
            evt.StoredFingerprint ?? "?");
    }

    private void LocalizeButtons()
    {
        if (_localizer is null) return;

        if (!_localeChangeSubscribed)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
            _localeChangeSubscribed = true;
        }

        DisconnectButton.Content = L("BtnDisconnectSession");
        ReconnectButton.Content = L("BtnReconnectSession");
        FallbackTitleText.Text = L("EmbeddedSshFallbackTitle");
        FallbackMessageText.Text = L("EmbeddedSshFallbackMessage");
        OverlayReconnectButton.Content = L("BtnReconnectSession");
        OverlayCloseButton.Content = L("BtnCloseOverlay");
        AutoReconnectCancelButton.Content = L("BtnCancelAutoReconnect");
        ReconnectMessageText.Text = L("SshDisconnectedMessage");
        AdminBadgeText.Text = L("AdminBadgeLabel");
        BroadcastBadgeText.Text = L("BroadcastBadgeLabel");

        // Tooltips
        DisconnectButton.ToolTip = L("TooltipDisconnectSession");
        ReconnectButton.ToolTip = L("TooltipReconnectSession");
        SplitButton.ToolTip = L("TooltipSplitSession");
        HealthToggleButton.ToolTip = L("TooltipToggleHealthMonitoring");
        ElevateButton.ToolTip = L("TooltipElevateToRoot");

        // Accessibility: automation names for toolbar buttons
        System.Windows.Automation.AutomationProperties.SetName(DisconnectButton, L("A11yDisconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton, L("A11yReconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(SplitButton, L("A11ySplitSession"));
        System.Windows.Automation.AutomationProperties.SetName(HealthToggleButton, L("A11yToggleHealthMonitoring"));
        System.Windows.Automation.AutomationProperties.SetName(ElevateButton, L("A11yElevateToRoot"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayReconnectButton, L("A11yReconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayCloseButton, L("A11yCloseOverlay"));
        System.Windows.Automation.AutomationProperties.SetName(AutoReconnectCancelButton, L("A11yCancelAutoReconnect"));
        System.Windows.Automation.AutomationProperties.SetName(StatusTextBlock, L("A11yConnectionStatus"));
    }

    private void OnLocaleChanged(string locale)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => OnLocaleChanged(locale));
            return;
        }

        LocalizeButtons();

        if (_healthPanelVisible)
        {
            LocalizeHealthLabels();
        }

        if (AutoReconnectOverlay.Visibility == Visibility.Visible)
        {
            int maxAttempts = Math.Clamp(TerminalSettings?.SshAutoReconnectAttempts ?? 3, 1, 10);
            AutoReconnectMessageText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("SshAutoReconnectMessage"),
                _autoReconnectAttempt,
                maxAttempts);
            UpdateAutoReconnectCountdownText();
        }
    }

    /// <summary>Show/hide the shield button for launching an elevated shell.</summary>
    public void ShowElevateButton(bool visible) =>
        ElevateButton.Visibility = visible && ElevateRequested is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Show/hide the ADMIN badge and hide the elevate button.</summary>
    public void SetElevatedIndicator(bool elevated)
    {
        AdminBadge.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Raised when the user clicks the shield button to request an elevated shell.</summary>
    public event Action? ElevateRequested;

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        ElevateRequested?.Invoke();
    }

    private void PostTerminalMessage(string message)
    {
        if (_disposed || _webViewUnavailable || IsDispatcherShuttingDown())
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => PostTerminalMessage(message));
            return;
        }

        if (_terminalReady && TryGetCoreWebView2(out var core))
        {
            try
            {
                core.PostWebMessageAsString(message);
            }
            catch (ObjectDisposedException)
            {
                DisableWebViewPosts();
            }
            catch (InvalidOperationException)
            {
                DisableWebViewPosts();
            }

            return;
        }

        if (!_webViewUnavailable && !IsDispatcherShuttingDown())
        {
            _pendingTerminalMessages.Enqueue(message);
        }
    }

    private void FlushPendingTerminalMessages()
    {
        if (_disposed || !_terminalReady || !TryGetCoreWebView2(out var core))
        {
            return;
        }

        while (_pendingTerminalMessages.TryDequeue(out var message))
        {
            try
            {
                core.PostWebMessageAsString(message);
            }
            catch (ObjectDisposedException)
            {
                DisableWebViewPosts();
                return;
            }
            catch (InvalidOperationException)
            {
                DisableWebViewPosts();
                return;
            }
        }
    }

    private bool TryGetCoreWebView2([NotNullWhen(true)] out CoreWebView2? core, bool allowDuringDispose = false)
    {
        core = null;

        if ((!allowDuringDispose && _disposed) || _webViewUnavailable || IsDispatcherShuttingDown())
        {
            return false;
        }

        try
        {
            core = TerminalWebView.CoreWebView2;
            return core is not null;
        }
        catch (ObjectDisposedException)
        {
            DisableWebViewPosts();
            return false;
        }
        catch (InvalidOperationException)
        {
            DisableWebViewPosts();
            return false;
        }
    }

    private bool IsDispatcherShuttingDown() =>
        Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished;

    private void BeginInvokeIfAvailable(
        Action action,
        System.Windows.Threading.DispatcherPriority priority =
            System.Windows.Threading.DispatcherPriority.Normal)
    {
        if (_disposed || IsDispatcherShuttingDown())
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(action, priority);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can start shutting down between the check and BeginInvoke.
        }
    }

    private void DisableWebViewPosts()
    {
        _terminalReady = false;
        _webViewUnavailable = true;

        _pendingTerminalMessages.Clear();
    }

    private void ApplyInitialTerminalFocus()
    {
        if (_disposed || _webViewUnavailable || _initialTerminalFocusApplied)
        {
            return;
        }

        if (_terminalReady)
        {
            _initialTerminalFocusApplied = true;

            // Only steal focus on first mount if this view is actually visible
            // and no other focusable control currently has keyboard focus
            var currentFocus = System.Windows.Input.Keyboard.FocusedElement;
            var focusIsElsewhere = currentFocus is not null
                && currentFocus is not System.Windows.Window
                && !IsKeyboardFocusWithin;

            if (IsVisible && !focusIsElsewhere)
            {
                Core.Logging.FileLogger.Info("EmbeddedSSH applying initial terminal focus");
                TerminalWebView.Focus();
                PostTerminalMessage("focus:");
            }
        }
    }

    private void ShowWebViewUnavailable(string message)
    {
        if (_disposed)
        {
            return;
        }

        HideConnectingOverlay();
        StopAutoReconnectTimer();
        AutoReconnectOverlay.Visibility = Visibility.Collapsed;
        _webViewUnavailable = true;
        _terminalReady = false;

        _pendingTerminalMessages.Clear();

        TerminalWebView.Visibility = System.Windows.Visibility.Collapsed;
        FallbackPanel.Visibility = System.Windows.Visibility.Visible;
        FallbackMessageText.Text = message;
        UpdateStatus("Error");
    }

    private void StartKeepAliveTimer(int intervalSeconds)
    {
        if (intervalSeconds <= 0)
        {
            return;
        }

        _keepAliveTimer = new System.Threading.Timer(
            _ => SendKeepAlive(),
            null,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(intervalSeconds));
        Core.Logging.FileLogger.Info(
            $"SSH keepalive timer started ({intervalSeconds}s interval)");
    }

    /// <remarks>
    /// Reached from the timer's own pool thread (a tick that finds the session gone) and from the
    /// UI thread (dispose, disconnect). The field is taken once, atomically, so two callers racing
    /// past a null check cannot both dereference it; the same shape as
    /// <see cref="StopAutoReconnectTimer(AutoReconnectTickScheduler, ref System.Threading.Timer?)"/>.
    /// </remarks>
    private void StopKeepAliveTimer()
    {
        System.Threading.Timer? stoppedTimer = Interlocked.Exchange(ref _keepAliveTimer, null);
        if (stoppedTimer is null)
        {
            return;
        }

        stoppedTimer.Dispose();
        Core.Logging.FileLogger.Info("SSH keepalive timer stopped");
    }

    private void SendKeepAlive()
    {
        if (_disposed)
        {
            return;
        }

        if (!IsSessionConnected)
        {
            Core.Logging.FileLogger.Warn("SSH keepalive skipped: session not connected");
            StopKeepAliveTimer();
            return;
        }

        try
        {
            WriteToSession(KeepAliveCr, marksTerminalInput: false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH keepalive failed: {ex.Message}");
        }
    }

    private void AcquireSleepPrevention()
    {
        if (!_sleepPreventionActive)
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

    private bool IsWinRmSessionTab()
    {
        return string.Equals(_sessionTab?.ConnectionType, "WINRM", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatus(
        string status,
        string? displayTextOverride = null,
        bool forceErrorBrush = false)
    {
        if (_sessionTab is not null)
        {
            _sessionTab.Status = status;
        }

        // Display localized status text while keeping internal state identifier
        var displayText = displayTextOverride ?? status switch
        {
            "Connected" => L("SshSessionStatusConnected"),
            "Disconnected" => L("SshSessionStatusDisconnected"),
            "Error" => L("SshSessionStatusError"),
            "Connecting" => L("SshSessionStatusConnecting"),
            "RemoteSessionHandedOff" => L("SshSessionStatusRemoteSessionHandedOff"),
            _ => status
        };
        StatusTextBlock.Text = displayText;

        var isDisconnected = string.Equals(status, "Disconnected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase);

        DisconnectButton.IsEnabled = !_disposed && !isDisconnected;
        DisconnectButton.Visibility = isDisconnected ? Visibility.Collapsed : Visibility.Visible;
        ReconnectButton.Visibility = isDisconnected ? Visibility.Visible : Visibility.Collapsed;

        StatusTextBlock.Foreground = forceErrorBrush
            || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
            ? GetBrush("ErrorBrush", Brushes.IndianRed)
            : GetBrush("TextPrimaryBrush", Brushes.White);
    }

    private Brush GetBrush(string resourceKey, Brush fallback)
    {
        return TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private static bool TryParseSize(ReadOnlySpan<char> payload, out int columns, out int rows)
    {
        columns = 0;
        rows = 0;

        var separatorIndex = payload.IndexOf(',');
        if (separatorIndex <= 0 || separatorIndex >= payload.Length - 1)
        {
            return false;
        }

        return int.TryParse(payload[..separatorIndex], out columns)
            && int.TryParse(payload[(separatorIndex + 1)..], out rows)
            && columns > 0
            && rows > 0;
    }

    private string GetTerminalHtml()
    {
        string html = TerminalAssetsLoader.TerminalHtml;

        foreach ((string Tag, Func<string> ContentFactory, string WrapperStart, string WrapperEnd) asset in InlineAssets)
        {
            string content = asset.ContentFactory();
            html = html.Replace(
                asset.Tag,
                asset.WrapperStart + content + asset.WrapperEnd,
                StringComparison.Ordinal);
        }

        // Inject terminal appearance settings from AppSettings
        string fontFamily = TerminalSettings?.TerminalFontFamily ?? "Consolas";
        int fontSize = TerminalSettings?.TerminalFontSize ?? 14;
        string schemeName = TerminalSettings?.TerminalColorScheme ?? "Dracula";
        string convertEol = _terminalSession is Heimdall.Terminal.PipeModeSession
            ? "true"
            : "false";

        string themeJson = ColorSchemes.TryGetValue(schemeName, out string? configuredThemeJson)
            && configuredThemeJson is not null
            ? configuredThemeJson
            : ColorSchemes["Dracula"];

        // Sanitize font family to prevent injection (allow alphanumeric, spaces, commas, quotes, hyphens)
        string safeFontFamily = System.Text.RegularExpressions.Regex.Replace(
            fontFamily, @"[^a-zA-Z0-9\s,'""\-]", "");

        int safeFontSize = Math.Clamp(fontSize, 8, 28);

        // The offline NavigateToString page requires inline script/style. Keep
        // placeholder values constrained to known constants or sanitized data.
        html = html.Replace("/*{{TERMINAL_FONT_FAMILY}}*/", safeFontFamily, StringComparison.Ordinal);
        html = html.Replace("/*{{TERMINAL_FONT_SIZE}}*/", safeFontSize.ToString(), StringComparison.Ordinal);
        html = html.Replace("/*{{TERMINAL_THEME}}*/", themeJson, StringComparison.Ordinal);
        html = html.Replace("/*{{TERMINAL_CONVERT_EOL}}*/", convertEol, StringComparison.Ordinal);

        // Extract background color from the theme for CSS variables
        string bgColor = ExtractThemeColor(themeJson, "background", "#282A36");
        string fgColor = ExtractThemeColor(themeJson, "foreground", "#F8F8F2");
        string cursorColor = ExtractThemeColor(themeJson, "cursor", "#BD93F9");
        string selectionColor = ExtractThemeColor(themeJson, "selectionBackground", "rgba(68, 71, 90, 0.7)");

        html = html.Replace("/*{{CSS_BG}}*/", bgColor, StringComparison.Ordinal);
        html = html.Replace("/*{{CSS_FG}}*/", fgColor, StringComparison.Ordinal);
        html = html.Replace("/*{{CSS_CURSOR}}*/", cursorColor, StringComparison.Ordinal);
        html = html.Replace("/*{{CSS_SELECTION}}*/", selectionColor, StringComparison.Ordinal);

        html = TerminalHtmlLocalizer.Localize(html, key => _localizer?[key]);
        return html;
    }

    /// <summary>Extracts a color value from the xterm.js theme JS object literal.</summary>
    private static string ExtractThemeColor(string themeJson, string key, string fallback)
    {
        // Match pattern like: background: '#282A36' or background: 'rgba(...)'
        Match match = Regex.Match(themeJson, $@"{key}:\s*'([^']+)'");
        return match.Success ? match.Groups[1].Value : fallback;
    }
}
