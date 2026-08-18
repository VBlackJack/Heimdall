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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.ViewModels.Tunnels;

internal interface ITunnelsHost
{
    ConnectionViewModel Connection { get; }

    AppSettings? CurrentSettings { get; }

    string StatusText { get; set; }
}

/// <summary>
/// View-model backing the Tunnels retractable panel and Tunnels tab
/// DataGrid. Owns the active tunnel list, close / copy / close-all
/// commands, and the tunnel-route resolver used by session tabs to
/// display "via GatewayA &#x2192; GatewayB" annotations.
/// </summary>
/// <remarks>
/// Composition: instantiated inside <see cref="MainViewModel"/>'s
/// constructor (<see cref="MainViewModel.Tunnels"/>) — no DI registration.
/// Subscribes to <see cref="TunnelManager.TunnelOpened"/> /
/// <see cref="TunnelManager.TunnelClosed"/> in its constructor and
/// unsubscribes in <see cref="Dispose"/> (which <see cref="MainViewModel.Dispose"/>
/// calls from its own cleanup path).
/// </remarks>
public sealed partial class TunnelsViewModel : ObservableObject, IDisposable
{
    private readonly ITunnelsHost _host;
    private readonly LocalizationManager _localizer;
    private readonly TunnelManager _tunnelManager;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly IConfigManager _configManager;
    private readonly PropertyChangedEventHandler _connectionPropertyChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _activeSessionsCollectionChangedHandler;
    private readonly PropertyChangedEventHandler _tabPropertyChangedHandler;
    private readonly HashSet<SessionTabViewModel> _trackedTabs = [];
    private ObservableCollection<SessionTabViewModel>? _activeSessionsSubscription;
    private AppSettings? _settingsSnapshot;
    private long _panelResolveVersion;
    private bool _disposed;

    /// <summary>
    /// Creates a tunnels VM bound to the shell and wires up the tunnel manager events.
    /// </summary>
    /// <remarks>
    /// Internal on purpose: production builds this through <see cref="ITunnelsViewModelFactory"/>,
    /// which owns the tunnelling collaborators. Leaving it public would let a caller assemble those
    /// dependencies itself and bypass the single composition path.
    /// </remarks>
    internal TunnelsViewModel(
        MainViewModel main,
        LocalizationManager localizer,
        TunnelManager tunnelManager,
        ConnectionStateMachine connectionSm,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        IConfigManager configManager)
        : this(
            (ITunnelsHost)main,
            localizer,
            tunnelManager,
            connectionSm,
            hostKeyStore,
            hostKeyVerifier,
            configManager)
    {
    }

    internal TunnelsViewModel(
        ITunnelsHost host,
        LocalizationManager localizer,
        TunnelManager tunnelManager,
        ConnectionStateMachine connectionSm,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        IConfigManager configManager)
    {
        _host = host;
        _localizer = localizer;
        _tunnelManager = tunnelManager;
        _connectionSm = connectionSm;
        _hostKeyStore = hostKeyStore;
        _hostKeyVerifier = hostKeyVerifier;
        _configManager = configManager;
        _settingsSnapshot = host.CurrentSettings;
        _connectionPropertyChangedHandler = OnConnectionPropertyChanged;
        _activeSessionsCollectionChangedHandler = OnActiveSessionsCollectionChanged;
        _tabPropertyChangedHandler = OnTabPropertyChanged;

        _tunnelManager.TunnelOpened += OnTunnelOpened;
        _tunnelManager.TunnelClosed += OnTunnelClosed;
        _localizer.LocaleChanged += OnLocaleChanged;
        _configManager.SettingsChanged += OnSettingsChanged;
        _host.Connection.PropertyChanged += _connectionPropertyChangedHandler;
        SubscribeToActiveSessions(_host.Connection.ActiveSessions);
    }

    // ── Observable state ─────────────────────────────────────────────

    /// <summary>Whether the retractable tunnels panel in the status bar is expanded.</summary>
    [ObservableProperty]
    private bool _isPanelOpen;

    /// <summary>Live snapshot of active tunnels — bound by the DataGrid.</summary>
    [ObservableProperty]
    private ObservableCollection<TunnelInfo> _list = new();

    /// <summary>Row currently selected in the tunnels DataGrid.</summary>
    [ObservableProperty]
    private TunnelInfo? _selectedItem;

    /// <summary>
    /// Number of active tunnels — bound by the status-bar badge and the
    /// tunnels tab count indicator. Maintained alongside
    /// <see cref="List"/> on every refresh.
    /// </summary>
    [ObservableProperty]
    private int _count;

    /// <summary>True when there are no active tunnels.</summary>
    public bool HasNoTunnels => List.Count == 0;

    /// <summary>Localized tunnel panel header text before the live count.</summary>
    public string TunnelPanelHeaderPrefix
    {
        get
        {
            var header = _localizer["TunnelPanelHeader"];
            var idx = header.IndexOf("{0}", StringComparison.Ordinal);
            return idx >= 0 ? header[..idx] : header;
        }
    }

    /// <summary>Localized tunnel panel header text after the live count.</summary>
    public string TunnelPanelHeaderSuffix
    {
        get
        {
            var header = _localizer["TunnelPanelHeader"];
            var idx = header.IndexOf("{0}", StringComparison.Ordinal);
            return idx >= 0 ? header[(idx + 3)..] : string.Empty;
        }
    }

    /// <summary>
    /// Generated partial: refreshes the <see cref="HasNoTunnels"/>
    /// notification whenever the list collection is replaced.
    /// </summary>
    partial void OnListChanged(ObservableCollection<TunnelInfo> value)
    {
        OnPropertyChanged(nameof(HasNoTunnels));
    }

    // ── Commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Toggles the retractable tunnels panel in the status bar. Bound to
    /// the tunnel icon button.
    /// </summary>
    [RelayCommand]
    private async Task TogglePanelAsync()
    {
        var newValue = !IsPanelOpen;
        var activeTab = _host.Connection.ActiveSession;

        if (activeTab is not null)
        {
            activeTab.TunnelsPanelManualOverride = newValue;
            await PersistPanelOverrideAsync(activeTab, newValue).ConfigureAwait(true);
        }

        IsPanelOpen = newValue;
    }

    internal async Task ResolveAndApplyPanelStateAsync()
    {
        var version = Interlocked.Increment(ref _panelResolveVersion);
        var resolved = await ResolvePanelStateAsync().ConfigureAwait(true);

        if (version == Volatile.Read(ref _panelResolveVersion))
        {
            IsPanelOpen = resolved;
        }
    }

    private async Task<bool> ResolvePanelStateAsync()
    {
        var defaultExpanded = GetApplicationDefaultExpanded();
        var activeTab = _host.Connection.ActiveSession;
        if (activeTab is null)
        {
            return defaultExpanded;
        }

        if (activeTab.TunnelsPanelManualOverride is bool manualOverride)
        {
            return manualOverride;
        }

        var profile = await LoadProfileForTabAsync(activeTab).ConfigureAwait(true);
        return profile?.TunnelsPanelExpanded ?? defaultExpanded;
    }

    private bool GetApplicationDefaultExpanded()
    {
        var settings = _settingsSnapshot ?? _host.CurrentSettings ?? new AppSettings();
        return !settings.CollapseTunnelsPanelByDefault;
    }

    private async Task<ServerProfileDto?> LoadProfileForTabAsync(SessionTabViewModel activeTab)
    {
        if (activeTab.IsAdHoc || string.IsNullOrWhiteSpace(activeTab.OriginalServerId))
        {
            return null;
        }

        try
        {
            var servers = await _configManager.LoadServersAsync().ConfigureAwait(true);
            return servers.FirstOrDefault(server =>
                string.Equals(server.Id, activeTab.OriginalServerId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[TunnelsViewModel] load profile panel state failed: {ex.Message}");
            return null;
        }
    }

    private async Task PersistPanelOverrideAsync(SessionTabViewModel activeTab, bool expanded)
    {
        if (activeTab.IsAdHoc || string.IsNullOrWhiteSpace(activeTab.OriginalServerId))
        {
            return;
        }

        try
        {
            bool updated = await _configManager.MutateServersAsync(servers =>
            {
                ServerProfileDto? profile = servers.FirstOrDefault(server =>
                    string.Equals(server.Id, activeTab.OriginalServerId, StringComparison.Ordinal));
                if (profile is null)
                {
                    return false;
                }

                profile.TunnelsPanelExpanded = expanded;
                return true;
            }).ConfigureAwait(true);

            if (!updated)
            {
                FileLogger.Warn(
                    $"[TunnelsViewModel] profile '{activeTab.OriginalServerId}' not found; using tab-local panel state only.");
                return;
            }

            FileLogger.Info(
                $"[TunnelsViewModel] persisted tunnels panel expanded={expanded} for profile '{activeTab.OriginalServerId}'.");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[TunnelsViewModel] persist profile panel state failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Force-closes a single tunnel, refreshes the list and reports the
    /// result in the shell status bar.
    /// </summary>
    [RelayCommand]
    private void Close(TunnelInfo? tunnel)
    {
        if (tunnel is null)
        {
            return;
        }

        _tunnelManager.ForceCloseTunnel(tunnel.LocalPort);
        RefreshList();
        _host.StatusText = _localizer.Format("StatusTunnelClosed", tunnel.LocalPort);
    }

    /// <summary>
    /// Closes every active tunnel, refreshes the list and reports the
    /// result in the shell status bar.
    /// </summary>
    [RelayCommand]
    private void CloseAll()
    {
        _tunnelManager.CloseAllTunnels();
        Count = 0;
        RefreshList();
        _host.StatusText = _localizer["StatusAllTunnelsClosed"];
    }

    /// <summary>
    /// Copies the local port of the given tunnel to the clipboard.
    /// Silent-warn on clipboard failures (common in RDP sessions).
    /// </summary>
    [RelayCommand]
    private void CopyPort(TunnelInfo? tunnel)
    {
        if (tunnel is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(tunnel.LocalPort.ToString());
            _host.StatusText = _localizer.Format("StatusPortCopied", tunnel.LocalPort);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[TunnelsViewModel] clipboard copy: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a user-requested local-forward tunnel without creating a
    /// synthetic server profile.
    /// </summary>
    [RelayCommand]
    private async Task NewTunnelAsync()
    {
        var settings = _host.CurrentSettings;
        if (settings is null)
        {
            _host.StatusText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer["StatusManualTunnelFailed"],
                _localizer["NewTunnelSettingsUnavailable"]);
            return;
        }

        var gateways = settings.SshGateways;
        var activePorts = _tunnelManager.GetActiveTunnels()
            .Select(t => t.LocalPort)
            .ToHashSet();
        var vm = new NewTunnelDialogViewModel(gateways, _localizer, activePorts);
        var dialog = new NewTunnelDialog
        {
            Owner = Application.Current?.MainWindow,
            DataContext = vm
        };

        if (dialog.ShowDialog() != true || vm.SelectedGateway is null)
        {
            return;
        }

        try
        {
            var result = await OpenManualTunnelAsync(vm, settings, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                _host.StatusText = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    _localizer["StatusManualTunnelFailed"],
                    result.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed]);
                return;
            }

            _host.StatusText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer["StatusManualTunnelOpened"],
                vm.LocalPort,
                vm.RemoteHost.Trim(),
                vm.RemotePort);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[TunnelsViewModel] manual open: {ex.Message}");
            _host.StatusText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer["StatusManualTunnelFailed"],
                ex.Message);
        }
    }

    private async Task<TunnelResult> OpenManualTunnelAsync(
        NewTunnelDialogViewModel vm,
        AppSettings settings,
        CancellationToken ct)
    {
        List<SshConnectionParams> chain;
        try
        {
            chain = GatewayChainResolver.ResolveChain(
                vm.SelectedGateway!.Id,
                settings.SshGateways,
                ConnectionHelpers.DecryptPassword,
                sshAgentPreference: settings.SshAgentPreference);
        }
        catch (Exception ex)
        {
            return new TunnelResult(false, null, ex.Message, SshFailureCode.Unknown);
        }

        var agentRegistry = SshAgentRegistry.CreateDefault(settings.SshAgentPreference);
        if (chain.Any(hop => hop.AgentForwarding)
            && !agentRegistry.HasPlinkCompatibleAgent()
            && agentRegistry.HasAnyNonPlinkAgent())
        {
            var message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
            return new TunnelResult(false, null, message, SshFailureCode.PageantKeyUnavailable);
        }

        var preflight = AuthPreflightChecker.Check(chain[0], isTunnelMode: true);
        if (!preflight.Success)
        {
            return new TunnelResult(
                false,
                null,
                ResolvePreflightMessage(preflight.Message),
                preflight.FailureCode);
        }

        var remoteHost = vm.RemoteHost.Trim();
        var label = string.IsNullOrWhiteSpace(vm.Label) ? null : vm.Label.Trim();
        if (chain.Count == 1)
        {
            return await _tunnelManager.OpenTunnelAsync(
                    chain[0],
                    remoteHost,
                    vm.RemotePort,
                    vm.LocalPort,
                    hostKeyStore: _hostKeyStore,
                    verifier: _hostKeyVerifier,
                    cancellationToken: ct,
                    keepAliveIntervalSeconds: settings.SshKeepAliveIntervalSeconds,
                    label: label)
                .ConfigureAwait(false);
        }

        return await _tunnelManager.OpenChainedTunnelAsync(
                chain,
                remoteHost,
                vm.RemotePort,
                vm.LocalPort,
                hostKeyStore: _hostKeyStore,
                verifier: _hostKeyVerifier,
                cancellationToken: ct,
                label: label)
            .ConfigureAwait(false);
    }

    private string ResolvePreflightMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return _localizer[SshLocalizationKeys.ErrorPreflightFailed];
        }

        var resolved = _localizer[message];
        return string.Equals(resolved, message, StringComparison.Ordinal)
            ? message
            : resolved;
    }

    // ── Public helpers ───────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="List"/> and <see cref="Count"/> from the
    /// tunnel manager's current active set. Called on startup
    /// (<see cref="MainViewModel.LoadAsync"/>), on tab switch to Tunnels,
    /// and after any tunnel-manager event.
    /// </summary>
    public void RefreshList()
    {
        var tunnels = _tunnelManager.GetActiveTunnels();
        List = new ObservableCollection<TunnelInfo>(tunnels);
        Count = tunnels.Count;
    }

    /// <summary>
    /// Resolves the tunnel chain route for a server, returning a display
    /// string like <c>"via GatewayA"</c> or <c>"via GatewayA &#x2192; GatewayB"</c>
    /// for chained tunnels. Returns an empty string for direct connections
    /// or when the server has no active tunnel.
    /// </summary>
    public string ResolveRoute(string serverId)
    {
        var settings = _host.CurrentSettings;
        if (settings is null) return string.Empty;

        var stateData = _connectionSm.GetStateData(serverId);
        if (stateData?.TunnelLocalPort is null) return string.Empty;

        // Find which gateway hosts this tunnel by matching the tunnel's ServerName
        var tunnels = _tunnelManager.GetActiveTunnels();
        var tunnel = tunnels.FirstOrDefault(t => t.LocalPort == stateData.TunnelLocalPort);
        if (tunnel is null) return string.Empty;

        var gatewayId = settings.SshGateways
            .FirstOrDefault(g => string.Equals(g.Host, tunnel.ServerName, StringComparison.OrdinalIgnoreCase))?.Id;

        if (string.IsNullOrEmpty(gatewayId))
            return $"via {tunnel.ServerName}";

        // Walk the gateway chain to build the full route
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrEmpty(gatewayId) && visited.Add(gatewayId))
        {
            var gw = settings.SshGateways.FirstOrDefault(
                g => string.Equals(g.Id, gatewayId, StringComparison.OrdinalIgnoreCase));
            if (gw is null) break;
            names.Add(string.IsNullOrWhiteSpace(gw.Name) ? gw.Host : gw.Name);
            gatewayId = gw.ParentGatewayId;
        }

        if (names.Count == 0) return string.Empty;
        names.Reverse();
        return "via " + string.Join(" \u2192 ", names);
    }

    // ── Event handlers ───────────────────────────────────────────────

    private void OnTunnelOpened(TunnelInfo info)
    {
        RefreshList();
        RefreshAllTunnelBadgeStates();
        // Phase 3.1 intentionally leaves visibility to tab/default state; the tab badge surfaces new tunnels.
    }

    private void OnTunnelClosed(int localPort, string? error)
    {
        RefreshList();
        RefreshAllTunnelBadgeStates();

        var status = _localizer.Format("StatusTunnelClosed", localPort);
        if (!string.IsNullOrEmpty(error))
        {
            status += $" ({error})";
        }

        _host.StatusText = status;
    }

    private void OnLocaleChanged(string _)
    {
        OnPropertyChanged(nameof(TunnelPanelHeaderPrefix));
        OnPropertyChanged(nameof(TunnelPanelHeaderSuffix));
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _settingsSnapshot = settings;
        QueueResolveAndApplyPanelState();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ConnectionViewModel.ActiveSession), StringComparison.Ordinal))
        {
            QueueResolveAndApplyPanelState();
            RefreshAllTunnelBadgeStates();
        }
        else if (string.Equals(e.PropertyName, nameof(ConnectionViewModel.ActiveSessions), StringComparison.Ordinal))
        {
            SubscribeToActiveSessions(_host.Connection.ActiveSessions);
        }
    }

    private void OnActiveSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<SessionTabViewModel>())
            {
                UnsubscribeFromTab(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<SessionTabViewModel>())
            {
                SubscribeToTab(item);
                UpdateTunnelBadgeState(item);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ReconcileTrackedTabsWithActiveSessions();
            RefreshAllTunnelBadgeStates();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SessionTabViewModel tab
            && string.Equals(e.PropertyName, nameof(SessionTabViewModel.RootContent), StringComparison.Ordinal))
        {
            UpdateTunnelBadgeState(tab);
        }
    }

    private void SubscribeToActiveSessions(ObservableCollection<SessionTabViewModel> activeSessions)
    {
        if (ReferenceEquals(_activeSessionsSubscription, activeSessions))
        {
            return;
        }

        UnsubscribeFromActiveSessions();
        _activeSessionsSubscription = activeSessions;
        _activeSessionsSubscription.CollectionChanged += _activeSessionsCollectionChangedHandler;

        foreach (var tab in _activeSessionsSubscription)
        {
            SubscribeToTab(tab);
        }

        RefreshAllTunnelBadgeStates();
    }

    private void UnsubscribeFromActiveSessions()
    {
        if (_activeSessionsSubscription is not null)
        {
            _activeSessionsSubscription.CollectionChanged -= _activeSessionsCollectionChangedHandler;
            _activeSessionsSubscription = null;
        }

        foreach (var tab in _trackedTabs.ToList())
        {
            UnsubscribeFromTab(tab);
        }
    }

    private void SubscribeToTab(SessionTabViewModel tab)
    {
        if (_trackedTabs.Add(tab))
        {
            tab.PropertyChanged += _tabPropertyChangedHandler;
        }
    }

    private void UnsubscribeFromTab(SessionTabViewModel tab)
    {
        if (_trackedTabs.Remove(tab))
        {
            tab.PropertyChanged -= _tabPropertyChangedHandler;
        }
    }

    private void ReconcileTrackedTabsWithActiveSessions()
    {
        var activeTabs = _host.Connection.ActiveSessions.ToHashSet();

        foreach (var trackedTab in _trackedTabs.ToList())
        {
            if (!activeTabs.Contains(trackedTab))
            {
                UnsubscribeFromTab(trackedTab);
            }
        }

        foreach (var activeTab in _host.Connection.ActiveSessions)
        {
            SubscribeToTab(activeTab);
        }
    }

    private void RefreshAllTunnelBadgeStates()
    {
        foreach (var tab in _host.Connection.ActiveSessions)
        {
            UpdateTunnelBadgeState(tab);
        }
    }

    private void UpdateTunnelBadgeState(SessionTabViewModel tab)
    {
        try
        {
            tab.TunnelBadgeState = TunnelBadgeStateResolver.Resolve(tab, _connectionSm, _tunnelManager);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[TunnelsViewModel] resolve tunnel badge state failed: {ex.Message}");
        }
    }

    private void QueueResolveAndApplyPanelState()
    {
        _ = ResolveAndApplyPanelStateSafelyAsync();
    }

    private async Task ResolveAndApplyPanelStateSafelyAsync()
    {
        try
        {
            await ResolveAndApplyPanelStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[TunnelsViewModel] resolve panel state failed: {ex.Message}");
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tunnelManager.TunnelOpened -= OnTunnelOpened;
        _tunnelManager.TunnelClosed -= OnTunnelClosed;
        _localizer.LocaleChanged -= OnLocaleChanged;
        _configManager.SettingsChanged -= OnSettingsChanged;
        _host.Connection.PropertyChanged -= _connectionPropertyChangedHandler;
        UnsubscribeFromActiveSessions();
    }
}
