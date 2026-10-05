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

using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Extensions;
using Heimdall.App.Services;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Views;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Terminal;
using AppDialogs = Heimdall.App.Views.Dialogs;
using AppDialogViewModels = Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.ViewModels.Session;

/// <summary>
/// Session-lifecycle coordinator. Owns the wire-up of SplitService +
/// EmbeddedSessionManager + ServerList.SessionReady, the broadcast-mode
/// cluster (toggle + fan-out + per-view indicators), and the async
/// handlers that materialize new session tabs
/// (<see cref="OnSessionReady"/>), auto-open SFTP companion panes
/// (<see cref="AutoOpenSftpAsync"/>) and reconnect stale sessions
/// (<see cref="OnReconnectRequestedAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Composition: instantiated inside <see cref="MainViewModel"/>'s
/// constructor (<see cref="MainViewModel.Session"/>) - no DI registration.
/// Follows the same pattern as <c>TunnelsViewModel</c> and
/// <c>ScheduledTasksViewModel</c>: takes <see cref="MainViewModel"/> as
/// first ctor parameter and reaches other sub-VMs
/// (<c>ServerList</c>, <c>Connection</c>, <c>Split</c>, <c>Tunnels</c>)
/// through <c>_main.X</c>.
/// </para>
/// <para>
/// The coordinator wires 8 external callbacks in its constructor:
/// 5 <c>Split.*</c> providers/setters and 3 <see cref="IEmbeddedSessionManager"/>
/// callbacks (<c>BroadcastCallback</c>, <c>IsBroadcastActive</c>,
/// <c>ReconnectRequestedCallback</c>). The <c>OpenToolCallback</c> stays
/// on <see cref="MainViewModel"/> because <c>OpenToolTabAsync</c> is a
/// shell concern shared with the sidebar/tools-tab/palette consumers.
/// </para>
/// </remarks>
public sealed partial class SessionCoordinator : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly LocalizationManager _localizer;
    private readonly IConfigManager _configManager;
    private readonly IEmbeddedSessionManager _embeddedSessionManager;
    private readonly IPostConnectSequenceRunner _postConnectSequenceRunner;
    private readonly IPostConnectStepResolver _postConnectStepResolver;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Dictionary<string, ConnectingSessionCancellation> _connectingCancellations = [];

    /// <summary>
    /// The SSH views mounted in their "Connecting" state, by session id. Written on the UI
    /// thread; read by the SSH handler from the connect thread to size the PTY, hence concurrent.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EmbeddedSshView> _connectingSshViews =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconnectChainState> _reconnectChainsBySessionId = [];
    private readonly HashSet<ReconnectChainState> _activeReconnectChains = [];
    private readonly AsyncLocal<ReconnectChainState?> _currentReconnectChain = new AsyncLocal<ReconnectChainState?>();
    private readonly object _reconnectChainGate = new object();
    private bool _disposed;

    internal Func<TimeSpan, CancellationToken, Task> ReconnectDelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    internal int ActiveReconnectChainCount
    {
        get
        {
            lock (_reconnectChainGate)
            {
                return _activeReconnectChains.Count;
            }
        }
    }

    /// <summary>
    /// Creates a new session coordinator and installs the 8 external
    /// wire-ups + the <see cref="ServerList.SessionReady"/> event handler.
    /// </summary>
    public SessionCoordinator(
        MainViewModel main,
        LocalizationManager localizer,
        IConfigManager configManager,
        IEmbeddedSessionManager embeddedSessionManager,
        IPostConnectSequenceRunner postConnectSequenceRunner,
        IPostConnectStepResolver postConnectStepResolver,
        IUiDispatcher uiDispatcher)
        : this(
            main,
            localizer,
            configManager,
            embeddedSessionManager,
            postConnectSequenceRunner,
            postConnectStepResolver,
            uiDispatcher,
            wireUpCallbacks: true)
    {
    }

    internal static SessionCoordinator CreateForTests(IUiDispatcher uiDispatcher)
    {
        return new SessionCoordinator(
            main: null!,
            localizer: null!,
            configManager: null!,
            embeddedSessionManager: null!,
            postConnectSequenceRunner: null!,
            postConnectStepResolver: null!,
            uiDispatcher,
            wireUpCallbacks: false);
    }

    private SessionCoordinator(
        MainViewModel main,
        LocalizationManager localizer,
        IConfigManager configManager,
        IEmbeddedSessionManager embeddedSessionManager,
        IPostConnectSequenceRunner postConnectSequenceRunner,
        IPostConnectStepResolver postConnectStepResolver,
        IUiDispatcher uiDispatcher,
        bool wireUpCallbacks)
    {
        _main = main;
        _localizer = localizer;
        _configManager = configManager;
        _embeddedSessionManager = embeddedSessionManager;
        _postConnectSequenceRunner = postConnectSequenceRunner;
        _postConnectStepResolver = postConnectStepResolver;
        _uiDispatcher = uiDispatcher;

        if (!wireUpCallbacks)
        {
            return;
        }

        // Wire SplitService callbacks for access to session tab state
        _main.Split.ActiveSessionsProvider = () => _main.Connection.ActiveSessions;
        _main.Split.ActiveSessionProvider = () => _main.Connection.ActiveSession;
        _main.Split.SetActiveSession = s => _main.Connection.ActiveSession = s;
        _main.Split.SetHasActiveSessions = v => _main.Connection.HasActiveSessions = v;
        _main.Split.SetStatusText = s => _main.StatusText = s;

        // Wire ConnectionService status-text relay
        _main.ServerList.ConnectionService.SetStatusText = s => _main.StatusText = s;

        // Wire the PTY-size relay: the SSH handler asks, just before it creates the PTY, what
        // size the connecting view's page has reported, waiting for it on the Plink path.
        _main.ServerList.ConnectionService.ResolveInitialTerminalSize = ResolveConnectingTerminalSizeAsync;

        // Wire connect-time execution-trust confirmation relay
        _main.ServerList.ConnectionService.ConfirmExecution =
            profile => _main.ServerList.ConfirmAndTrustExecutionAsync(profile);

        // Wire broadcast relay so terminal views can fan out input
        _embeddedSessionManager.BroadcastCallback = BroadcastToAllTerminals;
        _embeddedSessionManager.IsBroadcastActive = () => IsBroadcastMode;

        // Wire SSH reconnect: close the old session tab and re-connect from scratch
        _embeddedSessionManager.ReconnectRequestedCallback = OnReconnectRequested;
        _embeddedSessionManager.ReconnectPaneRequestedCallback = OnReconnectPaneRequested;
        _embeddedSessionManager.DisconnectRequestedCallback = OnDisconnectRequested;
        _embeddedSessionManager.EditServerRequestedCallback = OnEditServerRequested;
        // Wire overlay Close button: tear down the whole tab through the shared lifecycle path.
        _embeddedSessionManager.CloseRequestedCallback = OnCloseRequested;
        _embeddedSessionManager.HostReconnectRequestedCallback = OnHostReconnectRequested;
        _embeddedSessionManager.HostCloseRequestedCallback = OnHostCloseRequested;

        // Subscribe to ServerList session lifecycle events to materialize session tabs.
        _main.ServerList.SessionStarting += OnSessionStarting;
        _main.ServerList.SessionStartFailed += OnSessionStartFailed;
        _main.ServerList.SessionReady += OnSessionReady;
        _main.ServerList.SessionFailed += OnSessionFailed;

        // The targeted-pane count spans every open tab, and several paths outside this class
        // close one; follow the list rather than ask each of them to announce it.
        _main.Connection.ActiveSessions.CollectionChanged += OnActiveSessionsChanged;
    }

    /// <summary>The open tabs whose split layout this coordinator follows.</summary>
    private readonly HashSet<SessionTabViewModel> _observedTabs = [];

    private void OnActiveSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Resynchronised from the list itself rather than from the event's item lists: a Reset
        // carries no old items, and a tab missed here would be listened to forever.
        var current = new HashSet<SessionTabViewModel>(_main.Connection.ActiveSessions);
        foreach (SessionTabViewModel gone in _observedTabs.Where(tab => !current.Contains(tab)).ToList())
        {
            gone.PropertyChanged -= OnObservedTabPropertyChanged;
            _observedTabs.Remove(gone);
        }

        foreach (SessionTabViewModel added in current.Where(_observedTabs.Add))
        {
            added.PropertyChanged += OnObservedTabPropertyChanged;
        }

        OnPropertyChanged(nameof(BroadcastScopeLabel));
    }

    /// <summary>
    /// A targeted pane can close inside a split without the tab list changing; every split
    /// operation replaces the tab's root, so that is what is followed.
    /// </summary>
    private void OnObservedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTabViewModel.RootContent))
        {
            OnPropertyChanged(nameof(BroadcastScopeLabel));
        }
    }

    // ── Broadcast mode ───────────────────────────────────────────────

    /// <summary>
    /// True while broadcast mode is active: keystrokes typed in one
    /// terminal view fan out to the terminal panes in <see cref="BroadcastScope"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBroadcastSelectionActive))]
    private bool _isBroadcastMode;

    /// <summary>
    /// Active broadcast scope. CurrentTab (default), AllTabs, and SelectedPanes
    /// (per-pane subset, Lot B). Mirrors <see cref="AppSettings.BroadcastScope"/> and
    /// is persisted when changed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BroadcastScopeLabel))]
    [NotifyPropertyChangedFor(nameof(IsBroadcastSelectionActive))]
    private BroadcastScope _broadcastScope = BroadcastScope.CurrentTab;

    /// <summary>
    /// True when per-session broadcast target selection is active (broadcast mode on
    /// AND scope is SelectedPanes). Drives the visibility of the tab-strip target
    /// markers. Notifies whenever broadcast mode or scope changes.
    /// </summary>
    public bool IsBroadcastSelectionActive =>
        IsBroadcastMode && BroadcastScope == BroadcastScope.SelectedPanes;

    /// <summary>Localized tooltip for the broadcast toggle button (scope-aware when on).</summary>
    public string BroadcastToggleTooltip => IsBroadcastMode
        ? _localizer.Format("BroadcastModeOn", BroadcastScopeLabel)
        : _localizer["TooltipToggleBroadcast"];

    /// <summary>Localized label for the broadcast scope indicator near the toolbar button.</summary>
    public string BroadcastScopeLabel => BroadcastScope switch
    {
        BroadcastScope.AllTabs => _localizer["BroadcastScopeAllTabs"],
        BroadcastScope.SelectedPanes => _localizer.Format("BroadcastScopeSelectedPanes", CountBroadcastTargets()),
        _ => _localizer["BroadcastScopeCurrentTab"],
    };

    /// <summary>
    /// Rewords the coordinator's localized lines after an interface language switch. Called by the
    /// shell's own language refresh, on the UI thread.
    /// </summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(BroadcastScopeLabel));

        // A forced RDP mode is stored on its tab as worded text, so it is re-worded rather than
        // re-announced; setting it raises the tab's title and tooltip.
        foreach (SessionTabViewModel tab in _main.Connection.ActiveSessions)
        {
            if (tab.RdpModeOverride != RdpModeOverride.UseProfile)
            {
                tab.RdpModeOverrideSuffix = RdpModeOverrideSuffixFor(tab.RdpModeOverride);
            }
        }
    }

    /// <summary>
    /// Keeps <see cref="BroadcastToggleTooltip"/> in step with <see cref="BroadcastScopeLabel"/>,
    /// which it embeds while broadcast is on.
    /// </summary>
    /// <remarks>
    /// One rule here rather than a second notification beside every raise of the label: a pane or
    /// tab selection raised the label alone, so the tooltip kept the target count from before.
    /// </remarks>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(BroadcastScopeLabel))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BroadcastToggleTooltip)));
        }
    }

    /// <summary>
    /// Generated partial: updates the per-view broadcast indicators and
    /// refreshes the tooltip whenever <see cref="IsBroadcastMode"/> flips.
    /// </summary>
    partial void OnIsBroadcastModeChanged(bool value)
    {
        UpdateBroadcastIndicators(value);
        OnPropertyChanged(nameof(BroadcastToggleTooltip));
        RefreshBroadcastTabMarkers();
    }

    /// <summary>
    /// Toggles <see cref="IsBroadcastMode"/> and reports the transition in
    /// the shell status bar. Enabling broadcast while the scope is
    /// <see cref="Heimdall.Core.Models.BroadcastScope.AllTabs"/> first asks for
    /// confirmation because input would reach terminal panes in other tabs;
    /// <see cref="Heimdall.Core.Models.BroadcastScope.CurrentTab"/> never prompts.
    /// </summary>
    [RelayCommand]
    private async Task ToggleBroadcastAsync()
    {
        if (IsBroadcastMode)
        {
            IsBroadcastMode = false;
            _main.StatusText = _localizer["BroadcastModeOff"];
            return;
        }

        SyncBroadcastScopeFromSettings();

        if (BroadcastScope == BroadcastScope.AllTabs
            && !await ConfirmAllTabsBroadcastAsync())
        {
            return;
        }

        IsBroadcastMode = true;
        _main.StatusText = _localizer.Format("BroadcastModeOn", BroadcastScopeLabel);
    }

    /// <summary>
    /// Cycles the broadcast scope CurrentTab -> AllTabs -> SelectedPanes -> CurrentTab.
    /// Switching to AllTabs while broadcast is already active requires confirmation
    /// (Lot A). Entering SelectedPanes resets every pane to "not a target" so the
    /// subset starts empty (explicit opt-in); SelectedPanes never prompts.
    /// </summary>
    [RelayCommand]
    private async Task CycleBroadcastScopeAsync()
    {
        SyncBroadcastScopeFromSettings();

        var next = BroadcastScope switch
        {
            BroadcastScope.CurrentTab => BroadcastScope.AllTabs,
            BroadcastScope.AllTabs => BroadcastScope.SelectedPanes,
            _ => BroadcastScope.CurrentTab,
        };

        if (next == BroadcastScope.AllTabs
            && IsBroadcastMode
            && !await ConfirmAllTabsBroadcastAsync())
        {
            return;
        }

        if (next == BroadcastScope.SelectedPanes)
        {
            ResetBroadcastTargets();
        }

        BroadcastScope = next;
        await PersistBroadcastScopeAsync(next);

        // Reflect the new scope in the per-pane toggles/badges while broadcast is on.
        if (IsBroadcastMode)
        {
            UpdateBroadcastIndicators(true);
        }

        RefreshBroadcastTabMarkers();
        OnPropertyChanged(nameof(BroadcastScopeLabel));

        // While broadcast is on, the status reflects the active scope; otherwise it
        // just reports the new default scope.
        _main.StatusText = IsBroadcastMode
            ? _localizer.Format("BroadcastModeOn", BroadcastScopeLabel)
            : _localizer.Format("BroadcastScopeStatus", BroadcastScopeLabel);
    }

    private Task<bool> ConfirmAllTabsBroadcastAsync()
    {
        return _main.DialogService.ShowConfirmAsync(
            _localizer["BroadcastAllTabsConfirmTitle"],
            _localizer["BroadcastAllTabsConfirmMessage"],
            "warning");
    }

    private void SyncBroadcastScopeFromSettings()
    {
        if (_main.CurrentSettings?.BroadcastScope is { } scope && scope != BroadcastScope)
        {
            BroadcastScope = scope;
        }
    }

    private async Task PersistBroadcastScopeAsync(BroadcastScope scope)
    {
        if (_main.CurrentSettings is not null)
        {
            _main.CurrentSettings.BroadcastScope = scope;
        }

        await _configManager.MergeSettingAsync(s => s.BroadcastScope = scope);
    }

    /// <summary>
    /// Updates the per-pane broadcast chrome on all active SSH/Local terminal views.
    /// In SelectedPanes mode each terminal pane shows an include/exclude toggle whose
    /// state mirrors <see cref="SessionPaneModel.IsBroadcastTarget"/>; otherwise the
    /// generic broadcast badge reflects whether broadcast mode is active.
    /// </summary>
    private void UpdateBroadcastIndicators(bool active)
    {
        bool selectionMode = active && BroadcastScope == BroadcastScope.SelectedPanes;
        int matchedTerminalPanes = 0;

        foreach (var session in _main.Connection.ActiveSessions)
        {
            foreach (var pane in SplitTreeHelper.EnumerateLeaves(session.RootContent))
            {
                if (pane.HostControl is not EmbeddedSshView sshView)
                {
                    continue;
                }

                matchedTerminalPanes++;
                sshView.SetBroadcastSelectionMode(selectionMode);

                if (selectionMode)
                {
                    var capturedPane = pane;
                    var capturedView = sshView;
                    capturedView.BroadcastTargetToggleRequested =
                        () => ToggleBroadcastTarget(capturedPane, capturedView);
                    capturedView.SetBroadcastTargetState(capturedPane.IsBroadcastTarget);
                }
                else
                {
                    sshView.BroadcastTargetToggleRequested = null;
                    sshView.SetBroadcastIndicator(active);
                }
            }
        }

        // One-shot diagnostic so a missing per-pane toggle can be diagnosed from the
        // log: confirms selection mode was entered and how many terminal panes matched.
        if (selectionMode && !_broadcastIndicatorDiagnosticLogged)
        {
            _broadcastIndicatorDiagnosticLogged = true;
            FileLogger.Info(
                $"[Broadcast] UpdateBroadcastIndicators: selectionMode={selectionMode}, " +
                $"matchedTerminalPanes={matchedTerminalPanes}");
        }
    }

    private bool _broadcastIndicatorDiagnosticLogged;

    /// <summary>
    /// Toggles a whole session's broadcast-target membership from the tab strip:
    /// flips <see cref="SessionPaneModel.IsBroadcastTarget"/> on every terminal pane
    /// of the session, refreshes each pane's visual state, and updates the tab marker
    /// and the "Selected panes (N)" count. No-op for sessions with no terminal pane
    /// (RDP/VNC/SFTP/FTP/Citrix), which therefore cannot be selected.
    /// </summary>
    [RelayCommand]
    private void ToggleSessionBroadcastTarget(SessionTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        bool? newValue = BroadcastTargetSelection.ToggleSession(
            tab.RootContent, static p => p.HostControl is EmbeddedSshView);
        if (newValue is null)
        {
            return; // Non-terminal session: not selectable.
        }

        foreach (var pane in SplitTreeHelper.EnumerateLeaves(tab.RootContent))
        {
            if (pane.HostControl is EmbeddedSshView view)
            {
                view.SetBroadcastTargetState(pane.IsBroadcastTarget);
            }
        }

        tab.IsBroadcastTarget = newValue.Value;
        OnPropertyChanged(nameof(BroadcastScopeLabel));
    }

    /// <summary>
    /// Pushes per-tab broadcast-target state (CanBeBroadcastTarget, IsBroadcastTarget,
    /// ShowBroadcastTargetMarker) onto every session tab so the tab-strip markers
    /// reflect the current selection mode and pane flags.
    /// </summary>
    private void RefreshBroadcastTabMarkers()
    {
        bool selectionActive = IsBroadcastSelectionActive;
        foreach (var tab in _main.Connection.ActiveSessions)
        {
            bool canTarget = BroadcastTargetSelection.SessionHasTerminal(
                tab.RootContent, static p => p.HostControl is EmbeddedSshView);
            tab.CanBeBroadcastTarget = canTarget;
            tab.IsBroadcastTarget = BroadcastTargetSelection.IsSessionTargeted(
                tab.RootContent, static p => p.HostControl is EmbeddedSshView);
            tab.ShowBroadcastTargetMarker = selectionActive && canTarget;
            tab.BroadcastTargetAccessibleName = _localizer.Format(
                tab.IsBroadcastTarget
                    ? "A11yBroadcastTabTargetSelected"
                    : "A11yBroadcastTabTargetNotSelected",
                tab.DisplayTitle);
        }
    }

    /// <summary>
    /// Flips a pane's broadcast-target membership in SelectedPanes mode, refreshes
    /// the pane's visual state, and updates the scope indicator count.
    /// </summary>
    private void ToggleBroadcastTarget(SessionPaneModel pane, EmbeddedSshView view)
    {
        pane.IsBroadcastTarget = !pane.IsBroadcastTarget;
        view.SetBroadcastTargetState(pane.IsBroadcastTarget);
        RefreshBroadcastTabMarkers();
        OnPropertyChanged(nameof(BroadcastScopeLabel));
    }

    /// <summary>Clears the broadcast-target flag on every pane (used when entering SelectedPanes).</summary>
    private void ResetBroadcastTargets()
    {
        foreach (var session in _main.Connection.ActiveSessions)
        {
            foreach (var pane in SplitTreeHelper.EnumerateLeaves(session.RootContent))
            {
                pane.IsBroadcastTarget = false;
            }
        }
    }

    /// <summary>Counts terminal panes currently marked as broadcast targets, across every tab.</summary>
    private int CountBroadcastTargets()
        => BroadcastTargetSelection.CountTargets(
            _main.Connection.ActiveSessions.Select(s => (ISplitContent?)s.RootContent),
            static p => p.HostControl is EmbeddedSshView);

    /// <summary>
    /// Fans broadcast input out to the terminal panes resolved for the active
    /// <see cref="BroadcastScope"/>, excluding the originating view and routing
    /// the payload through <see cref="BroadcastFanout"/> (the same
    /// <see cref="SmartPasteGuard"/> the terminal paste path uses). Called by
    /// <see cref="EmbeddedSshView"/> when broadcast mode is enabled.
    /// </summary>
    public void BroadcastToAllTerminals(byte[] data, object? sender)
    {
        if (!IsBroadcastMode)
        {
            return;
        }

        var targets = ResolveBroadcastTargets(sender);
        if (targets.Count == 0)
        {
            return;
        }

        var writers = new List<Action<byte[]>>(targets.Count);
        foreach (var pane in targets)
        {
            if (pane.HostControl is EmbeddedSshView sshView)
            {
                writers.Add(sshView.WriteBytes);
            }
        }

        var previewText = Encoding.UTF8.GetString(data);
        BroadcastFanout.Dispatch(
            data,
            isProduction: false,
            writers,
            risk => ConfirmRiskyBroadcastPaste(risk, previewText));
    }

    /// <summary>
    /// Resolves the in-scope terminal panes for broadcast using the pure
    /// <see cref="BroadcastTargetResolver"/>. The terminal-pane predicate keys on
    /// <see cref="EmbeddedSshView"/>, the shared SSH/local/telnet/WinRM terminal
    /// surface; RDP/VNC/SFTP/FTP/Citrix host controls are never targeted.
    /// </summary>
    private IReadOnlyList<SessionPaneModel> ResolveBroadcastTargets(object? sender)
    {
        var roots = new List<ISplitContent?>(_main.Connection.ActiveSessions.Count);
        foreach (var session in _main.Connection.ActiveSessions)
        {
            roots.Add(session.RootContent);
        }

        // In SelectedPanes mode only the panes the user explicitly marked are
        // targets; the other scopes target every terminal pane in range.
        Func<SessionPaneModel, bool> isBroadcastTarget = BroadcastScope == BroadcastScope.SelectedPanes
            ? static pane => pane.HostControl is EmbeddedSshView && pane.IsBroadcastTarget
            : static pane => pane.HostControl is EmbeddedSshView;

        return BroadcastTargetResolver.ResolveTargets(
            roots,
            _main.Connection.ActiveSession?.RootContent,
            BroadcastScope,
            sender,
            isBroadcastTarget);
    }

    /// <summary>
    /// Synchronous confirmation for a risky broadcast payload, mirroring the
    /// terminal paste guard dialog (<c>EmbeddedSshView.ConfirmPaste</c>). Returns
    /// true when the user approves fanning the payload out to every in-scope
    /// terminal.
    /// </summary>
    private bool ConfirmRiskyBroadcastPaste(SmartPasteGuard.PasteRisk risk, string previewText)
    {
        if (_localizer is null)
        {
            return false;
        }

        var dialogRisk = risk == SmartPasteGuard.PasteRisk.Dangerous
            ? AppDialogViewModels.PasteRisk.Dangerous
            : AppDialogViewModels.PasteRisk.MultiLine;

        var viewModel = new AppDialogViewModels.PasteConfirmDialogViewModel(
            dialogRisk,
            previewText,
            _localizer);
        var dialog = new AppDialogs.PasteConfirmDialog
        {
            Owner = System.Windows.Application.Current?.MainWindow,
            DataContext = viewModel,
        };

        return dialog.ShowDialog() == true;
    }

    // ── Session lifecycle handlers ───────────────────────────────────

    /// <summary>
    /// Handles the SSH-only session-starting event by mounting the tab and
    /// its embedded terminal view before the SSH connect call completes.
    /// </summary>
    private void OnSessionStarting(
        string sessionId,
        string originalServerId,
        string displayName,
        string connectionType,
        ServerProfileDto server,
        AppSettings settings,
        CancellationTokenSource cancellationSource)
    {
        if (!string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReconnectChainState? reconnectChain = _currentReconnectChain.Value;
        if (!_uiDispatcher.CheckAccess())
        {
            InvokeOnUi(() => OnSessionStartingCore(
                sessionId,
                originalServerId,
                displayName,
                connectionType,
                server,
                settings,
                cancellationSource,
                reconnectChain));
            return;
        }

        OnSessionStartingCore(
            sessionId,
            originalServerId,
            displayName,
            connectionType,
            server,
            settings,
            cancellationSource,
            reconnectChain);
    }

    private void OnSessionStartingCore(
        string sessionId,
        string originalServerId,
        string displayName,
        string connectionType,
        ServerProfileDto server,
        AppSettings settings,
        CancellationTokenSource cancellationSource,
        ReconnectChainState? reconnectChain)
    {
        SessionTabViewModel? existingTab = _main.Connection.ActiveSessions.FirstOrDefault(
            t => string.Equals(t.ServerId, sessionId, StringComparison.Ordinal));
        if (existingTab is not null)
        {
            FileLogger.Warn($"SessionStarting ignored duplicate SSH tab for sessionId={sessionId}.");
            return;
        }

        SessionTabViewModel? tab = _main.Connection.AddSession(
            sessionId,
            displayName,
            connectionType,
            settings.MaxEmbeddedSessions);
        if (tab is null)
        {
            cancellationSource.Cancel();
            return;
        }

        tab.OriginalServerId = originalServerId;
        tab.FailureDetails = null;
        TrackConnectingCancellation(sessionId, tab, cancellationSource);
        if (reconnectChain is not null)
        {
            lock (_reconnectChainGate)
            {
                _reconnectChainsBySessionId[sessionId] = reconnectChain;
            }

            reconnectChain.CurrentSessionId = sessionId;
            EmbeddedSessionManager.QueueReconnectAttempt(tab, reconnectChain.Attempt);
        }

        EmbeddedSshView connectingView = _embeddedSessionManager.CreateConnectingSshHostControl(
            tab, displayName, server, settings);
        tab.HostControl = connectingView;
        _connectingSshViews[sessionId] = connectingView;

        // The view's Cancel button ends the attempt through the same token a tab close would.
        if (connectingView is not null)
        {
            connectingView.CancelConnectRequested += () =>
            {
                try
                {
                    cancellationSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The attempt already finished and released its token.
                }
            };
        }
    }

    /// <summary>
    /// Removes the SSH placeholder tab if the connect attempt fails before
    /// SessionReady can attach the real session.
    /// </summary>
    private void OnSessionStartFailed(string sessionId)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            InvokeOnUi(() => OnSessionStartFailed(sessionId));
            return;
        }

        bool cancellationRequested = ReleaseConnectingCancellation(sessionId);
        _connectingSshViews.TryRemove(sessionId, out _);
        ReconnectChainState? reconnectChain = RemoveReconnectChainSession(sessionId);
        if (reconnectChain is not null)
        {
            reconnectChain.FailureObserved = true;
            if (cancellationRequested)
            {
                reconnectChain.UserCancelled = true;
                reconnectChain.CancellationSource.Cancel();
            }
        }

        SessionTabViewModel? tab = _main.Connection.ActiveSessions.FirstOrDefault(
            t => string.Equals(t.ServerId, sessionId, StringComparison.Ordinal));
        if (tab is null)
        {
            if (reconnectChain is not null)
            {
                reconnectChain.FailureCleanupTask = Task.CompletedTask;
            }

            return;
        }

        // Silent: this is the placeholder tab of a session that never materialized. There is no
        // host, therefore no work to protect and nobody to ask - and a guard withholding it would
        // strand a dead placeholder in the tab strip.
        Task cleanupTask = _main.Connection.CloseSessionAsync(
            tab,
            DisconnectReason.FailedSession,
            confirm: false,
            CloseIntent.Silent);
        if (reconnectChain is not null)
        {
            reconnectChain.FailureCleanupTask = cleanupTask;
        }
        else
        {
            cleanupTask.SafeFireAndForget();
        }
    }

    /// <summary>
    /// Handles the session-ready event from <see cref="ServerListViewModel"/>
    /// by creating a session tab in <see cref="ConnectionViewModel"/>,
    /// recording the connect in the history log, resolving the tunnel
    /// route for the header display, and optionally auto-opening an SFTP
    /// companion pane for SSH connections when
    /// <see cref="AppSettings.SftpAutoOpenOnSsh"/> is enabled.
    /// </summary>
    private void OnSessionReady(
        string sessionId,
        string originalServerId,
        string displayName,
        string connectionType,
        ISessionResult? session,
        RdpModeOverride rdpModeOverride)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            InvokeOnUi(() => OnSessionReady(
                sessionId,
                originalServerId,
                displayName,
                connectionType,
                session,
                rdpModeOverride));
            return;
        }

        ConnectionHistory.RecordConnect(originalServerId, displayName, connectionType);

        if (session is null)
        {
            if (string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase))
            {
                if (rdpModeOverride != RdpModeOverride.UseProfile)
                {
                    var externalTab = _main.Connection.AddSession(sessionId, displayName, connectionType);
                    externalTab.OriginalServerId = originalServerId;
                    externalTab.FailureDetails = null;
                    ApplyRdpModeOverride(externalTab, connectionType, rdpModeOverride);
                    externalTab.Status = SessionStatusTokens.LaunchedExternalClient;
                }

                _main.StatusText = _localizer["StatusLaunchedExternalClient"];
            }
            else
            {
                _main.StatusText = _localizer.Format("StatusConnected", displayName);
            }

            return;
        }

        if (string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase)
            && session is SshSessionResult or TerminalSessionResult)
        {
            var existingTab = _main.Connection.ActiveSessions.FirstOrDefault(
                t => string.Equals(t.ServerId, sessionId, StringComparison.Ordinal));
            if (existingTab is not null)
            {
                existingTab.OriginalServerId = originalServerId;
                existingTab.FailureDetails = null;
                ReleaseConnectingCancellation(sessionId);
                _connectingSshViews.TryRemove(sessionId, out _);
                try
                {
                    _embeddedSessionManager.AttachSshSession(existingTab, session, _main.CurrentSettings);
                }
                catch
                {
                    // The view never took the session: close it here and drop the placeholder, so
                    // neither a live connection nor a tab stuck in Connecting is left behind.
                    _main.Connection.CloseFailedMaterialization(existingTab);
                    SafeDisposeSessionResult(session);
                    throw;
                }

                existingTab.Status = SessionStatusTokens.Connected;
                CompleteReadySession(existingTab, sessionId, originalServerId, displayName, connectionType, session);
                CompleteReconnectChainForSession(sessionId);
                return;
            }

            if (_connectingCancellations.TryGetValue(sessionId, out ConnectingSessionCancellation? connecting)
                && connecting.IsCancellationRequested)
            {
                // The placeholder mounted for this attempt was closed (or its Cancel pressed): the
                // user walked away from it, so the late session is closed instead of reopening a
                // tab. A placeholder that only moved to a floating window is not cancelled.
                FileLogger.Info($"SessionReady for SSH sessionId={sessionId} arrived after its tab was closed; discarding it.");
                SafeDisposeSessionResult(session);
                throw new OperationCanceledException();
            }

            FileLogger.Warn(
                $"SessionReady for SSH sessionId={sessionId} had no pre-mounted tab; falling back to legacy materialization.");
        }

        int maxEmbeddedSessions = _main.CurrentSettings?.MaxEmbeddedSessions
            ?? AppSettings.DefaultMaxEmbeddedSessions;
        var tab = _main.Connection.AddSession(
            sessionId,
            displayName,
            connectionType,
            maxEmbeddedSessions);
        if (tab is null)
        {
            SafeDisposeSessionResult(session);
            return;
        }

        tab.OriginalServerId = originalServerId;
        tab.FailureDetails = null;
        ApplyRdpModeOverride(tab, connectionType, rdpModeOverride);
        bool hostOwnsSession = false;
        try
        {
            object hostControl = _embeddedSessionManager.CreateHostControl(
                tab,
                displayName,
                connectionType,
                session,
                _main.CurrentSettings);
            tab.HostControl = hostControl;
            hostOwnsSession = true;

            tab.Status = string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase)
                ? SessionStatusTokens.Connecting
                : string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase)
                    ? SessionStatusTokens.RemoteSessionHandedOff
                    : SessionStatusTokens.Connected;

            CompleteReadySession(tab, sessionId, originalServerId, displayName, connectionType, session);
        }
        catch
        {
            if (!hostOwnsSession)
            {
                SafeDisposeSessionResult(session);
            }

            _main.Connection.CloseFailedMaterialization(tab);
            throw;
        }
    }

    private void CompleteReadySession(
        SessionTabViewModel tab,
        string sessionId,
        string originalServerId,
        string displayName,
        string connectionType,
        ISessionResult session)
    {
        // Resolve tunnel chain route for visual display in session header
        // (uses sessionId - correct for state machine lookup)
        tab.TunnelRoute = _main.Tunnels.ResolveRoute(sessionId);

        _main.StatusText = string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase)
            ? _localizer.Format("StatusEmbeddedRdpOpening", displayName)
            : _localizer.Format("StatusConnected", displayName);

        // Auto-open SFTP alongside SSH - use original server ID for inventory lookup
        if (string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase)
            && _main.CurrentSettings?.SftpBrowserEnabled == true
            && _main.CurrentSettings?.SftpAutoOpenOnSsh == true)
        {
            AutoOpenSftpAsync(tab, originalServerId, _main.Split.GetSessionToken(tab))
                .SafeFireAndForget();
        }

        if (string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase)
            && session is SshSessionResult sshSession)
        {
            RunPostConnectSequenceAsync(tab, originalServerId, displayName, sshSession, _main.Split.GetSessionToken(tab))
                .SafeFireAndForget();
        }
    }

    private void ApplyRdpModeOverride(
        SessionTabViewModel tab,
        string connectionType,
        RdpModeOverride rdpModeOverride)
    {
        if (!string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase)
            || rdpModeOverride == RdpModeOverride.UseProfile)
        {
            return;
        }

        tab.RdpModeOverride = rdpModeOverride;
        tab.RdpModeOverrideSuffix = RdpModeOverrideSuffixFor(rdpModeOverride);
    }

    /// <summary>The worded title suffix of a forced RDP mode, in the current language.</summary>
    private string RdpModeOverrideSuffixFor(RdpModeOverride rdpModeOverride) => rdpModeOverride switch
    {
        RdpModeOverride.ForceEmbedded => _localizer["SessionTitleSuffixForcedEmbedded"],
        RdpModeOverride.ForceExternal => _localizer["SessionTitleSuffixForcedExternal"],
        _ => string.Empty
    };

    /// <summary>
    /// Creates a failed session tab so diagnostics can be inspected after the connection flow aborts.
    /// </summary>
    private void OnSessionFailed(
        string sessionId,
        string originalServerId,
        string displayName,
        string connectionType,
        string statusText,
        SessionDiagnostic diagnostic)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            InvokeOnUi(() => OnSessionFailed(
                sessionId,
                originalServerId,
                displayName,
                connectionType,
                statusText,
                diagnostic));
            return;
        }

        var tab = _main.Connection.AddSession(sessionId, displayName, connectionType);
        tab.OriginalServerId = originalServerId;
        // Free-form on purpose: a failed pane shows why it failed, and the display converter
        // passes an unrecognised value through unchanged for exactly this. No token would say as
        // much, and a failed session is not counted as live either way.
        tab.Status = statusText;
        tab.FailureDetails = diagnostic;
        tab.TunnelRoute = _main.Tunnels.ResolveRoute(sessionId);
        _main.StatusText = statusText;
    }

    /// <summary>
    /// Closes the disconnected session tab and starts a fresh connection
    /// to the same server, reusing the standard connection flow. Sync
    /// entry point wired as the <see cref="IEmbeddedSessionManager"/>
    /// callback; delegates to <see cref="OnReconnectRequestedAsync"/>.
    /// </summary>
    // Reconnects requested while the vault workspace is locked: the stored
    // credential cannot be decrypted (CredentialProtector.Unprotect throws
    // VaultLockedException), so they are queued here and replayed after unlock
    // instead of being attempted (and failing/throwing) or wasting bounded retries.
    private readonly List<(
        SessionTabViewModel Tab,
        string ServerId,
        string ConnectionType,
        ReconnectRequestContext Context)> _deferredReconnects = new();
    private readonly List<(SessionTabViewModel Tab, string PaneId)> _deferredPaneReconnects = new();

    private void OnReconnectRequested(SessionTabViewModel tab, string serverId, string connectionType)
    {
        ReconnectRequestContext context = EmbeddedSessionManager.TakeReconnectRequest(tab);
        if (VaultReconnectPolicy.ShouldDeferReconnect(_main.IsWorkspaceLocked))
        {
            if (!_deferredReconnects.Any(d => ReferenceEquals(d.Tab, tab)))
            {
                _deferredReconnects.Add((tab, serverId, connectionType, context));
                FileLogger.Info("Reconnect deferred: vault workspace locked.");
            }

            return;
        }

        OnReconnectRequestedAsync(tab, serverId, connectionType, context).SafeFireAndForget();
    }

    /// <summary>
    /// Finds the tab a host's pane sits in now, and whether the pane is one of several in it.
    /// </summary>
    /// <remarks>
    /// A host is wired to the tab it was created for, and a merge, a detach or a split moves its
    /// pane elsewhere. The pane is looked up among the open tabs and the creating tab; a pane
    /// found nowhere falls back to the creating tab, which is what the host used to act on.
    /// </remarks>
    internal static (SessionTabViewModel Owner, bool IsOneOfSeveralPanes) ResolveHostOwner(
        IEnumerable<SessionTabViewModel> openTabs,
        SessionTabViewModel createdFor,
        SessionPaneModel? pane)
    {
        ArgumentNullException.ThrowIfNull(openTabs);
        ArgumentNullException.ThrowIfNull(createdFor);
        if (pane is null)
        {
            return (createdFor, false);
        }

        SessionTabViewModel? owner = openTabs
            .Prepend(createdFor)
            .FirstOrDefault(tab => ReferenceEquals(SplitTreeHelper.FindPane(tab.RootContent, pane.PaneId), pane));
        if (owner is null)
        {
            return (createdFor, false);
        }

        return (owner, owner.IsSplit);
    }

    private void OnHostReconnectRequested(SessionTabViewModel createdFor, SessionPaneModel? pane)
    {
        (SessionTabViewModel owner, bool oneOfSeveral) =
            ResolveHostOwner(_main.Connection.AllOpenSessions, createdFor, pane);
        if (oneOfSeveral)
        {
            // The tab-level request recorded for an SSH host is not used by a pane reconnect.
            _ = EmbeddedSessionManager.TakeReconnectRequest(createdFor);
            OnReconnectPaneRequested(owner, pane!);
            return;
        }

        if (!ReferenceEquals(owner, createdFor))
        {
            EmbeddedSessionManager.MoveReconnectRequest(createdFor, owner);
        }

        OnReconnectRequested(owner, owner.ProfileLookupServerId, owner.ConnectionType);
    }

    private void OnHostCloseRequested(SessionTabViewModel createdFor, SessionPaneModel? pane)
    {
        (SessionTabViewModel owner, bool oneOfSeveral) =
            ResolveHostOwner(_main.Connection.AllOpenSessions, createdFor, pane);
        if (oneOfSeveral)
        {
            // The full pane-close path: a guard that defers is asked, retried and released. Closing
            // the pane directly ignored the deferral, so a disconnected SFTP pane holding unsaved
            // edits did nothing at all when its Close button was pressed.
            _main.ClosePaneAsync(owner, pane!.PaneId, DisconnectReason.UserAction).SafeFireAndForget();
            return;
        }

        OnCloseRequested(owner);
    }

    private void OnReconnectPaneRequested(SessionTabViewModel tab, SessionPaneModel pane)
    {
        if (VaultReconnectPolicy.ShouldDeferReconnect(_main.IsWorkspaceLocked))
        {
            if (!_deferredPaneReconnects.Any(d =>
                    ReferenceEquals(d.Tab, tab)
                    && string.Equals(d.PaneId, pane.PaneId, StringComparison.Ordinal)))
            {
                _deferredPaneReconnects.Add((tab, pane.PaneId));
                FileLogger.Info("Pane reconnect deferred: vault workspace locked.");
            }

            return;
        }

        _main.Split.ReconnectPaneAsync(tab, pane.PaneId).SafeFireAndForget();
    }

    /// <summary>
    /// Replay reconnects that were deferred while the workspace was locked. Called
    /// after a successful unlock.
    /// </summary>
    public void ResumeDeferredReconnects()
    {
        if (_deferredReconnects.Count == 0 && _deferredPaneReconnects.Count == 0)
        {
            return;
        }

        // A tab or pane closed while the vault was locked was given up by the user: replaying its
        // reconnect would bring it back.
        var openTabs = _main.Connection.AllOpenSessions.ToHashSet();
        var pending = _deferredReconnects
            .Where(d => openTabs.Contains(d.Tab))
            .ToList();
        _deferredReconnects.Clear();
        var pendingPanes = _deferredPaneReconnects
            .Where(d => openTabs.Contains(d.Tab) && SplitTreeHelper.FindPane(d.Tab.RootContent, d.PaneId) is not null)
            .ToList();
        _deferredPaneReconnects.Clear();
        foreach ((
            SessionTabViewModel tab,
            string serverId,
            string connectionType,
            ReconnectRequestContext context) in pending)
        {
            OnReconnectRequestedAsync(tab, serverId, connectionType, context).SafeFireAndForget();
        }

        foreach (var (tab, paneId) in pendingPanes)
        {
            _main.Split.ReconnectPaneAsync(tab, paneId).SafeFireAndForget();
        }
    }

    /// <summary>
    /// Public entry point for "Reconnect Session" UI actions (tab context menu,
    /// keyboard shortcut). Resolves the persisted server id from
    /// <see cref="SessionTabViewModel.OriginalServerId"/> (falling back to the
    /// session id) and routes through the same close-then-reconnect path used
    /// by the disconnect overlay so the old tab is always replaced.
    /// </summary>
    public void ReconnectSession(SessionTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (tab.IsAdHoc && tab.AdHocProfileSnapshot is ServerProfileDto snapshot)
        {
            ServerProfileDto runtimeProfile = CloneAdHocProfileForConnection(snapshot);
            OnReconnectAdHocRequestedAsync(tab, snapshot, runtimeProfile).SafeFireAndForget();
            return;
        }

        string serverId = tab.ProfileLookupServerId;

        if (string.IsNullOrEmpty(serverId))
        {
            return;
        }

        OnReconnectRequested(tab, serverId, tab.ConnectionType);
    }

    /// <summary>
    /// Opens another connection for the supplied tab while retaining the source tab.
    /// Ad-hoc tabs reconnect from their immutable profile snapshot; persisted tabs keep
    /// using the inventory-backed connect command.
    /// </summary>
    public void DuplicateSession(SessionTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (tab.IsAdHoc && tab.AdHocProfileSnapshot is ServerProfileDto snapshot)
        {
            ServerProfileDto runtimeProfile = CloneAdHocProfileForConnection(snapshot);
            ConnectAdHocProfileAsync(snapshot, runtimeProfile).SafeFireAndForget();
            return;
        }

        string lookupId = tab.ProfileLookupServerId;
        if (string.IsNullOrEmpty(lookupId) || _main.ServerList.ConnectCommand is null)
        {
            return;
        }

        ServerItemViewModel? server = _main.ServerList.Servers.FirstOrDefault(
            candidate => string.Equals(candidate.Id, lookupId, StringComparison.Ordinal));
        if (server is not null)
        {
            _main.ServerList.ConnectCommand.Execute(server);
        }
    }

    private void OnDisconnectRequested(
        SessionTabViewModel tab,
        SessionPaneModel pane,
        DisconnectReason reason)
    {
        OnDisconnectRequestedAsync(tab, pane, reason).SafeFireAndForget();
    }

    /// <summary>
    /// Closes the entire session tab when the user clicks the "Close" button
    /// on a disconnect overlay. Routes through the shared lifecycle so the
    /// embedded host is disposed, tunnels are released, and the tab is
    /// removed from <c>ConnectionViewModel.ActiveSessions</c>.
    /// </summary>
    private void OnCloseRequested(SessionTabViewModel tab)
    {
        OnCloseRequestedAsync(tab).SafeFireAndForget();
    }

    private async Task OnCloseRequestedAsync(SessionTabViewModel tab)
    {
        var title = tab.Title;
        FileLogger.Info($"Overlay close requested: title='{title}'");

        PaneCloseResult result = await _main.Connection.CloseSessionAsync(
            tab, DisconnectReason.UserAction, confirm: false);

        // Defensive guard mirrors OnReconnectRequestedAsync: if the standard
        // close path failed to remove the tab from the collection for any
        // reason, force the removal so the user actually sees the tab close.
        //
        // Gated on the outcome, and that gate is load-bearing. A tab that survives because a close
        // guard withheld it is not the failure this block exists to paper over: forcing it out here
        // would make the tab vanish from the UI while CloseAllPanes never ran, leaving the host
        // undisposed, the tunnel reference unreleased and the transfer still going. A veto has to
        // leave the tab exactly where it is.
        if (result.IsClosed && _main.Connection.ActiveSessions.Contains(tab))
        {
            FileLogger.Warn(
                $"Overlay close: forcing removal of orphan tab title='{title}' " +
                $"(CloseSessionAsync did not remove it)");
            _main.Connection.ActiveSessions.Remove(tab);
            if (ReferenceEquals(_main.Connection.ActiveSession, tab))
            {
                _main.Connection.ActiveSession =
                    _main.Connection.ActiveSessions.LastOrDefault();
            }

            _main.Connection.HasActiveSessions =
                _main.Connection.ActiveSessions.Count > 0;
        }
    }

    private void OnEditServerRequested(string serverId)
    {
        OnEditServerRequestedAsync(serverId).SafeFireAndForget();
    }

    private async Task OnEditServerRequestedAsync(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return;
        }

        // A quick-connect session has no saved profile to edit: its Edit profile opens the session
        // dialog on what was typed, the dialog the tab menu's Save as profile opens. Resolved by the
        // open tab rather than by the identifier's prefix, which a saved profile may share.
        ServerProfileDto? adHocSnapshot = _main.Connection.ActiveSessions
            .FirstOrDefault(tab => tab.IsAdHoc
                && string.Equals(tab.ProfileLookupServerId, serverId, StringComparison.Ordinal))
            ?.AdHocProfileSnapshot;
        if (adHocSnapshot is not null)
        {
            await _main.ServerList.SaveAdHocAsProfileCommand.ExecuteAsync(adHocSnapshot);
            return;
        }

        try
        {
            if (!await _main.ServerList.EditServerByIdAsync(serverId, CancellationToken.None))
            {
                _main.StatusText = _localizer["ErrorServerNotFound"];
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Open server profile failed for {serverId}", ex);
            _main.StatusText = _localizer["ErrorServerNotFound"];
        }
    }

    private async Task OnReconnectRequestedAsync(
        SessionTabViewModel tab,
        string serverId,
        string connectionType,
        ReconnectRequestContext context)
    {
        if (string.IsNullOrEmpty(serverId))
        {
            return;
        }

        // A quick-connect tab is not in the inventory: it reconnects from its own snapshot, as the
        // tab menu already did. The session's Reconnect button lands here instead, and used to
        // close the tab and report "not found".
        if (tab.IsAdHoc && tab.AdHocProfileSnapshot is ServerProfileDto snapshot)
        {
            await OnReconnectAdHocRequestedAsync(tab, snapshot, CloneAdHocProfileForConnection(snapshot));
            return;
        }

        ReconnectChainState? reconnectChain = null;
        if (context.IsAutomatic)
        {
            reconnectChain = new ReconnectChainState(
                tab,
                serverId,
                connectionType,
                context.Attempt,
                context.MaxAttempts);
            lock (_reconnectChainGate)
            {
                _activeReconnectChains.Add(reconnectChain);
            }

            FileLogger.Info(
                $"Auto-reconnect chain {reconnectChain.LineageId} started: " +
                $"title='{reconnectChain.SourceTab.Title}' type={reconnectChain.ConnectionType} " +
                $"attempt={reconnectChain.Attempt}/{reconnectChain.MaxAttempts}.");
        }

        try
        {
            int oldTabCountBefore = _main.Connection.ActiveSessions.Count;
            bool oldTabWasPresent = _main.Connection.ActiveSessions.Contains(tab);
            FileLogger.Info(
                $"Reconnect requested: serverId={serverId} connectionType={connectionType} " +
                $"oldTabPresent={oldTabWasPresent} activeTabs={oldTabCountBefore}");

            // Close the old tab (disposes the dead session). Silent: the session being replaced is
            // already dead, there is no live work to protect and no question worth asking - and a
            // guard that withheld it here would strand the user between two tabs.
            PaneCloseResult closeResult = await _main.Connection.CloseSessionAsync(
                tab,
                DisconnectReason.ReconnectInitiated,
                confirm: false,
                CloseIntent.Silent);

            bool stillPresentAfterClose = _main.Connection.ActiveSessions.Contains(tab);
            FileLogger.Info(
                $"Reconnect: post-close oldTabStillPresent={stillPresentAfterClose} " +
                $"activeTabs={_main.Connection.ActiveSessions.Count}");

            // Defensive guard: if the standard close path did not remove the
            // tab (unexpected - see logs for the original failure), force the
            // removal so the user never sees a stale tab next to the new
            // connection. Production bug observed 2026-05-16: in some real
            // sessions the tab persisted after a clean CloseSessionAsync call
            // even though unit tests reproduced the removal correctly.
            //
            // Gated on the outcome for the same reason as the overlay path: a tab still present
            // because a guard withheld it must stay put, host and all, rather than be forced out
            // of the collection while its panes were never torn down.
            if (closeResult.IsClosed && stillPresentAfterClose)
            {
                FileLogger.Warn(
                    $"Reconnect: forcing removal of orphan tab serverId={serverId} " +
                    $"(CloseSessionAsync did not remove it)");
                _main.Connection.ActiveSessions.Remove(tab);
                if (ReferenceEquals(_main.Connection.ActiveSession, tab))
                {
                    _main.Connection.ActiveSession =
                        _main.Connection.ActiveSessions.LastOrDefault();
                }

                _main.Connection.HasActiveSessions =
                    _main.Connection.ActiveSessions.Count > 0;
            }

            if (reconnectChain is not null)
            {
                await RunReconnectAttemptAsync(reconnectChain);
                return;
            }

            IReadOnlyList<ServerProfileDto> servers = await _configManager.LoadServersAsync();
            if (!servers.Any(server => string.Equals(server.Id, serverId, StringComparison.Ordinal)))
            {
                _main.StatusText = _localizer["ErrorServerNotFound"];
                return;
            }

            // The tab's forced mode travels with the reconnect. A session opened as "force
            // embedded" over a profile whose mode is External used to reconnect from the profile,
            // so the overlay's Reconnect button launched the external client and closed the
            // embedded tab whose title still carried the forced suffix.
            await _main.ServerList.RestoreServerAsync(
                serverId,
                CancellationToken.None,
                tab.RdpModeOverride);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Reconnect failed for {serverId}", ex);
            _main.StatusText = _localizer.Format("StatusReconnectFailed", ex.Message);
            if (reconnectChain is not null)
            {
                reconnectChain.FailureObserved = true;
                await ContinueReconnectChainAfterFailureAsync(reconnectChain);
            }
        }
    }

    private async Task RunReconnectAttemptAsync(ReconnectChainState reconnectChain)
    {
        if (reconnectChain.UserCancelled || reconnectChain.CancellationSource.IsCancellationRequested)
        {
            CompleteReconnectChain(reconnectChain);
            return;
        }

        reconnectChain.FailureObserved = false;
        reconnectChain.FailureCleanupTask = Task.CompletedTask;
        _currentReconnectChain.Value = reconnectChain;
        bool restored;
        try
        {
            restored = await _main.ServerList.RestoreServerAsync(
                reconnectChain.ServerId,
                reconnectChain.CancellationSource.Token,
                reconnectChain.SourceTab.RdpModeOverride,
                ReportFor(reconnectChain));
        }
        catch (OperationCanceledException) when (reconnectChain.CancellationSource.IsCancellationRequested)
        {
            reconnectChain.UserCancelled = true;
            CompleteReconnectChain(reconnectChain);
            return;
        }
        catch (Exception ex)
        {
            FileLogger.Error(
                $"Auto-reconnect attempt {reconnectChain.Attempt} failed for {reconnectChain.ServerId}",
                ex);
            reconnectChain.FailureObserved = true;
            restored = false;
        }
        finally
        {
            _currentReconnectChain.Value = null;
        }

        if (restored)
        {
            CompleteReconnectChain(reconnectChain);
            return;
        }

        await reconnectChain.FailureCleanupTask;

        // Not every failure goes through a placeholder (a preflight refusal, a Telnet session):
        // the attempt itself said it did not restore, and a silent attempt must not end the
        // chain without the last one reporting.
        reconnectChain.FailureObserved = true;

        await ContinueReconnectChainAfterFailureAsync(reconnectChain);
    }

    /// <summary>
    /// Only the last attempt of an automatic chain reports its failure, and never with a modal:
    /// the user did not ask for these attempts and would otherwise get one dialog per retry.
    /// </summary>
    private static ConnectFailureReport ReportFor(ReconnectChainState reconnectChain)
        => ReportForAttempt(reconnectChain.Attempt, reconnectChain.MaxAttempts);

    internal static ConnectFailureReport ReportForAttempt(int attempt, int maxAttempts)
        => attempt >= maxAttempts ? ConnectFailureReport.FailedTabOnly : ConnectFailureReport.Silent;

    private async Task ContinueReconnectChainAfterFailureAsync(ReconnectChainState reconnectChain)
    {
        if (reconnectChain.UserCancelled
            || reconnectChain.CancellationSource.IsCancellationRequested
            || reconnectChain.Attempt >= reconnectChain.MaxAttempts)
        {
            CompleteReconnectChain(reconnectChain);
            return;
        }

        reconnectChain.Attempt++;
        int delaySeconds = EmbeddedSshView.ComputeAutoReconnectDelaySeconds(
            _main.CurrentSettings,
            reconnectChain.Attempt);
        try
        {
            await ReconnectDelayAsync(
                TimeSpan.FromSeconds(delaySeconds),
                reconnectChain.CancellationSource.Token);
        }
        catch (OperationCanceledException) when (reconnectChain.CancellationSource.IsCancellationRequested)
        {
            reconnectChain.UserCancelled = true;
            CompleteReconnectChain(reconnectChain);
            return;
        }
        catch (Exception ex)
        {
            FileLogger.Error(
                $"Auto-reconnect scheduler failed for chain {reconnectChain.LineageId}",
                ex);
            CompleteReconnectChain(reconnectChain);
            return;
        }

        await RunReconnectAttemptAsync(reconnectChain);
    }

    private async Task OnReconnectAdHocRequestedAsync(
        SessionTabViewModel tab,
        ServerProfileDto snapshot,
        ServerProfileDto runtimeProfile)
    {
        try
        {
            AppSettings settings = await _configManager.LoadSettingsAsync();

            // Silent, aligned with the standard reconnect: the session being replaced is already
            // dead, and a guard withholding it would strand the user between two tabs.
            await _main.Connection.CloseSessionAsync(
                tab,
                DisconnectReason.ReconnectInitiated,
                confirm: false,
                CloseIntent.Silent);

            await ConnectAdHocProfileAsync(snapshot, runtimeProfile, settings);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Ad-hoc reconnect failed for {snapshot.Id}", ex);
            _main.StatusText = _localizer.Format("StatusReconnectFailed", ex.Message);
        }
    }

    private async Task ConnectAdHocProfileAsync(
        ServerProfileDto snapshot,
        ServerProfileDto runtimeProfile)
    {
        try
        {
            AppSettings settings = await _configManager.LoadSettingsAsync();
            await ConnectAdHocProfileAsync(snapshot, runtimeProfile, settings);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Ad-hoc duplicate failed for {snapshot.Id}", ex);
            _main.StatusText = _localizer["ErrorConnectionFailed"];
        }
    }

    private async Task ConnectAdHocProfileAsync(
        ServerProfileDto snapshot,
        ServerProfileDto runtimeProfile,
        AppSettings settings)
    {
        string connectionType = runtimeProfile.ConnectionType.ToUpperInvariant();
        ConnectionResult result = connectionType switch
        {
            "RDP" => await _main.ServerList.ConnectionService.ConnectRdpAsync(runtimeProfile, settings),
            "SFTP" => await _main.ServerList.ConnectionService.ConnectSftpAsync(runtimeProfile, settings),
            "VNC" => await _main.ServerList.ConnectionService.ConnectVncAsync(runtimeProfile, settings),
            "TELNET" => await _main.ServerList.ConnectionService.ConnectTelnetAsync(runtimeProfile, settings),
            _ => await _main.ServerList.ConnectionService.ConnectSshAsync(runtimeProfile, settings),
        };

        if (result.Success && result.Session is not null)
        {
            SessionTabViewModel? tab = _main.Connection.AddSession(
                runtimeProfile.Id,
                runtimeProfile.DisplayName,
                connectionType,
                settings.MaxEmbeddedSessions);
            if (tab is null)
            {
                SafeDisposeSessionResult(result.Session);
                return;
            }

            tab.MarkAsAdHoc(snapshot);
            try
            {
                tab.HostControl = _embeddedSessionManager.CreateHostControl(
                    tab,
                    runtimeProfile.DisplayName,
                    connectionType,
                    result.Session,
                    settings);
            }
            catch
            {
                // No host took the session: close it and drop the tab rather than leave one
                // showing Connecting forever over a connection nobody can reach.
                SafeDisposeSessionResult(result.Session);
                _main.Connection.CloseFailedMaterialization(tab);
                throw;
            }

            tab.Status = SessionStatusTokens.Connected;
            _main.StatusText = _localizer.Format(
                "StatusConnected",
                !string.IsNullOrWhiteSpace(runtimeProfile.DisplayName)
                    ? runtimeProfile.DisplayName
                    : runtimeProfile.RemoteServer);
            return;
        }

        if (result.Success)
        {
            SessionTabViewModel tab = _main.Connection.AddSession(
                runtimeProfile.Id,
                runtimeProfile.DisplayName,
                connectionType);
            tab.MarkAsAdHoc(snapshot);
            tab.Status = SessionStatusTokens.LaunchedExternalClient;
            _main.StatusText = _localizer["StatusLaunchedExternalClient"];
            return;
        }

        _main.StatusText = result.ErrorMessage ?? _localizer["ErrorConnectionFailed"];
    }

    /// <summary>
    /// Runtime copy of an ad-hoc snapshot, carrying its own session-scoped identifier.
    /// </summary>
    /// <remarks>
    /// The JSON round-trip this replaced did not survive the presence flags, so reconnecting an
    /// ad-hoc SSH session silently dropped
    /// <see cref="ServerProfileDto.UsesLegacySshCredentialMapping"/>: the stored password stopped
    /// being offered as the key passphrase, and the reconnect could fail where the first connection
    /// had succeeded.
    /// </remarks>
    private static ServerProfileDto CloneAdHocProfileForConnection(ServerProfileDto snapshot)
    {
        ServerProfileDto runtimeProfile = snapshot.CloneFaithfully();
        runtimeProfile.AdoptSessionIdentity(SessionIdCodec.Create(snapshot.Id));
        return runtimeProfile;
    }

    private async Task OnDisconnectRequestedAsync(
        SessionTabViewModel createdFor,
        SessionPaneModel pane,
        DisconnectReason reason)
    {
        // Resolved like Reconnect and Close: a merge or a detach moves the pane out of the tab the
        // host was created for, and returning there left the view showing Disconnecting for good
        // over a session that stayed connected.
        (SessionTabViewModel tab, _) = ResolveHostOwner(_main.Connection.AllOpenSessions, createdFor, pane);
        if (SplitTreeHelper.FindPane(tab.RootContent, pane.PaneId) is null)
        {
            FileLogger.Warn($"Disconnect request for pane '{pane.Title}' found no tab holding it.");
            return;
        }

        if (tab.IsSplit)
        {
            // A disconnect the user asked for, so the pane's guard is consulted like any other
            // interactive close - it is not a programmatic teardown.
            await _main.ClosePaneAsync(tab, pane.PaneId, reason);
            return;
        }

        await _main.Connection.CloseSessionAsync(tab, reason, confirm: false);
    }

    /// <summary>
    /// Automatically connects an SFTP session and attaches it as the
    /// secondary split pane of an existing SSH session tab. UI work is
    /// marshaled to the dispatcher because
    /// <see cref="ServerListViewModel.ConnectionService.ConnectSftpAsync"/>
    /// runs on a background thread.
    /// </summary>
    private async Task AutoOpenSftpAsync(SessionTabViewModel tab, string serverId, CancellationToken ct = default)
    {
        try
        {
            var servers = await _configManager.LoadServersAsync();
            var server = servers.FirstOrDefault(
                s => string.Equals(s.Id, serverId, StringComparison.Ordinal));

            if (server is null || string.IsNullOrEmpty(server.SshUsername))
            {
                FileLogger.Info(
                    $"SFTP auto-open skipped for {serverId}: server not found or no SSH username.");
                return;
            }

            var sftpSessionId = BuildCompanionSftpSessionId(tab.ServerId);
            var sftpProfile = CreateCompanionSftpProfile(server, sftpSessionId);

            var sftpResult = await _main.ServerList.ConnectionService
                .ConnectSftpAsync(sftpProfile, _main.CurrentSettings!, ct)
                .ConfigureAwait(false);

            if (!sftpResult.Success || sftpResult.Session is null)
            {
                FileLogger.Warn(
                    $"SFTP auto-open failed for {serverId}: {sftpResult.ErrorMessage}");
                InvokeOnUi(() =>
                    _main.StatusText = _localizer.Format("StatusSftpAutoOpenFailed", sftpResult.ErrorMessage ?? ""));
                return;
            }

            if (ct.IsCancellationRequested)
            {
                SafeDisposeSessionResult(sftpResult.Session);
                _main.Split.CleanupOrphanedPane(sftpSessionId);
                return;
            }

            // Create the SFTP host control on the UI thread and wrap root in a split container
            var attached = false;
            InvokeOnUi(() =>
            {
                if (ct.IsCancellationRequested || !_main.Connection.ActiveSessions.Contains(tab))
                {
                    return;
                }

                var sftpPane = new SessionPaneModel
                {
                    // ServerId is the session/tunnel/state key. The companion SFTP
                    // pane gets a distinct key so closing or reconnecting it cannot
                    // reset the SSH pane's state entry. OriginalServerId remains the
                    // inventory id used for profile lookup and history.
                    ServerId = sftpSessionId,
                    OriginalServerId = serverId,
                    ConnectionType = "SFTP",
                    Title = tab.Title,
                    Status = "Connected"
                };
                sftpPane.HostControl = _embeddedSessionManager.CreateHostControl(
                    tab, tab.Title, "SFTP", sftpResult.Session, _main.CurrentSettings);
                var currentRoot = tab.RootContent;
                tab.RootContent = new SplitContainerModel
                {
                    First = currentRoot,
                    Second = sftpPane,
                    Orientation = SplitOrientation.Vertical,
                    SplitRatio = 0.5
                };
                attached = true;
            });

            if (!attached)
            {
                SafeDisposeSessionResult(sftpResult.Session);
                _main.Split.CleanupOrphanedPane(sftpSessionId);
                return;
            }

            FileLogger.Info($"SFTP auto-open succeeded for {serverId}.");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SFTP auto-open error for {serverId}: {ex.Message}");
        }
    }

    private static string BuildCompanionSftpSessionId(string sshSessionId)
    {
        var baseId = string.IsNullOrWhiteSpace(sshSessionId) ? "ssh" : sshSessionId;
        return $"sftp-{baseId}-{Guid.NewGuid():N}";
    }

    private static ServerProfileDto CreateCompanionSftpProfile(
        ServerProfileDto server,
        string sftpSessionId)
    {
        var profile = new ServerProfileDto
        {
            Id = sftpSessionId,
            DisplayName = server.DisplayName,
            Origin = server.Origin,
            RemoteServer = server.RemoteServer,
            RemotePort = server.RemotePort,
            LocalPort = server.LocalPort,
            Group = server.Group,
            SshGatewayId = server.SshGatewayId,
            UseDirectConnection = server.UseDirectConnection,
            ProjectId = server.ProjectId,
            ConnectionType = "SFTP",
            SessionLoggingOverride = server.SessionLoggingOverride,
            VaultEntryName = server.VaultEntryName,
            SshUsername = server.SshUsername,
            SshPort = server.SshPort,
            SshMode = server.SshMode,
            SshAgentForwarding = server.SshAgentForwarding,
            SshKeyPath = server.SshKeyPath,
            SshPasswordEncrypted = server.SshPasswordEncrypted,
            SshCompression = server.SshCompression,
            SshX11Forwarding = server.SshX11Forwarding,
            SocksProxyPort = server.SocksProxyPort,
            RemoteBindPort = server.RemoteBindPort,
            RemoteLocalPort = server.RemoteLocalPort,
            ExecutionConfirmed = server.ExecutionConfirmed
        };

        if (server.HasSshKeyPassphraseEncryptedField)
        {
            profile.SshKeyPassphraseEncrypted = server.SshKeyPassphraseEncrypted;
        }

        return profile;
    }

    internal static void SafeDisposeSessionResult(ISessionResult? session)
    {
        switch (session)
        {
            case null:
                return;
            case IDisposable disposable:
                SafeDispose(disposable);
                return;
            case SshSessionResult ssh:
                SafeDispose(ssh.Session);
                return;
            case TerminalSessionResult terminal:
                SafeDispose(terminal.Session);
                return;
            case LocalShellBundle local:
                SafeDispose(local.Session);
                return;
            case SftpSessionBundle sftp:
                SafeDispose(sftp.Browser);
                return;
            case FtpSessionBundle ftp:
                SafeDispose(ftp.Browser);
                return;
            case CitrixSessionResult citrix:
                SafeDispose(citrix.Process);
                return;
        }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Unexpected exception during session result disposal: {ex.Message}");
        }
    }

    private async Task RunPostConnectSequenceAsync(
        SessionTabViewModel tab,
        string serverId,
        string displayName,
        SshSessionResult sshSession,
        CancellationToken sessionToken)
    {
        try
        {
            var servers = await _configManager.LoadServersAsync().ConfigureAwait(false);
            var server = servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
            if (server is null || server.PostConnectSteps.Count == 0)
            {
                return;
            }

            using var userCancelCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, userCancelCts.Token);
            var progress = new Progress<PostConnectRunProgress>(update =>
            {
                var current = Math.Min(update.CurrentStepIndex + 1, update.TotalSteps);
                var progressText = $"{current}/{update.TotalSteps}";
                var tooltip = _localizer.Format(
                    "PostConnectProgressTooltip",
                    progressText,
                    LocalizePostConnectStatus(update.Status),
                    update.CurrentStepDisplayText);
                SetPostConnectStateOnUiThread(tab, progressText, tooltip, userCancelCts.Cancel);
            });

            var runnableSteps = server.PostConnectSteps.Count(step =>
                step.Enabled
                && (!string.IsNullOrWhiteSpace(step.Input) || !string.IsNullOrWhiteSpace(step.CommandLibraryId)));
            if (runnableSteps == 0)
            {
                return;
            }

            if (ProfileExecutionTrust.RequiresPostConnectConfirmation(server))
            {
                bool approved = await ConfirmOnUiThreadAsync(
                        () => _main.ServerList.ConfirmAndTrustPostConnectAsync(server, runnableSteps))
                    .ConfigureAwait(false);

                if (!approved)
                {
                    FileLogger.Info(
                        $"Post-connect skipped for {displayName}: unconfirmed imported commands.");
                    return;
                }
            }

            SetPostConnectStateOnUiThread(
                tab,
                $"0/{server.PostConnectSteps.Count}",
                _localizer["PostConnectProgressStarting"],
                userCancelCts.Cancel);

            FileLogger.Info($"Post-connect: starting {runnableSteps} command(s) for {displayName}.");
            var result = await _postConnectSequenceRunner.RunAsync(
                server.PostConnectSteps,
                input => sshSession.Session.Write(input + "\n"),
                progress,
                linkedCts.Token,
                _postConnectStepResolver).ConfigureAwait(false);
            FileLogger.Info(
                $"Post-connect: {displayName} executed={result.StepsExecuted}, " +
                $"skipped={result.StepsSkippedDisabled}, failed={result.StepsFailed}, broken={result.StepsBroken}, " +
                $"cancelled={result.WasCancelled}, stopped={result.WasStoppedByFailurePolicy}.");
        }
        catch (OperationCanceledException)
        {
            // Session closed or user cancelled; state cleanup happens in finally.
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Post-connect run failed for {displayName}: {ex.Message}", ex);
        }
        finally
        {
            ClearPostConnectStateOnUiThread(tab);
        }
    }

    internal void ClearPostConnectStateOnUiThread(SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        InvokeOnUi(tab.ClearPostConnectState);
    }

    // The post-connect run continues on the thread pool once the profiles have been read. The
    // progress state drives a command bound to a button and the confirmation is a modal
    // dialog; touched from the pool, both threw, the catch below logged "Post-connect run
    // failed", and the steps never ran. Only the clear went through the dispatcher.
    internal void SetPostConnectStateOnUiThread(
        SessionTabViewModel tab,
        string progressText,
        string tooltip,
        Action cancel)
    {
        ArgumentNullException.ThrowIfNull(tab);
        InvokeOnUi(() => tab.SetPostConnectState(true, progressText, tooltip, cancel));
    }

    internal async Task<bool> ConfirmOnUiThreadAsync(Func<Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        bool approved = false;
        await _uiDispatcher.InvokeAsync(async () => approved = await confirm()).ConfigureAwait(false);
        return approved;
    }

    private string LocalizePostConnectStatus(PostConnectStepStatus status)
    {
        return status switch
        {
            PostConnectStepStatus.Pending => _localizer["PostConnectProgressPending"],
            PostConnectStepStatus.Running => _localizer["PostConnectProgressRunning"],
            PostConnectStepStatus.Completed => _localizer["PostConnectProgressCompleted"],
            PostConnectStepStatus.Failed => _localizer["PostConnectProgressFailed"],
            PostConnectStepStatus.Skipped => _localizer["PostConnectProgressSkipped"],
            PostConnectStepStatus.Cancelled => _localizer["PostConnectProgressCancelled"],
            PostConnectStepStatus.Broken => _localizer["StatusPostConnectBroken"],
            _ => status.ToString()
        };
    }

    private void InvokeOnUi(Action action)
    {
        _uiDispatcher.Invoke(action);
    }

    /// <summary>
    /// The size the connecting SSH view's page reported for a session id, waiting at most
    /// <paramref name="wait"/> for its first report. Called from the connect thread; the
    /// dictionary and the view's report are safe for it, and nothing here blocks the UI thread.
    /// </summary>
    private Task<TerminalSizeLookup> ResolveConnectingTerminalSizeAsync(
        string sessionId,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        TerminalSizeReport? report = _connectingSshViews.TryGetValue(sessionId, out EmbeddedSshView? view)
            ? view.TerminalSizeReport
            : null;
        return TerminalSizeReport.ResolveAsync(report, wait, cancellationToken);
    }

    private void TrackConnectingCancellation(
        string sessionId,
        SessionTabViewModel tab,
        CancellationTokenSource cancellationSource)
    {
        ReleaseConnectingCancellation(sessionId);

        var tabCloseToken = _main.Split.GetSessionToken(tab);
        var registration = tabCloseToken.CanBeCanceled
            ? tabCloseToken.Register(static state =>
            {
                var source = (CancellationTokenSource)state!;
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }, cancellationSource)
            : default;

        _connectingCancellations[sessionId] = new ConnectingSessionCancellation(
            cancellationSource,
            registration);
    }

    private bool ReleaseConnectingCancellation(string sessionId)
    {
        if (_connectingCancellations.Remove(sessionId, out ConnectingSessionCancellation? cancellation))
        {
            bool cancellationRequested = cancellation.IsCancellationRequested;
            cancellation.Dispose();
            return cancellationRequested;
        }

        return false;
    }

    private ReconnectChainState? RemoveReconnectChainSession(string sessionId)
    {
        lock (_reconnectChainGate)
        {
            if (_reconnectChainsBySessionId.Remove(sessionId, out ReconnectChainState? reconnectChain))
            {
                return reconnectChain;
            }
        }

        return null;
    }

    private void CompleteReconnectChainForSession(string sessionId)
    {
        ReconnectChainState? reconnectChain = RemoveReconnectChainSession(sessionId);
        if (reconnectChain is not null)
        {
            CompleteReconnectChain(reconnectChain);
        }
    }

    private void CompleteReconnectChain(ReconnectChainState reconnectChain)
    {
        bool disposeCancellationSource = false;
        lock (_reconnectChainGate)
        {
            if (reconnectChain.IsCompleted)
            {
                return;
            }

            reconnectChain.IsCompleted = true;
            _activeReconnectChains.Remove(reconnectChain);
            if (!string.IsNullOrEmpty(reconnectChain.CurrentSessionId)
                && _reconnectChainsBySessionId.TryGetValue(
                    reconnectChain.CurrentSessionId,
                    out ReconnectChainState? current)
                && ReferenceEquals(current, reconnectChain))
            {
                _reconnectChainsBySessionId.Remove(reconnectChain.CurrentSessionId);
            }

            disposeCancellationSource = true;
        }

        if (disposeCancellationSource)
        {
            reconnectChain.CancellationSource.Dispose();
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _main.ServerList.SessionStarting -= OnSessionStarting;
        _main.ServerList.SessionStartFailed -= OnSessionStartFailed;
        _main.ServerList.SessionReady -= OnSessionReady;
        _main.ServerList.SessionFailed -= OnSessionFailed;
        _main.Connection.ActiveSessions.CollectionChanged -= OnActiveSessionsChanged;
        foreach (SessionTabViewModel tab in _observedTabs)
        {
            tab.PropertyChanged -= OnObservedTabPropertyChanged;
        }

        _observedTabs.Clear();

        foreach (var cancellation in _connectingCancellations.Values)
        {
            cancellation.Dispose();
        }
        _connectingCancellations.Clear();

        lock (_reconnectChainGate)
        {
            foreach (ReconnectChainState reconnectChain in _activeReconnectChains)
            {
                reconnectChain.CancellationSource.Cancel();
                reconnectChain.CancellationSource.Dispose();
            }

            _activeReconnectChains.Clear();
            _reconnectChainsBySessionId.Clear();
        }

        // The 8 provider/callback wire-ups on Split + EmbeddedSessionManager
        // + ConnectionService are owned by external services and are left
        // in place on shutdown - clearing them could break other teardown
        // paths that still reference them. No harm in leaving the delegate
        // references since the owning services are themselves disposed.
    }

    private sealed class ConnectingSessionCancellation(
        CancellationTokenSource source,
        CancellationTokenRegistration tabCloseRegistration) : IDisposable
    {
        internal bool IsCancellationRequested => source.IsCancellationRequested;

        public void Dispose()
        {
            tabCloseRegistration.Dispose();
            source.Dispose();
        }
    }

    private sealed class ReconnectChainState(
        SessionTabViewModel sourceTab,
        string serverId,
        string connectionType,
        int attempt,
        int maxAttempts)
    {
        internal Guid LineageId { get; } = Guid.NewGuid();

        internal SessionTabViewModel SourceTab { get; } = sourceTab;

        internal string ServerId { get; } = serverId;

        internal string ConnectionType { get; } = connectionType;

        internal int Attempt { get; set; } = attempt;

        internal int MaxAttempts { get; } = maxAttempts;

        internal string? CurrentSessionId { get; set; }

        internal bool FailureObserved { get; set; }

        internal bool UserCancelled { get; set; }

        internal bool IsCompleted { get; set; }

        internal Task FailureCleanupTask { get; set; } = Task.CompletedTask;

        internal CancellationTokenSource CancellationSource { get; } = new CancellationTokenSource();
    }
}
