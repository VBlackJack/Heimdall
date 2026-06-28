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

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Views;

/// <summary>
/// Manages a Citrix Workspace session with two modes:
/// 1. Embedded mode: captures the wfica32.exe window via SetParent and hosts it inline.
/// 2. External mode (fallback): monitors the process and provides Bring to Front / Terminate controls.
/// </summary>
public partial class EmbeddedCitrixView : UserControl, IDisposable
{
    private const int HealthCheckIntervalMs = 3000;
    private const int WindowCaptureMaxAttempts = 60;
    private const int WindowCapturePollIntervalMs = 500;

    // 360 * 500ms gives users roughly three minutes for MFA-backed sign-in.
    private const int AuthWatchMaxAttempts = 360;
    private const int AuthWatchShutdownTimeoutMs = 500;
    private const int WindowClassNameCapacity = 256;
    private const string WorkspaceShellProcessName = "SelfService";

    // Large enough to exclude tray and splash popups, small enough to tolerate DPI variance.
    private const int WorkspaceAuthMinAreaPx = 50_000;

    // Skips tiny helper windows while keeping seamless published-app windows eligible.
    private const int SessionMinAreaPx = 10_000;

    // Win32 window style flags
    private const int GwlStyle = -16;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickframe = 0x00040000;
    private const int SwShowNormal = 1;
    private const int SwRestore = 9;
    private const int SwMaximize = 3;

    private CitrixSessionResult? _session;
    private SessionTabViewModel? _sessionTab;
    private LocalizationManager? _localizer;
    private IDialogService? _dialogService;
    private DispatcherTimer? _healthTimer;
    private WinForms.Panel? _hostPanel;
    private IntPtr _capturedHwnd;
    private CancellationTokenSource? _authWatchCts;
    private Task? _authWatchTask;
    private bool _embedded;
    private bool _captureInProgress;
    private bool _disposed;

    // Title fallback for the event log when neither application name nor StoreFront URL is known.
    // Language-neutral token (the NDJSON event log carries no localized content).
    private const string EventTitleFallback = "Citrix";

    // Session-event state. Citrix has no view-level connect event and a binary 3s health poll, so
    // connect is emitted once from the real embed/fallback moment and disconnect is edge-detected
    // (the latch absorbs external mode's every-tick "dead" re-fire). Reconnect is a full recreate.
    private string _eventHost = string.Empty;
    private string _eventTitle = string.Empty;
    private DateTime _eventConnectedAtUtc;
    private bool _eventConnectEmitted;
    private bool _eventDisconnectEmitted;

    /// <summary>
    /// Shared sink for graphical-protocol connect/disconnect events. Injected by
    /// <c>EmbeddedSessionManager</c>, mirroring the RDP/VNC wiring.
    /// </summary>
    public ISessionEventLog? SessionEventLog { get; set; }

    /// <summary>
    /// Provider for the LIVE global session-logging toggle, read at each emit so the setting takes
    /// effect without a restart. The view never snapshots it.
    /// </summary>
    public Func<bool>? SessionLoggingEnabledProvider { get; set; }

    /// <summary>Per-profile session-logging override. Null means inherit the live global setting.</summary>
    public bool? SessionLoggingOverride { get; set; }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public EmbeddedCitrixView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the view with a Citrix session result and attempts to embed the window.
    /// Falls back to external mode if window capture fails.
    /// </summary>
    public void InitializeSession(
        CitrixSessionResult session,
        SessionTabViewModel sessionTab,
        string displayName,
        LocalizationManager? localizer = null,
        IDialogService? dialogService = null)
    {
        _session = session;
        _sessionTab = sessionTab;
        _localizer = localizer;
        _dialogService = dialogService;

        // Baseline event host/title; refined by SetConnectionInfo once StoreFront/app metadata is known.
        _eventHost = string.Empty;
        _eventTitle = string.IsNullOrWhiteSpace(displayName) ? EventTitleFallback : displayName;

        TerminateButton.Content = localizer?["BtnTerminateSession"] ?? "Terminate";
        BringToFrontButton.Content = localizer?["BtnBringToFront"] ?? "Bring to Front";
        CancelCaptureButton.Content = localizer?["CitrixCancelCapture"] ?? "Cancel";
        CloseTabButton.Content = localizer?["BtnClose"] ?? "Close";

        // Tooltips
        TerminateButton.ToolTip = localizer?["TooltipDisconnectSession"] ?? "Disconnect session";
        BringToFrontButton.ToolTip = localizer?["TooltipBringToFront"] ?? "Bring to front";
        CloseTabButton.ToolTip = localizer?["TooltipCloseSessionTab"] ?? "Close tab";

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(TerminateButton, localizer?["A11yDisconnectSession"] ?? "Disconnect session");
        System.Windows.Automation.AutomationProperties.SetName(BringToFrontButton, localizer?["A11yBringToFront"] ?? "Bring to front");
        System.Windows.Automation.AutomationProperties.SetName(CloseTabButton, localizer?["A11yCloseSessionTab"] ?? "Close session tab");

        SessionTitleText.Text = displayName;
        TitleText.Text = displayName;
        CaptureLoadingText.Text = localizer?["CitrixCaptureSearching"] ?? "Locating Citrix session window...";
        CaptureLoadingPanel.Visibility = Visibility.Visible;
        InfoPanel.Visibility = Visibility.Collapsed;
        EmbeddedContainer.Visibility = Visibility.Collapsed;
        UpdateStatus(true);

        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HealthCheckIntervalMs)
        };
        _healthTimer.Tick += OnHealthTimerTick;
        _healthTimer.Start();

        // Attempt to capture the Citrix window asynchronously
        _ = TryCaptureWindowAsync();
    }

    /// <summary>
    /// Populates the info panel with StoreFront URL and application name.
    /// </summary>
    public void SetConnectionInfo(
        string? storeFrontUrl,
        string? appName,
        CitrixLaunchMode mode = CitrixLaunchMode.Unknown)
    {
        // Capture the event host/title: host = StoreFront URL (the factory strips any user@; these
        // URLs can embed credentials), title = application name, falling back to the URL then to a
        // non-empty constant.
        _eventHost = storeFrontUrl ?? string.Empty;
        _eventTitle = !string.IsNullOrWhiteSpace(appName)
            ? appName
            : (!string.IsNullOrWhiteSpace(storeFrontUrl) ? storeFrontUrl : EventTitleFallback);

        StoreFrontText.Text = !string.IsNullOrWhiteSpace(storeFrontUrl)
            ? _localizer?.Format("CitrixStoreFrontLabel", storeFrontUrl) ?? $"StoreFront: {storeFrontUrl}"
            : string.Empty;

        AppNameText.Text = !string.IsNullOrWhiteSpace(appName)
            ? _localizer?.Format("CitrixAppNameLabel", appName) ?? $"Application: {appName}"
            : string.Empty;

        ActiveModeText.Text = mode switch
        {
            CitrixLaunchMode.SelfServiceCache => _localizer?["CitrixModeCacheLaunch"] ?? "Mode: SelfService cache",
            CitrixLaunchMode.IcaFile => _localizer?["CitrixModeIcaFile"] ?? "Mode: ICA file",
            CitrixLaunchMode.StoreFront => _localizer?["CitrixModeStoreFront"] ?? "Mode: StoreFront",
            _ => string.Empty
        };
    }

    // Citrix process names to scan (covers different Workspace versions)
    private static readonly string[] CitrixProcessNames =
        ["wfica32", "wfcrun32", "CDViewer", "Receiver", WorkspaceShellProcessName];

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Polls for new Citrix windows and reparents the first viable one.
    /// Searches multiple process names and uses EnumWindows for robust detection.
    /// Falls back to external mode after timeout.
    /// </summary>
    private async Task TryCaptureWindowAsync()
    {
        _captureInProgress = true;
        Core.Logging.FileLogger.Info("Citrix: attempting to capture Citrix session window...");

        // Snapshot visible top-level windows before our launch so we can detect the new
        // one. Tracking window handles (not just PIDs) also captures apps that open inside
        // an already-running ICA session (Citrix session sharing), where no new process
        // appears and a PID-only scan would never see the window.
        HashSet<IntPtr> preLaunchWindows = SnapshotVisibleWindows();
        Core.Logging.FileLogger.Info($"Citrix: {preLaunchWindows.Count} visible window(s) before launch");

        bool extendedMessageShown = false;
        for (int attempt = 1; attempt <= WindowCaptureMaxAttempts; attempt++)
        {
            if (_disposed || !_captureInProgress) return;

            await Task.Delay(WindowCapturePollIntervalMs);
            if (_disposed || !_captureInProgress) return;

            if (!extendedMessageShown && attempt * WindowCapturePollIntervalMs >= 10_000)
            {
                extendedMessageShown = true;
                await Dispatcher.InvokeAsync(() =>
                {
                    CaptureLoadingText.Text = _localizer?["CitrixCaptureSearchingExtended"]
                        ?? "Still searching... (this can take up to 30 seconds)";
                });
            }

            IntPtr bestHwnd = FindNewSessionWindow(preLaunchWindows);
            if (bestHwnd != IntPtr.Zero)
            {
                _captureInProgress = false;
                Core.Logging.FileLogger.Info(
                    $"Citrix: capturing session window hwnd=0x{bestHwnd.ToInt64():X}");
                await Dispatcher.InvokeAsync(() =>
                {
                    CaptureLoadingPanel.Visibility = Visibility.Collapsed;
                    EmbedWindow(bestHwnd);
                });
                return;
            }
        }

        // Timeout — no seamless session window appeared. This commonly means Citrix
        // Workspace needs the user to (re)authenticate before the app can launch.
        // Try to embed Workspace's own sign-in window inline (Option 2b spike) so the
        // user authenticates without leaving Heimdall; Citrix keeps ownership of the
        // token. Fall back to external mode if no auth window is present.
        _captureInProgress = false;

        // Only treat this as a sign-in situation when Workspace's own window is actually
        // in the foreground — Citrix surfaces its login front-and-centre. A bare timeout
        // otherwise just means the app opened into a shared session or as an unseen
        // seamless window, so fall through to external mode instead of grabbing the shell.
        IntPtr authHwnd = TryFindCitrixAuthWindow();
        if (authHwnd != IntPtr.Zero && authHwnd == GetForegroundWindow())
        {
            Core.Logging.FileLogger.Info(
                $"Citrix: Workspace sign-in window is foreground; embedding it inline hwnd=0x{authHwnd.ToInt64():X}");
            await Dispatcher.InvokeAsync(() =>
            {
                CaptureLoadingText.Text = _localizer?["CitrixAuthSignInHint"] ?? "Sign in to Citrix…";
                EmbedWindow(authHwnd);
            });

            // After the user signs in, Citrix launches the published app. Swap the embedded
            // auth window for the real session window when it appears.
            StartAuthWatch(preLaunchWindows, authHwnd);
            return;
        }

        Core.Logging.FileLogger.Info("Citrix: window capture timed out after 30s, using external mode");
        await Dispatcher.InvokeAsync(() =>
        {
            ShowExternalFallback();
        });
    }

    private void StartAuthWatch(HashSet<IntPtr> preLaunchWindows, IntPtr authHwnd)
    {
        StopAuthWatch();

        CancellationTokenSource authWatchCts = new();
        _authWatchCts = authWatchCts;
        _authWatchTask = WatchForSessionAfterAuthAsync(preLaunchWindows, authHwnd, authWatchCts.Token)
            .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Core.Logging.FileLogger.Error(
                            "Citrix: auth-watch task faulted: " + t.Exception?.GetBaseException());
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    private void StopAuthWatch()
    {
        CancellationTokenSource? authWatchCts = _authWatchCts;
        Task? authWatchTask = _authWatchTask;
        _authWatchCts = null;
        _authWatchTask = null;

        if (authWatchCts is null)
        {
            return;
        }

        try
        {
            authWatchCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // Dispose is synchronous; wait asynchronously so UI teardown is not blocked.
        _ = AwaitAuthWatchShutdownAsync(authWatchTask, authWatchCts);
    }

    private static async Task AwaitAuthWatchShutdownAsync(
        Task? authWatchTask,
        CancellationTokenSource authWatchCts)
    {
        try
        {
            if (authWatchTask is not null)
            {
                Task shutdownTimeoutTask = Task.Delay(AuthWatchShutdownTimeoutMs);
                await Task.WhenAny(authWatchTask, shutdownTimeoutTask).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Citrix: auth-watch shutdown wait failed: {ex.Message}");
        }
        finally
        {
            authWatchCts.Dispose();
        }
    }

    /// <summary>
    /// Locates Citrix Workspace's own sign-in window so it can be embedded inline.
    /// The Workspace UI is a top-level WPF window (class "HwndWrapper[SelfService;main;...]")
    /// that hosts the federated login in an embedded WebView2; reparenting the top-level
    /// window carries the WebView2 child windows with it. Returns IntPtr.Zero if not found.
    /// </summary>
    private static IntPtr TryFindCitrixAuthWindow()
    {
        HashSet<int> selfServicePids = new();
        foreach (Process process in Process.GetProcessesByName(WorkspaceShellProcessName))
        {
            selfServicePids.Add(process.Id);
        }

        if (selfServicePids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        int bestArea = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!selfServicePids.Contains((int)pid)) return true;

            StringBuilder className = new(WindowClassNameCapacity);
            GetClassName(hwnd, className, WindowClassNameCapacity);
            if (!className.ToString().StartsWith("HwndWrapper[SelfService;main", StringComparison.OrdinalIgnoreCase))
                return true;

            GetWindowRect(hwnd, out RECT rect);
            int area = (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area < WorkspaceAuthMinAreaPx) return true;

            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    /// <summary>
    /// Scans for a visible published-app session window owned by a Citrix process that
    /// did not exist at launch time. Excludes the Workspace UI / sign-in window
    /// (class "HwndWrapper[SelfService;...]"), which is never the app session.
    /// Returns IntPtr.Zero if no session window is present yet.
    /// </summary>
    private static IntPtr FindNewSessionWindow(HashSet<IntPtr> preLaunchWindows)
    {
        // The session window must belong to a live Citrix process.
        HashSet<int> citrixPids = new();
        foreach (string name in CitrixProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (!process.HasExited) citrixPids.Add(process.Id);
            }
        }

        if (citrixPids.Count == 0) return IntPtr.Zero;

        IntPtr bestHwnd = IntPtr.Zero;
        int bestArea = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            // Only windows that appeared after our launch — covers both a fresh ICA
            // process and a new window opened inside an already-running (shared) session.
            if (preLaunchWindows.Contains(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!citrixPids.Contains((int)pid)) return true;

            StringBuilder className = new(WindowClassNameCapacity);
            GetClassName(hwnd, className, WindowClassNameCapacity);
            string cName = className.ToString();

            // The Workspace shell / sign-in window is not a published-app session.
            if (cName.StartsWith("HwndWrapper[SelfService", StringComparison.OrdinalIgnoreCase))
                return true;

            // Accept Citrix session windows by class name (seamless windows have no title),
            // and any titled window as a fallback.
            bool isCitrixClass = cName.StartsWith("Transparent Windows Client", StringComparison.OrdinalIgnoreCase)
                || cName.StartsWith("CtxSeamless", StringComparison.OrdinalIgnoreCase)
                || cName.Contains("CDViewer", StringComparison.OrdinalIgnoreCase)
                || cName.StartsWith("TUIWindowClass", StringComparison.OrdinalIgnoreCase)
                || cName.StartsWith("IHWindow", StringComparison.OrdinalIgnoreCase);

            if (!isCitrixClass && GetWindowTextLength(hwnd) == 0)
                return true; // Skip unknown windows without titles

            GetWindowRect(hwnd, out RECT rect);
            int area = (rect.Right - rect.Left) * (rect.Bottom - rect.Top);

            // Skip tiny windows (toolbars, tray icons, etc.)
            if (area < SessionMinAreaPx) return true;

            if (area > bestArea)
            {
                bestArea = area;
                bestHwnd = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestHwnd;
    }

    /// <summary>Snapshots the handles of all currently-visible top-level windows.</summary>
    private static HashSet<IntPtr> SnapshotVisibleWindows()
    {
        HashSet<IntPtr> set = new();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd)) set.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    /// <summary>
    /// After the Workspace sign-in window has been embedded, waits for the user to
    /// authenticate and for Citrix to launch the published app, then swaps the embedded
    /// auth window for the real session window. Leaves the auth window in place if no
    /// session appears within the timeout.
    /// </summary>
    private async Task WatchForSessionAfterAuthAsync(
        HashSet<IntPtr> preLaunchWindows,
        IntPtr authHwnd,
        CancellationToken cancellationToken = default)
    {
        // Allow generous time for interactive (possibly MFA) sign-in plus app launch.
        for (int attempt = 1; attempt <= AuthWatchMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return;
            await Task.Delay(WindowCapturePollIntervalMs, cancellationToken);
            if (_disposed) return;

            IntPtr sessionHwnd = FindNewSessionWindow(preLaunchWindows);
            if (sessionHwnd == IntPtr.Zero || sessionHwnd == authHwnd) continue;
            if (!IsWindow(sessionHwnd)) continue;

            Core.Logging.FileLogger.Info(
                $"Citrix: post-auth session window hwnd=0x{sessionHwnd.ToInt64():X} appeared; swapping out auth window");
            await Dispatcher.InvokeAsync(() =>
            {
                // Hand the sign-in window back to Citrix, then embed the app session.
                ReleaseEmbeddedWindow();
                EmbedWindow(sessionHwnd);
            });
            return;
        }

        Core.Logging.FileLogger.Info(
            "Citrix: no session window appeared after auth within timeout; leaving auth window embedded");
    }

    private void OnCancelCaptureClick(object sender, RoutedEventArgs e)
    {
        if (!_captureInProgress) return;

        Core.Logging.FileLogger.Info("Citrix: user cancelled window capture, falling back to external mode");
        _captureInProgress = false;
        ShowExternalFallback();
    }

    private void ShowExternalFallback()
    {
        CaptureLoadingPanel.Visibility = Visibility.Collapsed;
        EmbeddedContainer.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;
        BringToFrontButton.Visibility = Visibility.Visible;
        StatusTextBlock.Text = _localizer?["CitrixStatusExternal"] ?? "External window";

        // External mode is a valid "connected" outcome (the session runs in its own window). This is
        // also reached from EmbedWindow's failure path; EmitConnect is idempotent, so a prior embed
        // success is not double-counted.
        EmitConnect();
    }

    /// <summary>
    /// Reparents the given window handle into the embedded WinForms panel.
    /// </summary>
    private void EmbedWindow(IntPtr hwnd)
    {
        try
        {
            // Create a host panel for the captured window
            _hostPanel = new WinForms.Panel { Dock = WinForms.DockStyle.Fill };
            _hostPanel.Resize += (_, _) => ResizeCapturedWindow();
            FormsHost.Child = _hostPanel;

            // Strip popup/caption styles and make it a child window
            var style = GetWindowLong(hwnd, GwlStyle);
            style &= ~(WsPopup | WsCaption | WsThickframe);
            style |= WsChild;
            SetWindowLong(hwnd, GwlStyle, style);

            // Reparent into our panel
            SetParent(hwnd, _hostPanel.Handle);
            _capturedHwnd = hwnd;
            _embedded = true;
            EmitConnect();

            // Show embedded container, hide info panel
            CaptureLoadingPanel.Visibility = Visibility.Collapsed;
            EmbeddedContainer.Visibility = Visibility.Visible;
            InfoPanel.Visibility = Visibility.Collapsed;
            BringToFrontButton.Visibility = Visibility.Collapsed;

            // Resize to fill
            ResizeCapturedWindow();
            ShowWindow(hwnd, SwMaximize);

            Core.Logging.FileLogger.Info($"Citrix: window embedded successfully (hwnd=0x{hwnd.ToInt64():X})");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Citrix: SetParent failed: {ex.Message}");
            // Fall back to external mode
            _embedded = false;
            ShowExternalFallback();
        }
    }

    /// <summary>
    /// Resizes the captured window to fill the host panel.
    /// </summary>
    private void ResizeCapturedWindow()
    {
        if (_capturedHwnd == IntPtr.Zero || _hostPanel is null) return;

        try
        {
            MoveWindow(_capturedHwnd, 0, 0,
                _hostPanel.ClientSize.Width,
                _hostPanel.ClientSize.Height,
                true);
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedCitrixView] resize: {ex.Message}"); }
    }

    private void OnBringToFrontClick(object sender, RoutedEventArgs e)
    {
        if (_session?.Process is null || _session.Process.HasExited) return;

        try
        {
            var hWnd = _session.Process.MainWindowHandle;
            if (hWnd == IntPtr.Zero) return;

            if (IsIconic(hWnd))
                ShowWindow(hWnd, SwRestore);

            SetForegroundWindow(hWnd);
        }
        catch (InvalidOperationException ex) { Core.Logging.FileLogger.Warn($"[EmbeddedCitrixView] bring to front: {ex.Message}"); }
    }

    /// <summary>
    /// Raised when the user asks the shared pane lifecycle to close this session tab.
    /// </summary>
    public event Action? CloseRequested;

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("[EmbeddedCitrixView] close requested by user");
        CloseRequested?.Invoke();
    }

    private async void OnTerminateClick(object sender, RoutedEventArgs e)
    {
        if (_session?.Process is null || _session.Process.HasExited) return;

        if (_dialogService is not null && _localizer is not null)
        {
            var confirmed = await _dialogService.ShowConfirmAsync(
                _localizer["CitrixConfirmTerminateTitle"],
                _localizer["CitrixConfirmTerminateMessage"],
                "warning");

            if (!confirmed || _disposed) return;
        }

        ReleaseEmbeddedWindow();

        EmitDisconnect("user");

        if (_session?.Process is not null && !_session.Process.HasExited)
        {
            try { _session.Process.Kill(); }
            catch (InvalidOperationException ex) { Core.Logging.FileLogger.Warn($"[EmbeddedCitrixView] terminate process: {ex.Message}"); }
        }

        UpdateStatus(false);
    }

    // Live gate at the seam: the sink is a dumb writer, so the view decides per-emit against the
    // LIVE global toggle and the Citrix event eligibility (the policy stays the single source of truth).
    private bool ShouldLogSessionEvents()
    {
        bool globalEnabled = SessionLoggingEnabledProvider?.Invoke() ?? false;
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(SessionLoggingOverride, globalEnabled);
        return SessionEventLog is not null && SessionEventGatePolicy.ShouldLog(enabled, "CITRIX");
    }

    // Emits the Connected event once, from the first real "connected" moment (window embed or
    // external-mode fallback), never from the optimistic InitializeSession status or the handler.
    private void EmitConnect()
    {
        if (!ShouldLogSessionEvents() || _eventConnectEmitted)
        {
            return;
        }

        _eventConnectedAtUtc = DateTime.UtcNow;
        _eventConnectEmitted = true;
        _eventDisconnectEmitted = false;

        try
        {
            SessionEventLog!.LogEvent(CitrixSessionEventFactory.BuildConnected(_eventHost, _eventTitle));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedCitrix session-event connect emit failed: {ex.Message}");
        }
    }

    // Single idempotent disconnect funnel. The latch gives edge semantics: external mode's every-3s
    // "dead" re-fire collapses to a single Disconnected, and a session that never connected emits none.
    private void EmitDisconnect(string endTrigger)
    {
        if (!ShouldLogSessionEvents() || !_eventConnectEmitted || _eventDisconnectEmitted)
        {
            return;
        }

        _eventDisconnectEmitted = true;

        try
        {
            long? durationMs = GraphicalSessionEventHelpers.ResolveDurationMs(_eventConnectedAtUtc, DateTime.UtcNow);
            SessionEventLog!.LogEvent(
                CitrixSessionEventFactory.BuildDisconnected(_eventHost, _eventTitle, durationMs, endTrigger));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedCitrix session-event disconnect emit failed: {ex.Message}");
        }
    }

    private void OnHealthTimerTick(object? sender, EventArgs e)
    {
        if (_embedded && _capturedHwnd != IntPtr.Zero)
        {
            // In embedded mode — only check if the captured window still exists
            if (!IsWindow(_capturedHwnd))
            {
                EmitDisconnect("remote");
                _embedded = false;
                _capturedHwnd = IntPtr.Zero;
                EmbeddedContainer.Visibility = Visibility.Collapsed;
                InfoPanel.Visibility = Visibility.Visible;
                UpdateStatus(false);
            }

            return;
        }

        if (_captureInProgress)
        {
            // Still searching for a window to capture — don't declare dead.
            // SelfService.exe exits immediately; that's expected.
            return;
        }

        // External mode (capture timed out) — monitor the last known process
        bool alive = _session?.Process is not null && !_session.Process.HasExited;
        if (!alive)
        {
            // The latch collapses this every-3s "dead" re-fire to a single Disconnected.
            EmitDisconnect("remote");
        }

        UpdateStatus(alive);
    }

    private void UpdateStatus(bool isRunning)
    {
        if (isRunning)
        {
            HealthDot.Fill = Application.Current.TryFindResource("SuccessBrush") as Brush
                             ?? Brushes.LimeGreen;
            StatusTextBlock.Text = _embedded
                ? (_localizer?["CitrixStatusEmbedded"] ?? "Embedded")
                : (_localizer?["CitrixStatusConnected"] ?? "Connected");
            SessionInfoText.Text = _session?.Process?.Id > 0
                ? $"PID: {_session.Process.Id}"
                : string.Empty;
            TerminateButton.IsEnabled = true;
            BringToFrontButton.IsEnabled = !_embedded;
            CloseTabButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            HealthDot.Fill = Application.Current.TryFindResource("ErrorBrush") as Brush
                             ?? Brushes.Red;
            StatusTextBlock.Text = _localizer?["CitrixStatusDisconnected"] ?? "Disconnected";
            SessionInfoText.Text = string.Empty;
            BringToFrontButton.IsEnabled = false;
            TerminateButton.IsEnabled = false;
            CloseTabButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Releases the captured window back to the desktop before disposing.
    /// </summary>
    private void ReleaseEmbeddedWindow()
    {
        if (_capturedHwnd != IntPtr.Zero && IsWindow(_capturedHwnd))
        {
            try
            {
                // Restore original window style
                var style = GetWindowLong(_capturedHwnd, GwlStyle);
                style &= ~WsChild;
                style |= WsPopup | WsCaption | WsThickframe;
                SetWindowLong(_capturedHwnd, GwlStyle, style);

                // Reparent back to desktop
                SetParent(_capturedHwnd, IntPtr.Zero);
                ShowWindow(_capturedHwnd, SwShowNormal);
            }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedCitrixView] window release: {ex.Message}"); }
        }

        _capturedHwnd = IntPtr.Zero;
        _embedded = false;
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Backstop: emit a teardown disconnect before any teardown, but only if no health-poll or
        // user signal already did. The event sink is an app singleton, not disposed per-view, so
        // enqueuing here is safe. "teardown" can overstate duration if the remote died earlier and
        // the user left a dead tab open; endTrigger makes that explicit in the log.
        EmitDisconnect("teardown");

        _disposed = true;
        StopAuthWatch();

        if (_healthTimer is not null)
        {
            _healthTimer.Stop();
            _healthTimer.Tick -= OnHealthTimerTick;
            _healthTimer = null;
        }

        ReleaseEmbeddedWindow();

        try { FormsHost.Child = null; }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EmbeddedCitrixView] FormsHost cleanup: {ex.Message}"); }
        _hostPanel?.Dispose();

        if (_session?.Process is not null && !_session.Process.HasExited)
        {
            try { _session.Process.Kill(); }
            catch (InvalidOperationException ex) { Core.Logging.FileLogger.Warn($"[EmbeddedCitrixView] dispose process kill: {ex.Message}"); }
        }

        _session?.Process?.Dispose();
    }
}
