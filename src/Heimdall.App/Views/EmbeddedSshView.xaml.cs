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
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Macros;
using Heimdall.App.Services.WinRm;
using Heimdall.App.Theming;
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

/// <summary>
/// WPF host for an interactive SSH shell session rendered through WebView2 + xterm.js.
/// The browser surface handles VT parsing, ANSI colors, cursor movement, and scrollback.
/// </summary>
public partial class EmbeddedSshView : UserControl, IDisposable, ITerminalCommandSink, ITerminalTextSource, ISessionPaneOwner, ITerminalSizeSource
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
    private const string MsgBell = "bell:";
    private const string MsgClipboardWrite = "clipboard-write:";
    private const string MsgClipboardRead = "clipboard-read:";
    private const string MsgFileDrop = "file-drop:";
    private const string MsgLastCmdOutput = "last-cmd-output:";
    private const string MsgCopyLastCommandOutput = "copy-last-command-output:";
    private const string MsgCmdDone = "cmd-done:";
    internal const long LongCommandAlertThresholdMs = 10_000;
    internal const string MsgOsc52Write = "osc52-write:";
    internal const int MaxOsc52DecodedBytes = 128 * 1024;
    internal const int MaxOsc52Base64Length = ((MaxOsc52DecodedBytes + 2) / 3) * 4;

    /// <summary>Inbound message: the page's text, <c>buffer:&lt;id&gt;:&lt;truncated 0|1&gt;:&lt;base64&gt;</c>.</summary>
    private const string MsgBuffer = TerminalBufferReadCoordinator.ReplyPrefix;

    /// <summary>Outbound message: gives the keyboard focus to the terminal.</summary>
    private const string MsgFocus = "focus:";
    private const string TerminalPageMessageSource = "about:blank";
    private const int LoggedWebViewTextLimit = 256;
    internal const int MaxPendingTerminalMessageBytes = 4 * 1024 * 1024;
    internal const int MaxInboundWebMessageDecodedBytes = 1 * 1024 * 1024;
    internal const int MaxInboundWebMessageBase64Length = ((MaxInboundWebMessageDecodedBytes + 2) / 3) * 4;
    private const int MaxResizeColumns = 999;
    private const int MaxResizeRows = 999;

    /// <summary>Bounds of the terminal font zoom, shared with the page through placeholders.</summary>
    internal const int TerminalFontSizeMin = 8;
    internal const int TerminalFontSizeMax = 28;

    /// <summary>Width of the server health side panel when it is open.</summary>
    private const double HealthPanelWidth = 180;

    /// <summary>
    /// ANSI sequence for the host-written end-of-session marker. Yellow keeps a readable
    /// contrast on every built-in scheme, unlike the bright-black grey it replaces.
    /// </summary>
    private const string TerminalHostMarkerColor = "\x1b[33m";
    private const string TerminalColorReset = "\x1b[0m";

    /// <summary>
    /// Argument of the <c>session-ended:</c> message telling the page the host already wrote a
    /// marker line, so the page must not add its own generic one.
    /// </summary>
    private const string SessionEndedMarkedArgument = "marked";

    /// <summary>Outbound message: sets the xterm.js convertEol option at runtime.</summary>
    private const string MsgSetConvertEol = "set-convert-eol:";

    /// <summary>Outbound message: toggles real-time regex log highlighting at runtime.</summary>
    private const string MsgSetHighlights = "set-highlights:";

    /// <summary>Outbound message: updates compiled regex highlight rules at runtime.</summary>
    private const string MsgSetHighlightRules = "set-highlight-rules:";

    /// <summary>Pushes updated regex highlight rules to the running xterm.js surface.</summary>
    internal void ApplyHighlightRules(IReadOnlyList<TerminalHighlightRuleDto>? rules)
        => PostTerminalMessage(MsgSetHighlightRules + TerminalHighlightRuleDefaults.SerializeForTerminalJs(rules));

    /// <summary>Returns active direct session-bound forwards (-L/-D/-R) owned by this SSH view.</summary>
    internal IReadOnlyList<TunnelInfo> GetActiveSessionForwards()
        => _session?.GetActiveSessionForwards() ?? [];

    /// <summary>
    /// Validates and decodes an inbound write-only OSC 52 clipboard payload.
    /// Enforces the user opt-in flag, rejects clipboard read queries (<c>?</c>), and bounds payload size.
    /// </summary>
    internal static bool TryDecodeOsc52Payload(bool allowOsc52, string base64Payload, out string decodedText)
    {
        decodedText = string.Empty;
        if (!allowOsc52 || string.IsNullOrEmpty(base64Payload))
        {
            return false;
        }

        var trimmed = base64Payload.Trim();
        if (trimmed.Length == 0 || trimmed == "?" || trimmed.Length > MaxOsc52Base64Length)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length == 0 || bytes.Length > MaxOsc52DecodedBytes)
            {
                return false;
            }

            decodedText = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrEmpty(decodedText);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Outbound message: the box the page lays itself out in while its surface is hidden
    /// (<c>viewport:W,H</c>), or an empty payload once the surface is shown. See
    /// <see cref="TerminalViewportMessage"/>.
    /// </summary>
    private const string MsgViewport = TerminalViewportMessage.Prefix;

    /// <summary>Inbound message: the page's own view of its box, <c>WxH@ratio</c>, for the log.</summary>
    private const string MsgDiag = "diag:";

    /// <summary>Outbound message: the page's localized texts, as a JSON object.</summary>
    private const string MsgSetLabels = "set-labels:";

    /// <summary>
    /// Outbound message: clipboard text the host has confirmed, base64 encoded. The page hands it
    /// to xterm's own paste path so bracketed paste and newline folding apply.
    /// </summary>
    private const string MsgClipboardPaste = "clipboard-paste:";

    /// <summary>
    /// Maps color scheme names to xterm.js theme JSON object literals.
    /// Keys must match the values stored in <see cref="AppSettings.TerminalColorScheme"/>.
    /// </summary>
    /// <summary>
    /// The colours xterm.js 5.5 draws with when a scheme sets none (the Default scheme is empty), for
    /// the page chrome that must match the terminal under it.
    /// </summary>
    internal const string XtermDefaultColours = "{ background: '#000000', foreground: '#FFFFFF', cursor: '#FFFFFF', red: '#CC0000' }";

    internal static readonly FrozenDictionary<string, string> ColorSchemes =
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
    private readonly TerminalOutputCoalescer _outputCoalescer;
    private bool _holdOutputForReadyFlush;
    private int _outputCountersLogged;
    private long _dataChunksIn;
    private long _dataPostsOut;
    private long _dataBytes;
    private readonly ResizeFailureLogThrottle _resizeLogThrottle = new ResizeFailureLogThrottle();
    private readonly object _macroExpectSync = new();

    private string? _sessionLogKey;
    private string _sessionLogDisplayName = string.Empty;
    private string _sessionLogEndpoint = string.Empty;
    private string? _lastKnownCwd;
    private Heimdall.Terminal.Logging.AsciinemaCastRecorder? _castRecorder;
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

    private readonly TerminalBufferReadCoordinator _bufferReads = new();
    private bool _healthPanelVisible;
    private bool _highlightsEnabled;
    private bool _disposed;
    private IDisposable? _lockdown;
    private bool _webViewInitializationStarted;
    private bool _webViewInitialized;
    private bool _terminalPageNavigationPending;
    private bool _terminalPageLoaded;
    private bool _terminalReady;
    private bool _webViewUnavailable;
    private bool _initialTerminalFocusApplied;
    private bool _sleepPreventionActive;
    // Written on the UI thread by the Disconnect click, read by the process-exit event, which
    // may arrive on another thread.
    private volatile bool _userInitiatedDisconnect;
    private bool _isRecording;
    private bool _localeChangeSubscribed;
    private bool _autoReconnectOnProcessExit = true;

    // True when the attached terminal session was launched with no password and no key, relying
    // on Kerberos or a Pageant key. Set on every attach and cleared when the session is torn down.
    private bool _credentialFreeLaunch;
    private bool _terminalSessionHasInput;
    private string? _pendingSecurityDisconnectMessage;
    private DateTimeOffset? _terminalSessionAttachedAtUtc;
    private DateTimeOffset? _shellSessionAttachedAtUtc;
    private bool _shellSessionHasInput;
    private int _autoReconnectAttempt;
    private int _autoReconnectMaxAttempts;
    private int _autoReconnectSecondsRemaining;

    /// <summary>
    /// Monotonic clock (<see cref="Environment.TickCount64"/>) of the last real terminal input,
    /// read by the keepalive tick on the timer thread. Written on the UI thread.
    /// </summary>
    private long _lastTerminalInputMilliseconds = TerminalKeepAlivePolicy.NoInputRecorded;

    /// <summary>The interval the keepalive timer runs at, for the tick's idle check.</summary>
    private int _keepAliveIntervalSeconds;

    /// <summary>
    /// Set for a pipe-mode session only, which keeps the size it was launched with: tells when the
    /// terminal has moved away from it. <see langword="null"/> for a session that resizes.
    /// </summary>
    private PipeSessionSizeNotice? _pipeSizeNotice;

    /// <summary>The terminal width the size badge states; meaningful while the badge is shown.</summary>
    private int _pipeNoticeColumns;

    /// <summary>Whether the size badge is up.</summary>
    private bool _pipeSizeBadgeShown;

    /// <summary>The surface visibility last synchronised, to tell a change from a repeat.</summary>
    private Visibility _lastSurfaceVisibility = Visibility.Visible;

    /// <summary>
    /// Compares the size the page reported while hidden with the first one once shown, for the
    /// support log.
    /// </summary>
    private readonly FirstShownSizeCheck _firstShownSizeCheck = new();

    /// <summary>
    /// The sizes the page reported (<c>ready:</c> or <c>resize:</c>). Written on the UI thread;
    /// read by the SSH handler from the connect thread just before it creates the PTY, and
    /// awaited there on the Plink path when the page has not spoken yet.
    /// </summary>
    internal TerminalSizeReport TerminalSizeReport { get; } = new();

    /// <summary>
    /// The last size the terminal page reported, or <see langword="null"/> before it has spoken.
    /// The SSH handler creates the PTY at this size when it is known before the connect.
    /// </summary>
    internal TerminalSize? LastKnownTerminalSize => TerminalSizeReport.Latest;

    /// <inheritdoc />
    TerminalSize? ITerminalSizeSource.LastKnownTerminalSize => TerminalSizeReport.Latest;

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

    /// <summary>Raised when a direct port forward is added to or removed from the active SSH session.</summary>
    public event Action? SessionForwardsChanged;

    /// <summary>
    /// Raised when the user drops local files onto a connected SSH terminal surface.
    /// Carries the validated existing local file paths and the last known OSC 7 working directory.
    /// </summary>
    public event Action<IReadOnlyList<string>, string?>? FilesDroppedRequested;

    /// <summary>The currently attached SSH.NET shell session, or <see langword="null"/> for non-SSH.NET transports.</summary>
    internal SshShellSession? ActiveSshSession => _session;

    /// <summary>The last known remote directory reported via OSC 7 for this session.</summary>
    internal string? LastKnownRemoteDirectory => _lastKnownCwd;

    /// <summary>Removes a single session-bound port forward without disconnecting the shell.</summary>
    internal bool TryRemoveSessionForward(string sessionForwardId)
    {
        if (_disposed || _session is null)
        {
            return false;
        }

        bool removed = _session.RemoveSessionPortForward(sessionForwardId);
        if (removed)
        {
            BeginInvokeIfAvailable(UpdateSessionCryptoInfo);
        }

        return removed;
    }

    /// <summary>
    /// Raised with the terminal selection when the user asks to save it to the Command Library
    /// (Ctrl+Shift+L, or the entry of the Shift+right-click menu). The subscriber
    /// (EmbeddedSessionManager) opens the library editor prefilled with it.
    /// </summary>
    public event Action<string>? SaveToLibraryRequested;

    /// <summary>
    /// Raised when the user clicks the Reconnect button after a disconnection.
    /// The subscriber (EmbeddedSessionManager) re-establishes the connection
    /// using the original server parameters.
    /// </summary>
    public event Action? ReconnectRequested;

    public event Action? EditProfileRequested;

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

    /// <summary>
    /// Raised on the UI thread when the hosted session ends while the pane stays open, with the
    /// reason the pane reports when it was lost, or null when it ended cleanly or on request.
    /// </summary>
    internal event Action<string?>? SessionEnded;

    internal SessionPaneModel? OwningPane => _ownerPane;

    public void SetOwningPane(SessionPaneModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _ownerPane = pane;
    }

    public EmbeddedSshView()
    {
        _outputCoalescer = new TerminalOutputCoalescer(
            action => BeginInvokeIfAvailable(action),
            PostDataMessage);

        InitializeComponent();

        // Set WebView2 background from theme to avoid flash of wrong color
        if (TryFindResource("BackgroundColor") is System.Windows.Media.Color themeColor)
        {
            TerminalWebView.DefaultBackgroundColor =
                System.Drawing.Color.FromArgb(themeColor.A, themeColor.R, themeColor.G, themeColor.B);
        }

        // The view starts under its connecting veil, so the surface starts out of its way.
        SyncTerminalSurfaceVisibility();
        TerminalWebView.SizeChanged += (_, _) => SendViewportIfHidden();

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
        string connectedStatus = SessionStatusTokens.Connected,
        bool autoReconnectOnProcessExit = true,
        bool credentialFreeLaunch = false)
    {
        var endpointLabel = string.IsNullOrWhiteSpace(endpoint)
            ? L("SshEndpointViaPlink")
            : endpoint;

        InitializeConnecting(sessionTab, displayName, endpointLabel);
        AttachTerminalSession(
            terminalSession,
            keepAliveIntervalSeconds,
            connectedStatus,
            autoReconnectOnProcessExit,
            credentialFreeLaunch);
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
        _sessionTab.PropertyChanged += OnSessionTabPropertyChanged;
        UpdateGatewayRoute();
        _sessionLogDisplayName = displayName;
        _sessionLogEndpoint = endpoint;

        LocalizeButtons();
        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = endpoint;
        UpdateConnectingOverlay(displayName, endpoint);
        UpdateStatus(SessionStatusTokens.Connecting);
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
        _lastKnownCwd = null;
        _pipeSizeNotice = null;
        // Stamped before the subscriptions: a disconnect already latched on the session is
        // delivered inside the Disconnected subscription below.
        _shellSessionAttachedAtUtc = DateTimeOffset.UtcNow;
        _shellSessionHasInput = false;
        _winRmEarlyOutputDiagnostic = null;
        HotForwardButton.Visibility = Visibility.Visible;
        ShowHealthButton(true);
        UpdateSessionCryptoInfo();

        _session.DataReceived += OnDataReceived;
        _session.Disconnected += OnDisconnected;
        _session.SecurityEventOccurred += OnSessionSecurityEvent;
        _session.SessionForwardsChanged += OnSessionForwardsChanged;

        // A panel left open across a reconnect is watching a monitor the
        // disconnect stopped; point it at the new session's client.
        if (_healthPanelVisible)
        {
            StartHealthMonitor();
        }

        // The page usually reports its size before the session exists; that resize went to a
        // null session. Replayed now that there is one to receive it.
        ReplayLastKnownTerminalSize();

        UpdateStatus(SessionStatusTokens.Connected);
        StartKeepAliveTimer(keepAliveIntervalSeconds);
        AcquireSleepPrevention();
        // A successful attach ends the coordinator-owned reconnect chain.
        _autoReconnectAttempt = 0;
        // A Disconnect click made while connecting belongs to the attempt it cancelled, not to
        // the session that is now attached.
        _userInitiatedDisconnect = false;
        HideConnectingOverlayWhenEstablished();

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
        string connectedStatus = SessionStatusTokens.Connected,
        bool autoReconnectOnProcessExit = true,
        bool credentialFreeLaunch = false)
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

        // Cleared before the replay below: a tracker left by an earlier session of this view
        // describes another launch size.
        _pipeSizeNotice = null;
        _autoReconnectOnProcessExit = autoReconnectOnProcessExit;
        _credentialFreeLaunch = credentialFreeLaunch;
        _lastKnownCwd = null;
        HotForwardButton.Visibility = Visibility.Collapsed;
        ShowHealthButton(false);
        _terminalSessionHasInput = false;
        _terminalSessionAttachedAtUtc = DateTimeOffset.UtcNow;
        _winRmEarlyOutputDiagnostic = IsWinRmSessionTab() ? new WinRmEarlyOutputDiagnostic() : null;
        _terminalDataHandler = OnTerminalDataReceived;
        _terminalExitHandler = OnTerminalProcessExited;

        _terminalSession.DataReceived += _terminalDataHandler;
        _terminalSession.ProcessExited += _terminalExitHandler;

        // Same replay as AttachSession. A no-op on the Plink pipe transport, which cannot
        // resize after start; that path carries the size into the launch instead.
        ReplayLastKnownTerminalSize();

        // The xterm.js convertEol option is baked at terminal construction time
        // (GetTerminalHtml). For SSH the view is mounted in a "Connecting" state
        // before the session exists, so that early value is always false. Push the
        // correct value now that the real session type is known: pipe-mode (plink)
        // output mixes bare-LF content (e.g. the SSH pre-auth banner) that xterm
        // must render as CR+LF.
        PostTerminalMessage(MsgSetConvertEol
            + (terminalSession is Heimdall.Terminal.PipeModeSession ? "true" : "false"));

        UpdateStatus(connectedStatus);

        // After the status, which would overwrite the notice: a page that already measured a size
        // other than the launch size is the case the notice exists for.
        _pipeSizeNotice = PipeSessionSizeNotice.ForSession(terminalSession);
        if (TerminalSizeReport.Latest is { } measuredAtAttach)
        {
            ObservePipeSessionSize(measuredAtAttach);
        }

        // Only an SSH shell has a remote TMOUT to reset. The local shell and WinRM sessions share
        // this view, and for them the reset is nothing but a stray Enter.
        StartKeepAliveTimer(TerminalKeepAlivePolicy.ResolveIntervalSeconds(_sessionTab?.ConnectionType, keepAliveIntervalSeconds));
        AcquireSleepPrevention();
        // A successful attach ends the coordinator-owned reconnect chain.
        _autoReconnectAttempt = 0;
        _userInitiatedDisconnect = false;
        HideConnectingOverlayWhenEstablished();

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
        StopCastRecorderSilently();
        StopKeepAliveTimer();
        StopAutoReconnectTimer();
        ReleaseSleepPrevention();

        Loaded -= OnLoaded;
        IsVisibleChanged -= OnVisibilityChanged;

        _terminalReady = false;
        _bufferReads.FailPending();

        if (_sessionTab is not null)
        {
            _sessionTab.PropertyChanged -= OnSessionTabPropertyChanged;
        }

        if (_localeChangeSubscribed && _localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localeChangeSubscribed = false;
        }

        _lockdown?.Dispose();
        _lockdown = null;

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
            _session.SessionForwardsChanged -= OnSessionForwardsChanged;

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

        _credentialFreeLaunch = false;
        _winRmEarlyOutputDiagnostic = null;
        _terminalDataHandler = null;
        _terminalExitHandler = null;

        if (TerminalWebView is IDisposable disposableWebView)
        {
            try { disposableWebView.Dispose(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedSshView] WebView dispose: {ex.Message}"); }
        }

        _outputCoalescer.Close();
        _pendingTerminalMessages.Clear();
        LogOutputCounters();

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
                    FocusTerminal();
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

        // While connecting there is no session to disconnect: the click cancels the attempt
        // itself. The status stays "Connecting" until the coordinator tears the placeholder down.
        if (IsConnectingWithoutSession())
        {
            RequestConnectCancellation();
            return;
        }

        try
        {
            _userInitiatedDisconnect = true;
            Core.Logging.FileLogger.Info("EmbeddedSSH Disconnect requested by user");
            UpdateStatus(SessionStatusTokens.Disconnected);

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
            UpdateStatus(SessionStatusTokens.Error);
        }
    }

    private void OnSessionTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SessionTabViewModel.TunnelRoute), StringComparison.Ordinal))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(UpdateGatewayRoute);
            return;
        }

        UpdateGatewayRoute();
    }

    /// <summary>Shows the resolved gateway / tunnel route ("via ...") in the header, if any.</summary>
    private void UpdateGatewayRoute()
    {
        if (_disposed || _sessionTab is null)
        {
            return;
        }

        string route = _sessionTab.TunnelRoute;
        GatewayRouteText.Text = route;
        GatewayRouteText.Visibility = string.IsNullOrWhiteSpace(route)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Raised when the user cancels a connection attempt that has not produced a session yet.
    /// The coordinator owns the cancellation token of the attempt and answers it.
    /// </summary>
    public event Action? CancelConnectRequested;

    /// <summary>
    /// Raised when xterm.js emits a terminal bell (BEL / 0x07), allowing background tabs to alert the user.
    /// </summary>
    public event Action? BellReceived;

    internal static bool IsConnectionAttemptPending(bool hasSession, bool disposed)
        => !hasSession && !disposed;

    private bool IsConnectingWithoutSession()
        => IsConnectionAttemptPending(_session is not null || _terminalSession is not null, _disposed);

    private void RequestConnectCancellation()
    {
        Core.Logging.FileLogger.Info("EmbeddedSSH connection attempt cancelled by user");
        ConnectingCancelButton.IsEnabled = false;
        CancelConnectRequested?.Invoke();
    }

    private void OnConnectingCancelClick(object sender, RoutedEventArgs e)
    {
        if (IsConnectingWithoutSession())
        {
            RequestConnectCancellation();
        }
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        SplitRequested?.Invoke();
    }

    private void OnHighlightsToggleClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _highlightsEnabled = !_highlightsEnabled;
        UpdateHighlightsToggleVisual();
        PostTerminalMessage(MsgSetHighlights + (_highlightsEnabled ? "true" : "false"));
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

            Core.Logging.FileLogger.Info("EmbeddedSSH WebView2 environment creation started");
            CoreWebView2Environment env = await Services.WebView2Helper.CreateEnvironmentAsync("SSH");
            Core.Logging.FileLogger.Info("EmbeddedSSH WebView2 environment creation ended");
            await TerminalWebView.EnsureCoreWebView2Async(env);
            Core.Logging.FileLogger.Info("EmbeddedSSH WebView2 EnsureCoreWebView2Async returned");
            if (_disposed || !TryGetCoreWebView2(out CoreWebView2? core))
            {
                return;
            }

            _lockdown?.Dispose();
            _lockdown = Services.WebView2Lockdown.Apply(core, onNewWindow: null);
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
        if (!TerminalSurfaceFailurePolicy.IsFatal(e.ProcessFailedKind))
        {
            return;
        }

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
        Core.Logging.FileLogger.Info($"EmbeddedSSH WebView2 NavigationCompleted success={navArgs.IsSuccess}");

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

        if (message.StartsWith(MsgBell, StringComparison.Ordinal))
        {
            BellReceived?.Invoke();
            return;
        }

        if (message.StartsWith(MsgCmdDone, StringComparison.Ordinal))
        {
            string payload = message[MsgCmdDone.Length..];
            if (TryParseCmdDoneMessage(payload, out int exitCode, out long durationMs)
                && durationMs >= LongCommandAlertThresholdMs)
            {
                string durationText = FormatCommandDuration(durationMs);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    string statusText = exitCode == 0
                        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, L("Osc133CmdDoneSuccess"), durationText)
                        : string.Format(System.Globalization.CultureInfo.CurrentCulture, L("Osc133CmdDoneFailed"), exitCode, durationText);
                    UpdateStatus(
                        CurrentPaneStatus ?? SessionStatusTokens.Connected,
                        displayTextOverride: statusText,
                        forceErrorBrush: exitCode != 0);

                    if (!IsVisible)
                    {
                        BellReceived?.Invoke();
                    }
                });
            }

            return;
        }

        // Open URL: terminal requests to open a link in the default browser
        if (message.StartsWith(MsgOpenUrl, StringComparison.Ordinal))
        {
            string url = message[MsgOpenUrl.Length..];

            // The URL arrives from the terminal page, which renders whatever the remote host
            // printed. One decision, shared: see Services.ExternalUrlPolicy.
            if (Services.ExternalUrlPolicy.TryResolveLaunchable(url, out string? launchableUrl))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = launchableUrl,
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
            // Output queued before the page was ready stays behind the surface and viewport
            // messages below: it is held in the pending queue until that queue has been flushed.
            _holdOutputForReadyFlush = true;
            _terminalReady = true;
            Core.Logging.FileLogger.Info($"EmbeddedSSH terminal ready: {message}");

            if (TryParseSize(message.AsSpan(MsgReady.Length), out int readyCols, out int readyRows))
            {
                RememberTerminalSize(readyCols, readyRows);
                ResizeSession(readyCols, readyRows);
            }

            // The page reports no size until the host has told it what its surface is: the box to
            // lay out in while hidden, or "measure yourself" while shown.
            _lastSurfaceVisibility = TerminalWebView.Visibility;
            if (TerminalWebView.Visibility == Visibility.Visible)
            {
                PostSurfaceShown();
            }
            else
            {
                SendViewportIfHidden();
            }

            DrainOutputCoalescer();
            FlushPendingTerminalMessages();
            _holdOutputForReadyFlush = false;
            ApplyInitialTerminalFocus();
            HideConnectingOverlayWhenEstablished();
            return;
        }

        if (message.StartsWith(MsgDiag + TerminalViewportMessage.DiagSurfaceNeverTookBounds, StringComparison.Ordinal))
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSSH the terminal surface never took its bounds: {message[MsgDiag.Length..]}; host WebView "
                + $"{TerminalWebView.ActualWidth:0.##}x{TerminalWebView.ActualHeight:0.##} "
                + $"visible={TerminalWebView.IsVisible}");
            return;
        }

        if (message.StartsWith(MsgDiag, StringComparison.Ordinal))
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedSSH page box {message[MsgDiag.Length..]}; host WebView "
                + $"{TerminalWebView.ActualWidth:0.##}x{TerminalWebView.ActualHeight:0.##} "
                + $"visible={TerminalWebView.IsVisible} dpiScale={VisualTreeHelper.GetDpi(TerminalWebView).DpiScaleX:0.##}");
            return;
        }

        if (message.StartsWith(MsgResize, StringComparison.Ordinal))
        {
            if (TryParseSize(message.AsSpan(MsgResize.Length), out int cols, out int rows))
            {
                RememberTerminalSize(cols, rows);
                ResizeSession(cols, rows);
                NoteSizeForShownCheck(
                    new TerminalSize(cols, rows),
                    TerminalSizeReport.TryParseSizeReport(message.AsSpan(MsgResize.Length), out _, out _, out bool shownMarker)
                        && shownMarker);
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
                _lastKnownCwd = path;
                CurrentDirectoryChanged?.Invoke(path);
            }

            return;
        }

        if (message.StartsWith(MsgFileDrop, StringComparison.Ordinal))
        {
            HandleWebViewFileDrop(args);
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

        // Opt-in write-only OSC 52 clipboard write (remote -> local, reads strictly refused)
        if (message.StartsWith(MsgOsc52Write, StringComparison.Ordinal))
        {
            bool allowOsc52 = TerminalSettings?.TerminalAllowOsc52Clipboard == true;
            string payload = message[MsgOsc52Write.Length..];
            if (!TryDecodeOsc52Payload(allowOsc52, payload, out string oscText))
            {
                return;
            }

            try
            {
                Clipboard.SetText(oscText);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"EmbeddedSSH osc52-write failed: {ex.Message}");
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

        if (message == WebViewFocusRelease.Message)
        {
            ReleaseFocusToHeader();
            return;
        }

        if (TerminalLibraryCapture.TryRead(message, out TerminalLibraryRequest libraryRequest, out string librarySelection))
        {
            HandleLibraryRequest(libraryRequest, librarySelection);
            return;
        }

        // Terminal text reply: terminal on purpose, so it can never reach the unknown-message
        // log below. The page is trusted (checked at the top); the text is unmasked output.
        if (message.StartsWith(MsgBuffer, StringComparison.Ordinal))
        {
            _ = _bufferReads.HandleReplyAsync(message);
            return;
        }

        if (message.StartsWith(MsgLastCmdOutput, StringComparison.Ordinal))
        {
            string payload = message[MsgLastCmdOutput.Length..];
            if (string.IsNullOrEmpty(payload))
            {
                _ = Dispatcher.BeginInvoke(() => _ = OpenMaskedCopyForLastCommandAsync(string.Empty, truncated: false));
                return;
            }

            bool truncated = false;
            string b64 = payload;
            if (payload.Length >= 2 && payload[1] == ':' && (payload[0] == '0' || payload[0] == '1'))
            {
                truncated = payload[0] == '1';
                b64 = payload[2..];
            }

            if (string.IsNullOrEmpty(b64))
            {
                _ = Dispatcher.BeginInvoke(() => _ = OpenMaskedCopyForLastCommandAsync(string.Empty, truncated));
                return;
            }

            if (TryAcceptInboundBase64Payload("last-cmd-output", b64))
            {
                try
                {
                    string outputText = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    _ = Dispatcher.BeginInvoke(() => _ = OpenMaskedCopyForLastCommandAsync(outputText, truncated));
                }
                catch (FormatException ex)
                {
                    Core.Logging.FileLogger.Warn($"EmbeddedSSH invalid last-cmd-output payload: {ex.Message}");
                }
            }

            return;
        }

        Core.Logging.FileLogger.Warn(
            $"EmbeddedSSH dropped unknown WebView2 terminal message: {DescribeWebViewText(message)}");
    }

    private void HandleLibraryRequest(TerminalLibraryRequest request, string selection)
    {
        switch (request)
        {
            case TerminalLibraryRequest.Save:
                // Posted, not raised inline: the subscriber may open a modal editor, and a nested
                // message loop inside a WebView2 event handler stalls the page until it closes.
                _ = Dispatcher.BeginInvoke(() => RaiseSaveToLibraryRequested(selection));
                break;
            case TerminalLibraryRequest.Menu:
                OpenSaveToLibraryMenu(selection);
                break;
        }
    }

    private void OpenSaveToLibraryMenu(string selection)
    {
        string label = _localizer?[TerminalLibraryCapture.MenuItemLabelKey]
            ?? TerminalLibraryCapture.MenuItemLabelKey;
        ContextMenu menu = TerminalLibraryCapture.CreateMenu(selection, label, RaiseSaveToLibraryRequested);
        var copyLastCmdItem = new MenuItem { Header = L("MenuCopyLastCommandOutput") };
        copyLastCmdItem.Click += (_, _) => PostTerminalMessage(MsgCopyLastCommandOutput);
        menu.Items.Add(copyLastCmdItem);
        menu.PlacementTarget = TerminalWebView;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void RaiseSaveToLibraryRequested(string selection)
    {
        try
        {
            SaveToLibraryRequested?.Invoke(selection);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedSSH SaveToLibraryRequested subscriber failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Hands the keyboard back to the session header: the first action the header offers in the
    /// current state, Split when the session has neither.
    /// </summary>
    private void ReleaseFocusToHeader()
    {
        if (DisconnectButton.Visibility == Visibility.Visible && DisconnectButton.IsEnabled)
        {
            DisconnectButton.Focus();
        }
        else if (ReconnectButton.Visibility == Visibility.Visible && ReconnectButton.IsEnabled)
        {
            ReconnectButton.Focus();
        }
        else
        {
            SplitButton.Focus();
        }
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

    internal static bool TryParseCmdDoneMessage(string? payload, out int exitCode, out long durationMs)
    {
        exitCode = 0;
        durationMs = 0;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        int sep = payload.IndexOf(':');
        if (sep <= 0 || sep >= payload.Length - 1)
        {
            return false;
        }

        return int.TryParse(
                payload.AsSpan(0, sep),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out exitCode)
            && long.TryParse(
                payload.AsSpan(sep + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out durationMs);
    }

    internal static string FormatCommandDuration(long durationMs)
    {
        if (durationMs < 0)
        {
            durationMs = 0;
        }

        long totalSeconds = durationMs / 1000;
        if (totalSeconds < 60)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{totalSeconds}s");
        }

        long minutes = totalSeconds / 60;
        long seconds = totalSeconds % 60;
        if (minutes < 60)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{minutes}m {seconds}s");
        }

        long hours = minutes / 60;
        long remMinutes = minutes % 60;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hours}h {remMinutes}m");
    }

    internal static string DescribeWebViewText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        // Terminal text is unmasked output (it can hold secrets): log its length, never a slice.
        if (text.StartsWith(MsgBuffer, StringComparison.Ordinal))
        {
            return $"{MsgBuffer}<{text.Length - MsgBuffer.Length} chars>";
        }

        if (text.StartsWith(MsgLastCmdOutput, StringComparison.Ordinal))
        {
            return $"{MsgLastCmdOutput}<{text.Length - MsgLastCmdOutput.Length} chars>";
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
        if (_disposed || data.IsEmpty)
        {
            return;
        }

        ForwardToActiveMacroExpect(data);
        Volatile.Read(ref _castRecorder)?.WriteOutput(data);

        if (SessionLogService is not null && _sessionLogKey is not null)
        {
            // Forward raw bytes; the service owns decoding + ANSI stripping. No-op until a
            // transcript is active for this key, so replayed bootstrap bytes that arrive before
            // StartSession are not logged, and live bytes after it are logged exactly once.
            SessionLogService.WriteOutput(_sessionLogKey, data);
        }

        // Only the drawing stops with the renderer. The transcript and a running macro read
        // the session, which is still connected; they used to stop silently with the page.
        if (_webViewUnavailable)
        {
            return;
        }

        ObserveWinRmEarlyOutput(data);

        Interlocked.Increment(ref _dataChunksIn);
        Interlocked.Add(ref _dataBytes, data.Length);
        _outputCoalescer.Append(data);
    }

    /// <summary>
    /// Posts one coalesced output message (UI thread). Output goes straight to the page only when
    /// the page is ready and nothing older is waiting; otherwise it queues behind what is pending.
    /// </summary>
    private void PostDataMessage(string message)
    {
        if (_disposed || _webViewUnavailable || IsDispatcherShuttingDown())
        {
            return;
        }

        Interlocked.Increment(ref _dataPostsOut);

        if (_terminalReady && !_holdOutputForReadyFlush && _pendingTerminalMessages.Count == 0
            && TryGetCoreWebView2(out var core))
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

        _pendingTerminalMessages.Enqueue(message);
    }

    private void DrainOutputCoalescer()
    {
        if (_disposed || _webViewUnavailable)
        {
            return;
        }

        _outputCoalescer.Drain();
    }

    private void LogOutputCounters()
    {
        if (Interlocked.Exchange(ref _outputCountersLogged, 1) != 0)
        {
            return;
        }

        Core.Logging.FileLogger.Debug(
            $"EmbeddedSSH output chunks={Interlocked.Read(ref _dataChunksIn)} "
            + $"posts={Interlocked.Read(ref _dataPostsOut)} bytes={Interlocked.Read(ref _dataBytes)}");
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
            // Queued after the end of the session when the last output chunk arrives late: the
            // diagnostic is already in the terminal output, the status must stay ended.
            if (_disposed || !TerminalReconnectPolicy.MayWriteHandedOffDiagnostic(CurrentPaneStatus))
            {
                return;
            }

            UpdateStatus(
                SessionStatusTokens.RemoteSessionHandedOff,
                displayTextOverride: L(localizationKey),
                forceErrorBrush: true);
        });
    }

    private void OnDisconnected(SshSessionDisconnectInfo disconnectInfo)
        => OnDisconnected(disconnectInfo, panelReason: null);

    /// <summary>
    /// Handles the end of the session. <paramref name="panelReason"/> is a resolved, localized
    /// explanation shown first in the disconnected panel's detail, above the generic detail.
    /// </summary>
    private void OnDisconnected(SshSessionDisconnectInfo disconnectInfo, string? panelReason)
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
            StopCastRecorderSilently();
            UpdateCastRecordVisual();

            // Nothing is connected any more: a tab left open on a dead session kept the machine
            // awake overnight and kept sending keep-alives into a closed shell. A reconnect builds
            // a new view, which acquires its own.
            ReleaseSleepPrevention();
            StopKeepAliveTimer();
            LogOutputCounters();

            string? errorMessage = SshDisconnectMessageResolver.Resolve(
                disconnectInfo,
                _localizer,
                _sessionTab?.Title ?? string.Empty);
            bool markerWritten = !string.IsNullOrWhiteSpace(errorMessage);
            if (markerWritten)
            {
                string template = L("SshTerminalDisconnectMarker");
                string marker = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    template, errorMessage);
                string disconnectText =
                    $"\r\n{TerminalHostMarkerColor}{marker}{TerminalColorReset}\r\n";
                QueueOutput(Encoding.UTF8.GetBytes(disconnectText));
            }

            // One end-of-session line, not two: the page adds its generic one only when the
            // host wrote none.
            PostTerminalMessage(markerWritten
                ? "session-ended:" + SessionEndedMarkedArgument
                : "session-ended:");

            string? securityDisconnectMessage = _pendingSecurityDisconnectMessage;
            _pendingSecurityDisconnectMessage = null;
            if (!string.IsNullOrWhiteSpace(securityDisconnectMessage))
            {
                ReconnectMessageText.Text = securityDisconnectMessage;
                Behaviors.LiveRegionBehavior.Announce(ReconnectMessageText);
                SetReconnectDetail(null);
                UpdateStatus(SessionStatusTokens.Error);
                SessionEnded?.Invoke(securityDisconnectMessage);
                ShowReconnectOverlay();
                return;
            }

            // A clean exit is not an alarm; a failure carries its real cause, not a generic line.
            if (disconnectInfo.IsClean)
            {
                ReconnectMessageText.Text = L("SshSessionEndedMessage");
                Behaviors.LiveRegionBehavior.Announce(ReconnectMessageText);
                SetReconnectDetail(null);
            }
            else
            {
                ReconnectMessageText.Text = L("SshDisconnectedMessage");
                Behaviors.LiveRegionBehavior.Announce(ReconnectMessageText);
                SetReconnectDetail(TerminalReconnectPolicy.ComposePanelDetail(panelReason, errorMessage));
                if (!string.IsNullOrWhiteSpace(ReconnectDetailText.Text))
                {
                    Behaviors.LiveRegionBehavior.Announce(ReconnectDetailText);
                }
            }

            UpdateStatus(SessionStatusTokens.Disconnected);

            // The pane's status is not the connection state: the session tree, its connected
            // filter and the status bar count read the state machine, and kept this session
            // connected until the tab closed.
            SessionEnded?.Invoke(_userInitiatedDisconnect || disconnectInfo.IsClean
                ? null
                : markerWritten ? errorMessage : L("SshDisconnectedMessage"));

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

            // The reconnect chain is completed by the attach, so a session that ends right after
            // it would count as attempt 1 forever. Like the plink path, a session that ends
            // before the user typed anything, within the connect window, is not retried.
            if (_session is not null
                && _shellSessionAttachedAtUtc is { } shellAttachedAt
                && TerminalReconnectPolicy.SuppressesConnectTimeShellDisconnect(
                    _shellSessionHasInput,
                    DateTimeOffset.UtcNow - shellAttachedAt,
                    TerminalReconnectPolicy.ResolveConnectTimeExitWindow(
                        TerminalSettings?.SshConnectTimeExitWindowSeconds)))
            {
                Core.Logging.FileLogger.Info(
                    "EmbeddedSSH auto-reconnect skipped: the session ended within the connect window");
                ShowReconnectOverlay();
                return;
            }

            int maxAttempts = ResolveMaxAutoReconnectAttempts();
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
            Behaviors.LiveRegionBehavior.Announce(ReconnectMessageText);
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
        string? notEnteredKey = TerminalReconnectPolicy.ResolveWinRmNotEnteredMessageKey(
            _sessionTab?.ConnectionType,
            exitCode);
        if (notEnteredKey is not null)
        {
            string notEnteredText = $"\r\n\x1b[91m{L(notEnteredKey)}\x1b[0m\r\n";
            QueueOutput(Encoding.UTF8.GetBytes(notEnteredText));
        }

        // The window is the one that decides auto-reconnect suppression, so the hint and the
        // suppression agree. Consumed here so a late second exit event cannot write it twice.
        string? credentialFreeKey = TerminalReconnectPolicy.ResolveCredentialFreeExitMessageKey(
            _credentialFreeLaunch,
            exitCode,
            suppressAutoReconnect,
            _userInitiatedDisconnect);
        _credentialFreeLaunch = false;
        string? panelReason = null;
        if (credentialFreeKey is not null)
        {
            panelReason = L(credentialFreeKey);
            string credentialFreeText = $"\r\n\x1b[91m{panelReason}\x1b[0m\r\n";
            QueueOutput(Encoding.UTF8.GetBytes(credentialFreeText));
        }

        SshSessionDisconnectInfo disconnectInfo =
            TerminalReconnectPolicy.ClassifyProcessExit(
                exitCode,
                _autoReconnectOnProcessExit,
                suppressAutoReconnect);
        OnDisconnected(disconnectInfo, panelReason);
    }

    /// <summary>
    /// Keeps the last size the page reported, for the handler to create the PTY with and for the
    /// attach to replay. Sizes arrive validated by <see cref="TryParseSize"/>: both positive.
    /// </summary>
    private void RememberTerminalSize(int columns, int rows)
    {
        TerminalSizeReport.Remember(columns, rows);
    }

    /// <summary>
    /// Resizes the freshly attached session to the size the page reported before it existed.
    /// </summary>
    private void ReplayLastKnownTerminalSize()
    {
        if (TerminalSizeReport.Latest is { } size)
        {
            ResizeSession(size.Columns, size.Rows);
        }
    }

    private void ResizeSession(int columns, int rows)
    {
        if (_disposed || columns <= 0 || rows <= 0)
        {
            return;
        }

        int clampedColumns = Math.Clamp(columns, 1, MaxResizeColumns);
        int clampedRows = Math.Clamp(rows, 1, MaxResizeRows);

        ObservePipeSessionSize(new TerminalSize(clampedColumns, clampedRows));
        Volatile.Read(ref _castRecorder)?.WriteResize(clampedColumns, clampedRows);

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

        if (marksTerminalInput)
        {
            NoteTerminalInput();
        }

        // A macro records what the user typed. The keepalive reset is not input, and recording it
        // would replay a phantom Enter.
        if (_isRecording && marksTerminalInput)
        {
            int delayMs = (int)_macroStopwatch.ElapsedMilliseconds;
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
            if (marksTerminalInput)
            {
                _shellSessionHasInput = true;
            }

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
    /// Sends a command string to the active session, submitted as if the user had pressed
    /// Enter. Used by the Command Library, the broadcast panel, and SFTP "Open in Terminal".
    /// </summary>
    /// <remarks>
    /// This terminated with LF, which a remote tty translates but a Windows console does not:
    /// on a Local Shell or a WinRM session, both of which run PowerShell over ConPTY, the
    /// command was typed into the prompt and left sitting on the "&gt;&gt; " continuation line,
    /// unexecuted. The tests could not see it because every one of them writes through a fake
    /// <see cref="Services.ITerminalCommandSink"/>, and the terminator lives here, below the
    /// interface. It is now the shared <see cref="AppConstants.TerminalSubmitKey"/>, the same
    /// byte <see cref="KeepAliveCr"/> already sends.
    /// </remarks>
    /// <remarks>
    /// Two conventions reach this class: strings the formatter already terminated, written
    /// straight to the session, and bare strings arriving here. Nothing stops a caller passing
    /// one where the other is expected, and a doubled Enter is invisible to a test that writes
    /// through a fake sink. <see cref="Services.TerminalCommandFormatter.Submit"/> makes the
    /// question moot by stripping whatever terminator arrived before adding the canonical one.
    /// </remarks>
    public void WriteCommand(string command)
    {
        WriteToSession(Services.TerminalCommandFormatter.Submit(command));
    }

    /// <summary>
    /// Types <paramref name="text"/> at the prompt without submitting it, for the operator to
    /// review, edit and run with their own Enter.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="Services.TerminalCommandFormatter.Insert"/>, which removes every
    /// line break: a break reaching the session would run the line before it, which is the
    /// one thing this write promises not to do.
    /// </remarks>
    public void WriteText(string text)
    {
        WriteToSession(Services.TerminalCommandFormatter.Insert(text));
    }

    /// <summary>
    /// Sends raw bytes to the active session without triggering broadcast.
    /// Used by the broadcast relay to avoid infinite loops.
    /// </summary>
    public void WriteBytes(byte[] data) => WriteToSession(data);

    private void WriteToSession(string text, bool marksTerminalInput = true)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (marksTerminalInput)
        {
            NoteTerminalInput();
        }

        if (_session is not null)
        {
            SshShellSession session = _session;
            if (marksTerminalInput)
            {
                _shellSessionHasInput = true;
            }

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

    /// <summary>
    /// Stamps the clock the keepalive tick reads, so a reset is not written into a shell the user
    /// is typing into.
    /// </summary>
    private void NoteTerminalInput()
    {
        Volatile.Write(ref _lastTerminalInputMilliseconds, Environment.TickCount64);
    }

    /// <summary>Show/hide the shield button for launching an elevated shell.</summary>
    public void ShowElevateButton(bool visible) =>
        ElevateButton.Visibility = visible && ElevateRequested is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void StartKeepAliveTimer(int intervalSeconds)
    {
        if (intervalSeconds <= 0)
        {
            return;
        }

        _keepAliveIntervalSeconds = intervalSeconds;
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

        // A bare CR is a keystroke. Into a shell the user is typing into it submits a half-typed
        // line; the user's own input has already reset TMOUT, so the tick has nothing to do.
        if (!TerminalKeepAlivePolicy.ShouldSendTick(
            Environment.TickCount64,
            Volatile.Read(ref _lastTerminalInputMilliseconds),
            _keepAliveIntervalSeconds))
        {
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
        string fontFamily = TerminalSettings?.TerminalFontFamily ?? AppSettings.DefaultTerminalFontFamily;
        int fontSize = TerminalSettings?.TerminalFontSize ?? AppSettings.DefaultTerminalFontSize;
        int scrollbackLines = TerminalSettings?.TerminalScrollbackLines ?? AppSettings.DefaultTerminalScrollbackLines;
        string schemeName = TerminalSettings?.TerminalColorScheme ?? AppSettings.DefaultTerminalColorScheme;
        string convertEol = _terminalSession is Heimdall.Terminal.PipeModeSession
            ? "true"
            : "false";

        string themeJson = ColorSchemes.TryGetValue(schemeName, out string? configuredThemeJson)
            && configuredThemeJson is not null
            ? configuredThemeJson
            : ColorSchemes["Dracula"];

        int safeFontSize = Math.Clamp(fontSize, TerminalFontSizeMin, TerminalFontSizeMax);
        int safeScrollbackLines = Math.Clamp(
            scrollbackLines,
            AppSettings.MinTerminalScrollbackLines,
            AppSettings.MaxTerminalScrollbackLines);

        // The offline NavigateToString page requires inline script/style. Keep
        // placeholder values constrained to known constants or sanitized data.
        html = TerminalFontFamilyFormatter.Apply(html, fontFamily);
        html = html.Replace("/*{{TERMINAL_FONT_SIZE}}*/", safeFontSize.ToString(), StringComparison.Ordinal);
        html = html.Replace(
            "/*{{TERMINAL_FONT_MIN}}*/",
            TerminalFontSizeMin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        html = html.Replace(
            "/*{{TERMINAL_FONT_MAX}}*/",
            TerminalFontSizeMax.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        html = html.Replace(
            "/*{{TERMINAL_SCROLLBACK}}*/",
            safeScrollbackLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        _highlightsEnabled = TerminalSettings?.TerminalHighlightsEnabled ?? false;
        UpdateHighlightsToggleVisual();
        html = html.Replace(
            "/*{{TERMINAL_HIGHLIGHTS}}*/",
            _highlightsEnabled ? "true" : "false",
            StringComparison.Ordinal);
        html = html.Replace(
            "/*{{TERMINAL_HIGHLIGHT_RULES}}*/",
            TerminalHighlightRuleDefaults.SerializeForTerminalJs(TerminalSettings?.TerminalHighlightRules),
            StringComparison.Ordinal);
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
        foreach ((string placeholder, string colour) in TerminalSearchChrome.CssVariables(themeJson, XtermDefaultColours))
        {
            html = html.Replace(placeholder, colour, StringComparison.Ordinal);
        }

        // WCAG 1.4.3 for every ANSI colour: xterm.js raises a cell's foreground until it reaches this
        // ratio on the cell's background (Solarized Dark's own palette fails for 9 of 16 colours).
        html = html.Replace(
            "/*{{TERMINAL_MIN_CONTRAST}}*/",
            ThemeTextColours.MinimumContrast.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

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
