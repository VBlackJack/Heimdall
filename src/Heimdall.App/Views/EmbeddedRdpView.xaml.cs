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

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Core.StateMachine;
using Heimdall.Rdp;
using Heimdall.Rdp.ActiveX;
using Heimdall.Rdp.Display;
using Microsoft.Extensions.DependencyInjection;
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Views;

/// <summary>
/// WPF host for the MsTscAx ActiveX control used by embedded RDP sessions.
/// Applies the proven WPF/WinForms layout flush pattern before Connect()
/// and delays dynamic resolution reconnects until the session is stable.
/// </summary>
public partial class EmbeddedRdpView
    : UserControl,
        IDisposable,
        IRdpDisconnectTeardownTarget,
        IRdpConnectWatchdogTimer,
        IRdpConnectAttemptRunner,
        IRdpTrustPromptSurface
{
    private const int BeginConnectMaxAttempts = 10;
    private const int MaxReconnectAttemptTimestamps = 3;

    private TimeSpan _initialResizeEnableDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BeginConnectRetryDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ResizeDebounceInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan AutofillFilledDisplayDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TransientToastDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LetterboxHintDisplayDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LetterboxHintFadeDuration = TimeSpan.FromMilliseconds(600);
    private const string EnterFullscreenGlyph = "\uE1D9";
    private const string ExitFullscreenGlyph = "\uE799";
    private const string RedirectionClipboardGlyph = "\uE16D";
    private const string RedirectionDrivesGlyph = "\uEDA2";
    private const string RedirectionPrintersGlyph = "\uE749";
    private const string RedirectionComPortsGlyph = "\uE7BC";
    private const string RedirectionSmartCardsGlyph = "\uE192";
    private const string RedirectionUsbGlyph = "\uE88E";
    private const string RedirectionAudioGlyph = "\uE7F6";
    private const string RedirectionMultiMonitorGlyph = "\uE7F4";
    private const string HealthHealthyGlyph = "\uE73E";
    private const string HealthFaultedGlyph = "\uE783";
    private const string HealthTransitionalGlyph = "\uE7BA";
    private const string HealthIdleGlyph = "\uE946";
    private int _lastExtendedDisconnectReason = RdpActiveXHost.NoExtendedDisconnectReason;
    private readonly record struct RdpDisplayUpdateSettings(
        uint PhysicalWidthMm,
        uint PhysicalHeightMm,
        uint DesktopScaleFactor,
        uint DeviceScaleFactor,
        double DpiScaleX,
        double DpiScaleY);

    /// <summary>
    /// Transient state of the embedded credential autofill watcher.
    /// </summary>
    private enum RdpAutofillState
    {
        None,
        Searching,
        Filled,
        TimedOut,
        Failed
    }

    private sealed record AutofillRetryContext(string Password, string HostHint);

    private readonly DispatcherTimer _resizeTimer;
    private readonly List<DateTime> _reconnectAttemptTimestampsUtc = new(MaxReconnectAttemptTimestamps);
    private readonly LetterboxHintState _letterboxHintState = new();
    private readonly RdpConnectWatchdogArbiter _connectWatchdogArbiter;

    /// <summary>
    /// The connect attempt in flight: its abandonment state, and every decision that follows.
    /// </summary>
    /// <remarks>
    /// The view held the abandonment as two plain fields and cleared them at the top of every
    /// connect, retries included, so a Cancel pressed while a retry was pending was erased by that
    /// retry. The state lives in one object now, only a connect the user asked for may clear it,
    /// and the decisions it feeds - whether a retry may still connect, whether a connect that
    /// arrives may be promoted - are taken in the arbiter rather than here, where a test can play
    /// the whole sequence against them.
    /// </remarks>
    private readonly RdpConnectAttemptArbiter _connectAttempts;

    private bool _redirectionExpandedOverride;

    /// <summary>
    /// The token this view's certificate questions are routed by.
    /// </summary>
    /// <remarks>
    /// Minted per view, carried through <c>Heimdall.Core</c> untouched, and resolved back to
    /// this instance by <see cref="RdpTrustPromptSurfaceRegistry"/>. It is what makes the
    /// question arrive in the pane that asked it rather than at the main window.
    /// </remarks>
    private readonly string _trustPromptScopeId = Guid.NewGuid().ToString("N");

    /// <summary>The certificate question this pane is waiting on, if any.</summary>
    /// <remarks>
    /// Built in the constructor rather than here because it is handed this pane's dispatcher:
    /// a withdrawal is applied where the question is drawn, so it cannot settle in the hop
    /// between a person pressing an answer and the overlay coming down.
    /// </remarks>
    private readonly RdpTrustPromptSession _trustPrompt;

    private IDisposable? _trustPromptRegistration;
    private string? _statusTextBeforeTrustPrompt;

    private CancellationTokenSource? _autofillCts;
    private CancellationTokenSource? _stabilizationCts;
    private CancellationTokenSource? _certificateVerificationCts;
    private DispatcherTimer? _antiIdleTimer;
    private DispatcherTimer? _autofillFilledTimer;
    private DispatcherTimer? _transientToastTimer;
    private DispatcherTimer? _letterboxHintTimer;
    private DispatcherTimer? _stabilizationTimer;
    private DispatcherTimer? _reconnectElapsedTimer;
    private DispatcherTimer? _connectWatchdogTimer;
    private bool _watchdogCredentialWaitActive;
    private RdpActiveXHost? _rdpHost;
    private RdpRedirectionOptions? _pendingRedirections;
    private ServerProfileDto? _server;
    private AppSettings? _settings;
    private SessionPaneModel? _ownerPane;
    private SessionTabViewModel? _sessionTab;
    private ConnectionStateMachine? _connectionStateMachine;
    private AutofillRetryContext? _autofillRetryContext;

    private Core.Localization.LocalizationManager? _localizer;
    private int? _tunnelPort;
    private Func<int, Heimdall.Ssh.TunnelForwardedPortFailure?>? _tunnelFailureLookup;
    private string? _connectStatusOverrideKey;
    private RdpAutofillState _autofillState;

    private RdpConnectionPhase _connectionPhase = RdpConnectionPhase.None;
    private RdpSessionStatus _sessionStatus = RdpSessionStatus.Disconnecting;
    private bool _initialized;
    private bool _connectStarted;
    private bool _disposed;
    private bool _allowResolutionUpdates;
    private bool _sleepPreventionActive;
    private bool _comDrivenStatusActive;
    private long _lastConnectionStateRevision;
    private bool _escapeHookRegistered;
    private bool _isFullscreen;
    private bool _disconnectConfirmInFlight;
    private bool _resolutionReconnectConfirmInFlight;
    private bool _autofillAttemptInFlight;
    private bool _dpiChangeDroppedDuringLockout;
    private Window? _dpiWindow;

    // Session-event latches: at most one Disconnected per connected segment, paired with one
    // Connected per (re)connect. Reset across the auto-reconnect bounce so each bounce is truthful.
    private bool _eventConnectEmitted;
    private bool _eventDisconnectEmitted;

    /// <summary>
    /// One-shot flag set when the header bar explicitly initiates the disconnect.
    /// </summary>
    private bool _userInitiatedDisconnect;
    private int _antiIdleIntervalSeconds;
    private int _beginConnectAttempt;
    private int _lastAppliedWidth;
    private int _lastAppliedHeight;
    private int _manualResolutionWidth;
    private int _manualResolutionHeight;
    private DateTime _connectedAtUtc;
    private DateTime _stabilizationDeadlineUtc;
    private DateTime? _reconnectStartUtc;

    // Connect instant used solely for event-log duration. Refreshed on auto-reconnect success so a
    // post-reconnect Disconnected reports the duration of the new segment, not the original connect.
    private DateTime _eventConnectedAtUtc;

    /// <summary>
    /// Shared sink for graphical-protocol connect/disconnect events. Injected by
    /// <c>EmbeddedSessionManager</c>, mirroring how <c>EmbeddedSshView.SessionLogService</c> is wired.
    /// </summary>
    public ISessionEventLog? SessionEventLog { get; set; }

    /// <summary>
    /// Where this view gets its RDP control and gives it back. Defaults to creating one per
    /// session, which is what happened before pooling existed, so a view constructed without
    /// a provider behaves exactly as it used to.
    /// </summary>
    public IRdpHostProvider HostProvider { get; set; } = new TransientRdpHostProvider();

    /// <summary>
    /// Provider for the LIVE global session-logging toggle, read at each emit so the setting takes
    /// effect without a restart. Supplied by <c>EmbeddedSessionManager</c> over
    /// <c>ConfigManager.CurrentSettings</c>; the view never snapshots it.
    /// </summary>
    public Func<bool>? SessionLoggingEnabledProvider { get; set; }

    /// <summary>Per-profile session-logging override. Null means inherit the live global setting.</summary>
    public bool? SessionLoggingOverride { get; set; }

    /// <summary>
    /// The settings instance this pane's connection resolved its gateway chain from, supplied by
    /// <c>EmbeddedSessionManager</c> from <c>RdpSessionResult</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not the same thing as <c>_settings</c>, and never merged into
    /// it.</b> <c>_settings</c> is the snapshot the pane was materialised with, and every
    /// resolution, redirection and timeout the pane reads from it is a question about how to
    /// present a session that is already opening. The certificate question asks something else -
    /// which machine the certificate arrived from - and only the connect-time instance can answer
    /// it. Materialisation happens after the connect, and settings are handed out as a fresh deep
    /// clone, so the two genuinely differ once a gateway is edited during a slow establishment.
    /// </para>
    /// <para>Null when nothing recorded it. The route line then says nothing rather than
    /// something it cannot stand behind: see
    /// <see cref="Services.RdpTrustPromptRoute.DescribeConnection"/>.</para>
    /// </remarks>
    internal AppSettings? ConnectionSettings { get; set; }

    /// <summary>
    /// Raised when the user clicks the Split button in the header strip.
    /// The subscriber (EmbeddedSessionManager) shows the split picker context menu.
    /// </summary>
    public event Action? SplitRequested;

    /// <summary>
    /// Raised when the user clicks "Reconnect" in the disconnect overlay.
    /// The subscriber should close this session and open a new connection.
    /// </summary>
    public event Action? ReconnectRequested;

    /// <summary>
    /// Raised when the user requests disconnect from the RDP toolbar.
    /// The shell closes the owning pane/tab so teardown uses the shared lifecycle path.
    /// </summary>
    public event Action? DisconnectRequested;

    /// <summary>
    /// Raised when the user clicks "Edit profile" in the disconnect overlay.
    /// The subscriber opens the server profile editor for the current session.
    /// </summary>
    public event Action<string>? EditServerRequested;

    /// <summary>
    /// Raised when the user clicks "Close" in the disconnect overlay.
    /// The subscriber closes the owning session tab through the shared
    /// <c>ConnectionViewModel.CloseSessionAsync</c> path.
    /// </summary>
    public event Action? CloseRequested;

    public EmbeddedRdpView()
    {
        InitializeComponent();

        _connectWatchdogArbiter = new RdpConnectWatchdogArbiter(this);
        _connectAttempts = new RdpConnectAttemptArbiter(this);

        _resizeTimer = new DispatcherTimer(
            ResizeDebounceInterval,
            DispatcherPriority.Background,
            OnResizeTimerTick,
            Dispatcher)
        {
            IsEnabled = false
        };

        _trustPrompt = new RdpTrustPromptSession(PostTrustPromptWithdrawal);
        _trustPrompt.QuestionChanged += OnTrustPromptQuestionChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SurfaceContainer.SizeChanged += OnSurfaceContainerSizeChanged;
    }

    public void SetFullscreen(bool isFullscreen)
    {
        _isFullscreen = isFullscreen;
        SessionHeaderBar.Visibility = isFullscreen
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        FullscreenButton.Content = isFullscreen ? ExitFullscreenGlyph : EnterFullscreenGlyph;

        if (isFullscreen && _localizer is not null)
        {
            ShowTransientToast(_localizer["RdpFullscreenExitHint"]);
        }

        TryRetriggerDisplayResolver(isFullscreen);
    }

    public void ToggleFullscreen()
    {
        if (Window.GetWindow(this) is ISessionTabContextCallbacks callbacks)
        {
            callbacks.ToggleFullscreen();
            return;
        }

        SetFullscreen(!_isFullscreen);
    }

    private void TryRetriggerDisplayResolver(bool isFullscreen)
    {
        var host = _rdpHost;
        if (host is null)
        {
            return;
        }

        var sinceConnected = _connectedAtUtc == default
            ? TimeSpan.Zero
            : DateTime.UtcNow - _connectedAtUtc;

        if (!RdpFullscreenRetriggerPolicy.ShouldRetrigger(
            _connectionPhase == RdpConnectionPhase.Connected,
            sinceConnected,
            _initialResizeEnableDelay))
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP fullscreen retrigger skipped: phase={_connectionPhase} sinceConnected={sinceConnected.TotalSeconds:0.0}s");
            return;
        }

        var effective = host.RecomputeDisplayForFullscreen(isFullscreen);
        if (effective is null)
        {
            return;
        }

        _ = ApplyResolvedResolutionAsync(
            effective.Width,
            effective.Height,
            $"fullscreen-toggle-{(isFullscreen ? "enter" : "exit")}",
            force: true);
    }

    /// <summary>
    /// Localization keys this view resolves at runtime rather than through XAML markup.
    /// </summary>
    /// <remarks>
    /// Named here so the guard that checks the locale files can reference the same constant. A key
    /// spelled out at the call site compiles, passes every comparison, and resolves to nothing.
    /// </remarks>
    internal static class LocaleKeys
    {
        internal const string ReconnectSucceededAfterCancel = "RdpReconnectSucceededAfterCancelToast";
        internal const string CopyErrorToast = "RdpCopyErrorToast";
        internal const string CopyErrorFailedToast = "RdpCopyErrorFailedToast";
        internal const string ErrorDisconnectFailed = "RdpErrorDisconnectFailed";
        internal const string ErrorCancelReconnectFailed = "RdpErrorCancelReconnectFailed";
        internal const string ErrorCancelConnectFailed = "RdpErrorCancelConnectFailed";
        internal const string ErrorStartEmbeddedSessionFailed = "RdpErrorStartEmbeddedSessionFailed";
        internal const string CertificateNotVerifiableToast = "RdpCertificateNotVerifiableToast";
        internal const string ResolutionHeaderFormat = "RdpResolutionHeaderFormat";
        internal const string ResolutionHeaderWithSizeFormat = "RdpResolutionHeaderWithSizeFormat";
    }

    private void ShowTransientToast(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowTransientToast(message));
            return;
        }

        if (_disposed)
        {
            return;
        }

        StopTransientToastTimer();

        if (string.IsNullOrWhiteSpace(message))
        {
            TransientToastText.Text = string.Empty;
            TransientToast.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        TransientToastText.Text = message;
        TransientToast.Visibility = System.Windows.Visibility.Visible;
        _ = RdpLiveRegion.Announce(TransientToast);
        _transientToastTimer = new DispatcherTimer(
            TransientToastDuration,
            DispatcherPriority.Background,
            OnTransientToastTick,
            Dispatcher);
        _transientToastTimer.Start();
    }

    private void OnTransientToastTick(object? sender, EventArgs e)
    {
        StopTransientToastTimer();
        TransientToastText.Text = string.Empty;
        TransientToast.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void StopTransientToastTimer()
    {
        if (_transientToastTimer is null)
        {
            return;
        }

        _transientToastTimer.Stop();
        _transientToastTimer.Tick -= OnTransientToastTick;
        _transientToastTimer = null;
    }

    private static string FormatShortcutForDisplay(RdpShortcut shortcut)
    {
        var parts = new List<string>();

        if (shortcut.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (shortcut.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (shortcut.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (shortcut.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Windows");
        }

        parts.Add(FormatKeyForDisplay(shortcut.Key));
        return string.Join("+", parts);
    }

    private static string FormatKeyForDisplay(Key key)
    {
        return key switch
        {
            Key.Escape => "Esc",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Space => "Space",
            _ => key.ToString()
        };
    }

    public void InitializeSession(
        ServerProfileDto server,
        SessionTabViewModel sessionTab,
        AppSettings settings,
        int antiIdleIntervalSeconds = 60,
        Core.Localization.LocalizationManager? localizer = null,
        int? tunnelPort = null,
        int resizeEnableDelayMs = 10000,
        ConnectionStateMachine? connectionStateMachine = null,
        string? connectStatusOverrideKey = null,
        Func<int, Heimdall.Ssh.TunnelForwardedPortFailure?>? tunnelFailureLookup = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentNullException.ThrowIfNull(settings);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EmbeddedRdpView));
        }

        if (_initialized)
        {
            return;
        }

        _server = server;
        _settings = settings;
        _sessionTab = sessionTab;
        _antiIdleIntervalSeconds = antiIdleIntervalSeconds;
        _localizer = localizer;
        _tunnelPort = tunnelPort;
        _tunnelFailureLookup = tunnelFailureLookup;
        _initialResizeEnableDelay = TimeSpan.FromMilliseconds(resizeEnableDelayMs);
        _connectionStateMachine = connectionStateMachine;
        _connectStatusOverrideKey = connectStatusOverrideKey;
        if (_connectionStateMachine is not null)
        {
            _connectionStateMachine.StateChanged += OnConnectionStateChanged;
        }

        if (IsProfileFixedResolution(server))
        {
            _manualResolutionWidth = server.RdpFixedWidth;
            _manualResolutionHeight = server.RdpFixedHeight;
        }

        _initialized = true;

        SessionTitleText.Text = server.DisplayName;
        EndpointTextBlock.Text = BuildEndpointText(server);

        if (_localizer is not null)
        {
            ResolutionButton.ToolTip = L("TooltipChangeResolution");
        }
        UpdateResolutionButtonState();

        PopulateResolutionMenu();
        CreateHostControl();
        UpdateConnectingStatusFromStateMachineOrDefault();

        RegisterTrustPromptSurface();
    }

    /// <summary>Publishes this pane as the surface its certificate questions are put to.</summary>
    /// <remarks>
    /// Called from <see cref="InitializeSession"/> rather than the constructor because a
    /// surface that cannot say which machine it is connecting is worse than no surface at all:
    /// the profile is what supplies the logical host, and until it is set this view would
    /// answer the question with the address that was dialled - 127.0.0.1 for every tunnelled
    /// profile, which is the identification failure the whole change exists to remove.
    /// </remarks>
    private void RegisterTrustPromptSurface()
    {
        RdpTrustPromptSurfaceRegistry? registry = (Application.Current as App)
            ?.Services?.GetService<RdpTrustPromptSurfaceRegistry>();

        _trustPromptRegistration = registry?.Register(_trustPromptScopeId, this);
    }

    public void SetOwningPane(SessionPaneModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _ownerPane = pane;
    }

    internal SessionPaneModel? OwningPane => _ownerPane;

    public void DisconnectForTeardown(DisconnectReason reason)
    {
        Dispose(reason);
    }

    public void Dispose()
    {
        Dispose(DisconnectReason.UserAction);
    }

    private void Dispose(DisconnectReason reason)
    {
        if (_disposed)
        {
            return;
        }

        // Backstop: a user-initiated tab close/disconnect tears down here. The COM OnRdpDisconnected
        // handler short-circuits once _disposed is set below (and the event sink is detached during
        // teardown), so emit the teardown Disconnected now, before _disposed flips. Idempotent via the
        // latch, so a real disconnect or reconnect bounce that already logged one is not double-counted.
        // The teardown reason picks the trigger: a toolbar/menu Disconnect (UserAction) tags "user",
        // every other teardown (tab close, failed session, app shutdown) tags "teardown".
        EmitTeardownDisconnectEvent(reason);

        _disposed = true;
        Core.Logging.FileLogger.Info($"EmbeddedRDP Dispose started reason={reason}");
        UnregisterEscapeHook();
        UnregisterDpiChangedHandler();

        if (_connectionStateMachine is not null)
        {
            _connectionStateMachine.StateChanged -= OnConnectionStateChanged;
            _connectionStateMachine = null;
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SurfaceContainer.SizeChanged -= OnSurfaceContainerSizeChanged;
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeTimerTick;
        if (_autofillFilledTimer is not null)
        {
            _autofillFilledTimer.Stop();
            _autofillFilledTimer.Tick -= OnAutofillFilledTimerTick;
            _autofillFilledTimer = null;
        }

        _autofillRetryContext = null;
        StopTransientToastTimer();
        HideLetterboxHint();
        StopStabilizationCountdown();
        _stabilizationCts?.Cancel();
        _stabilizationCts?.Dispose();
        _stabilizationCts = null;
        _certificateVerificationCts?.Cancel();
        _certificateVerificationCts?.Dispose();
        _certificateVerificationCts = null;

        // Unregistered first so no new question can be routed here, then closed so the one
        // already on screen stops waiting. Closing the pane is not an answer - it is something
        // the user did to the pane, not something they said about the certificate - so the
        // session settles it as NotAsked. Reporting it as a refusal is what told a user they had
        // declined a certificate they were never shown.
        //
        // NotAsked does not stop a connection on its own; a pane sharing its question is handed
        // the answer given elsewhere. This path stops for a reason of its own, and it is the
        // ordering a few lines above: the verification token is cancelled BEFORE the question is
        // closed, and the coalescer takes no shared answer for a pane whose own connection was
        // given up. Moving that cancellation below this block would put a session behind a pane
        // that no longer exists.
        _trustPromptRegistration?.Dispose();
        _trustPromptRegistration = null;
        _trustPrompt.Close();
        _trustPrompt.QuestionChanged -= OnTrustPromptQuestionChanged;

        StopReconnectElapsedTracking();
        StopAntiIdleTimer();
        StopConnectWatchdog();
        ReleaseSleepPrevention();
        CancelAutofill();
        TransitionPhase(RdpConnectionPhase.None);
        HideRedirectionIndicators();
        _allowResolutionUpdates = false;
        _sessionStatus = RdpSessionStatus.Disconnecting;
        UpdateHealthDot();

        if (_rdpHost is not null)
        {
            _rdpHost.Connected -= OnRdpConnected;
            _rdpHost.Disconnected -= OnRdpDisconnected;
            _rdpHost.FatalError -= OnRdpFatalError;
            _rdpHost.LoginComplete -= OnRdpLoginComplete;
            _rdpHost.AutoReconnecting -= OnRdpAutoReconnecting;
            _rdpHost.AutoReconnected -= OnRdpAutoReconnected;

            _rdpHost.CancelAutoReconnect = true;
            RdpDisconnectTeardownSequence.Execute(this, reason);
        }

        _autofillCts?.Dispose();
        _autofillCts = null;
        Core.Logging.FileLogger.Info($"EmbeddedRDP Dispose completed reason={reason}");
    }

    string IRdpDisconnectTeardownTarget.TeardownTargetName =>
        $"EmbeddedRdpView serverId={_server?.Id ?? "<unknown>"}";

    void IRdpDisconnectTeardownTarget.CollapseHost()
    {
        FormsHost.Visibility = System.Windows.Visibility.Collapsed;
    }

    void IRdpDisconnectTeardownTarget.ClearHostChild()
    {
        FormsHost.Child = null;
    }

    void IRdpDisconnectTeardownTarget.Disconnect()
    {
        _rdpHost?.Disconnect();
    }

    void IRdpDisconnectTeardownTarget.DetachEventSink()
    {
        // Whether the sink is advised is the control's own fact. Mirroring it here would
        // be a second copy of one truth, free to disagree with the first.
        if (_rdpHost?.IsEventSinkAttached == true)
        {
            _rdpHost.DetachEventSink();
        }
    }

    void IRdpDisconnectTeardownTarget.DisposeHost()
    {
        // Handed back rather than destroyed. The field is cleared first, so nothing here can
        // reach a control that now belongs to the provider, or to another session.
        RdpActiveXHost? host = _rdpHost;
        _rdpHost = null;
        if (host is not null)
        {
            HostProvider.Release(host);
        }
    }

    internal IntPtr GetRdpKeyboardInputHandle()
    {
        return FormsHost.Child is WinForms.Control control && control.IsHandleCreated
            ? control.Handle
            : IntPtr.Zero;
    }

    internal void FocusRdpToolbarFromEscapeHook()
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(FocusRdpToolbarFromEscapeHook));
            return;
        }

        _ = DisconnectButton.Focus();
        _ = Keyboard.Focus(DisconnectButton);
    }

    private void FocusRdpSurfaceIfAppropriate()
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(FocusRdpSurfaceIfAppropriate));
            return;
        }

        bool viewVisible = IsVisible;
        bool reconnectOverlayVisible = ReconnectOverlay.Visibility == System.Windows.Visibility.Visible;
        bool windowIsForeground = System.Windows.Window.GetWindow(this)?.IsActive == true;
        bool autofillInFlight = _autofillAttemptInFlight;

        if (RdpSurfaceFocusPolicy.ShouldFocusSurface(
            viewVisible,
            reconnectOverlayVisible,
            windowIsForeground,
            autofillInFlight))
        {
            _rdpHost?.FocusRdpSurface();
            return;
        }

        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP focus skipped: viewVisible={viewVisible} reconnectOverlayVisible={reconnectOverlayVisible} windowIsForeground={windowIsForeground}");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_initialized)
        {
            return;
        }

        RegisterEscapeHook();
        RegisterDpiChangedHandler();

        if (_connectStarted)
        {
            return;
        }

        _connectStarted = true;
        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP Loaded: isVisible={IsVisible} formsVisible={FormsHost.IsVisible} formsSize={FormsHost.ActualWidth:0.##}x{FormsHost.ActualHeight:0.##} surfaceSize={SurfaceContainer.ActualWidth:0.##}x{SurfaceContainer.ActualHeight:0.##}");

        _ = StartVerifiedConnectAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnregisterEscapeHook();
        UnregisterDpiChangedHandler();
        _autofillRetryContext = null;
        UpdateAutofillActionButtonsVisibility(_autofillState);
    }

    private void RegisterDpiChangedHandler()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(_dpiWindow, window))
        {
            return;
        }

        UnregisterDpiChangedHandler();
        _dpiWindow = window;
        if (_dpiWindow is not null)
        {
            _dpiWindow.DpiChanged += OnWindowDpiChanged;
        }
    }

    private void UnregisterDpiChangedHandler()
    {
        if (_dpiWindow is not null)
        {
            _dpiWindow.DpiChanged -= OnWindowDpiChanged;
            _dpiWindow = null;
        }
    }

    private async void OnWindowDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        try
        {
            if (_disposed || _rdpHost is null || _server is null)
            {
                return;
            }

            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP DPI changed: old={e.OldDpi.DpiScaleX:0.##}x{e.OldDpi.DpiScaleY:0.##} new={e.NewDpi.DpiScaleX:0.##}x{e.NewDpi.DpiScaleY:0.##}");

            if (!_allowResolutionUpdates)
            {
                _dpiChangeDroppedDuringLockout = true;
                Core.Logging.FileLogger.Info("EmbeddedRDP DPI change dropped during post-connect stabilization.");
                return;
            }

            await ApplyCurrentResolutionAsync("dpi-change", force: true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[EmbeddedRdpView] OnWindowDpiChanged: {ex.Message}");
        }
    }

    private void RegisterEscapeHook()
    {
        if (_escapeHookRegistered)
        {
            return;
        }

        _escapeHookRegistered = RdpKeyboardEscapeHook.Register(this);
    }

    private void UnregisterEscapeHook()
    {
        if (!_escapeHookRegistered)
        {
            return;
        }

        RdpKeyboardEscapeHook.Unregister(this);
        _escapeHookRegistered = false;
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _rdpHost is null)
        {
            return;
        }

        if (_settings?.RdpConfirmDisconnect == true)
        {
            if (_disconnectConfirmInFlight)
            {
                return;
            }

            var dialogService = (Application.Current as App)?.Services?.GetService<IDialogService>();
            if (dialogService is not null)
            {
                bool confirmed;
                try
                {
                    _disconnectConfirmInFlight = true;

                    // A confirmation that could not be asked is not a Yes. The sibling
                    // confirmation for a resolution-driven reconnect already fails closed on the
                    // same exception; this one used to fall through to the teardown, so the
                    // session died with no prompt ever having been shown.
                    confirmed = await RdpDisconnectConfirmationPolicy.ConfirmAsync(
                        () => dialogService.ShowConfirmAsync(
                            _localizer?["RdpConfirmDisconnectTitle"] ?? "RdpConfirmDisconnectTitle",
                            _localizer?.Format(
                                "RdpConfirmDisconnectMessage",
                                _server?.DisplayName ?? string.Empty)
                            ?? "RdpConfirmDisconnectMessage",
                            "warning"),
                        ex => Core.Logging.FileLogger.Warn(
                            $"[EmbeddedRdpView] Disconnect confirmation failed: {ex.Message}"));
                }
                finally
                {
                    _disconnectConfirmInFlight = false;
                }

                if (!confirmed || _disposed || _rdpHost is null)
                {
                    return;
                }
            }
        }

        try
        {
            Core.Logging.FileLogger.Info("EmbeddedRDP Disconnect requested by user");
            _userInitiatedDisconnect = true;
            UpdateHealthDot();
            _allowResolutionUpdates = false;
            StopStabilizationCountdown();
            StopReconnectElapsedTracking();
            TransitionPhase(RdpConnectionPhase.None);
            TryTransitionConnectionState(ConnectionState.Disconnecting);
            UpdateSessionStatus(RdpSessionStatus.Disconnecting);

            if (DisconnectRequested is { } disconnectRequested)
            {
                disconnectRequested();
            }
            else
            {
                DisconnectForTeardown(DisconnectReason.UserAction);
            }
        }
        catch (Exception ex)
        {
            HandleFailure(L(LocaleKeys.ErrorDisconnectFailed), ex);
        }
    }

    private void OnCancelReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _rdpHost is null)
        {
            return;
        }

        // No phase, because nothing is being prepared. This used to say Preparing, and three
        // surfaces read that literally: the phase stepper lit its first segment as though a
        // connection were starting, the connect-cancel button appeared in place of the one just
        // clicked with the same "_Cancel" label so the click looked like it had done nothing, and
        // the connect watchdog was armed, whose expiry raises a reconnect overlay on a session the
        // user asked to abandon. The sibling handler for cancelling an in-progress connection has
        // always used None; these two do the same kind of thing and now say the same thing.
        TransitionPhase(RdpConnectionPhase.None);

        try
        {
            Core.Logging.FileLogger.Info("EmbeddedRDP user cancelled auto-reconnect");
            _userInitiatedDisconnect = true;
            UpdateHealthDot();
            _rdpHost.CancelAutoReconnect = true;
            StopReconnectElapsedTracking();
            TryTransitionConnectionState(ConnectionState.Disconnecting);
            UpdateSessionStatus(RdpSessionStatus.Disconnecting);
        }
        catch (Exception ex)
        {
            HandleFailure(L(LocaleKeys.ErrorCancelReconnectFailed), ex);
        }
    }

    private void OnCancelConnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _rdpHost is null)
        {
            return;
        }

        // Outside the try, like the sibling handler: a throw from any statement below must not
        // leave the view describing a connection in progress the user has just abandoned - the
        // connect-cancel button would stay on screen carrying the same label, and the watchdog
        // would stay armed to raise a reconnect overlay on the abandoned session.
        TransitionPhase(RdpConnectionPhase.None);

        // The attempt already in flight can still complete: abandoning it here is what stops a
        // late OnConnected from promoting a session the user asked to stop. It also stops a
        // surface retry still pending against this attempt, which used to clear the latch and
        // connect.
        _connectAttempts.UserCancelled();

        // The certificate check runs before the connect does, so cancelling during it has to stop
        // the probe rather than wait out its timeout.
        _certificateVerificationCts?.Cancel();

        try
        {
            Core.Logging.FileLogger.Info("EmbeddedRDP user cancelled in-progress connection");
            _userInitiatedDisconnect = true;
            UpdateHealthDot();
            _allowResolutionUpdates = false;
            StopStabilizationCountdown();
            StopReconnectElapsedTracking();
            _rdpHost.CancelAutoReconnect = true;
            TryTransitionConnectionState(ConnectionState.Disconnecting);
            UpdateSessionStatus(RdpSessionStatus.Disconnecting);
            _rdpHost.Disconnect();
        }
        catch (Exception ex)
        {
            HandleFailure(L(LocaleKeys.ErrorCancelConnectFailed), ex);
        }
    }

    private void OnFullscreenButtonClick(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void OnSendKeysButtonClick(object sender, RoutedEventArgs e)
    {
        SendKeysMenu.PlacementTarget = SendKeysButton;
        SendKeysMenu.IsOpen = true;
    }

    /// <summary>
    /// The virtual keys each Send Keys menu entry posts to the remote session, keyed by the
    /// locale key that labels that entry in the menu.
    /// </summary>
    /// <remarks>
    /// The click handlers read this table rather than each carrying its own key list, so the
    /// entries the menu offers and the keys they deliver stay one decision instead of two. F11
    /// is here because it is the only fullscreen chord with no modifier: the keyboard hook
    /// consumes it while the remote surface holds the focus, so without this route the remote
    /// session can never be sent an F11 at all.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static readonly IReadOnlyDictionary<string, byte[]> SendKeysSequences =
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["RdpSendKeysCtrlAltDel"] =
                [NativeMethods.VK_CONTROL, NativeMethods.VK_MENU, NativeMethods.VK_DELETE],
            ["RdpSendKeysWindows"] = [NativeMethods.VK_LWIN],
            ["RdpSendKeysAltTab"] = [NativeMethods.VK_MENU, NativeMethods.VK_TAB],
            ["RdpSendKeysCtrlEsc"] = [NativeMethods.VK_CONTROL, NativeMethods.VK_ESCAPE],
            ["RdpSendKeysPrintScreen"] = [NativeMethods.VK_SNAPSHOT],
            ["RdpSendKeysEscape"] = [NativeMethods.VK_ESCAPE],
            ["RdpSendKeysF11"] = [NativeMethods.VK_F11],
            ["RdpSendKeysWinL"] = [NativeMethods.VK_LWIN, NativeMethods.VK_L],
            ["RdpSendKeysWinD"] = [NativeMethods.VK_LWIN, NativeMethods.VK_D],
            ["RdpSendKeysWinE"] = [NativeMethods.VK_LWIN, NativeMethods.VK_E]
        };

    [SupportedOSPlatform("windows")]
    private void OnSendKeysCtrlAltDelClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysCtrlAltDel");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysWindowsClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysWindows");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysAltTabClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysAltTab");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysCtrlEscClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysCtrlEsc");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysPrintScreenClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysPrintScreen");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysEscapeClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysEscape");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysF11Click(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysF11");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysWinLClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysWinL");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysWinDClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysWinD");

    [SupportedOSPlatform("windows")]
    private void OnSendKeysWinEClick(object sender, RoutedEventArgs e)
        => SendKeysToRemote("RdpSendKeysWinE");

    private void OnSendKeysShortcutsHelpClick(object sender, RoutedEventArgs e)
    {
        ShowShortcutsHelp();
    }

    private void ShowShortcutsHelp()
    {
        var localizer = _localizer;
        if (_disposed || localizer is null)
        {
            return;
        }

        var body = BuildShortcutsHelpContent(
            localizer,
            FormatShortcutForDisplay(RdpShortcutParser.DefaultShortcut),
            FormatShortcutForDisplay(RdpShortcutParser.DefaultFullscreenShortcut));
        var title = localizer["RdpShortcutsHelpTitle"];

        var dialogService = (Application.Current as App)?.Services
            ?.GetService(typeof(IDialogService)) as IDialogService;
        dialogService?.ShowInfo(title, body);
    }

    private static string BuildShortcutsHelpContent(
        Core.Localization.LocalizationManager localizer,
        string releaseFocusShortcut,
        string fullscreenShortcut)
    {
        var builder = new StringBuilder();
        builder.AppendLine(localizer["RdpShortcutsHelpToolbarSection"]);
        AppendHelpLine(builder, localizer["RdpShortcutsHelpDisconnect"]);
        AppendHelpLine(builder, localizer["RdpShortcutsHelpSendKeysEntry"]);
        AppendHelpLine(builder, localizer["RdpShortcutsHelpSplit"]);
        AppendHelpLine(builder, localizer["RdpShortcutsHelpResolution"]);
        AppendHelpLine(builder, localizer.Format("RdpShortcutsHelpFullscreen", fullscreenShortcut));
        AppendHelpLine(builder, localizer.Format("RdpShortcutsHelpReleaseFocus", releaseFocusShortcut));
        builder.AppendLine();
        builder.AppendLine(localizer["RdpShortcutsHelpSendKeysSection"]);
        AppendHelpLine(builder, localizer["RdpSendKeysCtrlAltDel"]);
        AppendHelpLine(builder, localizer["RdpSendKeysWindows"]);
        AppendHelpLine(builder, localizer["RdpSendKeysAltTab"]);
        AppendHelpLine(builder, localizer["RdpSendKeysCtrlEsc"]);
        AppendHelpLine(builder, localizer["RdpSendKeysPrintScreen"]);
        AppendHelpLine(builder, localizer["RdpSendKeysEscape"]);
        AppendHelpLine(builder, localizer["RdpSendKeysF11"]);

        return builder.ToString().TrimEnd();
    }

    private static void AppendHelpLine(StringBuilder builder, string text)
    {
        builder.Append("  ").AppendLine(text);
    }

    [SupportedOSPlatform("windows")]
    private void SendKeysToRemote(string feedbackLabelKey)
    {
        if (!SendKeysSequences.TryGetValue(feedbackLabelKey, out byte[]? virtualKeys))
        {
            return;
        }

        if (_disposed || _rdpHost is null || !_rdpHost.IsConnected || virtualKeys.Length == 0)
        {
            return;
        }

        try
        {
            var hwnd = _rdpHost.HostHandle;
            if (hwnd == IntPtr.Zero)
            {
                ShowTransientToast(_localizer?["RdpSendKeysSentFailedToast"] ?? string.Empty);
                return;
            }

            var target = FindDeepestRdpChildWindow(hwnd);
            foreach (var virtualKey in virtualKeys)
            {
                NativeMethods.PostMessage(
                    target,
                    NativeMethods.WM_KEYDOWN,
                    new IntPtr(virtualKey),
                    IntPtr.Zero);
            }

            for (var index = virtualKeys.Length - 1; index >= 0; index--)
            {
                NativeMethods.PostMessage(
                    target,
                    NativeMethods.WM_KEYUP,
                    new IntPtr(virtualKeys[index]),
                    IntPtr.Zero);
            }

            if (_localizer is not null)
            {
                var label = _localizer[feedbackLabelKey];
                ShowTransientToast(_localizer.Format("RdpSendKeysSentToast", label));
            }

            Core.Logging.FileLogger.Info("EmbeddedRDP user sent keys to the remote session");
        }
        catch (Exception ex)
        {
            ShowTransientToast(_localizer?["RdpSendKeysSentFailedToast"] ?? string.Empty);
            Core.Logging.FileLogger.Error("Send keys to the remote session failed.", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr FindDeepestRdpChildWindow(IntPtr hwnd)
    {
        var target = hwnd;
        var child = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, null, null);
        while (child != IntPtr.Zero)
        {
            target = child;
            child = NativeMethods.FindWindowEx(target, IntPtr.Zero, null, null);
        }

        return target;
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        EmitSplitDiagnostic();
        MaybeShowSplitWarningToast();
        SplitRequested?.Invoke();
    }

    private void EmitSplitDiagnostic()
    {
        if (_disposed)
        {
            return;
        }

        var phase = _connectionPhase.ToString();
        var resolutionMode = _server?.RdpResolutionMode.ToString() ?? "n/a";
        var dynamicResolution = _server?.RdpDynamicResolution ?? false;
        var hasFixedLocalResolution = UsesFixedLocalResolution();
        var surfaceWidth = SurfaceContainer.ActualWidth;
        var surfaceHeight = SurfaceContainer.ActualHeight;
        var paneId = _ownerPane?.PaneId ?? "n/a";

        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP split clicked: phase={phase} resolutionMode={resolutionMode} "
            + $"dynamicResolution={dynamicResolution} fixedLocalResolution={hasFixedLocalResolution} "
            + $"surfaceSize={surfaceWidth:0}x{surfaceHeight:0} "
            + $"lastApplied={_lastAppliedWidth}x{_lastAppliedHeight} "
            + $"resizeDebouncePending={_resizeTimer.IsEnabled} "
            + $"paneId={paneId} splitOrientation=n/a");
    }

    private void MaybeShowSplitWarningToast()
    {
        if (_disposed || _server is null || _localizer is null)
        {
            return;
        }

        var shouldWarn = RdpSplitWarningPolicy.ShouldWarn(
            _server.RdpDynamicResolution,
            UsesFixedLocalResolution(),
            _connectionPhase == RdpConnectionPhase.Connected);

        if (shouldWarn)
        {
            ShowTransientToast(_localizer["RdpSplitDisplayResizeWarning"]);
        }
    }

    private void OnSurfaceContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _server is null)
        {
            return;
        }

        ApplyHostLayout();

        if (!_server.RdpDynamicResolution || UsesFixedLocalResolution())
        {
            return;
        }

        // Only log significant size changes to avoid polluting logs
        double dw = Math.Abs(e.NewSize.Width - e.PreviousSize.Width);
        double dh = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);
        if (dw > 50 || dh > 50)
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP SizeChanged: {e.PreviousSize.Width:0}x{e.PreviousSize.Height:0} -> {e.NewSize.Width:0}x{e.NewSize.Height:0}");
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private async void OnResizeTimerTick(object? sender, EventArgs e)
    {
        try
        {
            _resizeTimer.Stop();

            if (_disposed || _rdpHost is null || _server is null)
            {
                return;
            }

            if (UsesFixedLocalResolution())
            {
                ApplyHostLayout();
                return;
            }

            await ApplyCurrentResolutionAsync("resize");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[EmbeddedRdpView] OnResizeTimerTick: {ex.Message}");
        }
    }

    private bool ShouldUseDynamicResolutionUpdates()
        => _server is { RdpDynamicResolution: true } && !UsesFixedLocalResolution();

    private bool UsesFixedLocalResolution()
        => _manualResolutionWidth > 0 && _manualResolutionHeight > 0;

    private bool IsLetterboxLayoutActive()
        => _server is { RdpResolutionMode: RdpResolutionMode.Fixed, RdpInitialSmartSizing: false }
            && UsesFixedLocalResolution();

    private RdpResolutionMode CurrentResolutionMode => _server?.RdpResolutionMode ?? RdpResolutionMode.Auto;

    private void ApplyHostLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ApplyHostLayout);
            return;
        }

        if (_disposed)
        {
            return;
        }

        if (!TryGetLetterboxContentSize(out var contentWidth, out var contentHeight))
        {
            ResetHostLayout();
            return;
        }

        var layout = RdpRegionFrameLayout.FromPaneAndContent(
            SurfaceContainer.ActualWidth,
            SurfaceContainer.ActualHeight,
            contentWidth,
            contentHeight);

        RdpRegionFrame.HorizontalAlignment = layout.FrameHorizontalAlignment;
        RdpRegionFrame.VerticalAlignment = layout.FrameVerticalAlignment;
        RdpRegionFrame.Margin = layout.FrameMargin;
        RdpRegionFrame.Width = layout.FrameWidth;
        RdpRegionFrame.Height = layout.FrameHeight;
        ApplyFormsHostLayout(layout);

        if (layout.IsLetterboxActive)
        {
            ShowLetterboxHintOnce(contentWidth, contentHeight);
        }
        else
        {
            HideLetterboxHint();
        }
    }

    private bool TryGetLetterboxContentSize(out double contentWidth, out double contentHeight)
    {
        contentWidth = 0;
        contentHeight = 0;

        if (!IsLetterboxLayoutActive())
        {
            return false;
        }

        contentWidth = _manualResolutionWidth;
        contentHeight = _manualResolutionHeight;
        return contentWidth > 0 && contentHeight > 0;
    }

    private void ResetHostLayout()
    {
        _letterboxHintState.Observe(CurrentResolutionMode, UsesFixedLocalResolution());
        RdpRegionFrame.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        RdpRegionFrame.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        RdpRegionFrame.Margin = new Thickness(0);
        RdpRegionFrame.Width = double.NaN;
        RdpRegionFrame.Height = double.NaN;
        ResetFormsHostLayout();
        HideLetterboxHint();
    }

    private void ResetFormsHostLayout()
    {
        FormsHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        FormsHost.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        FormsHost.Margin = new Thickness(0);
        FormsHost.Width = double.NaN;
        FormsHost.Height = double.NaN;
    }

    private void ApplyFormsHostLayout(RdpRegionFrameLayout layout)
    {
        FormsHost.HorizontalAlignment = layout.HostHorizontalAlignment;
        FormsHost.VerticalAlignment = layout.HostVerticalAlignment;
        FormsHost.Margin = layout.HostMargin;
        FormsHost.Width = layout.HostWidth;
        FormsHost.Height = layout.HostHeight;
    }

    private void ShowLetterboxHintOnce(double contentWidth, double contentHeight)
    {
        if (!_letterboxHintState.ShouldShow(
            CurrentResolutionMode,
            UsesFixedLocalResolution(),
            isLetterboxActive: true))
        {
            return;
        }

        StopLetterboxHintTimer();
        LetterboxHintBadge.BeginAnimation(OpacityProperty, null);
        LetterboxHintText.Text = FormatLetterboxHint(contentWidth, contentHeight);
        LetterboxHintBadge.Opacity = 0.85;
        LetterboxHintBadge.Visibility = System.Windows.Visibility.Visible;

        _letterboxHintTimer = new DispatcherTimer(
            LetterboxHintDisplayDuration,
            DispatcherPriority.Background,
            OnLetterboxHintTimerTick,
            Dispatcher);
        _letterboxHintTimer.Start();
    }

    private string FormatLetterboxHint(double contentWidth, double contentHeight)
    {
        var width = (int)Math.Round(contentWidth);
        var height = (int)Math.Round(contentHeight);
        return _localizer?.Format("RdpLetterboxHintFormat", width, height)
            ?? string.Format(
                CultureInfo.CurrentCulture,
                "Fixed {0}x{1} - resize the window or change resolution to fill.",
                width,
                height);
    }

    private void OnLetterboxHintTimerTick(object? sender, EventArgs e)
    {
        StopLetterboxHintTimer();
        FadeOutLetterboxHint();
    }

    private void FadeOutLetterboxHint()
    {
        if (LetterboxHintBadge.Visibility != System.Windows.Visibility.Visible)
        {
            return;
        }

        var animation = new DoubleAnimation(
            LetterboxHintBadge.Opacity,
            0,
            new Duration(LetterboxHintFadeDuration))
        {
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            LetterboxHintBadge.Visibility = System.Windows.Visibility.Collapsed;
            LetterboxHintBadge.Opacity = 0;
        };

        LetterboxHintBadge.BeginAnimation(OpacityProperty, animation);
    }

    private void HideLetterboxHint()
    {
        StopLetterboxHintTimer();
        LetterboxHintBadge.BeginAnimation(OpacityProperty, null);
        LetterboxHintBadge.Visibility = System.Windows.Visibility.Collapsed;
        LetterboxHintBadge.Opacity = 0;
    }

    private void StopLetterboxHintTimer()
    {
        if (_letterboxHintTimer is null)
        {
            return;
        }

        _letterboxHintTimer.Stop();
        _letterboxHintTimer.Tick -= OnLetterboxHintTimerTick;
        _letterboxHintTimer = null;
    }

    private async Task ApplyCurrentResolutionAsync(string reason, bool force = false)
    {
        var (width, height) = GetDisplayDimensions();
        await ApplyResolvedResolutionAsync(width, height, reason, force);
    }

    private async Task ApplyResolvedResolutionAsync(int width, int height, string reason, bool force = false)
    {
        if (_disposed || _rdpHost is null || _server is null)
        {
            return;
        }

        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (!_rdpHost.IsConnected)
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP {reason} skipped while not connected: target={width}x{height}");
            return;
        }

        if (!_allowResolutionUpdates)
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP {reason} deferred until post-connect stabilization: target={width}x{height}");
            return;
        }

        if (!force)
        {
            // Skip small resizes caused by tab hover, panel toggles, and scrollbar churn.
            int deltaW = Math.Abs(width - _lastAppliedWidth);
            int deltaH = Math.Abs(height - _lastAppliedHeight);
            if (deltaW < 50 && deltaH < 50)
            {
                return;
            }
        }

        try
        {
            var updateSettings = GetDisplayUpdateSettings(width, height);
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP UpdateResolution requested: reason={reason} target={width}x{height} physical={updateSettings.PhysicalWidthMm}x{updateSettings.PhysicalHeightMm}mm scale={updateSettings.DesktopScaleFactor}/{updateSettings.DeviceScaleFactor} connectedFor={(DateTime.UtcNow - _connectedAtUtc).TotalSeconds:0.0}s");

            var allowFallback = _settings?.RdpConfirmReconnectOnResize != true;
            var result = _rdpHost.UpdateResolution(
                width,
                height,
                updateSettings.PhysicalWidthMm,
                updateSettings.PhysicalHeightMm,
                updateSettings.DesktopScaleFactor,
                updateSettings.DeviceScaleFactor,
                allowFallback);

            if (result == RdpDisplayUpdateResult.ReconnectRequired
                && await ConfirmResolutionReconnectAsync(width, height))
            {
                if (_disposed || _rdpHost is null || !_rdpHost.IsConnected || !_allowResolutionUpdates)
                {
                    Core.Logging.FileLogger.Info(
                        $"EmbeddedRDP {reason} reconnect fallback skipped after confirmation: disposed={_disposed} connected={_rdpHost?.IsConnected ?? false} allowUpdates={_allowResolutionUpdates}");
                    return;
                }

                result = _rdpHost.UpdateResolution(
                    width,
                    height,
                    updateSettings.PhysicalWidthMm,
                    updateSettings.PhysicalHeightMm,
                    updateSettings.DesktopScaleFactor,
                    updateSettings.DeviceScaleFactor,
                    allowReconnectFallback: true);
            }

            if (result is RdpDisplayUpdateResult.Seamless or RdpDisplayUpdateResult.ReconnectFallback)
            {
                _lastAppliedWidth = width;
                _lastAppliedHeight = height;
            }

            if (result == RdpDisplayUpdateResult.ReconnectFallback)
            {
                ShowTransientToast(_localizer?["RdpResolutionReconnectFallbackToast"]
                    ?? "RdpResolutionReconnectFallbackToast");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RDP display update ({reason}): {ex.Message}");
        }
    }

    private async Task<bool> ConfirmResolutionReconnectAsync(int width, int height)
    {
        if (_resolutionReconnectConfirmInFlight)
        {
            return false;
        }

        var dialogService = (Application.Current as App)?.Services?.GetService<IDialogService>();
        if (dialogService is null)
        {
            Core.Logging.FileLogger.Warn("EmbeddedRDP reconnect confirmation requested but no dialog service is available.");
            return false;
        }

        try
        {
            _resolutionReconnectConfirmInFlight = true;
            return await dialogService.ShowConfirmAsync(
                _localizer?["RdpConfirmResolutionReconnectTitle"] ?? "RdpConfirmResolutionReconnectTitle",
                _localizer?.Format("RdpConfirmResolutionReconnectMessage", width, height)
                    ?? "RdpConfirmResolutionReconnectMessage",
                "warning");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedRdpView] Resolution reconnect confirmation failed: {ex.Message}");
            return false;
        }
        finally
        {
            _resolutionReconnectConfirmInFlight = false;
        }
    }

    /// <summary>Starts a fresh connect attempt, on behalf of the user.</summary>
    /// <remarks>
    /// Opening the attempt is what clears any prior abandonment, so this connect can promote
    /// normally and is not refused by the late-connect guard. Nothing else may clear it: a retry
    /// of an attempt the user cancelled in the meantime is not a new decision by the user, and
    /// used to be treated as one.
    /// </remarks>
    private void BeginConnect()
    {
        if (_disposed || _rdpHost is null || _server is null || _settings is null)
        {
            return;
        }

        _connectAttempts.UserRequestedConnect();
    }

    /// <summary>Hands a retry that has waited out a render pass back to the arbiter.</summary>
    /// <param name="attempt">The attempt this retry was scheduled for.</param>
    /// <remarks>
    /// Whether the retry connects is the arbiter's decision, and running it is the arbiter's call
    /// into <see cref="IRdpConnectAttemptRunner.RunAttempt"/>. What is left here is the refusal
    /// log, which describes the decision and does not take it.
    /// </remarks>
    private void ContinueConnectAttempt(int attempt)
    {
        if (_connectAttempts.RetryArrived(attempt, _disposed) == RdpConnectRetryAdmission.Refuse)
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP surface retry for attempt {attempt} dropped: the attempt is "
                + $"over (current={_connectAttempts.CurrentAttempt} "
                + $"cancelled={_connectAttempts.AbandonedByUser} "
                + $"watchdog={_connectAttempts.AbandonedByWatchdog} "
                + $"disposed={_disposed})");
        }
    }

    /// <summary>Configures the control and calls Connect, for an attempt already admitted.</summary>
    /// <param name="attempt">The attempt being run, carried into any retry it schedules.</param>
    private void RunConnectAttempt(int attempt)
    {
        var settings = _settings;
        if (_disposed || _rdpHost is null || _server is null || settings is null)
        {
            return;
        }

        try
        {
            _beginConnectAttempt++;
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP BeginConnect attempt={_beginConnectAttempt} viewVisible={IsVisible} formsVisible={FormsHost.IsVisible} formsSize={FormsHost.ActualWidth:0.##}x{FormsHost.ActualHeight:0.##} surfaceSize={SurfaceContainer.ActualWidth:0.##}x{SurfaceContainer.ActualHeight:0.##}");

            if (!IsVisualSurfaceReady())
            {
                if (_beginConnectAttempt <= BeginConnectMaxAttempts)
                {
                    Core.Logging.FileLogger.Warn("EmbeddedRDP visual surface is not ready; retrying after render pass.");
                    _ = RetryBeginConnectAsync(attempt);
                    return;
                }

                Core.Logging.FileLogger.Warn("EmbeddedRDP continuing even though the visual surface did not report as ready.");
                SetPaneDiagnostic(new SessionDiagnostic(
                    SessionFailureStage.RdpActiveXDisconnect,
                    "RdpSurfaceNotReady"));
            }

            FlushLayoutPipeline("pre-connect");
            EnsureHostHandle();
            FlushLayoutPipeline("post-handle");

            var connectHost = ResolveConnectHost(_server);
            var connectPort = ResolveConnectPort(_server);
            (string username, string? domain) = RdpProfileResolver.ResolveCredentialIdentity(
                _server.RdpUsername,
                _server.RdpDomain);
            var password = TryDecryptPassword(_server);
            var (width, height) = GetDisplayDimensions();
            var displayUpdateSettings = GetDisplayUpdateSettings(width, height);

            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP BeginConnect: host={connectHost}:{connectPort} size={width}x{height} dpi={displayUpdateSettings.DpiScaleX:0.##}x{displayUpdateSettings.DpiScaleY:0.##} scale={displayUpdateSettings.DesktopScaleFactor}/{displayUpdateSettings.DeviceScaleFactor} handle=0x{_rdpHost.HostHandle.ToInt64():X} clsid={_rdpHost.ActiveXClsid}");

            _rdpHost.SetServer(connectHost, connectPort);
            if (!string.IsNullOrWhiteSpace(username))
            {
                _rdpHost.SetCredentials(username, password, domain);
            }

            _rdpHost.SetDisplayScaleFactors(
                displayUpdateSettings.DesktopScaleFactor,
                displayUpdateSettings.DeviceScaleFactor,
                displayUpdateSettings.DpiScaleX,
                displayUpdateSettings.DpiScaleY);
            _rdpHost.SetDisplay(width, height, RdpProfileResolver.ResolveColorDepth(_server, settings));
            _rdpHost.SetResolutionMode(
                _server.RdpResolutionMode,
                _isFullscreen,
                ResolutionPresetCatalog.GetPresets(settings)
                    .Select(preset => (preset.Width, preset.Height))
                    .ToArray(),
                _server.RdpSelectedMonitorIndices);
            _lastAppliedWidth = width;
            _lastAppliedHeight = height;
            _pendingRedirections = RdpProfileResolver.BuildRedirections(_server, settings);
            _rdpHost.SetRedirections(_pendingRedirections);
            _rdpHost.SetResilienceOptions(
                settings.RdpAutoReconnectMaxAttempts,
                settings.RdpKeepAliveIntervalMs);

            if (!_rdpHost.IsEventSinkAttached)
            {
                if (!_rdpHost.AttachEventSink())
                {
                    throw new InvalidOperationException(
                        _rdpHost.LastError ?? "Failed to attach the Remote Desktop event sink.");
                }
            }

            Core.Logging.FileLogger.Info("EmbeddedRDP calling Connect()...");
            _rdpHost.Connect();
            TransitionPhase(RdpConnectionPhase.Connecting);

            // Post-connect flush removed: layout is already stable after pre-connect + post-handle flushes.
            // The third flush added ~50-150ms latency with no airspace benefit since Connect() is async.
            UpdateConnectingStatusFromStateMachineOrDefault();

            if (!string.IsNullOrWhiteSpace(password))
            {
                StartCredentialAutofill(password, _server.RemoteServer);
            }
        }
        catch (RdpGatewayAttestationException ex)
        {
            HandleGatewayAttestationFailure(ex);
        }
        catch (Exception ex)
        {
            HandleFailure(L(LocaleKeys.ErrorStartEmbeddedSessionFailed), ex);
        }
    }

    /// <summary>Waits out a render pass, then resumes <paramref name="attempt"/>.</summary>
    /// <remarks>
    /// The token travels with the retry rather than being re-read on arrival: the whole point is
    /// that the attempt may have ended while this was waiting, and a retry that asked "is anything
    /// abandoned?" would have to be told about the cancel that has not been recorded yet. Carrying
    /// the token makes the answer "which attempt are you a retry of", which cannot go stale.
    /// </remarks>
    private async Task RetryBeginConnectAsync(int attempt)
    {
        UpdateBeginConnectRetryStatus();

        try
        {
            await Task.Delay(BeginConnectRetryDelay);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[EmbeddedRdpView] retry delay: {ex.Message}");
            return;
        }

        if (_disposed)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => ContinueConnectAttempt(attempt)));
    }

    private void UpdateBeginConnectRetryStatus()
    {
        var localizer = _localizer;
        if (_disposed || localizer is null)
        {
            return;
        }

        void ApplyStatus()
        {
            if (_disposed)
            {
                return;
            }

            SetStatusText(localizer.Format(
                "RdpStatusInitializingSurface",
                _beginConnectAttempt,
                BeginConnectMaxAttempts));
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyStatus();
        }
        else
        {
            Dispatcher.Invoke(ApplyStatus);
        }
    }

    // Live gate at the seam: the sink is a dumb writer, so the view decides per-emit against the
    // LIVE global toggle and the RDP event eligibility. No snapshot, no second frozen gate.
    private bool ShouldLogSessionEvents()
    {
        bool globalEnabled = SessionLoggingEnabledProvider?.Invoke() ?? false;
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(SessionLoggingOverride, globalEnabled);
        return SessionEventLog is not null && SessionEventGatePolicy.ShouldLog(enabled, "RDP");
    }

    // Emits a Connected event and opens a connected segment. Refreshes the event connect timestamp
    // so the matching Disconnected reports this segment's duration. The latch is set regardless of
    // the gate so a later toggle-off/on cannot desynchronize the connect/disconnect pairing.
    private void EmitConnectEvent()
    {
        _eventConnectedAtUtc = DateTime.UtcNow;
        _eventConnectEmitted = true;
        _eventDisconnectEmitted = false;

        if (!ShouldLogSessionEvents() || _server is null)
        {
            return;
        }

        try
        {
            SessionEventLog!.LogEvent(
                RdpSessionEventFactory.BuildConnected(_server.RemoteServer, _server.DisplayName));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedRDP session-event connect emit failed: {ex.Message}");
        }
    }

    // Emits a Disconnected event at most once per connected segment (idempotency latch). Closes the
    // segment. Safe to call from the final OnRdpDisconnected and from the auto-reconnect bounce; the
    // second caller is a no-op once the first has emitted. Requires a live connected segment
    // (_eventConnectEmitted): a connect FAILURE fires OnRdpDisconnected with a reason but no preceding
    // Connected, and must NOT log an orphaned Disconnected - parity with VNC/Citrix.
    private void EmitDisconnectEvent(int reason, int extendedReason)
    {
        if (!_eventConnectEmitted || _eventDisconnectEmitted)
        {
            return;
        }

        _eventDisconnectEmitted = true;
        _eventConnectEmitted = false;

        if (!ShouldLogSessionEvents() || _server is null)
        {
            return;
        }

        try
        {
            SessionEventLog!.LogEvent(RdpSessionEventFactory.BuildDisconnected(
                _server.RemoteServer,
                _server.DisplayName,
                reason,
                extendedReason,
                _eventConnectedAtUtc,
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedRDP session-event disconnect emit failed: {ex.Message}");
        }
    }

    // Bounce start: emit the segment-ending Disconnected only when a connected segment is actually
    // live (it ends here), never for a failed connect or a repeated reconnect attempt.
    private void EmitBounceDisconnectEvent(int reason, int extendedReason)
    {
        if (_eventConnectEmitted && !_eventDisconnectEmitted)
        {
            EmitDisconnectEvent(reason, extendedReason);
        }
    }

    // Dispose backstop: a user-initiated tab close/disconnect tears down through Dispose, where the
    // COM OnRdpDisconnected handler short-circuits on _disposed and never reaches EmitDisconnectEvent.
    // Emit a reasonless "teardown" Disconnected here. Idempotent: a no-op when a real disconnect or a
    // reconnect bounce already logged one this cycle, and never fires for a never-connected session.
    private void EmitTeardownDisconnectEvent(DisconnectReason reason)
    {
        if (!_eventConnectEmitted || _eventDisconnectEmitted)
        {
            return;
        }

        _eventDisconnectEmitted = true;
        _eventConnectEmitted = false;

        if (!ShouldLogSessionEvents() || _server is null)
        {
            return;
        }

        try
        {
            SessionEventLog!.LogEvent(RdpSessionEventFactory.BuildTeardownDisconnected(
                _server.RemoteServer,
                _server.DisplayName,
                reason,
                _eventConnectedAtUtc,
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedRDP session-event teardown emit failed: {ex.Message}");
        }
    }

    private void OnRdpConnected()
    {
        Core.Logging.FileLogger.Info("EmbeddedRDP OnConnected fired");
        if (_disposed)
        {
            return;
        }

        // A connect that completes after the attempt was abandoned must not be promoted to
        // Connected: after a watchdog abort that is a zombie session behind the error overlay,
        // and after a user cancel it is a live session the user asked to stop. The arbiter
        // hard-disconnects the COM; nothing here touches the existing state. That disconnect is
        // also
        // what clears the user-disconnect flag, which is what stops the next genuine drop of this
        // session from being read as a user disconnect and dying in silence.
        if (_connectAttempts.ConnectArrived() == RdpLateConnectDecision.Refuse)
        {
            Core.Logging.FileLogger.Info(
                "EmbeddedRDP OnConnected ignored: attempt abandoned "
                + $"(watchdog={_connectAttempts.AbandonedByWatchdog} "
                + $"user={_connectAttempts.AbandonedByUser})");
            return;
        }

        _comDrivenStatusActive = true;
        _lastExtendedDisconnectReason = RdpActiveXHost.NoExtendedDisconnectReason;
        TryTransitionConnectionState(ConnectionState.Connected);

        Dispatcher.Invoke(() =>
        {
            CancelAutofill();
            _autofillRetryContext = null;
            UpdateAutofillState(RdpAutofillState.None);

            _connectedAtUtc = DateTime.UtcNow;
            EmitConnectEvent();
            _allowResolutionUpdates = false;
            ClearPaneDiagnostic();
            TransitionPhase(RdpConnectionPhase.Connected);
            UpdateSessionStatus(RdpSessionStatus.Connected);
            UpdateRedirectionIndicators();
            FlushLayoutPipeline("on-connected");

            if (_server is not null && _server.RdpAntiIdle && _antiIdleIntervalSeconds > 0)
            {
                StartAntiIdleTimer(_antiIdleIntervalSeconds);
            }

            AcquireSleepPrevention();

            if (ShouldUseDynamicResolutionUpdates())
            {
                StartStabilizationCountdown(_initialResizeEnableDelay);
                _ = EnableResolutionUpdatesAsync();
            }
            else
            {
                _allowResolutionUpdates = true;
            }

            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(FocusRdpSurfaceIfAppropriate));
        });
    }

    private async Task EnableResolutionUpdatesAsync()
    {
        _stabilizationCts?.Cancel();
        _stabilizationCts?.Dispose();
        var stabilizationCts = new CancellationTokenSource();
        var stabilizationToken = stabilizationCts.Token;
        _stabilizationCts = stabilizationCts;

        try
        {
            try
            {
                await Task.Delay(_initialResizeEnableDelay, stabilizationToken);
            }
            catch (OperationCanceledException) when (stabilizationToken.IsCancellationRequested)
            {
                if (_disposed || !ReferenceEquals(_stabilizationCts, stabilizationCts))
                {
                    return;
                }

                Core.Logging.FileLogger.Info("EmbeddedRDP stabilization skipped by user");
            }

            if (_disposed || !ReferenceEquals(_stabilizationCts, stabilizationCts) || _rdpHost is null || !_rdpHost.IsConnected)
            {
                StopStabilizationCountdown();
                return;
            }

            _allowResolutionUpdates = true;
            StopStabilizationCountdown();
            Core.Logging.FileLogger.Info("EmbeddedRDP dynamic resolution is now enabled.");

            var (queuedWidth, queuedHeight) = GetDisplayDimensions();
            RdpStabilizationResumeAction resumeAction = RdpStabilizationResumePolicy.Decide(
                _dpiChangeDroppedDuringLockout,
                queuedWidth,
                queuedHeight,
                _lastAppliedWidth,
                _lastAppliedHeight);

            _dpiChangeDroppedDuringLockout = false;

            if (resumeAction != RdpStabilizationResumeAction.None)
            {
                Core.Logging.FileLogger.Info(
                    $"EmbeddedRDP applying resolution after stabilization ({resumeAction}): {queuedWidth}x{queuedHeight}");
                await ApplyCurrentResolutionAsync("post-stabilization", force: true);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[EmbeddedRdpView] resolution delay: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_stabilizationCts, stabilizationCts))
            {
                _stabilizationCts.Dispose();
                _stabilizationCts = null;
            }
        }
    }

    private void OnRdpDisconnected(int reason)
    {
        int extendedReason = _rdpHost?.LastExtendedDisconnectReason
            ?? RdpActiveXHost.NoExtendedDisconnectReason;
        _lastExtendedDisconnectReason = extendedReason;
        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP OnDisconnected fired: reason={reason} extendedReason={extendedReason}");
        if (_disposed)
        {
            return;
        }

        // The control is no longer connected, so this is the first point where the native password
        // reset is contractually legal. It runs before the watchdog early return on purpose: an
        // abandoned connect attempt must clear the secret exactly like a normal disconnect.
        TryResetNativePassword(nameof(OnRdpDisconnected));

        // This disconnect was triggered by the watchdog aborting the COM connect.
        // The connect-timeout error UI is already shown; run no further teardown so
        // the normal overlay/status/diagnostic path does not overwrite it. The flag
        // stays set (reset only on a fresh BeginConnect) so a still-pending late
        // OnConnected remains refused.
        if (_connectAttempts.AbandonedByWatchdog)
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP OnDisconnected from watchdog abort: reason={reason}; preserving connect-timeout error state");
            _userInitiatedDisconnect = false;
            return;
        }

        // Record the end of the session segment. Idempotent: a no-op if the auto-reconnect bounce
        // already emitted a Disconnected for this segment, or if the COM stack re-fires during the
        // teardown Disconnect() call.
        EmitDisconnectEvent(reason, extendedReason);

        var wasUserInitiated = _userInitiatedDisconnect;
        _userInitiatedDisconnect = false;
        var suppressOverlay = ShouldSuppressReconnectOverlay(wasUserInitiated, reason);
        TryTransitionConnectionState(ConnectionState.Disconnected);

        Dispatcher.Invoke(() =>
        {
            _watchdogCredentialWaitActive = false;
            CancelAutofill();
            _autofillRetryContext = null;
            UpdateAutofillState(RdpAutofillState.None);
            StopAntiIdleTimer();
            StopStabilizationCountdown();
            StopReconnectElapsedTracking();
            ReleaseSleepPrevention();
            TransitionPhase(RdpConnectionPhase.None);
            HideRedirectionIndicators();
            _allowResolutionUpdates = false;

            if (suppressOverlay)
            {
                ClearPaneDiagnostic();
                UpdateSessionStatus(RdpSessionStatus.Disconnected);
                UpdateHealthDot(wasUserInitiated);
                Core.Logging.FileLogger.Info(
                    $"EmbeddedRDP suppressed reconnect overlay: userInitiated={wasUserInitiated} reason={reason}");
                return;
            }

            SetPaneDiagnostic(
                TryBuildTunnelFailureDiagnostic(reason)
                ?? RdpHostDiagnosticFactory.FromDisconnect(reason, extendedReason));
            UpdateSessionStatus(RdpSessionStatus.Disconnected);
            UpdateHealthDot(wasUserInitiated);
            ShowReconnectOverlay();
        });
    }

    private void OnRdpFatalError(int errorCode)
    {
        Core.Logging.FileLogger.Warn($"EmbeddedRDP OnFatalError fired: errorCode={errorCode}");
        if (_disposed)
        {
            return;
        }

        // Guarded attempt only: the fatal error does not by itself prove the COM disconnected
        // state, so the reset is refused unless the control confirms it. A later OnDisconnected
        // retries the idempotent reset.
        TryResetNativePassword(nameof(OnRdpFatalError));

        var fatalMessage = _localizer?.Format("RdpStatusFatalErrorDetail", errorCode)
            ?? "RdpStatusFatalErrorDetail";
        SetConnectionStateError(fatalMessage);

        Dispatcher.Invoke(() =>
        {
            CancelAutofill();
            _autofillRetryContext = null;
            UpdateAutofillState(RdpAutofillState.None);
            StopAntiIdleTimer();
            StopStabilizationCountdown();
            StopReconnectElapsedTracking();
            ReleaseSleepPrevention();
            TransitionPhase(RdpConnectionPhase.None);
            HideRedirectionIndicators();
            _allowResolutionUpdates = false;
            SetPaneDiagnostic(RdpHostDiagnosticFactory.FromFatalError(errorCode));
            UpdateSessionStatus(RdpSessionStatus.Error);
            ShowReconnectOverlay();
        });
    }

    /// <summary>
    /// Clears every password representation held by the native control, fail-closed. The helper
    /// refuses the reset unless the COM connected state is readable and reports the disconnected
    /// value, so the call is safe to repeat. Success is silent; every other outcome emits one
    /// bounded warning carrying technical evidence only.
    /// </summary>
    /// <param name="trigger">Name of the COM callback requesting the reset.</param>
    private void TryResetNativePassword(string trigger)
    {
        RdpPasswordResetResult result = RdpPasswordReset.TryReset(_rdpHost?.GetActiveXInstance());
        if (result.IsSuccess)
        {
            return;
        }

        string connectedState = result.ConnectedState is int state
            ? state.ToString(CultureInfo.InvariantCulture)
            : "n/a";
        string hResult = result.HResult is int code
            ? "0x" + code.ToString("X8", CultureInfo.InvariantCulture)
            : "n/a";

        Core.Logging.FileLogger.Warn(
            $"EmbeddedRDP password reset not applied: trigger={trigger} outcome={result.Outcome} "
            + $"connected={connectedState} stateType={result.ObservedStateTypeName ?? "n/a"} "
            + $"error={result.FailureTypeName ?? "n/a"} hr={hResult}");
    }

    private void OnRdpLoginComplete()
    {
        Core.Logging.FileLogger.Info("EmbeddedRDP OnLoginComplete fired");
        if (_disposed) return;

        Dispatcher.Invoke(() =>
        {
            if (_connectionPhase is RdpConnectionPhase.Preparing or RdpConnectionPhase.Connecting)
            {
                TransitionPhase(RdpConnectionPhase.Loading);
            }
        });
    }

    private void OnRdpAutoReconnecting(int disconnectReason, int attemptCount)
    {
        int extendedReason = _rdpHost?.LastExtendedDisconnectReason
            ?? RdpActiveXHost.NoExtendedDisconnectReason;
        _lastExtendedDisconnectReason = extendedReason;
        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP OnAutoReconnecting: reason={disconnectReason} extendedReason={extendedReason} attempt={attemptCount}");
        if (_disposed) return;

        // The session segment dropped: close it with one Disconnected on the first attempt of the
        // bounce. MsTscAx fires OnAutoReconnecting (not OnDisconnected) per attempt; a successful
        // bounce ends in OnAutoReconnected with no OnDisconnected, so this is the only disconnect
        // signal for a recovered drop. A cancelled/failed bounce later reaches OnDisconnected, which
        // then idempotently skips.
        EmitBounceDisconnectEvent(disconnectReason, extendedReason);

        SessionDiagnostic? gatewayDiagnostic = TryBuildTunnelFailureDiagnostic(disconnectReason);
        if (gatewayDiagnostic is not null)
        {
            if (_rdpHost is not null)
            {
                _rdpHost.CancelAutoReconnect = true;
            }

            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP auto-reconnect cancelled for gateway-attributable disconnect: reason={disconnectReason} attempt={attemptCount}");
            TryTransitionConnectionState(ConnectionState.Disconnected);

            Dispatcher.Invoke(() =>
            {
                CancelAutofill();
                _autofillRetryContext = null;
                UpdateAutofillState(RdpAutofillState.None);
                StopAntiIdleTimer();
                StopStabilizationCountdown();
                StopReconnectElapsedTracking();
                ReleaseSleepPrevention();
                TransitionPhase(RdpConnectionPhase.None);
                HideRedirectionIndicators();
                _allowResolutionUpdates = false;
                SetPaneDiagnostic(gatewayDiagnostic);
                UpdateSessionStatus(RdpSessionStatus.Disconnected);
                UpdateHealthDot(false);
                ShowReconnectOverlay();
            });
            return;
        }

        if (_rdpHost is not null && !RdpActiveXHost.AllowsAutoReconnect(disconnectReason, extendedReason))
        {
            _rdpHost.CancelAutoReconnect = true;
            RdpActiveXHost.RdpDisconnectSeverity severity =
                RdpActiveXHost.GetDisconnectSeverity(disconnectReason, extendedReason);
            Core.Logging.FileLogger.Info(
                $"EmbeddedRDP auto-reconnect cancelled for non-transient disconnect: " +
                $"reason={disconnectReason} extendedReason={extendedReason} severity={severity} attempt={attemptCount}");
            TryTransitionConnectionState(ConnectionState.Disconnected);

            Dispatcher.Invoke(() =>
            {
                CancelAutofill();
                _autofillRetryContext = null;
                UpdateAutofillState(RdpAutofillState.None);
                StopAntiIdleTimer();
                StopStabilizationCountdown();
                StopReconnectElapsedTracking();
                ReleaseSleepPrevention();
                TransitionPhase(RdpConnectionPhase.None);
                HideRedirectionIndicators();
                _allowResolutionUpdates = false;
                SetPaneDiagnostic(RdpHostDiagnosticFactory.FromDisconnect(disconnectReason, extendedReason));
                UpdateSessionStatus(RdpSessionStatus.Disconnected);
                UpdateHealthDot(false);
                ShowReconnectOverlay();
            });
            return;
        }

        var attemptTimestampUtc = DateTime.UtcNow;
        Dispatcher.Invoke(() =>
        {
            RecordReconnectAttemptTimestamp(attemptTimestampUtc);
            StopStabilizationCountdown();
            TransitionPhase(RdpConnectionPhase.None);
            StartReconnectElapsedTracking();
            HideRedirectionIndicators();
            _allowResolutionUpdates = false;
            UpdateSessionStatus(RdpSessionStatus.Reconnecting, attemptCount);
        });
    }

    private void OnRdpAutoReconnected()
    {
        Core.Logging.FileLogger.Info("EmbeddedRDP OnAutoReconnected fired");
        if (_disposed) return;

        Dispatcher.Invoke(() =>
        {
            // Read before it is cleared, because it is the whole reason there is anything to say:
            // the user asked for this reconnection to stop and it succeeded anyway.
            bool cancelLostTheRace = _userInitiatedDisconnect;

            // The cancel raised this flag for a disconnect that then never happened, because the
            // attempt already in flight succeeded instead. Left raised it outlives the race: it is
            // cleared nowhere but in OnRdpDisconnected, so the NEXT genuine drop of this live
            // session would be read as a user disconnect - no reconnect overlay, and a health dot
            // painted as if the user had asked for it. A session dying in silence.
            _userInitiatedDisconnect = false;

            // And the machine has to be told the session came back. The two lines below already
            // declare this view Connected; without this the state machine would still be reporting
            // the disconnect it was told about, and everything that counts live sessions from it -
            // the close confirmations above all - would not see this one.
            TryTransitionConnectionState(ConnectionState.Connected);

            // OnRdpConnected is not re-entered on an auto-reconnect, and _connectedAtUtc is not
            // refreshed here, so the event log opens a fresh segment at the reconnect-success point.
            // This keeps the next Disconnected duration measured from the reconnect, not the original
            // connect, and pairs the bounce as one Disconnected then one Connected.
            EmitConnectEvent();
            ClearPaneDiagnostic();
            StopReconnectElapsedTracking();
            TransitionPhase(RdpConnectionPhase.Connected);
            UpdateSessionStatus(RdpSessionStatus.Connected);
            UpdateRedirectionIndicators();

            // Cancelling stops the retries that have not started yet; an attempt already inside
            // MsTscAx keeps going and can still succeed. The session is kept, because the button
            // says Cancel rather than Close and asks to stop waiting, not to throw work away. But
            // without this the screen went from "closing" to "connected" and never said why, so the
            // user was left holding a session they thought they had given up on.
            if (cancelLostTheRace)
            {
                ShowTransientToast(_localizer?[LocaleKeys.ReconnectSucceededAfterCancel] ?? string.Empty);
            }

            if (ShouldUseDynamicResolutionUpdates())
            {
                StartStabilizationCountdown(_initialResizeEnableDelay);
                _ = EnableResolutionUpdatesAsync();
            }
            else
            {
                _allowResolutionUpdates = true;
            }

            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(FocusRdpSurfaceIfAppropriate));
        });
    }

    private void CreateHostControl()
    {
        // Acquired rather than constructed: a control that has ever connected costs a
        // measured 66 kernel handles that are never returned, so one is reused when the
        // provider has a spare.
        _rdpHost = HostProvider.Acquire();
        _rdpHost.Dock = WinForms.DockStyle.Fill;
        _rdpHost.InitialSmartSizing = ResolveInitialSmartSizing(_server);

        _rdpHost.Connected += OnRdpConnected;
        _rdpHost.Disconnected += OnRdpDisconnected;
        _rdpHost.FatalError += OnRdpFatalError;
        _rdpHost.LoginComplete += OnRdpLoginComplete;
        _rdpHost.AutoReconnecting += OnRdpAutoReconnecting;
        _rdpHost.AutoReconnected += OnRdpAutoReconnected;

        FormsHost.Child = _rdpHost.GetHostControl();
        ApplyHostLayout();

        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP host created: clsid={_rdpHost.ActiveXClsid} childType={FormsHost.Child?.GetType().FullName ?? "null"}");
    }

    private void EnsureHostHandle()
    {
        if (_rdpHost is null)
        {
            throw new InvalidOperationException("The RDP host control is not available.");
        }

        if (!_rdpHost.IsHandleCreated)
        {
            _ = _rdpHost.Handle;
            WinForms.Application.DoEvents();
        }

        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP EnsureHostHandle: handle=0x{_rdpHost.HostHandle.ToInt64():X} handleCreated={_rdpHost.IsHandleCreated}");
    }

    private void StartCredentialAutofill(string password, string hostHint)
    {
        CancelAutofill();
        _autofillRetryContext = new AutofillRetryContext(password, hostHint);
        _autofillAttemptInFlight = true;
        _autofillCts = new CancellationTokenSource();
        var token = _autofillCts.Token;

        // The view-side state belongs to the caller's thread, which is the UI thread at both call
        // sites. The watcher does not: its first scan enumerates every visible top-level window,
        // resolves a process name for each one and walks this process's threads, with nothing
        // awaited before it, so a bare call runs that inline - right after Connect(), inside a
        // render-priority operation, with the control's own callbacks queued behind it.
        Dispatcher.Invoke(() =>
        {
            UpdateAutofillState(RdpAutofillState.Searching);
            ArmStageTwoConnectWatchdog();
        });

        _ = RdpAutofillLauncher.StartAsync(
            watcherToken => TryAutofillCredentialsAsync(password, hostHint, watcherToken),
            token);
    }

    private async Task TryAutofillCredentialsAsync(string password, string hostHint, CancellationToken cancellationToken)
    {
        try
        {
            var timeoutMs = _settings?.RdpCredentialAutofillTimeoutMs ?? 90000;
            var filled = await CredentialAutofill.WaitAndFillAsync(
                Environment.ProcessId,
                hostHint,
                password,
                TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken).ConfigureAwait(false);

            if (filled)
            {
                Dispatcher.Invoke(() => UpdateAutofillState(RdpAutofillState.Filled));
            }
            else
            {
                Core.Logging.FileLogger.Warn(
                    $"EmbeddedRDP CredUI autofill timed out for hostHint={hostHint}");
                Dispatcher.Invoke(() => UpdateAutofillState(RdpAutofillState.TimedOut));
            }
        }
        catch (OperationCanceledException)
        {
            // Session connected or was disposed before a credential dialog appeared.
            if (!_disposed)
            {
                Dispatcher.Invoke(() => UpdateAutofillState(RdpAutofillState.None));
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"Embedded RDP credential autofill failed: {ex.Message}");
            if (!_disposed)
            {
                Dispatcher.Invoke(() => UpdateAutofillState(RdpAutofillState.Failed));
            }
        }
    }

    private void CancelAutofill()
    {
        _autofillAttemptInFlight = false;

        if (_autofillCts is null)
        {
            return;
        }

        var cts = _autofillCts;
        _autofillCts = null;

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[EmbeddedRdpView] autofill cancel: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Updates the credential autofill sub-status in the header.
    /// </summary>
    private void UpdateAutofillState(RdpAutofillState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateAutofillState(state));
            return;
        }

        if (_disposed)
        {
            return;
        }

        _autofillFilledTimer?.Stop();
        _autofillState = state;

        var key = state switch
        {
            RdpAutofillState.Searching => "RdpAutofillSearching",
            RdpAutofillState.Filled => "RdpAutofillFilled",
            RdpAutofillState.TimedOut => "RdpAutofillTimedOut",
            RdpAutofillState.Failed => "RdpAutofillFailed",
            _ => null
        };

        if (state != RdpAutofillState.Searching)
        {
            _autofillAttemptInFlight = false;
        }

        if (key is null)
        {
            AutofillStatusText.Text = string.Empty;
            AutofillStatusText.Visibility = Visibility.Collapsed;
            AutofillSeparator.Visibility = Visibility.Collapsed;
            UpdateAutofillActionButtonsVisibility(state);
            return;
        }

        if (state == RdpAutofillState.Filled)
        {
            _autofillRetryContext = null;
        }

        var mappedState = MapAutofillStateForBehavior(state);
        var isConnected = _connectionPhase == RdpConnectionPhase.Connected;
        if (isConnected && state is RdpAutofillState.TimedOut or RdpAutofillState.Failed)
        {
            _autofillRetryContext = null;
        }

        AutofillStatusText.Text = L(key);
        _ = RdpLiveRegion.Announce(AutofillStatusText);
        AutofillStatusText.Visibility = Visibility.Visible;
        AutofillSeparator.Visibility = Visibility.Visible;
        UpdateAutofillActionButtonsVisibility(state);

        if (RdpAutofillStateBehavior.ShouldAutoDismiss(mappedState, isConnected))
        {
            _autofillFilledTimer ??= new DispatcherTimer(
                AutofillFilledDisplayDuration,
                DispatcherPriority.Background,
                OnAutofillFilledTimerTick,
                Dispatcher);
            _autofillFilledTimer.Interval = AutofillFilledDisplayDuration;
            _autofillFilledTimer.Start();
        }

    }

    private void OnAutofillFilledTimerTick(object? sender, EventArgs e)
    {
        _autofillFilledTimer?.Stop();
        UpdateAutofillState(RdpAutofillState.None);
    }

    private void UpdateAutofillActionButtonsVisibility(RdpAutofillState state)
    {
        var mappedState = MapAutofillStateForBehavior(state);
        var canRetry = RdpAutofillStateBehavior.CanRetry(mappedState, CanShowCredentialPrompt())
            && _autofillRetryContext is not null
            && !_autofillAttemptInFlight;

        var isTerminal = state is RdpAutofillState.TimedOut or RdpAutofillState.Failed;
        var isConnected = _connectionPhase == RdpConnectionPhase.Connected;
        var canDismiss = isTerminal && !isConnected;

        AutofillRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        AutofillDismissButton.Visibility = canDismiss ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutofillRetryClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var context = _autofillRetryContext;
        var mappedState = MapAutofillStateForBehavior(_autofillState);
        var canRetry = context is not null
            && !_autofillAttemptInFlight
            && RdpAutofillStateBehavior.CanRetry(
                mappedState,
                CanShowCredentialPrompt());

        if (!canRetry || context is null)
        {
            return;
        }

        StartCredentialAutofill(context.Password, context.HostHint);
    }

    private void OnAutofillDismissClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _autofillRetryContext = null;
        UpdateAutofillState(RdpAutofillState.None);
    }

    private bool CanShowCredentialPrompt()
        => _connectionPhase is RdpConnectionPhase.Preparing
            or RdpConnectionPhase.Connecting
            or RdpConnectionPhase.Loading;

    private static RdpAutofillStateForBehavior MapAutofillStateForBehavior(RdpAutofillState state)
        => state switch
        {
            RdpAutofillState.None => RdpAutofillStateForBehavior.None,
            RdpAutofillState.Searching => RdpAutofillStateForBehavior.Searching,
            RdpAutofillState.Filled => RdpAutofillStateForBehavior.Filled,
            RdpAutofillState.TimedOut => RdpAutofillStateForBehavior.TimedOut,
            RdpAutofillState.Failed => RdpAutofillStateForBehavior.Failed,
            _ => RdpAutofillStateForBehavior.None
        };

    private void TransitionPhase(RdpConnectionPhase newPhase)
    {
        if (newPhase == _connectionPhase)
        {
            return;
        }

        if (newPhase is RdpConnectionPhase.Preparing
            or RdpConnectionPhase.Connecting
            or RdpConnectionPhase.Loading
            or RdpConnectionPhase.Connected)
        {
            StopReconnectElapsedTracking();
        }

        _connectionPhase = newPhase;

        // The arbiter, not this method, decides whether the watchdog runs: a certificate
        // question outstanding on this view suspends it, and no phase may arm a connect
        // budget over a wait for a human answer.
        _connectWatchdogArbiter.PhaseChanged(newPhase);

        UpdatePhaseStepper();
        UpdateVisibilityForPhase();

        var statusKey = RdpConnectionPhasePolicy.GetStatusKey(newPhase);
        if (statusKey is not null)
        {
            SetStatusText(L(statusKey));
        }

        UpdateHealthDot();
    }

    private void UpdatePhaseStepper()
    {
        var litSegments = RdpConnectionPhasePolicy.GetLitSegmentCount(_connectionPhase);
        if (litSegments == 0)
        {
            ConnectionPhaseStepper.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(ConnectionPhaseStepper, L("A11yConnectionPhaseStepper"));
            return;
        }

        ConnectionPhaseStepper.Visibility = Visibility.Visible;
        SetPhaseSegmentState(PhaseSegmentPreparing, litSegments >= 1);
        SetPhaseSegmentState(PhaseSegmentConnecting, litSegments >= 2);
        SetPhaseSegmentState(PhaseSegmentLoading, litSegments >= 3);
        SetPhaseSegmentState(PhaseSegmentConnected, litSegments >= 4);

        UpdatePhaseStepperAutomationName(litSegments);
    }

    private static void SetPhaseSegmentState(Border segment, bool isLit)
    {
        segment.SetResourceReference(
            Border.BackgroundProperty,
            isLit ? "AccentBrush" : "TextDisabledBrush");
    }

    private void UpdatePhaseStepperAutomationName(int litSegments)
    {
        var statusKey = RdpConnectionPhasePolicy.GetStatusKey(_connectionPhase);
        if (_localizer is null || statusKey is null)
        {
            return;
        }

        const int totalSegments = 4;
        var phaseLabel = _localizer[statusKey];
        AutomationProperties.SetName(
            ConnectionPhaseStepper,
            _localizer.Format(
                "A11yRdpPhaseAnnouncementFormat",
                phaseLabel,
                litSegments,
                totalSegments));
        _ = RdpLiveRegion.Announce(ConnectionPhaseStepper);
    }

    private void UpdateVisibilityForPhase()
    {
        var (cancelConnectVisible, disconnectVisible) =
            RdpConnectionPhasePolicy.ResolveVisibility(_connectionPhase);

        CancelConnectButton.Visibility = cancelConnectVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelConnectButton.IsEnabled = !_disposed && cancelConnectVisible;

        DisconnectButton.Visibility = disconnectVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        DisconnectButton.IsEnabled = !_disposed && disconnectVisible;
    }

    private void UpdateHealthDot(bool? wasUserInitiatedDisconnectOverride = null)
    {
        var state = RdpHealthDotPolicy.Resolve(
            _connectionPhase,
            _sessionStatus,
            wasUserInitiatedDisconnectOverride ?? _userInitiatedDisconnect);

        var brushKey = ResolveHealthDotBrushKey(state);
        HealthDotColor.SetResourceReference(
            Border.BackgroundProperty,
            brushKey);
        HealthDotGlyph.Text = ResolveHealthDotGlyph(state);
        HealthDotGlyph.SetResourceReference(
            TextBlock.ForegroundProperty,
            brushKey);

        var label = L(ResolveHealthDotLabelKey(state));
        AutomationProperties.SetName(HealthDot, label);
        _ = RdpLiveRegion.Announce(HealthDot);
        var endpoint = _server is null ? string.Empty : BuildEndpointText(_server);
        HealthDot.ToolTip = string.IsNullOrWhiteSpace(endpoint)
            ? label
            : string.Format(CultureInfo.CurrentCulture, "{0} - {1}", label, endpoint);
    }

    private static string ResolveHealthDotBrushKey(RdpHealthDotState state) => state switch
    {
        RdpHealthDotState.Healthy => "SuccessBrush",
        RdpHealthDotState.Transitional => "WarningBrush",
        RdpHealthDotState.Faulted => "ErrorBrush",
        _ => "TextDisabledBrush"
    };

    private static string ResolveHealthDotGlyph(RdpHealthDotState state) => state switch
    {
        RdpHealthDotState.Healthy => HealthHealthyGlyph,
        RdpHealthDotState.Transitional => HealthTransitionalGlyph,
        RdpHealthDotState.Faulted => HealthFaultedGlyph,
        _ => HealthIdleGlyph
    };

    private static string ResolveHealthDotLabelKey(RdpHealthDotState state) => state switch
    {
        RdpHealthDotState.Healthy => "RdpHealthDotHealthy",
        RdpHealthDotState.Transitional => "RdpHealthDotTransitional",
        RdpHealthDotState.Faulted => "RdpHealthDotFaulted",
        _ => "RdpHealthDotIdle"
    };

    private void UpdateRedirectionIndicators()
    {
        if (_pendingRedirections is null
            || _rdpHost is null
            || !_rdpHost.IsConnected)
        {
            HideRedirectionIndicators();
            return;
        }

        var alwaysExpanded = _settings?.RdpRedirectionIndicatorsAlwaysExpanded ?? false;

        SetRedirectionIndicator(
            RedirIconClipboard,
            RedirectionClipboardGlyph,
            "RdpRedirectionLabelClipboard",
            _pendingRedirections.Clipboard,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconDrives,
            RedirectionDrivesGlyph,
            "RdpRedirectionLabelDrives",
            _pendingRedirections.Drives,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconPrinters,
            RedirectionPrintersGlyph,
            "RdpRedirectionLabelPrinters",
            _pendingRedirections.Printers,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconComPorts,
            RedirectionComPortsGlyph,
            "RdpRedirectionLabelComPorts",
            _pendingRedirections.ComPorts,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconSmartCards,
            RedirectionSmartCardsGlyph,
            "RdpRedirectionLabelSmartCards",
            _pendingRedirections.SmartCards,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconUsb,
            RedirectionUsbGlyph,
            "RdpRedirectionLabelUsb",
            _pendingRedirections.Usb,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconAudio,
            RedirectionAudioGlyph,
            "RdpRedirectionLabelAudio",
            _pendingRedirections.AudioMode != 0,
            alwaysExpanded);
        SetRedirectionIndicator(
            RedirIconMultiMonitor,
            RedirectionMultiMonitorGlyph,
            "RdpRedirectionLabelMultiMonitor",
            _pendingRedirections.MultiMonitor,
            alwaysExpanded);

        UpdateRedirectionExpandBadge(alwaysExpanded);

        RedirectionIndicatorsPanel.Visibility = Visibility.Visible;
        _ = RdpLiveRegion.Announce(RedirectionIndicatorsPanel);
    }

    private void HideRedirectionIndicators()
    {
        RedirectionIndicatorsPanel.Visibility = Visibility.Collapsed;
    }

    private void SetRedirectionIndicator(
        TextBlock icon,
        string glyph,
        string labelKey,
        bool isActive,
        bool alwaysExpanded)
    {
        icon.Text = glyph;
        icon.SetResourceReference(
            TextBlock.ForegroundProperty,
            isActive ? "AccentBrush" : "TextDisabledBrush");
        icon.TextDecorations = isActive ? null : TextDecorations.Strikethrough;
        icon.Visibility = RdpRedirectionVisibilityPolicy.IsIndicatorVisible(
            isActive,
            alwaysExpanded,
            _redirectionExpandedOverride)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_localizer is null)
        {
            return;
        }

        var label = _localizer[labelKey];
        var helpText = _localizer.Format(
            isActive ? "RdpRedirectionStatusOnFormat" : "RdpRedirectionStatusOffFormat",
            label);
        AutomationProperties.SetHelpText(icon, helpText);
    }

    private void UpdateRedirectionExpandBadge(bool alwaysExpanded)
    {
        if (_pendingRedirections is null)
        {
            RedirExpandBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var disabledStates = new[]
        {
            !_pendingRedirections.Clipboard,
            !_pendingRedirections.Drives,
            !_pendingRedirections.Printers,
            !_pendingRedirections.ComPorts,
            !_pendingRedirections.SmartCards,
            !_pendingRedirections.Usb,
            _pendingRedirections.AudioMode == 0,
            !_pendingRedirections.MultiMonitor,
        };

        var disabledCount = 0;
        foreach (var d in disabledStates)
        {
            if (d) { disabledCount++; }
        }

        if (RdpRedirectionVisibilityPolicy.ShouldShowExpandBadge(
                disabledCount,
                alwaysExpanded,
                _redirectionExpandedOverride))
        {
            RedirExpandBadge.Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "+{0}",
                disabledCount);
            RedirExpandBadge.Visibility = Visibility.Visible;
        }
        else
        {
            RedirExpandBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRedirExpandBadgeClick(object sender, RoutedEventArgs e)
    {
        _redirectionExpandedOverride = !_redirectionExpandedOverride;
        UpdateRedirectionIndicators();
    }

    private void OnConnectionStateChanged(ConnectionStateChange change)
    {
        if (_server is null
            || !ShouldHandleStateChange(
                change.ServerId,
                _server.Id,
                _comDrivenStatusActive,
                _disposed)
            || !TryAcceptConnectionStateRevision(change.Revision))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyConnectionStateStatus(change.NewState);
        }
        else
        {
            Dispatcher.Invoke(() => ApplyConnectionStateStatus(change.NewState));
        }
    }

    private void UpdateConnectingStatusFromStateMachineOrDefault()
    {
        UpdateSessionStatus(RdpSessionStatus.Connecting);
        ApplyCurrentConnectionStateStatus();
        ApplyConnectStatusOverride();
    }

    private void ApplyCurrentConnectionStateStatus()
    {
        if (_connectionStateMachine is null || _server is null || _comDrivenStatusActive)
        {
            return;
        }

        ConnectionStateData? stateData = _connectionStateMachine.GetStateData(_server.Id);
        if (stateData is null
            || stateData.CurrentState is ConnectionState.Disconnected
            || !TryAcceptConnectionStateRevision(stateData.Revision))
        {
            return;
        }

        ApplyConnectionStateStatus(stateData.CurrentState);
    }

    private bool TryAcceptConnectionStateRevision(long revision)
    {
        while (true)
        {
            long currentRevision = Volatile.Read(ref _lastConnectionStateRevision);
            if (revision <= currentRevision)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _lastConnectionStateRevision,
                    revision,
                    currentRevision) == currentRevision)
            {
                return true;
            }
        }
    }

    private void ApplyConnectionStateStatus(ConnectionState state)
    {
        var metadata = ConnectionStateMachine.GetMetadata(state);
        if (string.IsNullOrWhiteSpace(metadata.DisplayKey))
        {
            return;
        }

        SetStatusText(FormatConnectionStateStatus(metadata.DisplayKey));
        RdpLoadingBar.Visibility = metadata.IsProgress ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Foreground = GetBrush("TextPrimaryBrush", Brushes.White);
    }


    private void ApplyConnectStatusOverride()
    {
        if (string.IsNullOrWhiteSpace(_connectStatusOverrideKey) || _comDrivenStatusActive)
        {
            return;
        }

        SetStatusText(L(_connectStatusOverrideKey));
        StatusTextBlock.Foreground = GetBrush("TextPrimaryBrush", Brushes.White);
    }
    private string FormatConnectionStateStatus(string statusKey)
    {
        if (_localizer is null)
        {
            return statusKey;
        }

        return statusKey switch
        {
            "StatusConnecting" => _localizer.Format(statusKey, BuildConnectionStateTarget()),
            "StatusEstablishingTunnel" => _localizer.Format(statusKey, BuildConnectionStateTarget()),
            "StatusTunnelEstablished" => _localizer.Format(statusKey, _tunnelPort ?? 0),
            _ => L(statusKey),
        };
    }

    private string BuildConnectionStateTarget()
        => _server is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(_server.RemoteServer)
                ? _server.DisplayName
                : _server.RemoteServer;

    private void TryTransitionConnectionState(ConnectionState state)
    {
        if (_connectionStateMachine is null || _server is null)
        {
            return;
        }

        _connectionStateMachine.TryTransition(_server.Id, state);
    }

    private void SetConnectionStateError(string message)
    {
        if (_connectionStateMachine is null || _server is null)
        {
            return;
        }

        _connectionStateMachine.SetError(_server.Id, message);
    }

    private void UpdateSessionStatus(
        RdpSessionStatus status,
        int? reconnectAttempt = null)
    {
        _sessionStatus = status;
        var invariantCode = RdpSessionStatusKeys.GetInvariantCode(status);
        string localizedLabel;

        if (status == RdpSessionStatus.Reconnecting && _localizer is not null)
        {
            var attempt = reconnectAttempt ?? 1;
            localizedLabel = _localizer.Format(
                RdpSessionStatusKeys.GetKey(status),
                attempt,
                ResolveReconnectAttemptCeiling());
        }
        else
        {
            localizedLabel = L(RdpSessionStatusKeys.GetKey(status));
        }

        if (_sessionTab is not null)
        {
            _sessionTab.Status = invariantCode;
        }

        SetStatusText(localizedLabel);

        var isProgress = status is RdpSessionStatus.Connecting
            or RdpSessionStatus.Preparing
            or RdpSessionStatus.Reconnecting;
        RdpLoadingBar.Visibility = isProgress ? Visibility.Visible : Visibility.Collapsed;
        if (status is RdpSessionStatus.Reconnecting)
        {
            RdpLoadingBar.IsIndeterminate = false;
            RdpLoadingBar.Minimum = 0;
            int ceiling = ResolveReconnectAttemptCeiling();
            RdpLoadingBar.Maximum = ceiling;
            RdpLoadingBar.Value = ResolveReconnectProgressValue(reconnectAttempt ?? 0, ceiling);
        }
        else
        {
            RdpLoadingBar.IsIndeterminate = true;
            RdpLoadingBar.Value = 0;
        }

        if (status is not RdpSessionStatus.Connected)
        {
            HideRedirectionIndicators();
        }

        UpdateVisibilityForPhase();
        FullscreenButton.IsEnabled = !_disposed;
        SendKeysButton.IsEnabled = !_disposed && status is RdpSessionStatus.Connected;
        CancelReconnectButton.Visibility = status == RdpSessionStatus.Reconnecting
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusTextBlock.Foreground = GetBrush(
            status is RdpSessionStatus.Error ? "ErrorBrush" : "TextPrimaryBrush",
            status is RdpSessionStatus.Error ? Brushes.IndianRed : Brushes.White);
        UpdateHealthDot();
    }

    /// <summary>
    /// How many attempts the control will actually make, rather than the compiled-in default.
    /// </summary>
    /// <remarks>
    /// A profile can raise or lower the cap and the control is configured from that value, so
    /// showing the default would present an attempt number past its own ceiling and a saturated
    /// bar. This surface only became reachable once the auto-reconnect events were delivered, so
    /// the divergence had never been displayed.
    /// </remarks>
    private int ResolveReconnectAttemptCeiling()
        => _rdpHost?.EffectiveMaxAutoReconnectAttempts ?? RdpActiveXHost.MaxAutoReconnectAttempts;

    internal static int ResolveReconnectProgressValue(int currentAttempt, int maxAttempts)
    {
        if (maxAttempts <= 0)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedRDP invalid auto-reconnect maxAttempts={maxAttempts}; progress reset to 0.");
            return 0;
        }

        return Math.Clamp(currentAttempt, 0, maxAttempts);
    }

    /// <summary>
    /// Checks the server certificate, then starts the connection unless the attempt was
    /// abandoned while the check ran or the user refused the certificate.
    /// </summary>
    /// <remarks>
    /// The single place a certificate question can stop a session. It runs once per
    /// view, not once per attempt: the retry path re-enters the connect directly,
    /// so a surface that is not ready yet does not re-probe the endpoint or ask the
    /// user twice.
    /// </remarks>
    private async Task StartVerifiedConnectAsync()
    {
        // The certificate probe and any trust question happen here, before Connect(). This is the
        // Preparing phase: it lights the stepper's first segment, shows the Cancel button and
        // arms the connect watchdog, none of which used to happen because the phase had no
        // producer and the view sat in None while the status line already said Connecting.
        // The watchdog is suspended for the check itself (see RdpConnectWatchdogArbiter). The
        // Cancel button shown here is clickable throughout, including while this pane or any
        // other is holding a certificate question: the question is displayed inside the pane
        // that asked it and is not modal to the application.
        TransitionPhase(RdpConnectionPhase.Preparing);

        RdpCertificateCheckResult check;
        try
        {
            check = await VerifyServerCertificateAsync();
        }
        finally
        {
            // Resumed here rather than at each exit below, so a refusal, a cancellation and a
            // teardown all leave the watchdog in a state the arbiter chose. The call is a no-op
            // when no check was owed, which is what keeps a profile without one on exactly the
            // budget it had before.
            _connectWatchdogArbiter.CertificateCheckCompleted(_connectionPhase, _disposed);
        }

        // Cancel during the check means cancel, not "start once the check finishes". The decision
        // is the arbiter's, taken over the same latches as the retry and the late connect, and
        // what is left here is the log that describes it. The teardown is one of its terms rather
        // than a separate return above, so that no term of this condition is dead by construction
        // at the site that reads it - a stopper that cannot fire is a stopper that is not there.
        // It is asked before the refusal below because a user who cancelled is not a user who
        // refused a certificate, and must not be shown that error instead of their own cancel.
        if (_connectAttempts.CertificateCheckSettled(_disposed) == RdpVerifiedConnectAdmission.Refuse)
        {
            Core.Logging.FileLogger.Info(
                "EmbeddedRDP connect abandoned during certificate verification "
                + $"(cancelled={_connectAttempts.AbandonedByUser} "
                + $"watchdog={_connectAttempts.AbandonedByWatchdog} disposed={_disposed})");
            return;
        }

        if (check.Decision == RdpConnectionDecision.Abandon)
        {
            HandleCertificateStopped(check.Outcome);
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(BeginConnect));
    }

    /// <summary>Runs the certificate check for this profile, when one is owed.</summary>
    /// <remarks>
    /// Returns the gate's own <c>RdpCertificateCheckResult</c>: the decision AND what was
    /// concluded. Two outcomes stop the connection - an answer a person gave, and a question
    /// that reached nobody - and the pane has a different sentence for each, so the one-bit
    /// decision on its own would leave the caller guessing which of them happened.
    /// </remarks>
    private async Task<RdpCertificateCheckResult> VerifyServerCertificateAsync()
    {
        AppSettings? settings = _settings;
        ServerProfileDto? server = _server;
        if (_disposed || server is null || settings is null)
        {
            return new RdpCertificateCheckResult(RdpConnectionDecision.Proceed, null);
        }

        RdpRedirectionOptions redirections =
            RdpProfileResolver.BuildRedirections(server, settings);
        RdpAuthenticationSettings auth = RdpAuthenticationResolver.Resolve(
            redirections.Nla,
            redirections.StrictServerAuthentication);

        if (!RdpCertificateGate.VerificationRequired(auth.AuthenticationLevel))
        {
            return new RdpCertificateCheckResult(RdpConnectionDecision.Proceed, null);
        }

        RdpCertificateVerifier? verifier = (Application.Current as App)
            ?.Services?.GetService<RdpCertificateVerifier>();
        if (verifier is null)
        {
            // Nothing was verified, so nothing may change.
            Core.Logging.FileLogger.Warn(
                "EmbeddedRDP certificate verifier is unavailable; connecting unverified.");
            return new RdpCertificateCheckResult(RdpConnectionDecision.Proceed, null);
        }

        // The address actually dialled, which for a tunneled session is the local end of the
        // tunnel - the certificate that answers there is the one that matters. A gateway-routed
        // profile has no such address: the session reaches the target through the RD Gateway, so
        // a probe dialling the bare target name resolves nothing or is filtered. That failure
        // used to be reported as CouldNotVerify and mapped to Proceed, which is indistinguishable
        // from a check that ran and found nothing wrong - and it cost the probe's full timeout in
        // front of every such connect.
        RdpCertificateProbeTarget? target =
            RdpProfileResolver.BuildCertificateVerificationTarget(server, _tunnelPort);
        if (target is null)
        {
            Core.Logging.FileLogger.Warn(
                "EmbeddedRDP certificate verification is not possible on this profile: the "
                + $"session is routed through RD Gateway '{server.RdpGateway}', which the probe "
                + "cannot reach. Connecting without a verified certificate.");
            ShowTransientToast(L(LocaleKeys.CertificateNotVerifiableToast));
            return new RdpCertificateCheckResult(RdpConnectionDecision.Proceed, null);
        }

        _certificateVerificationCts?.Dispose();
        _certificateVerificationCts = new CancellationTokenSource();

        // Built rather than written inline because of the scope token: it is what routes the
        // question back into this pane instead of onto the application's main window, and a
        // request that loses it is refused rather than asked somewhere else.
        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(server, target.Value, _trustPromptScopeId);

        // Past this point a probe runs and a trust question may be asked. The question is put to
        // a person inside this pane and may go unanswered indefinitely, so the connect watchdog
        // stops here and is resumed by the caller once the check has returned. Two questions
        // about the same certificate and the same profile are still coalesced into one, so a
        // second pane can be waiting on an answer given in the first.
        _connectWatchdogArbiter.CertificateCheckStarted();

        return await RdpCertificateGate.CheckConnectionAsync(
            auth.AuthenticationLevel,
            async ct =>
            {
                RdpVerificationOutcome outcome = await verifier.VerifyAsync(request, ct);
                Core.Logging.FileLogger.Info(
                    $"EmbeddedRDP certificate verification: host={request.Host}:"
                    + $"{request.Port} outcome={outcome}");
                return outcome;
            },
            ex => Core.Logging.FileLogger.Warn(
                $"EmbeddedRDP certificate verification could not run: {ex.Message}"),
            _certificateVerificationCts.Token);
    }

    /// <summary>Ends an attempt no approval cleared, and says which of the two it was.</summary>
    /// <param name="outcome">What the verifier concluded, or null when no outcome came back.</param>
    /// <remarks>
    /// <para>Reported through the error surface because the tab is finished either way, but with
    /// its own wording: this is not a fault.</para>
    /// <para><b>The wording is chosen from the outcome, not assumed.</b> This method used to say
    /// "you did not approve the certificate this server presented" whatever had happened, which
    /// was safe while a question always had a window to appear on. Once the question lives in a
    /// pane it can reach nobody - a pane torn down between the probe and the question, a surface
    /// already unregistered - and that sentence then attributes to the user a decision they were
    /// never offered.</para>
    /// </remarks>
    private void HandleCertificateStopped(RdpVerificationOutcome? outcome)
    {
        Core.Logging.FileLogger.Warn(
            "EmbeddedRDP connection abandoned without an approved certificate "
            + $"(outcome={outcome?.ToString() ?? "none"}).");
        _allowResolutionUpdates = false;
        TransitionPhase(RdpConnectionPhase.None);
        UpdateSessionStatus(RdpSessionStatus.Error);
        SetStatusText(L(RdpCertificateStoppedStatus.StatusKey(outcome)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The pane is the surface, so the marshalling happens here rather than in the presenter:
    /// the question is a WPF element of this view and everything it shows is read off this
    /// view's own state.
    /// </remarks>
    Task<RdpTrustAnswer> IRdpTrustPromptSurface.AskAsync(
        RdpCertificatePromptContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher
                .InvokeAsync(
                    () => AskInPaneAsync(context, cancellationToken),
                    DispatcherPriority.Normal)
                .Task
                .Unwrap();
        }

        return AskInPaneAsync(context, cancellationToken);
    }

    private Task<RdpTrustAnswer> AskInPaneAsync(
        RdpCertificatePromptContext context,
        CancellationToken cancellationToken)
    {
        if (_disposed || _localizer is null)
        {
            // Nothing can be shown, so nothing was asked and nothing was approved. A pane torn
            // down between the probe and the question is the ordinary case here, and it is
            // reported as a question nobody received rather than as an answer nobody gave.
            //
            // Not "and the connection stops": NotAsked leaves that to the coalescer, which hands
            // a pane sharing its question the answer given elsewhere. Nothing opens here for a
            // reason of its own - Dispose cancels the verification token before it closes the
            // question, and a pane whose own connection was given up adopts nothing.
            Core.Logging.FileLogger.Warn(
                "EmbeddedRDP cannot show the certificate question: the pane is gone "
                + $"(disposed={_disposed}). The question was not asked.");
            return Task.FromResult(RdpTrustAnswer.NotAsked);
        }

        RdpCertificatePromptDialogViewModel question =
            new(_localizer, context, BuildTrustPromptOrigin(context));

        return _trustPrompt.AskAsync(question, cancellationToken);
    }

    /// <summary>Says which machine this pane is reaching, through what, and where the pane is.</summary>
    /// <remarks>
    /// <para><b>The endpoint here is the profile's, not the probe's.</b> The probe dials the
    /// local end of the SSH tunnel when there is one, so its address is 127.0.0.1 for every
    /// tunnelled profile in the application; the same text the header bar shows is what
    /// identifies the machine, and it names the tunnel endpoint after it rather than instead of
    /// it.</para>
    /// <para><b>And the endpoint alone is still not an identity.</b> Two profiles reaching one
    /// short name through two different gateways are two machines whose endpoint text differs
    /// only by an ephemeral local port. The gateway chain is resolved here, from the profile
    /// rather than from the live tunnel, because this question is asked during Preparing - before
    /// the tab's own route string has been filled in.</para>
    /// <para><b>From the connection's own settings, never the application's current ones.</b>
    /// <see cref="ConnectionSettings"/> is the instance the chain was resolved from at connect
    /// time; <c>_settings</c> is a later clone taken when the pane was materialised. Reading the
    /// second is how a gateway edited during a slow tunnel establishment came to be named under
    /// "Reached through" for a certificate that had arrived through the first. With no carrier
    /// the line says nothing at all, which is the one honest answer available then.</para>
    /// <para><b>The tab is named as it is announced, not as it is displayed.</b>
    /// <c>DisplayTitle</c> is identical by construction for two sessions of one profile, so it
    /// added nothing precisely where two same-named sessions were the problem;
    /// <c>AccessibleName</c> is the same string with the ordinal this application already
    /// computes for colliding titles.</para>
    /// </remarks>
    private RdpTrustPromptOrigin BuildTrustPromptOrigin(RdpCertificatePromptContext context)
    {
        ServerProfileDto? server = _server;
        string endpoint = server is null ? string.Empty : BuildEndpointText(server);
        string? route = RdpTrustPromptRoute.DescribeConnection(server, ConnectionSettings);

        return new RdpTrustPromptOrigin(
            string.IsNullOrWhiteSpace(endpoint) ? context.Host : endpoint,
            route,
            AnnouncedTabTitle(),
            Window.GetWindow(this)?.Title);
    }

    /// <summary>How this pane's tab is announced, falling back to what it displays.</summary>
    /// <remarks>
    /// The choice itself lives in <see cref="RdpTrustPromptOwner.AnnouncedName"/>, with the
    /// reasoning: the displayed title is identical by construction for two sessions of one
    /// profile, so it identified nothing in exactly the case this line exists for.
    /// </remarks>
    private string? AnnouncedTabTitle()
    {
        SessionTabViewModel? tab = _sessionTab;
        if (tab is null)
        {
            return null;
        }

        return RdpTrustPromptOwner.AnnouncedName(tab.AccessibleName, tab.DisplayTitle);
    }

    /// <summary>Applies a withdrawal of the certificate question where the question is drawn.</summary>
    /// <remarks>
    /// <para>A withdrawal arrives on whichever thread cancelled - a pool thread running another
    /// pane's answer - while the three buttons that answer this pane's copy are hit-tested here.
    /// Posting it makes the settlement and the hiding one work item on this thread, so the
    /// question stops being answerable at the moment it stops being visible instead of a
    /// dispatcher hop earlier. In that hop a person pressed Do-not-connect on a live-looking
    /// question, had it discarded, and watched the pane adopt the other pane's approval and
    /// connect.</para>
    /// <para><b>Background, not Normal.</b> Background sits below Input, so a click already
    /// queued is dispatched before the withdrawal and counts as the answer to a question its
    /// user could still see. At Normal the withdrawal would overtake that click and lose it,
    /// which is the same defect in a shorter window.</para>
    /// </remarks>
    private void PostTrustPromptWithdrawal(Action withdrawal)
    {
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, withdrawal);
        }
        catch (InvalidOperationException ex)
        {
            // The dispatcher has finished shutting down, so nothing will ever draw or click this
            // question again and there is no press left to race. Applying it here is then as
            // safe as applying it there, and the alternative is a connection left waiting for an
            // answer that can no longer arrive.
            Core.Logging.FileLogger.Warn(
                "EmbeddedRDP could not post the certificate question's withdrawal to the UI "
                + $"thread ({ex.Message}); applying it here instead.");
            withdrawal();
        }
    }

    /// <summary>Shows or hides the question the session is waiting on.</summary>
    /// <remarks>
    /// The session raises this on whichever thread settled the question - the UI thread for an
    /// answer, the cancelling thread for a withdrawal - so it is marshalled here before any
    /// element is touched.
    /// </remarks>
    private void OnTrustPromptQuestionChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(OnTrustPromptQuestionChanged));
            return;
        }

        RdpCertificatePromptDialogViewModel? question = _trustPrompt.Question;
        if (question is null)
        {
            HideCertificatePrompt();
            return;
        }

        ShowCertificatePrompt(question);
    }

    private void ShowCertificatePrompt(RdpCertificatePromptDialogViewModel question)
    {
        if (_disposed)
        {
            return;
        }

        CertificatePromptOverlay.DataContext = question;

        // WindowsFormsHost is backed by a child HWND and paints over WPF whatever the z-order,
        // so the question would sit invisible behind the RDP surface. The reconnect overlay
        // obeys the same airspace rule; this one restores the host when the question is
        // answered, because unlike a disconnect there is a session still to come.
        FormsHost.Visibility = System.Windows.Visibility.Collapsed;
        CertificatePromptOverlay.Visibility = System.Windows.Visibility.Visible;

        // A pane the user is not looking at can be holding a question, and the question no
        // longer comes to them: the header line has to say why this session has stopped. The
        // previous line is kept so the pane does not come back from an answer with a status
        // that belongs to the question.
        _statusTextBeforeTrustPrompt = StatusTextBlock.Text;
        SetStatusText(L(RdpCertificatePromptLocaleKeys.PendingStatus));

        // LiveSetting alone publishes a property nothing reads. The event is what a screen
        // reader is told by, and it is raised on the message rather than on the container,
        // whose name is a constant.
        _ = RdpLiveRegion.Announce(CertificatePromptMessageText);

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed
                || CertificatePromptOverlay.Visibility != System.Windows.Visibility.Visible)
            {
                return;
            }

            // Refusal keeps the focus, which is what makes it the default action now that no
            // window carries IsDefault: the answer a stray Enter gives creates no durable trust.
            _ = CertificatePromptRefuseButton.Focus();
            _ = Keyboard.Focus(CertificatePromptRefuseButton);
        }));
    }

    private void HideCertificatePrompt()
    {
        CertificatePromptOverlay.Visibility = System.Windows.Visibility.Collapsed;
        CertificatePromptOverlay.DataContext = null;

        // Restored only when the line is still the one this question put there. A tunnel or a
        // state-machine update during the wait owns the status now, and writing the older text
        // back over it would report a stage the session has already left.
        if (_statusTextBeforeTrustPrompt is not null
            && string.Equals(
                StatusTextBlock.Text,
                L(RdpCertificatePromptLocaleKeys.PendingStatus),
                StringComparison.Ordinal))
        {
            SetStatusText(_statusTextBeforeTrustPrompt);
        }

        _statusTextBeforeTrustPrompt = null;

        RestoreRdpSurfaceAfterTrustPrompt();
    }

    /// <summary>Gives the native RDP surface back once no question stands in front of it.</summary>
    /// <remarks>
    /// The other half of an airspace pairing: <see cref="ShowCertificatePrompt"/> collapses the
    /// <c>WindowsFormsHost</c> because its child HWND paints over WPF whatever the z-order, and
    /// a collapse without this restore leaves every approved session showing a blank pane,
    /// which looks exactly like a failed connection. A torn-down pane keeps it collapsed: its
    /// own teardown collapses it anyway, and there is no session left to show.
    /// </remarks>
    private void RestoreRdpSurfaceAfterTrustPrompt()
    {
        if (_disposed)
        {
            return;
        }

        FormsHost.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnCertificatePromptPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || e is null)
        {
            return;
        }

        RdpCertificatePromptDialogViewModel? question = _trustPrompt.Question;
        if (question is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Escape is a refusal, exactly as the title-bar cross of the window it replaces was.
            question.RefuseFromDismissal();
            e.Handled = true;
            return;
        }

        // Enter is deliberately left alone, and that is the fix rather than an omission. Each
        // answer declares KeyboardNavigation.AcceptsReturn, which is the hook ButtonBase.OnKeyDown
        // reads to click the focused button on Enter - a real OnClick, so the bound command runs.
        // Handling Enter here would take the keystroke off the button before it ever saw it.
        //
        // What stood here raised ButtonBase.ClickEvent on the focused button by hand. OnClick
        // raises that event AND THEN executes the command source, and these three answers are
        // driven by bound commands, so the raise announced a click and answered nothing: a person
        // pressed "Do not connect", no refusal was recorded, and the pane went on to adopt the
        // approval given in another pane and open the session.
        //
        // The reconnect overlay above still delivers Enter by hand and is right to - its buttons
        // carry Click handlers, for which the raised event IS the whole click. Both halves are
        // measured on a real Button in RdpCertificatePromptSurfaceTests.
    }

    private void HandleFailure(string message, Exception ex)
    {
        Core.Logging.FileLogger.Error(message, ex);
        _allowResolutionUpdates = false;
        StopReconnectElapsedTracking();
        TransitionPhase(RdpConnectionPhase.None);
        HideRedirectionIndicators();
        UpdateSessionStatus(RdpSessionStatus.Error);
        SetStatusText(_localizer?.Format("RdpStatusErrorDetail", message, ex.Message)
            ?? $"{message} {ex.Message}");
    }

    private void HandleGatewayAttestationFailure(RdpGatewayAttestationException ex)
    {
        HandleFailure(L("RdpGatewayAttestationFailed"), ex);
    }

    private void SetPaneDiagnostic(SessionDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var pane = _ownerPane ?? _sessionTab?.PrimaryPane;
        if (pane is not null)
        {
            pane.FailureDetails = diagnostic;
        }
    }

    /// <summary>
    /// Builds an enriched diagnostic when a tunneled session dropped with a
    /// socket/network-level code and the SSH tunnel recorded a matching
    /// forwarded-port failure; otherwise returns null so the caller falls back
    /// to the generic disconnect diagnostic.
    /// </summary>
    private SessionDiagnostic? TryBuildTunnelFailureDiagnostic(int reason)
    {
        if (_tunnelPort is not int tunnelPort
            || _tunnelFailureLookup is null
            || !IsTunnelAttributableDisconnect(reason))
        {
            return null;
        }

        var failure = _tunnelFailureLookup(tunnelPort);
        return failure is null
            ? null
            : RdpHostDiagnosticFactory.FromTunnelForwardedPortFailure(failure, reason);
    }

    /// <summary>
    /// Disconnect codes consistent with the SSH tunnel's forwarded channel
    /// failing: a socket/network-level drop (SocketClosed 2308,
    /// SocketConnectFailed 516, ConnectionTimeout 264, NetworkError 772) that a
    /// gateway-to-target reachability failure can plausibly explain.
    /// </summary>
    internal static bool IsTunnelAttributableDisconnect(int reason)
        => reason is 2308 or 516 or 264 or 772;

    private void ClearPaneDiagnostic()
    {
        var pane = _ownerPane ?? _sessionTab?.PrimaryPane;
        if (pane is not null)
        {
            pane.FailureDetails = null;
        }
    }

    /// <summary>
    /// Updates the aspect ratio setting and triggers a resolution recalculation.
    /// </summary>
    public void UpdateAspectRatio(string ratioName)
    {
        if (_server is null) return;
        _server.RdpAspectRatio = ratioName;

        if (UsesFixedLocalResolution())
        {
            ApplyHostLayout();
            return;
        }

        // Trigger a resolution recalculation via the resize timer
        // (don't call OnSurfaceContainerSizeChanged directly - it needs a real SizeChangedEventArgs)
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>
    /// Returns the currently active aspect ratio mode for the embedded RDP
    /// session, normalised through <see cref="ParseAspectRatio"/>. Used by the
    /// tab context menu to show a checkmark next to the active sub-item under
    /// "Match Window".
    /// </summary>
    internal AspectRatio GetCurrentAspectRatio()
        => ParseAspectRatio(_server?.RdpAspectRatio);

    private void OnResolutionButtonClick(object sender, RoutedEventArgs e)
    {
        // Update checkmarks to reflect current resolution
        var currentTag = _manualResolutionWidth > 0
            ? $"{_manualResolutionWidth}x{_manualResolutionHeight}"
            : "Fit";

        foreach (var menuItem in ResolutionMenu.Items.OfType<MenuItem>())
        {
            menuItem.IsChecked = menuItem.Tag is string tag && tag == currentTag;
        }

        UpdateResolutionMenuHeader();

        ResolutionMenu.PlacementTarget = ResolutionButton;
        ResolutionMenu.IsOpen = true;
    }

    /// <summary>
    /// Live effective resolution mode + dimensions for this session, derived
    /// from the persisted profile mode and any per-session manual override.
    /// Exposed to <c>SessionTabContextMenuFactory</c> so the right-click
    /// resolution menu can mirror the toolbar mode header.
    /// </summary>
    internal RdpEffectiveResolutionState GetEffectiveResolutionState()
    {
        var profileMode = _server?.RdpResolutionMode ?? RdpResolutionMode.Auto;
        var profileWidth = _server?.RdpFixedWidth ?? 0;
        var profileHeight = _server?.RdpFixedHeight ?? 0;
        return RdpResolutionModeIndicator.Resolve(
            profileMode,
            _manualResolutionWidth,
            _manualResolutionHeight,
            profileWidth,
            profileHeight);
    }

    private void UpdateResolutionMenuHeader()
    {
        if (ResMenuModeHeaderText is null)
        {
            return;
        }

        var state = GetEffectiveResolutionState();
        var activeModeLabel = L("RdpResolutionActiveModeLabel");
        var modeLabel = L(RdpResolutionModeIndicator.GetModeLocalizationKey(state.Mode));
        ResMenuModeHeaderText.Text = RdpResolutionModeIndicator.FormatHeader(
            activeModeLabel,
            modeLabel,
            state.Width,
            state.Height,
            L(LocaleKeys.ResolutionHeaderFormat),
            L(LocaleKeys.ResolutionHeaderWithSizeFormat));
    }

    private void OnSkipStabilizationClick(object sender, RoutedEventArgs e)
    {
        RequestSkipStabilization();
    }

    /// <summary>
    /// Populates the resolution context menu from AppSettings.RdpResolutionPresets,
    /// with a built-in fallback when the setting is missing or empty.
    /// Items 0-5 are static (mode header, separator, skip-stab, skip-stab-sep, fit, separator);
    /// presets are appended starting at index 6.
    /// </summary>
    private void PopulateResolutionMenu()
    {
        const int StaticItemCount = 6;

        while (ResolutionMenu.Items.Count > StaticItemCount)
        {
            ResolutionMenu.Items.RemoveAt(StaticItemCount);
        }

        foreach (var preset in ResolutionPresetCatalog.GetPresets(_settings))
        {
            var item = new MenuItem
            {
                Header = preset.DisplayText,
                Tag = preset.Tag
            };
            item.Click += OnResolutionMenuClick;
            ResolutionMenu.Items.Add(item);
        }

        UpdateResolutionMenuHeader();
    }

    private void OnAntiIdleBadgeClick(object sender, RoutedEventArgs e)
    {
        if (_antiIdleTimer is null)
        {
            return;
        }

        Core.Logging.FileLogger.Info(
            "EmbeddedRDP user disabled anti-idle for the current session");
        StopAntiIdleTimer();
    }

    private async void OnResolutionMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
            {
                return;
            }

            if (tag == "Fit")
            {
                await ApplyResolutionChoiceAsync(ResolutionChoice.MatchWindow);
            }
            else if (ResolutionPresetCatalog.TryParse(tag, out ResolutionPreset preset))
            {
                await ApplyResolutionChoiceAsync(ResolutionChoice.Fixed(preset.Width, preset.Height));
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[EmbeddedRdpView] OnResolutionMenuClick: {ex.Message}");
        }
    }

    public async Task ApplyResolutionChoiceAsync(ResolutionChoice choice)
    {
        if (_disposed)
        {
            return;
        }

        switch (choice.Kind)
        {
            case ResolutionChoiceKind.MatchWindow:
                _manualResolutionWidth = 0;
                _manualResolutionHeight = 0;
                UpdateResolutionButtonState();

                // Stated, not merely turned on: SmartSizing lives on the native control and
                // survives a resolution change, so leaving a branch silent lets the previous
                // mode leak into this one.
                _rdpHost?.SetSmartSizing(RdpSmartSizingPolicy.ShouldEnable(
                    ResolutionChoiceKind.MatchWindow,
                    resolutionExceedsSurface: false));

                Core.Logging.FileLogger.Info("RDP resolution set to: Fit to Window");
                break;

            case ResolutionChoiceKind.Fixed:
                if (choice.Width <= 0 || choice.Height <= 0)
                {
                    return;
                }

                _manualResolutionWidth = choice.Width;
                _manualResolutionHeight = choice.Height;
                UpdateResolutionButtonState();

                bool exceedsSurface = IsResolutionLargerThanSurface(choice.Width, choice.Height);
                _rdpHost?.SetSmartSizing(RdpSmartSizingPolicy.ShouldEnable(
                    ResolutionChoiceKind.Fixed,
                    exceedsSurface));

                if (exceedsSurface)
                {
                    ShowTransientToast(_localizer?["RdpResolutionScaledToFitToast"]
                        ?? "RdpResolutionScaledToFitToast");
                }

                Core.Logging.FileLogger.Info($"RDP resolution set to: {choice.Width}x{choice.Height}");
                break;
        }

        ApplyHostLayout();

        if (_rdpHost?.IsConnected == true)
        {
            await ApplyCurrentResolutionAsync("manual-resolution", force: true);
        }
    }

    public void SetSmartSizing(bool enabled)
    {
        _rdpHost?.SetSmartSizing(enabled);
    }

    public bool WouldScaleResolution(int width, int height)
        => IsResolutionLargerThanSurface(width, height);

    public ResolutionChoice GetCurrentResolutionChoice()
        => _manualResolutionWidth > 0 && _manualResolutionHeight > 0
            ? ResolutionChoice.Fixed(_manualResolutionWidth, _manualResolutionHeight)
            : ResolutionChoice.MatchWindow;

    private void UpdateResolutionButtonState()
    {
        var state = GetEffectiveResolutionState();
        var modeLabel = L(RdpResolutionModeIndicator.GetModeLocalizationKey(state.Mode));

        ResolutionButton.Content = RdpResolutionModeIndicator.GetGlyph(state.Mode);
        ResolutionButton.ToolTip = RdpResolutionModeIndicator.FormatTooltip(
            L("RdpTooltipResolutionWithMode"),
            L("RdpTooltipResolutionWithModeAndSize"),
            modeLabel,
            state.Width,
            state.Height);

        var hasManualOverride = _manualResolutionWidth > 0 && _manualResolutionHeight > 0;
        var brushKey = hasManualOverride ? "AccentBrush" : "TextPrimaryBrush";
        if (TryFindResource(brushKey) is System.Windows.Media.Brush brush)
        {
            ResolutionButton.Foreground = brush;
        }

        UpdateResolutionMenuHeader();
    }

    /// <summary>Resolves a locale key, falling back to the key name if no localizer is set.</summary>
    private string L(string key) => _localizer?[key] ?? key;

    /// <summary>
    /// Writes the session status line and tells UI Automation the live region changed.
    /// </summary>
    /// <remarks>
    /// The LiveSetting on the element is only a property; without the event no screen reader ever
    /// learns that the text moved. Routing every write through here is what keeps that true of new
    /// write sites too.
    /// </remarks>
    private void SetStatusText(string text)
    {
        StatusTextBlock.Text = text;
        _ = RdpLiveRegion.Announce(StatusTextBlock);
    }

    private void RequestSkipStabilization()
    {
        if (_disposed || _stabilizationCts is null || _stabilizationCts.IsCancellationRequested)
        {
            return;
        }

        _stabilizationCts.Cancel();
        ShowTransientToast(_localizer?["RdpStabilizationSkippedToast"] ?? string.Empty);
    }

    private void StartStabilizationCountdown(TimeSpan delay)
    {
        StopStabilizationCountdown();

        if (_localizer is null || delay <= TimeSpan.Zero)
        {
            return;
        }

        _stabilizationDeadlineUtc = DateTime.UtcNow + delay;
        _stabilizationTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnStabilizationTimerTick,
            Dispatcher);
        _stabilizationTimer.Start();
        UpdateStabilizationDisplay();
        ResMenuSkipStabilization.Visibility = System.Windows.Visibility.Visible;
        ResMenuSkipStabilizationSeparator.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnStabilizationTimerTick(object? sender, EventArgs e)
    {
        UpdateStabilizationDisplay();
    }

    private void UpdateStabilizationDisplay()
    {
        var localizer = _localizer;
        if (localizer is null)
        {
            StopStabilizationCountdown();
            return;
        }

        var remaining = _stabilizationDeadlineUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            StopStabilizationCountdown();
            return;
        }

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        StabilizingStatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            localizer["RdpStabilizingStatus"],
            seconds);
        StabilizingSeparator.Visibility = System.Windows.Visibility.Visible;
        StabilizingStatusText.Visibility = System.Windows.Visibility.Visible;
    }

    private void StopStabilizationCountdown()
    {
        if (_stabilizationTimer is not null)
        {
            _stabilizationTimer.Stop();
            _stabilizationTimer.Tick -= OnStabilizationTimerTick;
            _stabilizationTimer = null;
        }

        StabilizingSeparator.Visibility = System.Windows.Visibility.Collapsed;
        StabilizingStatusText.Visibility = System.Windows.Visibility.Collapsed;
        ResMenuSkipStabilization.Visibility = System.Windows.Visibility.Collapsed;
        ResMenuSkipStabilizationSeparator.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void StartReconnectElapsedTracking()
    {
        if (_localizer is null)
        {
            StopReconnectElapsedTracking();
            return;
        }

        if (!_reconnectStartUtc.HasValue)
        {
            _reconnectStartUtc = DateTime.UtcNow;
            _reconnectElapsedTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(1),
                DispatcherPriority.Background,
                OnReconnectElapsedTick,
                Dispatcher);
            _reconnectElapsedTimer.Start();
        }

        UpdateReconnectStatusSegments();
    }

    private void OnReconnectElapsedTick(object? sender, EventArgs e)
    {
        UpdateReconnectStatusSegments();
    }

    private void UpdateReconnectStatusSegments()
    {
        var localizer = _localizer;
        if (localizer is null || _reconnectStartUtc is null)
        {
            StopReconnectElapsedTracking();
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var elapsed = nowUtc - _reconnectStartUtc.Value;
        var seconds = Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds));
        ReconnectElapsedText.Text = string.Format(
            CultureInfo.CurrentCulture,
            localizer["RdpReconnectElapsedFormat"],
            seconds);
        ReconnectElapsedSeparator.Visibility = Visibility.Visible;
        ReconnectElapsedText.Visibility = Visibility.Visible;

        var nextRetrySeconds = ReconnectEtaCalculator.EstimateSeconds(
            _reconnectAttemptTimestampsUtc,
            nowUtc);
        if (nextRetrySeconds is null)
        {
            NextRetrySeparator.Visibility = Visibility.Collapsed;
            NextRetryText.Visibility = Visibility.Collapsed;
            return;
        }

        NextRetryText.Text = string.Format(
            CultureInfo.CurrentCulture,
            localizer["RdpReconnectNextRetryFormat"],
            nextRetrySeconds.Value);
        NextRetrySeparator.Visibility = Visibility.Visible;
        NextRetryText.Visibility = Visibility.Visible;
    }

    private void StopReconnectElapsedTracking()
    {
        if (_reconnectElapsedTimer is not null)
        {
            _reconnectElapsedTimer.Stop();
            _reconnectElapsedTimer.Tick -= OnReconnectElapsedTick;
            _reconnectElapsedTimer = null;
        }

        _reconnectStartUtc = null;
        _reconnectAttemptTimestampsUtc.Clear();
        NextRetrySeparator.Visibility = Visibility.Collapsed;
        NextRetryText.Visibility = Visibility.Collapsed;
        ReconnectElapsedSeparator.Visibility = Visibility.Collapsed;
        ReconnectElapsedText.Visibility = Visibility.Collapsed;
    }

    private void RecordReconnectAttemptTimestamp(DateTime timestampUtc)
    {
        if (_reconnectAttemptTimestampsUtc.Count == MaxReconnectAttemptTimestamps)
        {
            _reconnectAttemptTimestampsUtc.RemoveAt(0);
        }

        _reconnectAttemptTimestampsUtc.Add(timestampUtc);
    }

    private void ShowReconnectOverlay()
    {
        var diagnostic = _ownerPane?.FailureDetails
                         ?? _sessionTab?.PrimaryPane?.FailureDetails;

        var hasDiagnosticMessage = diagnostic is not null
            && !string.IsNullOrWhiteSpace(diagnostic.MessageKey);

        string primary;
        if (hasDiagnosticMessage)
        {
            var template = L(diagnostic!.MessageKey);
            var formatArgument = ResolveDiagnosticFormatArgument(diagnostic);

            if (template.Contains("{0}", StringComparison.Ordinal)
                && formatArgument is not null)
            {
                try
                {
                    primary = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        template,
                        formatArgument);
                }
                catch (FormatException ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"[EmbeddedRdpView] Format failed for key '{diagnostic.MessageKey}': {ex.Message}");
                    primary = template;
                }
            }
            else
            {
                primary = template;
            }
        }
        else
        {
            primary = L("RdpDisconnectedMessage");
        }

        RdpActiveXHost.RdpDisconnectSeverity severity =
            ResolveOverlaySeverity(diagnostic, _lastExtendedDisconnectReason);
        var prefixKey = severity switch
        {
            RdpActiveXHost.RdpDisconnectSeverity.Transient => "RdpDisconnectSeverityPrefixNotice",
            RdpActiveXHost.RdpDisconnectSeverity.AuthIssue => "RdpDisconnectSeverityPrefixWarning",
            RdpActiveXHost.RdpDisconnectSeverity.TerminalError => "RdpDisconnectSeverityPrefixError",
            _ => null
        };

        if (prefixKey is not null && _localizer is not null)
        {
            var prefix = _localizer[prefixKey];
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                primary = _localizer.Format(
                    "RdpDisconnectMessagePrefixFormat",
                    prefix,
                    primary);
            }
        }

        ReconnectMessageText.Text = primary;
        _ = RdpLiveRegion.Announce(ReconnectMessageText);

        var hasSpecificPrimary = hasDiagnosticMessage
            && !string.Equals(diagnostic!.MessageKey, "RdpDisconnectedMessage", StringComparison.Ordinal);

        if (hasSpecificPrimary)
        {
            ReconnectSecondaryText.Text = L("RdpDisconnectedMessage");
            ReconnectSecondaryText.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            ReconnectSecondaryText.Text = string.Empty;
            ReconnectSecondaryText.Visibility = System.Windows.Visibility.Collapsed;
        }

        int? disconnectCode = null;
        if (diagnostic?.Code is int code)
        {
            disconnectCode = IsFatalErrorDiagnostic(diagnostic) ? null : code;
            ReconnectCodeText.Text = FormatOverlayCode(diagnostic, _lastExtendedDisconnectReason);
            ReconnectCodeText.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            ReconnectCodeText.Text = string.Empty;
            ReconnectCodeText.Visibility = System.Windows.Visibility.Collapsed;
        }

        ApplyOverlaySeverity(severity);
        OverlayCopyErrorButton.Visibility = System.Windows.Visibility.Visible;
        var primaryAction = RdpDisconnectActionPolicy.ResolvePrimaryAction(disconnectCode);
        OverlayEditProfileButton.Visibility = RdpDisconnectActionPolicy.ShouldOfferEditProfile(disconnectCode)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        ApplyReconnectOverlayPrimaryAction(primaryAction);

        // WindowsFormsHost is backed by a child HWND and otherwise paints over
        // WPF overlays due to airspace rules. Once the RDP session is gone, hide
        // the native host so the reconnect diagnostics are actually visible.
        FormsHost.Visibility = System.Windows.Visibility.Collapsed;
        ReconnectOverlay.Visibility = System.Windows.Visibility.Visible;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || ReconnectOverlay.Visibility != System.Windows.Visibility.Visible)
            {
                return;
            }

            UIElement target;
            if (primaryAction == RdpOverlayPrimaryAction.EditProfile
                && OverlayEditProfileButton.IsVisible)
            {
                target = OverlayEditProfileButton;
            }
            else if (OverlayReconnectButton.IsVisible)
            {
                target = OverlayReconnectButton;
            }
            else
            {
                target = OverlayCloseButton;
            }

            _ = target.Focus();
            _ = Keyboard.Focus(target);
        }));
    }

    internal static object? ResolveDiagnosticFormatArgument(SessionDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (!string.IsNullOrEmpty(diagnostic.Detail))
        {
            return diagnostic.Detail;
        }

        return diagnostic.Code is int diagnosticCode
            ? diagnosticCode
            : null;
    }

    private void ApplyReconnectOverlayPrimaryAction(RdpOverlayPrimaryAction primaryAction)
    {
        if (primaryAction == RdpOverlayPrimaryAction.EditProfile)
        {
            ApplyOverlayButtonStyle(OverlayEditProfileButton, "PrimaryButtonStyle");
            ApplyOverlayButtonStyle(OverlayReconnectButton, "SecondaryButtonStyle");
            OverlayEditProfileButton.TabIndex = 0;
            OverlayReconnectButton.TabIndex = 1;
            OverlayCopyErrorButton.TabIndex = 2;
            OverlayCloseButton.TabIndex = 3;
            return;
        }

        ApplyOverlayButtonStyle(OverlayReconnectButton, "PrimaryButtonStyle");
        ApplyOverlayButtonStyle(OverlayEditProfileButton, "SecondaryButtonStyle");
        OverlayReconnectButton.TabIndex = 0;
        OverlayCopyErrorButton.TabIndex = 1;
        OverlayEditProfileButton.TabIndex = 2;
        OverlayCloseButton.TabIndex = 3;
    }

    private void ApplyOverlayButtonStyle(Button button, string resourceKey)
    {
        if (TryFindResource(resourceKey) is Style)
        {
            button.SetResourceReference(FrameworkElement.StyleProperty, resourceKey);
        }
    }

    private void OnReconnectOverlayPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnOverlayCloseClick(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter
            && Keyboard.FocusedElement is Button focusedButton
            && IsWithinReconnectOverlay(focusedButton))
        {
            focusedButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                focusedButton));
            e.Handled = true;
        }
    }

    private bool IsWithinReconnectOverlay(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ReconnectOverlay))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatOverlayCode(
        Core.SessionDiagnostics.SessionDiagnostic diagnostic,
        int extendedReason)
    {
        if (diagnostic.Code is not int code)
        {
            return string.Empty;
        }

        // A fatal error carries no extended reason: it does not come from a disconnect.
        return IsFatalErrorDiagnostic(diagnostic)
            ? $"RDP_FATAL_ERROR \u00B7 {code}"
            : RdpActiveXHost.FormatDisconnectCode(code, extendedReason);
    }

    private static bool IsFatalErrorDiagnostic(Core.SessionDiagnostics.SessionDiagnostic diagnostic)
    {
        return string.Equals(diagnostic.MessageKey, "RdpStatusFatalErrorDetail", StringComparison.Ordinal);
    }

    internal static RdpActiveXHost.RdpDisconnectSeverity ResolveOverlaySeverity(
        Core.SessionDiagnostics.SessionDiagnostic? diagnostic,
        int extendedReason)
    {
        if (diagnostic?.MessageKey == "RdpStatusFatalErrorDetail")
        {
            return RdpActiveXHost.RdpDisconnectSeverity.TerminalError;
        }

        // Heimdall's own connect watchdog and the stack's ConnectionTimeout (264) describe the
        // same user-visible event, so they must be painted the same way. This diagnostic carries
        // no code on purpose - 264 was never reported by the stack here - so the message key is
        // what identifies it, next to the fatal-error case above.
        if (diagnostic?.MessageKey == RdpHostDiagnosticFactory.ConnectTimeoutMessageKey)
        {
            return RdpActiveXHost.RdpDisconnectSeverity.Transient;
        }

        return diagnostic?.Code is int code
            ? RdpActiveXHost.GetDisconnectSeverity(code, extendedReason)
            : RdpActiveXHost.RdpDisconnectSeverity.TerminalError;
    }

    private void ApplyOverlaySeverity(RdpActiveXHost.RdpDisconnectSeverity severity)
    {
        var (brushKey, glyph) = ResolveSeverityVisual(severity);
        OverlaySeverityStrip.SetResourceReference(Border.BackgroundProperty, brushKey);
        OverlaySeverityIcon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        OverlaySeverityIcon.Text = glyph;
    }

    private static (string BrushKey, string Glyph) ResolveSeverityVisual(
        RdpActiveXHost.RdpDisconnectSeverity severity)
    {
        return severity switch
        {
            RdpActiveXHost.RdpDisconnectSeverity.Transient => ("InfoBrush", "\uE7BA"),
            RdpActiveXHost.RdpDisconnectSeverity.AuthIssue => ("WarningBrush", "\uE192"),
            _ => ("ErrorBrush", "\uE783")
        };
    }

    private void OnOverlayReconnectClick(object sender, RoutedEventArgs e)
    {
        ReconnectOverlay.Visibility = System.Windows.Visibility.Collapsed;
        ReconnectRequested?.Invoke();
    }

    private void OnOverlayCopyErrorClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var messageLines = new List<string>();
        AddClipboardLine(messageLines, ReconnectMessageText);
        if (ReconnectSecondaryText.Visibility == System.Windows.Visibility.Visible)
        {
            AddClipboardLine(messageLines, ReconnectSecondaryText);
        }

        if (ReconnectCodeText.Visibility == System.Windows.Visibility.Visible)
        {
            AddClipboardLine(messageLines, ReconnectCodeText);
        }

        if (messageLines.Count == 0)
        {
            return;
        }

        bool copied = RdpClipboardCopy.TryCopy(
            report => Clipboard.SetDataObject(report, copy: true),
            BuildReconnectErrorReport(messageLines),
            ex => Core.Logging.FileLogger.Warn(
                $"[EmbeddedRdpView] Copy reconnect overlay error failed: {ex.Message}"));

        ShowTransientToast(L(copied
            ? LocaleKeys.CopyErrorToast
            : LocaleKeys.CopyErrorFailedToast));
    }

    private static void AddClipboardLine(ICollection<string> lines, TextBlock textBlock)
    {
        if (!string.IsNullOrWhiteSpace(textBlock.Text))
        {
            lines.Add(textBlock.Text.Trim());
        }
    }

    private string BuildReconnectErrorReport(IReadOnlyCollection<string> messageLines)
    {
        if (_localizer is null)
        {
            return string.Join(Environment.NewLine, messageLines);
        }

        var builder = new StringBuilder();
        builder.AppendLine(_localizer["RdpCopyErrorHeader"]);
        AppendReportLine(
            builder,
            _localizer["RdpCopyErrorTimeLabel"],
            DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));

        var server = BuildCopyErrorServerValue();
        if (!string.IsNullOrWhiteSpace(server))
        {
            AppendReportLine(builder, _localizer["RdpCopyErrorServerLabel"], server);
        }

        if (_tunnelPort is int tunnelPort)
        {
            AppendReportLine(
                builder,
                _localizer["RdpCopyErrorTunnelLabel"],
                _localizer.Format("RdpCopyErrorTunnelValueFormat", tunnelPort));
        }

        if (_connectedAtUtc != default)
        {
            AppendReportLine(
                builder,
                _localizer["RdpCopyErrorSessionLabel"],
                _localizer.Format(
                    "RdpCopyErrorSessionDurationFormat",
                    FormatSessionDuration(DateTime.UtcNow - _connectedAtUtc)));
        }

        AppendReportLine(
            builder,
            _localizer["RdpCopyErrorAppLabel"],
            BuildCopyErrorAppValue());
        builder.AppendLine();

        foreach (var line in messageLines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendReportLine(StringBuilder builder, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(label).Append(' ').AppendLine(value);
    }

    private string BuildCopyErrorServerValue()
    {
        if (_server is null)
        {
            return string.Empty;
        }

        var endpoint = string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1}",
            _server.RemoteServer,
            _server.RemotePort);
        return string.IsNullOrWhiteSpace(_server.DisplayName)
            ? endpoint
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} ({1})",
                _server.DisplayName,
                endpoint);
    }

    private static string BuildCopyErrorAppValue()
    {
        var assembly = typeof(EmbeddedRdpView).Assembly;
        var appName = assembly.GetName().Name ?? nameof(EmbeddedRdpView);
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? string.Empty;

        return string.IsNullOrWhiteSpace(version)
            ? appName
            : string.Format(CultureInfo.InvariantCulture, "{0} v{1}", appName, version);
    }

    private static string FormatSessionDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}m {1:00}s",
            (int)duration.TotalMinutes,
            duration.Seconds);
    }

    private void OnOverlayEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || string.IsNullOrWhiteSpace(_server?.Id))
        {
            return;
        }

        // The editor is a modal dialog over this same tab, so the overlay behind it costs
        // nothing and is still there when the dialog closes. Collapsing it left the pane empty
        // for good: the native host was already collapsed, the only writer that shows the overlay
        // again is a fresh callback on a session that is already dead, and this is the
        // pre-focused default for six disconnect codes.
        EditServerRequested?.Invoke(_server.Id);
    }

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e)
    {
        ReconnectOverlay.Visibility = System.Windows.Visibility.Collapsed;
        Core.Logging.FileLogger.Info("EmbeddedRDP Close requested via overlay");
        CloseRequested?.Invoke();
    }

    private (int Width, int Height) GetDisplayDimensions()
    {
        if (_server is null)
        {
            return (1024, 768);
        }

        // Manual resolution override - use exact dimensions if set
        if (_manualResolutionWidth > 0 && _manualResolutionHeight > 0)
        {
            return (SnapRdpWidth(_manualResolutionWidth), _manualResolutionHeight);
        }

        double logicalWidth = Math.Max(SurfaceContainer.ActualWidth, 2);
        double logicalHeight = Math.Max(SurfaceContainer.ActualHeight, 2);

        if (logicalWidth <= 2 || logicalHeight <= 2)
        {
            return (1024, 768);
        }

        // Convert WPF logical pixels (DIPs) to physical pixels for the ActiveX control.
        // On a 150% DPI display, WPF reports 2238 DIPs but the control needs 3357 physical pixels.
        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        int physicalWidth = SnapRdpWidth((int)Math.Round(logicalWidth * dpiScaleX));
        int physicalHeight = (int)Math.Round(logicalHeight * dpiScaleY);

        var (width, height) = AspectRatioManager.Calculate(
            physicalWidth,
            physicalHeight,
            ParseAspectRatio(_server.RdpAspectRatio));

        return (SnapRdpWidth(width), height);
    }

    private RdpDisplayUpdateSettings GetDisplayUpdateSettings(int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(FormsHost);
        var dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        var dpiX = dpi.PixelsPerInchX > 0 ? dpi.PixelsPerInchX : 96.0;
        var dpiY = dpi.PixelsPerInchY > 0 ? dpi.PixelsPerInchY : 96.0;

        return new RdpDisplayUpdateSettings(
            RdpDisplayHelper.ComputePhysicalSizeMm(width, dpiX),
            RdpDisplayHelper.ComputePhysicalSizeMm(height, dpiY),
            RdpDisplayHelper.MapDpiToDesktopScaleFactor(dpiScaleX),
            RdpDisplayHelper.MapDpiToDeviceScaleFactor(dpiScaleX),
            dpiScaleX,
            dpiScaleY);
    }

    private bool IsResolutionLargerThanSurface(int width, int height)
    {
        var (surfaceWidth, surfaceHeight) = GetSurfacePhysicalDimensions();
        return width > surfaceWidth || height > surfaceHeight;
    }

    private (int Width, int Height) GetSurfacePhysicalDimensions()
    {
        double logicalWidth = Math.Max(SurfaceContainer.ActualWidth, 2);
        double logicalHeight = Math.Max(SurfaceContainer.ActualHeight, 2);
        var dpi = VisualTreeHelper.GetDpi(SurfaceContainer);

        return (
            SnapRdpWidth((int)Math.Round(logicalWidth * dpi.DpiScaleX)),
            (int)Math.Round(logicalHeight * dpi.DpiScaleY));
    }

    private static int SnapRdpWidth(int width)
    {
        var snapped = RdpDisplayHelper.SnapToMultipleOf(width, 4);
        return snapped > 0 ? snapped : 4;
    }

    private static bool IsProfileFixedResolution(ServerProfileDto server)
        => server.RdpResolutionMode == RdpResolutionMode.Fixed
            && server.RdpFixedWidth > 0
            && server.RdpFixedHeight > 0;

    private static bool ResolveInitialSmartSizing(ServerProfileDto? server)
        => server is null
            || !IsProfileFixedResolution(server)
            || server.RdpInitialSmartSizing;

    private bool IsVisualSurfaceReady()
    {
        return IsLoaded
            && IsVisible
            && FormsHost.IsVisible
            && SurfaceContainer.ActualWidth >= 64
            && SurfaceContainer.ActualHeight >= 64;
    }

    private void FlushLayoutPipeline(string stage)
    {
        Core.Logging.FileLogger.Info(
            $"EmbeddedRDP layout flush ({stage}): viewVisible={IsVisible} formsVisible={FormsHost.IsVisible} formsSize={FormsHost.ActualWidth:0.##}x{FormsHost.ActualHeight:0.##} surfaceSize={SurfaceContainer.ActualWidth:0.##}x{SurfaceContainer.ActualHeight:0.##}");

        UpdateLayout();
        SurfaceContainer.UpdateLayout();
        ApplyHostLayout();
        FormsHost.UpdateLayout();

        if (FormsHost.Child is WinForms.Control control)
        {
            if (!control.IsHandleCreated)
            {
                control.CreateControl();
            }

            control.PerformLayout();
            control.Refresh();
        }

        WinForms.Application.DoEvents();
        Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
    }

    private void StartAntiIdleTimer(int intervalSeconds)
    {
        StopAntiIdleTimer();

        _antiIdleTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(intervalSeconds),
            DispatcherPriority.Background,
            OnAntiIdleTick,
            Dispatcher);
        _antiIdleTimer.Start();
        Core.Logging.FileLogger.Info(
            $"RDP anti-idle timer started ({intervalSeconds}s interval)");
        AntiIdleBadge.Visibility = Visibility.Visible;
    }

    private void StopAntiIdleTimer()
    {
        if (_antiIdleTimer is null)
        {
            return;
        }

        _antiIdleTimer.Stop();
        _antiIdleTimer.Tick -= OnAntiIdleTick;
        _antiIdleTimer = null;
        Core.Logging.FileLogger.Info("RDP anti-idle timer stopped");

        if (!_disposed)
        {
            AntiIdleBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void StartConnectWatchdog()
    {
        StopConnectWatchdog();

        int configuredTimeoutMs = _settings?.RdpConnectWatchdogTimeoutMs
            ?? RdpConnectWatchdogPolicy.DefaultTimeoutMs;

        int timeoutMs;
        string stage;
        if (_watchdogCredentialWaitActive)
        {
            // Stage 2: the credential-autofill watcher is searching for the remote NLA prompt, so
            // the budget is extended past the autofill timeout and its graceful retry runs before
            // a hard watchdog teardown does.
            //
            // What this does NOT establish is that the RDP stack is reachable, which is what this
            // comment used to claim. The watcher starts on the statement after Connect(), before
            // a single byte has been exchanged, so for a black-holed port the promotion happens
            // anyway and the user's configured connect timeout is outlived. Promoting on evidence
            // instead - the watcher having actually seen a credential window - needs an
            // observation callback on CredentialAutofill.WaitAndFillAsync, which lives in
            // Heimdall.Rdp; until that exists the trigger stays where it is rather than being
            // moved to a signal that arrives after the credential wait is already over.
            int autofillTimeoutMs = _settings?.RdpCredentialAutofillTimeoutMs ?? 90000;
            timeoutMs = RdpConnectWatchdogPolicy.ResolveStageTwoTimeoutMs(configuredTimeoutMs, autofillTimeoutMs);
            stage = "two";
        }
        else
        {
            // Stage 1: short budget to fail fast on a dead tunnel / stalled pre-negotiation.
            timeoutMs = RdpConnectWatchdogPolicy.ResolveTimeoutMs(configuredTimeoutMs);
            stage = "one";
        }

        if (timeoutMs == RdpConnectWatchdogPolicy.DisabledTimeoutMs)
        {
            Core.Logging.FileLogger.Info("RDP connect watchdog disabled");
            return;
        }

        _connectWatchdogTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(timeoutMs),
            DispatcherPriority.Background,
            OnConnectWatchdogTick,
            Dispatcher);
        _connectWatchdogTimer.Start();
        Core.Logging.FileLogger.Info(
            $"RDP connect watchdog started phase={_connectionPhase} stage={stage} timeoutMs={timeoutMs}");
    }

    private void StopConnectWatchdog()
    {
        if (_connectWatchdogTimer is null)
        {
            return;
        }

        _connectWatchdogTimer.Stop();
        _connectWatchdogTimer.Tick -= OnConnectWatchdogTick;
        _connectWatchdogTimer = null;
        Core.Logging.FileLogger.Info("RDP connect watchdog stopped");
    }

    void IRdpConnectWatchdogTimer.Arm() => StartConnectWatchdog();

    void IRdpConnectWatchdogTimer.Cancel() => CancelConnectWatchdog();

    void IRdpConnectWatchdogTimer.Suspend() => SuspendConnectWatchdog();

    void IRdpConnectAttemptRunner.RunAttempt(int attempt) => RunConnectAttempt(attempt);

    void IRdpConnectAttemptRunner.DropAbandonedConnection()
    {
        try
        {
            _rdpHost?.Disconnect();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedRDP abandoned-connect disconnect failed: {ex.Message}");
        }
    }

    /// <summary>Stops the watchdog and forgets any credential-wait promotion.</summary>
    private void CancelConnectWatchdog()
    {
        _watchdogCredentialWaitActive = false;
        StopConnectWatchdog();
    }

    /// <summary>Stops the watchdog while the view waits on a human answer.</summary>
    /// <remarks>
    /// The credential-wait promotion is deliberately kept, unlike in
    /// <see cref="CancelConnectWatchdog"/>: a suspension is a pause on the same attempt, not
    /// its end.
    /// </remarks>
    private void SuspendConnectWatchdog()
    {
        if (_connectWatchdogTimer is not null)
        {
            Core.Logging.FileLogger.Info(
                "RDP connect watchdog suspended for the server certificate check");
        }

        StopConnectWatchdog();
    }

    /// <summary>
    /// Promotes the connect watchdog to Stage 2 once the credential-autofill watcher
    /// starts searching for the remote NLA prompt. Re-arms the watchdog with the
    /// longer credential-wait budget only when one is currently armed and the phase
    /// is still arming; otherwise it just records the credential-wait state.
    /// </summary>
    /// <remarks>
    /// The trigger is the watcher starting, not the watcher finding anything, so this fires for
    /// every profile that carries a saved password - including one whose target is unreachable.
    /// See the note in <see cref="StartConnectWatchdog"/>.
    /// </remarks>
    private void ArmStageTwoConnectWatchdog()
    {
        _watchdogCredentialWaitActive = true;

        if (_connectWatchdogTimer is null || !RdpConnectWatchdogPolicy.ShouldArm(_connectionPhase))
        {
            return;
        }

        StopConnectWatchdog();
        StartConnectWatchdog();
    }

    private void OnConnectWatchdogTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            StopConnectWatchdog();
            return;
        }

        if (!RdpConnectWatchdogPolicy.ShouldArm(_connectionPhase))
        {
            StopConnectWatchdog();
            return;
        }

        RdpConnectionPhase expiredPhase = _connectionPhase;
        Core.Logging.FileLogger.Warn(
            $"EmbeddedRDP connect watchdog expired phase={expiredPhase}");
        _watchdogCredentialWaitActive = false;
        StopConnectWatchdog();

        if (_rdpHost is not null)
        {
            _rdpHost.CancelAutoReconnect = true;
        }

        string timeoutMessage = L("RdpDisconnectConnectTimeout");
        SetConnectionStateError(timeoutMessage);
        CancelAutofill();
        _autofillRetryContext = null;
        UpdateAutofillState(RdpAutofillState.None);
        StopAntiIdleTimer();
        StopStabilizationCountdown();
        StopReconnectElapsedTracking();
        ReleaseSleepPrevention();
        TransitionPhase(RdpConnectionPhase.None);
        HideRedirectionIndicators();
        _allowResolutionUpdates = false;
        SetPaneDiagnostic(RdpHostDiagnosticFactory.FromConnectTimeout());
        UpdateSessionStatus(RdpSessionStatus.Error);
        ShowReconnectOverlay();

        // Abandon the attempt and abort the underlying COM connect so a late
        // OnConnected cannot resurrect this torn-down session, and so a surface retry still
        // pending against it cannot restart a connection behind the error above. The error UI
        // is already in place; OnRdpDisconnected preserves it for the abort path.
        _connectAttempts.WatchdogAborted();
        try
        {
            Core.Logging.FileLogger.Info("EmbeddedRDP watchdog aborting in-progress COM connect");
            _rdpHost?.Disconnect();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedRDP watchdog abort disconnect failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnAntiIdleTick(object? sender, EventArgs e)
    {
        if (_disposed || _rdpHost is null || !_rdpHost.IsConnected)
        {
            StopAntiIdleTimer();
            return;
        }

        try
        {
            // Send a Shift key press/release to the RDP ActiveX inner rendering window.
            // PostMessage places the input directly into the target window's message queue.
            // With allowBackgroundInput=1, the RDP control processes it and relays to the
            // remote server, resetting the server-side idle timer (GetLastInputInfo).
            // Shift key has no visible effect on the remote desktop.
            IntPtr hwnd = _rdpHost.HostHandle;
            if (hwnd != IntPtr.Zero)
            {
                // Drill to the deepest child window (the ActiveX rendering surface)
                IntPtr target = hwnd;
                IntPtr child = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, null, null);
                while (child != IntPtr.Zero)
                {
                    target = child;
                    child = NativeMethods.FindWindowEx(target, IntPtr.Zero, null, null);
                }

                NativeMethods.PostMessage(
                    target,
                    NativeMethods.WM_KEYDOWN,
                    new IntPtr(NativeMethods.VK_SHIFT),
                    IntPtr.Zero);
                NativeMethods.PostMessage(
                    target,
                    NativeMethods.WM_KEYUP,
                    new IntPtr(NativeMethods.VK_SHIFT),
                    IntPtr.Zero);
            }

            // Keeping the local machine awake is SleepPrevention's decision, not this timer's: it
            // is bound to the user's "prevent sleep during session" setting, and it holds the
            // display request already whenever that setting allows it. Writing the execution
            // state from here ran whether or not the user had allowed it, and - because
            // SetThreadExecutionState replaces the continuous flag set rather than merging into
            // it - withdrew the system-required request the service had put on this same thread.
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RDP anti-idle tick failed: {ex.Message}");
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

    private static string ResolveConnectHost(ServerProfileDto server)
    {
        return server.UseDirectConnection || string.IsNullOrWhiteSpace(server.SshGatewayId)
            ? server.RemoteServer
            : "127.0.0.1";
    }

    private int ResolveConnectPort(ServerProfileDto server)
    {
        if (server.UseDirectConnection || string.IsNullOrWhiteSpace(server.SshGatewayId))
        {
            return server.RemotePort;
        }

        // Use the dynamically allocated tunnel port, falling back to server.LocalPort
        return _tunnelPort ?? server.LocalPort;
    }

    private string BuildEndpointText(ServerProfileDto server)
    {
        if (server.UseDirectConnection || string.IsNullOrWhiteSpace(server.SshGatewayId))
        {
            return string.Format("{0}:{1}", server.RemoteServer, server.RemotePort);
        }

        var localPort = _tunnelPort ?? server.LocalPort;
        var format = _localizer?["RdpEndpointTunneledFormat"]
            ?? "RdpEndpointTunneledFormat";
        return string.Format(
            format,
            server.RemoteServer,
            server.RemotePort,
            localPort);
    }

    private static string? TryDecryptPassword(ServerProfileDto server)
    {
        if (string.IsNullOrWhiteSpace(server.RdpPasswordEncrypted))
        {
            return null;
        }

        return CredentialProtector.Unprotect(server.RdpPasswordEncrypted);
    }

    /// <summary>
    /// Suppresses the reconnect overlay for explicit user disconnects and clean-exit COM codes.
    /// </summary>
    internal static bool ShouldSuppressReconnectOverlay(bool userInitiated, int reason)
        => userInitiated || reason is 0 or 1 or 2;

    internal static bool ShouldHandleStateChange(
        string serverId,
        string? targetServerId,
        bool comDrivenStatusActive,
        bool disposed)
        => !disposed
            && !comDrivenStatusActive
            && !string.IsNullOrWhiteSpace(targetServerId)
            && string.Equals(serverId, targetServerId, StringComparison.Ordinal);

    private static AspectRatio ParseAspectRatio(string? aspectRatio)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
        {
            return AspectRatio.Stretch;
        }

        return aspectRatio.Trim() switch
        {
            "Preserve" => AspectRatio.Auto,
            "16:9" => AspectRatio.Ratio16x9,
            "4:3" => AspectRatio.Ratio4x3,
            "21:9" => AspectRatio.Ratio21x9,
            _ when Enum.TryParse<AspectRatio>(aspectRatio, true, out var parsed) => parsed,
            _ => AspectRatio.Stretch
        };
    }

    private Brush GetBrush(string resourceKey, Brush fallback)
    {
        return TryFindResource(resourceKey) as Brush ?? fallback;
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        internal const uint WM_KEYDOWN = 0x0100;
        internal const uint WM_KEYUP = 0x0101;
        internal const byte VK_SHIFT = 0x10;
        internal const byte VK_CONTROL = 0x11;
        internal const byte VK_MENU = 0x12;
        internal const byte VK_ESCAPE = 0x1B;
        internal const byte VK_DELETE = 0x2E;
        internal const byte VK_TAB = 0x09;
        internal const byte VK_SNAPSHOT = 0x2C;
        internal const byte VK_F11 = 0x7A;
        internal const byte VK_LWIN = 0x5B;
        internal const byte VK_D = 0x44;
        internal const byte VK_E = 0x45;
        internal const byte VK_L = 0x4C;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
    }
}
