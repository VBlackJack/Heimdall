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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Scheduled;
using Heimdall.App.ViewModels.Session;
using Heimdall.App.ViewModels.Sidebar;
using Heimdall.App.ViewModels.ToolsTab;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Root ViewModel orchestrating the application shell: tabs, status bar,
/// and coordination between child ViewModels.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable, ITunnelsHost
{
    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly ApplicationStatusMachine _appStatus;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IDialogService _dialogService;
    private readonly IEmbeddedSessionManager _embeddedSessionManager;
    private readonly HeimdallThemeService _themeService;
    private readonly ISessionSnapshotService _sessionSnapshotService;
    private readonly IUiDispatcher _uiDispatcher;

    private bool _disposed;
    private Action? _onConfigurationChanged;
    private Action<string, string, Core.Models.ToolContext>? _onToolSessionRequested;
    private Action<string>? _onStatusMessageRequested;
    private System.ComponentModel.PropertyChangedEventHandler? _connectionPropertyChangedHandler;

    private AppSettings? _currentSettings;

    /// <summary>Split/merge service handling all pane operations.</summary>
    public SplitService Split { get; }

    // Exposed for split pane session creation and context menu building from code-behind
    internal IConfigManager ConfigManager => _configManager;
    internal IDialogService DialogService => _dialogService;
    internal IEmbeddedSessionManager EmbeddedSessionManager => _embeddedSessionManager;
    internal AppSettings? CurrentSettings => _currentSettings;
    AppSettings? ITunnelsHost.CurrentSettings => _currentSettings;

    [ObservableProperty]
    private string _windowTitle = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _serverCount;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Monotonic counter bumped after every theme swap. Bound as a trigger value
    /// by <c>MultiBinding</c>s that need to re-run their brush-resolving converters
    /// when the active theme changes (see e.g. <c>ConnectionTypeToColorConverter</c>
    /// in the server TreeView).
    /// </summary>
    [ObservableProperty]
    private int _themeRevision;

    [ObservableProperty]
    private string _selectedTab = "Sessions";

    [ObservableProperty]
    private SessionTabViewModel? _dragDisplaySession;

    private string _previousTab = "Sessions";
    private bool _suppressTabChangeGuard;

    /// <summary>True when the Sessions tab is selected.</summary>
    public bool IsSessionsTabSelected => string.Equals(SelectedTab, "Sessions", StringComparison.Ordinal);

    /// <summary>
    /// Session currently rendered in the main content area. During a tab drag,
    /// this freezes on the original merge target so the visible content does
    /// not follow WPF's transient tab selection changes.
    /// </summary>
    public SessionTabViewModel? DisplayedSession => DragDisplaySession ?? Connection.ActiveSession;

    /// <summary>True when the Tunnels tab is selected.</summary>
    public bool IsTunnelsTabSelected => string.Equals(SelectedTab, "Tunnels", StringComparison.Ordinal);

    /// <summary>True when the Scheduled tab is selected.</summary>
    public bool IsScheduledTabSelected => string.Equals(SelectedTab, "Scheduled", StringComparison.Ordinal);

    /// <summary>True when the Settings tab is selected.</summary>
    public bool IsSettingsTabSelected => string.Equals(SelectedTab, "Settings", StringComparison.Ordinal);

    /// <summary>True when the Tools tab is selected.</summary>
    public bool IsToolsTabSelected => string.Equals(SelectedTab, "Tools", StringComparison.Ordinal);

    /// <summary>True when the About tab is selected.</summary>
    public bool IsAboutTabSelected => string.Equals(SelectedTab, "About", StringComparison.Ordinal);

    partial void OnSelectedTabChanged(string value)
    {
        Heimdall.Core.Logging.FileLogger.Info($"MainViewModel SelectedTab changed to {value}");

        // Guard: prompt to save unsaved settings when leaving Settings tab.
        // Revert immediately and handle the dialog asynchronously to avoid
        // blocking the WPF dispatcher (the previous .GetAwaiter().GetResult()
        // pattern caused deadlocks when the dialog posted back to the UI thread).
        if (!_suppressTabChangeGuard
            && string.Equals(_previousTab, "Settings", StringComparison.Ordinal)
            && !string.Equals(value, "Settings", StringComparison.Ordinal)
            && Settings.IsDirty)
        {
            _suppressTabChangeGuard = true;
            SelectedTab = "Settings";
            _suppressTabChangeGuard = false;
            _ = SafeFireAndForgetAsync(HandleUnsavedSettingsGuardAsync(value));
            return;
        }

        ApplyTabChange(value);
    }

    partial void OnDragDisplaySessionChanged(SessionTabViewModel? value)
    {
        OnPropertyChanged(nameof(DisplayedSession));
    }

    /// <summary>
    /// Shows the save/discard/cancel dialog asynchronously when leaving the
    /// Settings tab with unsaved changes, then navigates to the target tab
    /// if the user chose Save or Discard.
    /// </summary>
    private async Task HandleUnsavedSettingsGuardAsync(string targetTab)
    {
        var title = _localizer["SettingsUnsavedWarningTitle"];
        var message = _localizer["SettingsUnsavedWarning"];
        var result = await _dialogService.ShowSaveDiscardCancelAsync(title, message);

        if (result is null)
        {
            // Cancel: stay on Settings (already reverted)
            return;
        }

        if (result == true)
        {
            // Save
            Settings.SaveCommand.Execute(null);
        }
        else
        {
            // Discard
            await Settings.DiscardChangesAsync();
        }

        // Navigate to the originally intended tab
        _suppressTabChangeGuard = true;
        SelectedTab = targetTab;
        _suppressTabChangeGuard = false;
    }

    /// <summary>
    /// Applies the post-navigation side effects for a tab change:
    /// updates <c>_previousTab</c>, raises property-changed notifications,
    /// and refreshes tab-specific data.
    /// </summary>
    private void ApplyTabChange(string value)
    {
        _previousTab = value;

        OnPropertyChanged(nameof(IsSessionsTabSelected));
        OnPropertyChanged(nameof(IsTunnelsTabSelected));
        OnPropertyChanged(nameof(IsScheduledTabSelected));
        OnPropertyChanged(nameof(IsToolsTabSelected));
        OnPropertyChanged(nameof(IsSettingsTabSelected));
        OnPropertyChanged(nameof(IsAboutTabSelected));

        // Refresh tunnel list when switching to Tunnels tab
        if (IsTunnelsTabSelected)
        {
            Tunnels.RefreshList();
        }

        // Reload scheduled tasks when switching to Scheduled tab
        if (IsScheduledTabSelected && _currentSettings is not null)
        {
            Scheduled.Load(_currentSettings);
        }
    }

    /// <summary>
    /// Child ViewModel for the server list and filtering.
    /// </summary>
    public ServerListViewModel ServerList { get; }

    /// <summary>
    /// Child ViewModel for active embedded sessions (tabs).
    /// </summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>
    /// Child ViewModel for application settings.
    /// </summary>
    public SettingsViewModel Settings { get; }

    public UpdateBannerViewModel Update { get; }

    /// <summary>Centralized tool registry shared with MainWindow for menus.</summary>
    public ToolRegistry ToolRegistry { get; }

    /// <summary>Sidebar (Sessions / Tools tabs) state and commands.</summary>
    public SidebarViewModel Sidebar { get; }

    /// <summary>Full-page Tools tab (top tab-strip) state and commands.</summary>
    public ToolsTabViewModel ToolsTab { get; }

    /// <summary>Ctrl+K Command Palette state, commands and fuzzy search.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>Active tunnels list + retractable panel state + route resolver.</summary>
    public TunnelsViewModel Tunnels { get; }

    /// <summary>Scheduled tasks list + scheduler lifecycle + add/edit/delete commands.</summary>
    public ScheduledTasksViewModel Scheduled { get; }

    /// <summary>Session lifecycle hub: broadcast, reconnect, SFTP auto-open, workspace restore.</summary>
    public SessionCoordinator Session { get; }

    /// <summary>Recently used tool IDs (most recent first, max 5).</summary>
    private readonly List<string> _recentToolIds = new();
    private const int MaxRecentTools = 5;

    /// <summary>Returns the current list of recently used tool IDs.</summary>
    internal IReadOnlyList<string> RecentToolIds => _recentToolIds;

    /// <summary>Returns the favorite tool IDs from persisted settings.</summary>
    internal List<string> FavoriteToolIds => _currentSettings?.FavoriteToolIds ?? [];

    /// <summary>
    /// Raised after a tool favorite toggle is persisted. Carries the normalized tool ID.
    /// </summary>
    internal event Action<string>? FavoritesChanged;

    /// <summary>Toggles a tool's favorite status and persists the change.</summary>
    internal async Task ToggleFavoriteToolAsync(string toolId)
    {
        if (_currentSettings is null) return;

        var id = toolId.ToUpperInvariant();
        var removed = _currentSettings.FavoriteToolIds.RemoveAll(
            favoriteId => string.Equals(favoriteId, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            _currentSettings.FavoriteToolIds.Add(id);
        }

        await _configManager.MergeSettingAsync(
            s => s.FavoriteToolIds = new List<string>(_currentSettings.FavoriteToolIds));

        Heimdall.Core.Logging.FileLogger.Debug(
            $"Favorite tools updated: {id} => {(removed ? "removed" : "added")}");
        FavoritesChanged?.Invoke(id);
    }

    public MainViewModel(
        IConfigManager configManager,
        LocalizationManager localizer,
        ConnectionStateMachine connectionSm,
        ApplicationStatusMachine appStatus,
        TunnelManager tunnelManager,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        IDialogService dialogService,
        IEmbeddedSessionManager embeddedSessionManager,
        HeimdallThemeService themeService,
        ISessionSnapshotService sessionSnapshotService,
        IPostConnectSequenceRunner postConnectSequenceRunner,
        IPostConnectStepResolver postConnectStepResolver,
        ToolRegistry toolRegistry,
        SplitService splitService,
        ExternalToolLaunchService externalToolLaunchService,
        ToolsTabPopulationService toolsTabPopulation,
        IToolContextProvider toolContextProvider,
        IUiDispatcher uiDispatcher,
        ServerListViewModel serverList,
        ConnectionViewModel connection,
        SettingsViewModel settings,
        UpdateBannerViewModel update,
        IServiceProvider serviceProvider,
        IRecentConnectionTracker? recentConnections = null)
    {
        _configManager = configManager;
        _localizer = localizer;
        _appStatus = appStatus;
        _hostKeyStore = hostKeyStore;
        _dialogService = dialogService;
        _embeddedSessionManager = embeddedSessionManager;
        _themeService = themeService;
        _sessionSnapshotService = sessionSnapshotService;
        _uiDispatcher = uiDispatcher;
        ToolRegistry = toolRegistry;
        Split = splitService;
        ServerList = serverList;
        Connection = connection;
        Settings = settings;
        Update = update;
        Sidebar = new SidebarViewModel(this, localizer, configManager, toolsTabPopulation, toolContextProvider, _uiDispatcher);
        ToolsTab = new ToolsTabViewModel(this, localizer, toolContextProvider);
        CommandPalette = new CommandPaletteViewModel(
            this, localizer, dialogService, toolRegistry, configManager, embeddedSessionManager,
            externalToolLaunchService, recentConnections ?? new RecentConnectionTracker(), serviceProvider);
        Tunnels = new TunnelsViewModel(
            this,
            localizer,
            tunnelManager,
            connectionSm,
            hostKeyStore,
            hostKeyVerifier,
            configManager);
        Scheduled = new ScheduledTasksViewModel(this, localizer, dialogService, configManager, _uiDispatcher);
        Session = new SessionCoordinator(this, localizer, configManager, embeddedSessionManager, postConnectSequenceRunner, postConnectStepResolver, _uiDispatcher);

        _appStatus.StatusChanged += OnApplicationStatusChanged;

        // Keep _currentSettings in sync when settings are saved elsewhere
        _configManager.SettingsChanged += OnSettingsChanged;

        // Reload server list after a config import
        _onConfigurationChanged = async () =>
            await ReloadConfigurationAsync(await _configManager.LoadSettingsAsync());
        Settings.ConfigurationChanged += _onConfigurationChanged;
        Settings.GatewayReferenceMutationHandler = async (request, cancellationToken) =>
            await ServerList.UpdateGatewayReferencesAsync(
                request.ServerIds,
                request.TargetGatewayId,
                cancellationToken);

        // Wire cross-tool navigation so tools can open other tool tabs
        _embeddedSessionManager.OpenToolCallback = (toolId, title, ctx) =>
            OpenToolTabAsync(toolId, title, ctx);

        // Instant theme preview when the user changes the Settings combo.
        // Actual persistence triggers a second apply via IConfigManager.SettingsChanged,
        // which is a no-op because HeimdallThemeService is idempotent.
        Settings.ThemeChanged += OnSettingsThemePreview;
        Settings.AccentTintChanged += OnSettingsAccentTintPreview;

        // Track the theme revision counter so MultiBinding triggers fire on swap.
        // Initial sync covers the startup apply that happened before this subscription.
        _themeService.ThemeChanged += OnThemeServiceThemeChanged;
        ThemeRevision = _themeService.ThemeRevision;

        // Wire server list tool navigation events to the connection tab manager
        // (SessionReady is handled by SessionCoordinator, not here)
        _onToolSessionRequested = (toolId, title, ctx) =>
        {
            TrackRecentTool(toolId.ToUpperInvariant());
            _ = SafeFireAndForgetAsync(OpenToolTabAsync(toolId, title, ctx));
        };
        ServerList.ToolSessionRequested += _onToolSessionRequested;
        _onStatusMessageRequested = message => StatusText = message;
        ServerList.StatusMessageRequested += _onStatusMessageRequested;

        _connectionPropertyChangedHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ConnectionViewModel.ActiveSession), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(DisplayedSession));
            }
        };
        Connection.PropertyChanged += _connectionPropertyChangedHandler;

        StatusText = _localizer["StatusReady"];
    }

    /// <summary>
    /// Loads settings, server inventory, and initializes the UI state.
    /// Called after the window is shown.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        using var _ = _appStatus.BeginOperation("Loading");

        try
        {
            var settings = await _configManager.LoadSettingsAsync();
            var servers = await _configManager.LoadServersAsync();

            ServerCount = servers.Count;
            Tunnels.RefreshList();

            // Load host keys from gateway configurations into the TOFU store
            var hostKeyEntries = settings.SshGateways
                .Where(g => !string.IsNullOrEmpty(g.HostKeyFingerprint))
                .Select(g => (g.Host, g.Port, (string?)g.HostKeyFingerprint));

            _hostKeyStore.LoadFromConfig(hostKeyEntries);

            _currentSettings = settings;
            await Tunnels.ResolveAndApplyPanelStateAsync();
            ServerList.LoadServers(servers, settings);
            Settings.LoadFromSettings(settings);
            Scheduled.Load(settings);

            await RestoreSessionSnapshotAsync(cancellationToken);

            // OperationScope.Dispose() handles the Ready transition
            StatusText = _localizer["StatusReady"];
            WindowTitle = _localizer.Format("WindowTitle", ServerCount);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Collects the currently open top-level remote sessions for restore-on-launch.
    /// Tool tabs are intentionally excluded.
    /// </summary>
    public IReadOnlyList<SessionSnapshotEntry> GetSessionSnapshotEntries()
    {
        return Connection.ActiveSessions
            .Where(session => !string.IsNullOrWhiteSpace(session.OriginalServerId))
            .Where(session => !string.IsNullOrWhiteSpace(session.ConnectionType))
            .Where(session => !session.ConnectionType.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase))
            .Select((session, order) => new SessionSnapshotEntry
            {
                ServerId = session.OriginalServerId,
                ConnectionType = session.ConnectionType,
                Order = order
            })
            .ToList();
    }

    private async Task RestoreSessionSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _sessionSnapshotService.LoadAsync(cancellationToken);
        if (snapshot?.Sessions.Count is not > 0)
        {
            return;
        }

        SnapshotRestoreDialogResult? dialogResult;
        try
        {
            var dialogVm = new SnapshotRestoreDialogViewModel(
                _localizer,
                snapshot,
                ServerList.Servers);
            dialogResult = await _dialogService.ShowSnapshotRestoreDialogAsync(dialogVm);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Snapshot restore dialog failed.", ex);
            _dialogService.ShowError(
                _localizer["DialogSnapshotRestoreTitle"],
                _localizer.Format("ErrorSnapshotRestoreFailed", ex.Message));
            return;
        }

        if (dialogResult is null)
        {
            return;
        }

        if (dialogResult.Action == SnapshotRestoreDialogAction.DontRestore)
        {
            await _sessionSnapshotService.ClearAsync(cancellationToken);
            return;
        }

        var restoredCount = 0;
        var selectedSessions = dialogResult.Sessions
            .OrderBy(session => session.Order)
            .ToList();

        foreach (var session in selectedSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await ServerList.RestoreServerAsync(session.ServerId, cancellationToken))
                {
                    restoredCount++;
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Error(
                    $"Session snapshot restore failed for {session.ServerId}.", ex);
            }
        }

        await _sessionSnapshotService.ClearAsync(cancellationToken);

        if (restoredCount < selectedSessions.Count)
        {
            _dialogService.ShowWarning(
                _localizer["DialogSnapshotRestoreTitle"],
                _localizer.Format(
                    "WarningSnapshotRestorePartial",
                    restoredCount,
                    selectedSessions.Count));
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _currentSettings = settings;
        SleepPrevention.Enabled = settings.PreventSleepDuringSession;
    }

    private void OnApplicationStatusChanged(ApplicationStatus previous, ApplicationStatus current)
    {
        var metadata = ApplicationStatusMachine.GetMetadata(current);
        StatusText = _localizer[metadata.DisplayKey];
        IsBusy = !metadata.AllowsUserAction;
    }

    /// <summary>
    /// Forwards the instant-preview theme change from the Settings combo to
    /// the centralized <see cref="HeimdallThemeService"/>. The service performs the
    /// actual dictionary swap, DWM title bar update, and event broadcast.
    /// </summary>
    private void OnSettingsThemePreview(string themeName)
    {
        _themeService.ApplyTheme(themeName);
    }

    /// <summary>
    /// Forwards the instant-preview accent change from the Settings combo to
    /// the centralized <see cref="HeimdallThemeService"/>.
    /// </summary>
    private void OnSettingsAccentTintPreview(string accentTint)
    {
        _themeService.ApplyAccentTint(accentTint);
    }

    /// <summary>
    /// Mirrors <see cref="HeimdallThemeService.ThemeRevision"/> into <see cref="ThemeRevision"/>
    /// so XAML <c>MultiBinding</c>s re-run their brush-resolving converters after a swap.
    /// </summary>
    private void OnThemeServiceThemeChanged(string themeName)
    {
        ThemeRevision = _themeService.ThemeRevision;
    }

    /// <summary>
    /// Stops the scheduler. Called by <c>App.OnExit</c> during application
    /// shutdown. Thin forwarder to <see cref="ScheduledTasksViewModel.Dispose"/>
    /// so the existing external call site does not need to change.
    /// </summary>
    public void StopScheduler() => Scheduled.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _appStatus.StatusChanged -= OnApplicationStatusChanged;
        Sidebar.Dispose();
        ToolsTab.Dispose();
        Tunnels.Dispose();
        Scheduled.Dispose();
        Session.Dispose();
        _configManager.SettingsChanged -= OnSettingsChanged;
        Settings.ConfigurationChanged -= _onConfigurationChanged;
        Settings.GatewayReferenceMutationHandler = null;
        Settings.ThemeChanged -= OnSettingsThemePreview;
        Settings.AccentTintChanged -= OnSettingsAccentTintPreview;
        Settings.Dispose();
        _themeService.ThemeChanged -= OnThemeServiceThemeChanged;
        ServerList.ToolSessionRequested -= _onToolSessionRequested;
        ServerList.StatusMessageRequested -= _onStatusMessageRequested;
        Connection.PropertyChanged -= _connectionPropertyChangedHandler;
    }

    private static async Task SafeFireAndForgetAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error($"Fire-and-forget task failed: {ex.Message}", ex);
        }
    }

    // ── Split operations (delegated to SplitService) ─────────────────

    public async Task SplitSessionWithServerAsync(
        SessionTabViewModel session,
        string serverId,
        Core.Models.SplitOrientation orientation,
        string? paneId = null)
    {
        await Split.SplitSessionWithServerAsync(session, serverId, orientation, paneId);
    }

    public void MergeExistingSession(
        SessionTabViewModel target,
        string sourceSessionId,
        Core.Models.SplitOrientation orientation,
        string? targetPaneId = null)
    {
        Split.MergeExistingSession(target, sourceSessionId, orientation, targetPaneId);
    }

    public Task SwapSplitPanesAsync(SessionTabViewModel session, string? paneId = null)
    {
        return Split.SwapSplitPanesAsync(session, paneId);
    }

    public void ToggleSplitOrientation(SessionTabViewModel session, string? paneId = null)
    {
        Split.ToggleSplitOrientation(session, paneId);
    }

    [RelayCommand]
    private void CloseSecondaryPane(SessionTabViewModel? session)
    {
        if (session is null || !session.IsSplit) return;

        var secondaryPane = session.RootContent is SplitContainerModel c
            ? Core.Models.SplitTreeHelper.FirstLeaf(c.Second)
            : null;
        if (secondaryPane is not null)
        {
            ClosePane(session, secondaryPane.PaneId);
        }
    }

    public void ClosePane(
        SessionTabViewModel session,
        string paneId,
        DisconnectReason reason = DisconnectReason.UserAction)
    {
        Split.ClosePane(session, paneId, reason);
    }

    [RelayCommand]
    private async Task ReconnectSecondaryAsync(SessionTabViewModel? session)
    {
        if (session is null || !session.IsSplit) return;

        var secondaryPane = session.RootContent is SplitContainerModel c
            ? Core.Models.SplitTreeHelper.FirstLeaf(c.Second)
            : null;
        if (secondaryPane is not null)
        {
            await ReconnectPaneAsync(session, secondaryPane.PaneId);
        }
    }

    public async Task ReconnectPaneAsync(SessionTabViewModel session, string paneId)
    {
        await Split.ReconnectPaneAsync(session, paneId);
    }

    public void CleanupOrphanedPane(string serverId)
    {
        Split.CleanupOrphanedPane(serverId);
    }

    /// <summary>
    /// Localized button text for the secondary pane reconnect button.
    /// </summary>
    public string ReconnectSecondaryButtonText => _localizer["SplitReconnectSecondary"];

    /// <summary>
    /// Localized button text for the secondary pane close button.
    /// </summary>
    public string CloseSecondaryButtonText => _localizer["SplitCloseSecondary"];

    /// <summary>
    /// Localized text for the drag-to-split drop zone.
    /// </summary>
    public string DropToMergeText => _localizer["SplitDropToMerge"];

    /// <summary>
    /// Tracks a tool ID as recently used for the palette's "recent tools" section.
    /// </summary>
    public void TrackRecentTool(string toolId)
    {
        _recentToolIds.Remove(toolId);
        _recentToolIds.Insert(0, toolId);
        while (_recentToolIds.Count > MaxRecentTools)
            _recentToolIds.RemoveAt(_recentToolIds.Count - 1);
    }

    public string Localize(string key)
    {
        return _localizer[key];
    }

    /// <summary>
    /// Provides access to the localization manager for components that need
    /// to resolve i18n keys outside the view model (e.g., floating windows).
    /// </summary>
    public LocalizationManager GetLocalizer() => _localizer;

    internal async Task ReloadConfigurationAsync(AppSettings settings, List<ServerProfileDto>? servers = null)
    {
        _currentSettings = settings;
        var currentServers = servers ?? await _configManager.LoadServersAsync();

        ServerCount = currentServers.Count;
        ServerList.LoadServers(currentServers, settings);
        Settings.LoadFromSettings(settings);
        Scheduled.Load(settings);
        WindowTitle = _localizer.Format("WindowTitle", ServerCount);
    }

    // ── Tool tabs ────────────────────────────────────────────────

    /// <summary>
    /// Opens a non-connection tool as a session tab, bypassing
    /// ConnectionService and ConnectionStateMachine entirely.
    /// </summary>
    internal async Task OpenToolTabAsync(string toolId, string title, ToolContext? context)
    {
        await Task.CompletedTask;

        var connectionType = $"TOOL:{toolId.ToUpperInvariant()}";

        // Singleton behavior: reuse existing tab for tools that have no context
        // (e.g., Password Generator, UUID, Chmod). Network tools or tools opened
        // with a specific argument are allowed to have multiple instances.
        var isNetworkTool = ToolRegistry.IsNetworkTool(toolId);
        var hasContext = !string.IsNullOrWhiteSpace(context?.TargetHost) || !string.IsNullOrWhiteSpace(context?.Argument);

        if (!isNetworkTool && !hasContext)
        {
            var existing = Connection.ActiveSessions
                .FirstOrDefault(s => s.ConnectionType == connectionType);
            if (existing is not null)
            {
                Connection.ActiveSession = existing;
                return;
            }
        }

        var sessionId = $"tool-{toolId.ToLowerInvariant()}-{Guid.NewGuid():N}";

        var tab = Connection.AddSession(sessionId, title, connectionType);
        try
        {
            tab.HostControl = _embeddedSessionManager.CreateToolControl(
                tab, toolId, context, _currentSettings);
            tab.Status = _localizer["StatusReady"];
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error($"Failed to create tool control: {toolId}", ex);
            Connection.ActiveSessions.Remove(tab);
            Connection.HasActiveSessions = Connection.ActiveSessions.Count > 0;
            throw;
        }
    }
}
