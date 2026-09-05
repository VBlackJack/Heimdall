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

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Rdp.Display;
using Heimdall.Sftp;
using Heimdall.Ssh;
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Services;

/// <summary>
/// Creates visual hosts for connection sessions so the shell can render
/// embedded protocol surfaces without teaching the ViewModel layer about WPF.
/// </summary>
public sealed class EmbeddedSessionManager : IEmbeddedSessionManager, IDisposable
{
    internal const int DefaultRdpResizeEnableDelayMs = AppSettings.DefaultRdpResizeEnableDelayMs;
    private static readonly ConditionalWeakTable<SessionTabViewModel, PendingReconnectState>
        PendingReconnectStates = new ConditionalWeakTable<SessionTabViewModel, PendingReconnectState>();

    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly HostKeyStore _hostKeyStore;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly ToolRegistry _toolRegistry;
    private readonly ITunnelService _tunnelService;
    private readonly ISessionLogService _sessionLogService;
    private readonly ISessionEventLog _sessionEventLog;

    /// <summary>
    /// One pool for the life of the application, so a session that follows another can
    /// inherit its RDP control. Creating a control that ever connects costs a measured 66
    /// kernel handles the operating system never returns; reusing one costs about 3.
    /// </summary>
    /// <remarks>
    /// Its capacity and idle expiry are read from the live settings on every release and
    /// trim, so the settings screen applies without a restart; a manager built without a
    /// configuration source runs on the pool's own defaults. Disposed with the manager, which
    /// the application does before its first exit await, so the idle controls do not outlive
    /// the sessions they served.
    /// </remarks>
    private readonly PooledRdpHostProvider _rdpHostProvider;
    private bool _disposed;
    private readonly ISessionOperationLog _sessionOperationLog;
    private readonly ConfigManager _configManager;
    private ConfigGatewayInventory? _gatewayInventory;

    /// <summary>
    /// Optional callback invoked when a terminal view broadcasts input.
    /// Parameters: (byte[] data, object? senderView).
    /// Wired by MainViewModel to relay keystrokes to all other terminals.
    /// </summary>
    public Action<byte[], object?>? BroadcastCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded view's Split button is clicked.
    /// Parameters: (SessionTabViewModel session).
    /// Wired by MainWindow code-behind to show the split picker context menu.
    /// </summary>
    public Action<SessionTabViewModel>? SplitRequestedCallback { get; set; }

    /// <summary>
    /// Func that returns the current broadcast mode state.
    /// Wired by MainViewModel so newly created views show the badge immediately.
    /// </summary>
    public Func<bool>? IsBroadcastActive { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded session view requests reconnection.
    /// Parameters: (SessionTabViewModel session, string serverId, string connectionType).
    /// Wired by MainViewModel to restart the connection using the original server.
    /// </summary>
    public Action<SessionTabViewModel, string, string>? ReconnectRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded split-pane view requests reconnection.
    /// Parameters: (SessionTabViewModel session, SessionPaneModel pane).
    /// Wired by MainViewModel to reconnect only the owning pane.
    /// </summary>
    public Action<SessionTabViewModel, SessionPaneModel>? ReconnectPaneRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded view requests user-driven disconnect.
    /// Parameters: (SessionTabViewModel session, SessionPaneModel pane, DisconnectReason reason).
    /// Wired by MainViewModel to close the owning pane or tab through the shared lifecycle path.
    /// </summary>
    public Action<SessionTabViewModel, SessionPaneModel, DisconnectReason>? DisconnectRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded RDP view requests server profile editing.
    /// Parameters: (string serverId).
    /// Wired by MainViewModel to open the existing server edit flow.
    /// </summary>
    public Action<string>? EditServerRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded view's disconnect overlay
    /// requests the tab itself be closed (the user clicked "Close" rather than
    /// "Reconnect" or "Dismiss"). Parameters: (SessionTabViewModel session).
    /// Wired by <c>SessionCoordinator</c> to call
    /// <c>ConnectionViewModel.CloseSessionAsync</c>.
    /// </summary>
    public Action<SessionTabViewModel>? CloseRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback for cross-tool navigation. Allows tool views to open other tools.
    /// Parameters: (string toolId, string title, ToolContext? context).
    /// Wired by MainViewModel to delegate to <c>OpenToolTabAsync</c>.
    /// </summary>
    public Func<string, string, ToolContext?, Task>? OpenToolCallback { get; set; }

    public EmbeddedSessionManager(
        LocalizationManager localizer,
        IDialogService dialogService,
        HostKeyStore hostKeyStore,
        ConnectionStateMachine connectionSm,
        ToolRegistry toolRegistry,
        ITunnelService tunnelService,
        ISessionLogService sessionLogService,
        ISessionEventLog sessionEventLog,
        ISessionOperationLog sessionOperationLog,
        ConfigManager configManager)
    {
        _localizer = localizer;
        _dialogService = dialogService;
        _hostKeyStore = hostKeyStore;
        _connectionSm = connectionSm;
        _toolRegistry = toolRegistry;
        _tunnelService = tunnelService;
        _sessionLogService = sessionLogService;
        _sessionEventLog = sessionEventLog;
        _sessionOperationLog = sessionOperationLog;
        _configManager = configManager;
        _rdpHostProvider = new PooledRdpHostProvider(
            () => PooledRdpHostProvider.ResolveCapacity(_configManager?.CurrentSettings),
            () => PooledRdpHostProvider.ResolveIdleExpiry(_configManager?.CurrentSettings));
    }

    /// <summary>The pool the RDP session views draw their controls from. Exposed for diagnostics and tests.</summary>
    internal PooledRdpHostProvider RdpHostProvider => _rdpHostProvider;

    /// <summary>Releases the idle RDP controls. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rdpHostProvider.Dispose();
    }

    /// <summary>
    /// Builds the RDP session view and hands it the shared host pool.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreateHostControl"/>, and internal, so the provider a session
    /// ends up holding can be observed without acquiring an ActiveX control. The view's own
    /// default provider is the transient one, which builds and destroys a control per session at
    /// a measured 66 kernel handles each; losing the assignment below would revert pooling with
    /// nothing to notice it.
    /// </remarks>
    internal EmbeddedRdpView CreateRdpView()
    {
        return new EmbeddedRdpView
        {
            HostProvider = _rdpHostProvider,
            SessionEventLog = _sessionEventLog,
            // Read the LIVE global toggle at the seam (ConfigManager.CurrentSettings is replaced
            // on Save/Merge/Load), so enabling session logging takes effect without a restart.
            SessionLoggingEnabledProvider = () =>
                _configManager.CurrentSettings?.SessionLoggingEnabled ?? false
        };
    }

    public object CreateHostControl(
        SessionTabViewModel sessionTab,
        string displayName,
        string connectionType,
        ISessionResult session,
        AppSettings? settings = null,
        string? initialRemotePath = null)
    {
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentNullException.ThrowIfNull(session);

        var antiIdleInterval = settings?.AntiIdleIntervalSeconds ?? 60;
        var sshKeepAliveInterval = settings?.SshTmoutResetIntervalSeconds ?? AppSettings.DefaultSshTmoutResetIntervalSeconds;

        if (string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase) &&
            session is RdpSessionResult rdp)
        {
            var view = CreateRdpView();
            var rdpSettings = settings ?? new AppSettings();
            var (runtimeServer, multimonFallbackStatusKey) = ResolveEmbeddedRdpRuntimeServer(rdp.Server);
            view.SessionLoggingOverride = runtimeServer.SessionLoggingOverride;

            // The route the tunnel carrying this connection was dialled through, composed at the
            // dial and kept apart from the materialisation snapshot below. Only the certificate
            // question reads it, and only to name the gateway the certificate actually arrived
            // through: rdpSettings is a later clone and carries any gateway edited while the
            // tunnel was still being established.
            view.GatewayRoute = rdp.GatewayRoute;
            var globalResizeDelay = settings?.RdpResizeEnableDelayMs ?? DefaultRdpResizeEnableDelayMs;
            if (globalResizeDelay < 0)
            {
                Core.Logging.FileLogger.Warn(
                    $"EmbeddedSessionManager.RdpResizeEnableDelayMs invalid global value={globalResizeDelay}; fallback={DefaultRdpResizeEnableDelayMs}");
            }

            var resizeDelay = ResolveRdpResizeEnableDelayMs(runtimeServer.RdpResizeEnableDelayMs, globalResizeDelay);
            view.InitializeSession(
                runtimeServer,
                sessionTab,
                rdpSettings,
                antiIdleInterval,
                _localizer,
                rdp.TunnelPort,
                resizeDelay,
                _connectionSm,
                multimonFallbackStatusKey,
                _tunnelService.GetRecentForwardedPortFailure);
            WireSplitRequested(view, sessionTab);
            view.ReconnectRequested += () =>
                ReconnectRequestedCallback?.Invoke(
                    sessionTab,
                    sessionTab.ProfileLookupServerId,
                    sessionTab.ConnectionType);
            view.DisconnectRequested += () =>
                DisconnectRequestedCallback?.Invoke(
                    sessionTab,
                    view.OwningPane ?? sessionTab.PrimaryPane,
                    DisconnectReason.UserAction);
            view.EditServerRequested += serverId => EditServerRequestedCallback?.Invoke(serverId);
            view.CloseRequested += () => CloseRequestedCallback?.Invoke(sessionTab);
            return view;
        }

        if (string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase) &&
            session is SshSessionResult sshResult)
        {
            // Legacy SSH materialization path. The normal SSH pipeline now mounts
            // the view earlier via CreateConnectingSshHostControl and attaches here
            // only as a defensive fallback if SessionStarting was bypassed.
            return CreateSshView(
                sessionTab,
                sshResult.Session,
                displayName,
                sshKeepAliveInterval,
                settings,
                sshResult.SessionLoggingOverride);
        }

        if (string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase) &&
            session is TerminalSessionResult termResult)
        {
            return CreateTerminalSshView(
                sessionTab,
                termResult.Session,
                displayName,
                sshKeepAliveInterval,
                settings,
                endpoint: termResult.Endpoint,
                sessionLoggingOverride: termResult.SessionLoggingOverride);
        }

        if (string.Equals(connectionType, "LOCAL", StringComparison.OrdinalIgnoreCase) &&
            session is LocalShellBundle localBundle)
        {
            // External elevated window: no embedded terminal, show info panel
            if (localBundle.IsExternal)
            {
                var infoPanel = new System.Windows.Controls.TextBlock
                {
                    Text = _localizer?["LocalShellExternalElevated"] ?? "Elevated shell launched in external window.",
                    FontSize = 14,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Foreground = GetBrush("TextSecondaryBrush", System.Windows.Media.Brushes.Gray),
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    TextAlignment = System.Windows.TextAlignment.Center
                };
                return infoPanel;
            }

            var termView = CreateTerminalSshView(
                sessionTab,
                localBundle.Session!,
                displayName,
                0,
                settings,
                localBundle.IsElevated,
                sessionLoggingOverride: localBundle.SessionLoggingOverride);

            // Auto-attach local file browser panel in a vertical split
            var fileBrowser = new Views.LocalFileBrowserView(
                localBundle.WorkingDirectory, _localizer, settings?.ExternalEditorPath);

            fileBrowser.NavigateToPathRequested += (path) =>
            {
                var cdCommand = FormatCdCommand(localBundle.ShellExecutable, path);
                localBundle.Session!.Write(System.Text.Encoding.UTF8.GetBytes(cdCommand));
            };

            fileBrowser.RunInShellRequested += (path) =>
            {
                var command = FormatRunCommand(localBundle.ShellExecutable, path);
                localBundle.Session!.Write(System.Text.Encoding.UTF8.GetBytes(command));
            };

            // Edit in embedded editor: swap file browser with AvalonEdit editor
            var isEditInEditorHandlerAttached = false;
            Action<string> editInEditorRequestedHandler = OnEditInEditorRequested;

            fileBrowser.Loaded += OnFileBrowserLoaded;
            fileBrowser.Unloaded += OnFileBrowserUnloaded;
            AttachEditInEditorHandler();

            async void OnEditInEditorRequested(string path)
            {
                var editorView = new Views.EmbeddedEditorView();
                await editorView.OpenFile(path);

                // When editor closes, restore the file browser
                editorView.Unloaded += OnEditorUnloaded;
                editorView.CloseRequested += OnEditorCloseRequested;

                void OnEditorCloseRequested()
                {
                    DetachEditorCloseRequestedHandler();

                    var browserPane = Heimdall.Core.Models.SplitTreeHelper.FindPaneByHostControl(
                        sessionTab.RootContent, editorView);
                    if (browserPane is not null)
                    {
                        browserPane.HostControl = fileBrowser;
                    }

                    fileBrowser.RefreshCurrentDirectory();
                }

                void OnEditorUnloaded(object? sender, RoutedEventArgs e)
                {
                    DetachEditorCloseRequestedHandler();
                }

                void DetachEditorCloseRequestedHandler()
                {
                    editorView.Unloaded -= OnEditorUnloaded;
                    // Detach to prevent handler leak identified by audit-2026-04-22 (PERF-01).
                    editorView.CloseRequested -= OnEditorCloseRequested;
                }

                var editorPane = Heimdall.Core.Models.SplitTreeHelper.FindPaneByHostControl(
                    sessionTab.RootContent, fileBrowser);
                if (editorPane is not null)
                {
                    editorPane.HostControl = editorView;
                }
            }

            void OnFileBrowserLoaded(object? sender, RoutedEventArgs e)
            {
                AttachEditInEditorHandler();
            }

            void OnFileBrowserUnloaded(object? sender, RoutedEventArgs e)
            {
                DetachEditInEditorHandler();
            }

            void AttachEditInEditorHandler()
            {
                if (isEditInEditorHandlerAttached)
                {
                    return;
                }

                fileBrowser.EditInEditorRequested += editInEditorRequestedHandler;
                isEditInEditorHandlerAttached = true;
            }

            void DetachEditInEditorHandler()
            {
                if (!isEditInEditorHandlerAttached)
                {
                    return;
                }

                // Detach to prevent handler leak identified by audit-2026-04-22 (PERF-01).
                fileBrowser.EditInEditorRequested -= editInEditorRequestedHandler;
                isEditInEditorHandlerAttached = false;
            }

            // Wrap the terminal view's pane with a file browser in a vertical split
            var fileBrowserPane = new Heimdall.Core.Models.SessionPaneModel
            {
                ConnectionType = "LOCAL",
                Title = displayName,
                Status = "Connected"
            };
            fileBrowserPane.HostControl = fileBrowser;

            var currentRoot = sessionTab.RootContent;
            sessionTab.RootContent = new Heimdall.Core.Models.SplitContainerModel
            {
                First = currentRoot,
                Second = fileBrowserPane,
                Orientation = Heimdall.Core.Models.SplitOrientation.Vertical,
                SplitRatio = 0.5
            };

            return termView;
        }

        if (string.Equals(connectionType, "SFTP", StringComparison.OrdinalIgnoreCase) &&
            session is SftpSessionBundle bundle)
        {
            return CreateSftpView(
                sessionTab,
                bundle.Browser,
                displayName,
                bundle.SshParams,
                initialRemotePath,
                settings,
                bundle.SessionLoggingOverride);
        }

        if (string.Equals(connectionType, "FTP", StringComparison.OrdinalIgnoreCase) &&
            session is FtpSessionBundle ftpBundle)
        {
            return CreateSftpView(
                sessionTab,
                ftpBundle.Browser,
                displayName,
                null,
                initialRemotePath,
                settings,
                ftpBundle.SessionLoggingOverride);
        }

        if (string.Equals(connectionType, "CITRIX", StringComparison.OrdinalIgnoreCase)
            && session is CitrixSessionResult citrix)
        {
            var view = new EmbeddedCitrixView
            {
                SessionEventLog = _sessionEventLog,
                // Read the LIVE global toggle at the seam (mirrors the RDP/VNC wiring), so enabling
                // session logging takes effect without a restart.
                SessionLoggingEnabledProvider = () =>
                    _configManager.CurrentSettings?.SessionLoggingEnabled ?? false
            };
            view.SessionLoggingOverride = citrix.SessionLoggingOverride;
            view.InitializeSession(citrix, sessionTab, displayName, _localizer, _dialogService);
            view.SetConnectionInfo(citrix.StoreFrontUrl, citrix.AppName, citrix.Mode);
            view.CloseRequested += () => CloseRequestedCallback?.Invoke(sessionTab);
            return view;
        }

        if (string.Equals(connectionType, "VNC", StringComparison.OrdinalIgnoreCase)
            && session is VncSessionResult vnc)
        {
            var view = new EmbeddedVncView
            {
                SessionEventLog = _sessionEventLog,
                // Read the LIVE global toggle at the seam (mirrors the RDP wiring), so enabling
                // session logging takes effect without a restart.
                SessionLoggingEnabledProvider = () =>
                    _configManager.CurrentSettings?.SessionLoggingEnabled ?? false
            };
            view.SessionLoggingOverride = vnc.SessionLoggingOverride;
            view.SessionConnected += (serverId) =>
            {
                _connectionSm.TryTransition(serverId, ConnectionState.Connected);
                sessionTab.Status = SessionStatusTokens.Connected;
                Core.Logging.FileLogger.Info($"VNC connected: {serverId}");
            };
            view.SessionError += (serverId, errorMsg) =>
            {
                var localizedMsg = _localizer.Format("ErrorVncConnectionFailed", errorMsg);
                _connectionSm.SetError(serverId, localizedMsg);

                // Free-form on purpose, as on the other failure paths: the pane shows the reason,
                // and the display converter passes it through unchanged.
                sessionTab.Status = localizedMsg;
                Core.Logging.FileLogger.Error($"VNC error for {serverId}: {errorMsg}");
            };
            WireVncSplitRequested(view, sessionTab);
            WireVncReconnectRequested(view, sessionTab);
            _ = view.InitializeSessionAsync(vnc, sessionTab, displayName, _localizer)
                .ContinueWith(t =>
                {
                    if (t.Exception is not null)
                    {
                        Core.Logging.FileLogger.Error(
                            $"VNC init failed for {sessionTab.ServerId}: {t.Exception.GetBaseException()}");
                    }
                },
                    TaskContinuationOptions.OnlyOnFaulted);
            return view;
        }

        if (string.Equals(connectionType, "TELNET", StringComparison.OrdinalIgnoreCase)
            && session is TerminalSessionResult telnetResult)
        {
            return CreateTerminalSshView(
                sessionTab,
                telnetResult.Session,
                displayName,
                0,
                settings,
                endpoint: telnetResult.Endpoint,
                sessionLoggingOverride: telnetResult.SessionLoggingOverride);
        }

        if (string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase)
            && session is TerminalSessionResult winRmResult)
        {
            return CreateTerminalSshView(
                sessionTab,
                winRmResult.Session,
                displayName,
                0,
                settings,
                endpoint: winRmResult.Endpoint,
                connectedStatus: "RemoteSessionHandedOff",
                sessionLoggingOverride: winRmResult.SessionLoggingOverride,
                trackWinRmLifecycle: true);
        }

        return new DisposablePlaceholderView(displayName, connectionType, session);
    }

    internal static int ResolveRdpResizeEnableDelayMs(int? profileValue, int globalValue)
    {
        if (profileValue.HasValue)
        {
            return Math.Max(0, profileValue.Value);
        }

        return globalValue >= 0 ? globalValue : DefaultRdpResizeEnableDelayMs;
    }

    private static (ServerProfileDto Server, string? StatusKey) ResolveEmbeddedRdpRuntimeServer(
        ServerProfileDto server)
        => ResolveEmbeddedRdpRuntimeServer(server, GetRdpHostCapabilities());

    /// <summary>
    /// Decides whether the requested multi-monitor layout can be honoured, and returns the profile
    /// the session should actually run with.
    /// </summary>
    /// <param name="server">The configured profile. Never mutated.</param>
    /// <param name="host">
    /// What the host reports about its monitors. Injected so the decision can be exercised without
    /// the machine the tests happen to run on deciding the outcome.
    /// </param>
    /// <returns>
    /// The original profile when no fallback is needed, so the common path allocates nothing, or an
    /// independent copy carrying the coerced display settings.
    /// </returns>
    internal static (ServerProfileDto Server, string? StatusKey) ResolveEmbeddedRdpRuntimeServer(
        ServerProfileDto server,
        RdpDisplayCapabilities host)
    {
        var requested = new RdpDisplaySettings(
            server.RdpResolutionMode,
            UseMultimon: server.RdpResolutionMode == RdpResolutionMode.Multimon,
            SelectedMonitorIndices: server.RdpSelectedMonitorIndices);
        var validation = RdpDisplayResolver.ValidateMultimon(host, requested);

        if (!validation.ShouldFallback)
        {
            return (server, null);
        }

        Core.Logging.FileLogger.Warn(
            "EmbeddedSessionManager.RdpMultimonFallback "
            + $"reason={validation.Reason} requestedMode={requested.ResolutionMode} requestedUseMultimon={requested.UseMultimon} "
            + $"selectedMonitors={FormatMonitorIndices(requested.SelectedMonitorIndices)} monitorCount={host.MonitorCount} "
            + $"monitorGeometry={FormatMonitorGeometry(host.MonitorBounds)} "
            + $"coercedMode={validation.CoercedSettings.ResolutionMode} coercedUseMultimon={validation.CoercedSettings.UseMultimon}");

        var runtimeServer = CloneServerProfile(server);
        runtimeServer.RdpResolutionMode = validation.CoercedSettings.ResolutionMode;
        runtimeServer.RdpMultiMonitor = validation.CoercedSettings.UseMultimon;
        runtimeServer.RdpSelectedMonitorIndices = [.. validation.CoercedSettings.SelectedMonitorIndices];

        return (runtimeServer, ResolveMultimonFallbackStatusKey(validation.Reason));
    }

    /// <summary>
    /// What the host reports about its monitors, or an empty topology when it cannot be read.
    /// </summary>
    /// <remarks>
    /// The bounds are what makes a disconnected selection detectable at all. An enumeration failure
    /// yields no monitors, which the validation reads as a host that cannot offer multi-monitor,
    /// rather than as a host whose geometry happens to be unknown.
    /// </remarks>
    private static RdpDisplayCapabilities GetRdpHostCapabilities()
    {
        try
        {
            return RdpDisplayCapabilities.FromMonitorBounds(
                [.. WinForms.Screen.AllScreens.Select(screen => screen.Bounds)]);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedSessionManager.RdpMultimonFallback monitor enumeration failed: {ex.Message}");
            return RdpDisplayCapabilities.FromMonitorBounds([]);
        }
    }

    private static string FormatMonitorIndices(IReadOnlyList<int> indices)
        => indices.Count == 0 ? "all" : string.Join(',', indices);

    /// <summary>
    /// The host layout, so a fallback can be understood from the log alone.
    /// </summary>
    private static string FormatMonitorGeometry(IReadOnlyList<Rectangle> monitorBounds)
        => monitorBounds.Count == 0
            ? "unknown"
            : string.Join(
                ' ',
                monitorBounds.Select(bounds =>
                    $"{bounds.Width}x{bounds.Height}+{bounds.X}+{bounds.Y}"));

    /// <summary>
    /// The message shown for a fallback, or nothing when the session was not coerced.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can walk the reasons and prove each one has a
    /// message. A reason with no key coerces the session silently, which is the failure this
    /// feature exists to remove.
    /// </remarks>
    internal static string? ResolveMultimonFallbackStatusKey(MultimonFallbackReason reason)
        => reason switch
        {
            MultimonFallbackReason.SingleMonitorHost => "RdpMultimonFallbackSingleMonitor",
            MultimonFallbackReason.InvalidMonitorIndex => "RdpMultimonFallbackInvalidSelection",
            MultimonFallbackReason.NonContiguousSelection => "RdpMultimonFallbackNonContiguous",
            _ => null
        };

    /// <summary>
    /// Independent copy of the profile for a runtime override, through the single fidelity
    /// primitive rather than a hand-written assignment list.
    /// </summary>
    /// <remarks>
    /// The list this replaced had drifted: it omitted the session logging override - which is the
    /// defect the multimon fallback exhibits - along with the vault entry name and the whole WinRM
    /// group, and it assigned the key passphrase unconditionally, fabricating its presence flag on a
    /// clone whose source had none.
    /// </remarks>
    private static ServerProfileDto CloneServerProfile(ServerProfileDto server)
        => server.CloneFaithfully();

    public void DisconnectSession(SessionPaneModel pane, DisconnectReason reason)
    {
        ArgumentNullException.ThrowIfNull(pane);

        Core.Logging.FileLogger.Info(
            $"EmbeddedSessionManager.DisconnectSession started paneId={pane.PaneId} title='{pane.Title}' connectionType={pane.ConnectionType} reason={reason}");

        switch (pane.HostControl)
        {
            case EmbeddedRdpView rdpView:
                rdpView.DisconnectForTeardown(reason);
                break;

            case IDisposable disposable:
                try
                {
                    Core.Logging.FileLogger.Info(
                        $"EmbeddedSessionManager.DisconnectSession disposing host paneId={pane.PaneId} reason={reason} hostType={disposable.GetType().FullName}");
                    disposable.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    Core.Logging.FileLogger.Info(
                        $"EmbeddedSessionManager.DisconnectSession host already disposed paneId={pane.PaneId} reason={reason}");
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.WarnDetailed(
                        $"EmbeddedSessionManager.DisconnectSession host dispose failed paneId={pane.PaneId} reason={reason}",
                        ex);
                }
                break;

            case null:
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSessionManager.DisconnectSession no host paneId={pane.PaneId} reason={reason}");
                break;

            default:
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSessionManager.DisconnectSession non-disposable host paneId={pane.PaneId} reason={reason} hostType={pane.HostControl.GetType().FullName}");
                break;
        }

        Core.Logging.FileLogger.Info(
            $"EmbeddedSessionManager.DisconnectSession completed paneId={pane.PaneId} reason={reason}");
    }

    /// <summary>
    /// Creates an <see cref="EmbeddedSshView"/> mounted in the "Connecting"
    /// state before <c>SshHandler.ConnectAsync</c> has produced a session.
    /// Use <see cref="AttachSshSession"/> once the session is available.
    /// </summary>
    public EmbeddedSshView CreateConnectingSshHostControl(
        SessionTabViewModel sessionTab,
        string displayName,
        ServerProfileDto server,
        AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentNullException.ThrowIfNull(server);

        var view = new EmbeddedSshView
        {
            Localizer = _localizer,
            TerminalSettings = settings,
            SessionLoggingOverride = server.SessionLoggingOverride,
            SessionLogService = _sessionLogService
        };
        view.InitializeConnecting(sessionTab, displayName, BuildSshEndpointLabel(server));
        ApplyQueuedReconnectAttempt(view, sessionTab);

        WireBroadcast(view);
        WireSplitRequested(view, sessionTab);
        WireReconnectRequested(view, sessionTab);

        return view;
    }

    /// <summary>
    /// Attaches a freshly-connected SSH session result to a tab whose host
    /// control was previously created by <see cref="CreateConnectingSshHostControl"/>.
    /// </summary>
    public void AttachSshSession(
        SessionTabViewModel sessionTab,
        ISessionResult sessionResult,
        AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentNullException.ThrowIfNull(sessionResult);

        if (sessionTab.HostControl is not EmbeddedSshView view)
        {
            throw new InvalidOperationException(
                "AttachSshSession expects the tab's HostControl to be an EmbeddedSshView created by CreateConnectingSshHostControl.");
        }

        var keepAlive = settings?.SshTmoutResetIntervalSeconds ?? AppSettings.DefaultSshTmoutResetIntervalSeconds;
        switch (sessionResult)
        {
            case SshSessionResult sshResult:
                view.SessionLoggingOverride = sshResult.SessionLoggingOverride;
                view.AttachSession(sshResult.Session, keepAlive);
                break;
            case TerminalSessionResult terminalResult:
                view.SessionLoggingOverride = terminalResult.SessionLoggingOverride;
                bool autoReconnectOnProcessExit = TerminalReconnectPolicy.ReconnectsOnProcessExit(
                    sessionTab.ConnectionType);
                view.AttachTerminalSession(
                    terminalResult.Session,
                    keepAlive,
                    autoReconnectOnProcessExit: autoReconnectOnProcessExit);
                break;
            default:
                throw new InvalidOperationException(
                    $"AttachSshSession expects an SSH session result, got {sessionResult.GetType().Name}.");
        }
    }

    private static string BuildSshEndpointLabel(ServerProfileDto server)
    {
        var user = string.IsNullOrWhiteSpace(server.SshUsername) ? "?" : server.SshUsername;
        var host = string.IsNullOrWhiteSpace(server.RemoteServer) ? "?" : server.RemoteServer;
        var port = server.SshPort > 0 ? server.SshPort : 22;
        return $"{user}@{host}:{port}";
    }

    private EmbeddedSshView CreateSshView(
        SessionTabViewModel tab,
        SshShellSession session,
        string displayName,
        int keepAliveIntervalSeconds,
        AppSettings? settings = null,
        bool? sessionLoggingOverride = null)
    {
        var view = new EmbeddedSshView
        {
            Localizer = _localizer,
            TerminalSettings = settings,
            SessionLoggingOverride = sessionLoggingOverride,
            SessionLogService = _sessionLogService
        };
        view.InitializeSession(session, tab, displayName, string.Empty, keepAliveIntervalSeconds);
        WireBroadcast(view);
        WireSplitRequested(view, tab);
        WireReconnectRequested(view, tab);
        return view;
    }

    private EmbeddedSshView CreateTerminalSshView(
        SessionTabViewModel tab,
        Heimdall.Terminal.ITerminalSession terminalSession,
        string displayName,
        int keepAliveIntervalSeconds,
        AppSettings? settings = null,
        bool isElevated = false,
        string? endpoint = null,
        string connectedStatus = "Connected",
        bool? sessionLoggingOverride = null,
        bool trackWinRmLifecycle = false)
    {
        var view = new EmbeddedSshView
        {
            Localizer = _localizer,
            TerminalSettings = settings,
            SessionLoggingOverride = sessionLoggingOverride,
            SessionLogService = _sessionLogService
        };
        bool autoReconnectOnProcessExit = TerminalReconnectPolicy.ReconnectsOnProcessExit(
            tab.ConnectionType);
        view.InitializeTerminalSession(
            terminalSession,
            tab,
            displayName,
            keepAliveIntervalSeconds,
            endpoint,
            connectedStatus,
            autoReconnectOnProcessExit);
        if (isElevated)
        {
            view.SetElevatedIndicator(true);
        }
        else
        {
            view.ShowElevateButton(true);
        }

        WireBroadcast(view);
        WireSplitRequested(view, tab);
        WireReconnectRequested(view, tab);
        if (trackWinRmLifecycle)
        {
            WireWinRmTerminalProcessExited(view, tab);
        }
        return view;
    }

    private void WireWinRmTerminalProcessExited(EmbeddedSshView view, SessionTabViewModel tab)
    {
        view.TerminalProcessExited += () =>
        {
            SessionPaneModel? pane = view.OwningPane;
            if (pane is null && !tab.IsSplit)
            {
                pane = tab.PrimaryPane;
            }

            if (pane is null)
            {
                Core.Logging.FileLogger.Warn(
                    "WINRM terminal exited before its split-pane ownership was assigned.");
                return;
            }

            ConnectionStateData? state = _connectionSm.GetStateData(pane.ServerId);
            if (state is null)
            {
                return;
            }

            try
            {
                if (state.TunnelLocalPort is int localPort && localPort > 0)
                {
                    _tunnelService.ReleaseTunnelReference(localPort);
                }
            }
            finally
            {
                _connectionSm.Teardown(pane.ServerId);
            }
        };
    }

    private void WireBroadcast(EmbeddedSshView view)
    {
        var callback = BroadcastCallback;
        if (callback is not null)
        {
            view.BroadcastInput += (bytes) => callback(bytes, view);
        }

        // Show broadcast badge if broadcast mode is already active
        if (IsBroadcastActive?.Invoke() == true)
        {
            view.SetBroadcastIndicator(true);
        }
    }

    private EmbeddedSftpView CreateSftpView(
        SessionTabViewModel tab,
        IRemoteBrowser browser,
        string displayName,
        SshConnectionParams? sshParams,
        string? initialRemotePath = null,
        AppSettings? settings = null,
        bool? sessionLoggingOverride = null)
    {
        var view = new EmbeddedSftpView
        {
            SessionOperationLog = _sessionOperationLog,
            // Read the LIVE global toggle at the seam (mirrors the RDP/VNC/Citrix wiring), so enabling
            // session logging takes effect without a restart.
            SessionLoggingEnabledProvider = () =>
                _configManager.CurrentSettings?.SessionLoggingEnabled ?? false,
            SessionLoggingOverride = sessionLoggingOverride
        };
        view.InitializeSession(
            browser, tab, displayName, string.Empty,
            _localizer, _dialogService, _hostKeyStore, sshParams, initialRemotePath);
        view.SetFollowSshDirectoryEnabled(
            browser is SftpBrowser && settings?.SftpFollowSshDirectory == true);

        // Wire "Open in Terminal" to send a cd command to any SSH terminal
        // in the same tab's split tree.
        view.OpenInTerminalRequested += (path) =>
        {
            foreach (var pane in Heimdall.Core.Models.SplitTreeHelper.EnumerateLeaves(tab.RootContent))
            {
                if (pane.HostControl is EmbeddedSshView sshView)
                {
                    sshView.WriteCommand(TerminalCommandFormatter.FormatRemoteCd(path));
                    break;
                }
            }
        };

        WireSplitRequested(view, tab);
        WireReconnectRequested(view, tab);
        return view;
    }

    private void WireReconnectRequested(EmbeddedSshView view, SessionTabViewModel tab)
    {
        view.ReconnectContextRequested += context =>
            ForwardReconnectRequest(tab, context, ReconnectRequestedCallback);
        view.CloseRequested += () => CloseRequestedCallback?.Invoke(tab);
        view.CurrentDirectoryChanged += path => FollowSftpToCurrentDirectory(tab, path);
    }

    internal static void ForwardReconnectRequest(
        SessionTabViewModel tab,
        ReconnectRequestContext context,
        Action<SessionTabViewModel, string, string>? callback)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (callback is null)
        {
            return;
        }

        PendingReconnectState state = PendingReconnectStates.GetOrCreateValue(tab);
        lock (state.SyncRoot)
        {
            state.Request = context;
        }

        callback(tab, tab.ProfileLookupServerId, tab.ConnectionType);
    }

    internal static ReconnectRequestContext TakeReconnectRequest(SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!PendingReconnectStates.TryGetValue(tab, out PendingReconnectState? state))
        {
            return ReconnectRequestContext.Manual;
        }

        lock (state.SyncRoot)
        {
            ReconnectRequestContext context = state.Request ?? ReconnectRequestContext.Manual;
            state.Request = null;
            return context;
        }
    }

    internal static void QueueReconnectAttempt(SessionTabViewModel tab, int attempt)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        PendingReconnectState state = PendingReconnectStates.GetOrCreateValue(tab);
        lock (state.SyncRoot)
        {
            state.Attempt = attempt;
        }
    }

    internal static void ApplyQueuedReconnectAttempt(
        EmbeddedSshView view,
        SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(tab);
        if (!PendingReconnectStates.TryGetValue(tab, out PendingReconnectState? state))
        {
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.Attempt is not int attempt)
            {
                return;
            }

            state.Attempt = null;
            view.SeedAutoReconnectAttempt(attempt);
        }
    }

    private sealed class PendingReconnectState
    {
        internal object SyncRoot { get; } = new object();

        internal ReconnectRequestContext? Request { get; set; }

        internal int? Attempt { get; set; }
    }

    private static void FollowSftpToCurrentDirectory(SessionTabViewModel tab, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var pane = ResolveSftpFollowPane(
            tab.RootContent,
            static hostControl => hostControl is EmbeddedSftpView);
        if (pane?.HostControl is not EmbeddedSftpView sftpView)
        {
            return;
        }

        _ = NavigateSftpToCurrentDirectoryAsync(sftpView, path);
    }

    internal static SessionPaneModel? ResolveSftpFollowPane(
        ISplitContent? root,
        Func<object?, bool> isSftpHost)
    {
        ArgumentNullException.ThrowIfNull(isSftpHost);

        foreach (var pane in Heimdall.Core.Models.SplitTreeHelper.EnumerateLeaves(root))
        {
            if (pane.SftpFollowSshDirectory && isSftpHost(pane.HostControl))
            {
                return pane;
            }
        }

        return null;
    }

    private static async Task NavigateSftpToCurrentDirectoryAsync(
        EmbeddedSftpView sftpView,
        string path)
    {
        try
        {
            await sftpView.NavigateToPath(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Debug(
                $"EmbeddedSFTP follow-current-directory navigation failed ({ex.GetType().Name}).");
        }
    }

    private void WireReconnectRequested(EmbeddedSftpView view, SessionTabViewModel tab)
    {
        view.ReconnectRequested += () =>
        {
            if (tab.IsSplit && view.OwningPane is { } ownerPane)
            {
                ReconnectPaneRequestedCallback?.Invoke(tab, ownerPane);
                return;
            }

            ReconnectRequestedCallback?.Invoke(
                tab,
                tab.ProfileLookupServerId,
                tab.ConnectionType);
        };
        view.CloseRequested += () => CloseRequestedCallback?.Invoke(tab);
    }

    private void WireSplitRequested(EmbeddedSshView view, SessionTabViewModel tab)
    {
        view.SplitRequested += () => SplitRequestedCallback?.Invoke(tab);
    }

    private void WireSplitRequested(EmbeddedRdpView view, SessionTabViewModel tab)
    {
        view.SplitRequested += () => SplitRequestedCallback?.Invoke(tab);
    }

    private void WireSplitRequested(EmbeddedSftpView view, SessionTabViewModel tab)
    {
        view.SplitRequested += () => SplitRequestedCallback?.Invoke(tab);
    }

    private void WireVncSplitRequested(EmbeddedVncView view, SessionTabViewModel tab)
    {
        view.RequestSplit += (_) => SplitRequestedCallback?.Invoke(tab);
    }

    private void WireVncReconnectRequested(EmbeddedVncView view, SessionTabViewModel tab)
    {
        view.RequestReconnect += (_) =>
            ReconnectRequestedCallback?.Invoke(
                tab,
                tab.ProfileLookupServerId,
                tab.ConnectionType);
        view.RequestClose += (_) => CloseRequestedCallback?.Invoke(tab);
    }

    /// <summary>
    /// Builds the correct <c>cd</c> command for the detected shell type.
    /// PowerShell uses <c>cd 'path'</c>, cmd uses <c>cd /d "path"</c>,
    /// and bash/wsl uses <c>cd 'path'</c>.
    /// </summary>
    private static string FormatCdCommand(string shellExecutable, string path)
    {
        return TerminalCommandFormatter.FormatCd(shellExecutable, path);
    }

    /// <summary>
    /// Builds the correct run/execute command for the detected shell type.
    /// PowerShell uses <c>&amp; 'path'</c>, cmd uses <c>"path"</c>,
    /// and bash/wsl uses <c>'path'</c>.
    /// </summary>
    private static string FormatRunCommand(string shellExecutable, string path)
    {
        return TerminalCommandFormatter.FormatRun(shellExecutable, path);
    }

    private static Brush GetBrush(string resourceKey, Brush fallback)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private sealed class DisposablePlaceholderView : Border, IDisposable
    {
        private readonly IDisposable? _session;
        private bool _disposed;

        public DisposablePlaceholderView(string displayName, string connectionType, ISessionResult session)
        {
            _session = session as IDisposable;

            Background = GetBrush("BackgroundBrush", Brushes.Transparent);
            Child = BuildContent(displayName, connectionType);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _session?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by the session engine.
            }
        }

        private static FrameworkElement BuildContent(string displayName, string connectionType)
        {
            var message = string.Equals(connectionType, "SFTP", StringComparison.OrdinalIgnoreCase)
                ? "The SFTP session is connected, but the embedded browser view is not wired yet."
                : string.Format(
                    "The {0} session is connected, but no embedded view is available yet.",
                    connectionType);

            var outer = new Border
            {
                Margin = new Thickness(24),
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(16),
                Background = GetBrush("CardBrush", Brushes.Black),
                BorderBrush = GetBrush("BorderBrush", Brushes.DimGray),
                BorderThickness = new Thickness(1),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            var stack = new StackPanel
            {
                MaxWidth = 460
            };

            stack.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush", Brushes.White)
            });

            stack.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetBrush("TextSecondaryBrush", Brushes.Gainsboro)
            });

            outer.Child = stack;
            return outer;
        }
    }

    /// <summary>
    /// Creates a host control for a tool tab (non-connection UI surface).
    /// Uses the centralized <see cref="ToolRegistry"/> to instantiate the correct view.
    /// </summary>
    public object CreateToolControl(
        SessionTabViewModel sessionTab,
        string toolId,
        ToolContext? context,
        AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        var descriptor = _toolRegistry.GetById(toolId);
        if (descriptor is null)
        {
            Core.Logging.FileLogger.Warn($"Unknown tool ID: {toolId}");
            return new TextBlock
            {
                Text = $"Tool: {toolId}",
                FontSize = 18,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Foreground = GetBrush("TextPrimaryBrush", Brushes.White)
            };
        }

        var view = _toolRegistry.CreateView(toolId);

        // The gateways a tool lists and dials follow the settings from here on, through one
        // inventory shared by every tool tab. The snapshot list below is kept as the seed for a
        // context built without the inventory.
        _gatewayInventory ??= new ConfigGatewayInventory(_configManager, () => _configManager.CurrentSettings);
        context = (context ?? new ToolContext()) with
        {
            GatewayInventory = _gatewayInventory
        };

        // Enrich context with SSH gateways so tools can offer "Route via" tunnel support
        if (settings?.SshGateways is { Count: > 0 } gateways)
        {
            context = (context ?? new ToolContext()) with
            {
                SshGateways = (System.Collections.IList)gateways
            };
        }

        // Inject cross-tool navigation callback so tools can open other tools
        if (OpenToolCallback is not null)
        {
            context = (context ?? new ToolContext()) with
            {
                OpenToolAction = OpenToolCallback
            };
        }

        // Inject busy state callback so tools can signal long-running operations.
        // Inject send-to-terminal callback for command injection into sibling terminals.
        context = (context ?? new ToolContext()) with
        {
            SetBusyAction = busy => sessionTab.IsBusy = busy,
            SendCommandAction = command => TrySendCommandToSession(sessionTab, command),
            CanSendToTerminal = () => SessionHasTerminalSink(sessionTab)
        };

        view.Initialize(context, _localizer);
        return view;
    }

    /// <inheritdoc />
    public bool TrySendCommandToSession(SessionTabViewModel session, string command)
    {
        if (session is null) return false;

        return TrySendCommandToFirstSink(session.RootContent, command);
    }

    /// <summary>
    /// Reports whether the given session tab currently hosts at least one
    /// injectable terminal (an <see cref="ITerminalCommandSink"/>) anywhere in
    /// its split tree. Drives the Command Library Send button's enabled state:
    /// being attached to a session is not enough; the tab must contain a
    /// terminal pane that can actually receive the command.
    /// </summary>
    /// <param name="session">The session tab to inspect.</param>
    /// <returns>
    /// <c>true</c> when a terminal sink exists in the tab's split tree;
    /// otherwise <c>false</c> (including when <paramref name="session"/> is null).
    /// </returns>
    public bool SessionHasTerminalSink(SessionTabViewModel session)
    {
        if (session is null) return false;

        return HasTerminalSink(session.RootContent);
    }

    /// <summary>
    /// Pure helper that walks the split tree and forwards the command to the
    /// first leaf whose host control is an <see cref="ITerminalCommandSink"/>.
    /// </summary>
    /// <param name="root">The root of the split pane tree (may be null).</param>
    /// <param name="command">The command to inject (forwarded as-is).</param>
    /// <returns>
    /// <c>true</c> when a sink was found and written to; <c>false</c> when
    /// <paramref name="root"/> is <c>null</c> or no sink exists in the tree.
    /// </returns>
    internal static bool TrySendCommandToFirstSink(ISplitContent? root, string command)
    {
        var sink = FindFirstTerminalSink(root);
        if (sink is null) return false;

        sink.WriteCommand(command);
        return true;
    }

    /// <summary>
    /// Pure helper that reports whether the split tree contains at least one
    /// <see cref="ITerminalCommandSink"/> leaf, reusing the same traversal as
    /// <see cref="TrySendCommandToFirstSink"/>.
    /// </summary>
    internal static bool HasTerminalSink(ISplitContent? root) => FindFirstTerminalSink(root) is not null;

    /// <summary>
    /// Walks the split tree depth-first and returns the first leaf host control
    /// that is an <see cref="ITerminalCommandSink"/>, or <c>null</c> when none
    /// exists. Single source of the sink-lookup traversal.
    /// </summary>
    private static ITerminalCommandSink? FindFirstTerminalSink(ISplitContent? root)
    {
        foreach (var pane in SplitTreeHelper.EnumerateLeaves(root))
        {
            if (pane.HostControl is ITerminalCommandSink sink)
            {
                return sink;
            }
        }

        return null;
    }
}
