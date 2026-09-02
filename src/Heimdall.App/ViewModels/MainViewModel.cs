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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Extensions;
using Heimdall.App.Services;
using Heimdall.App.Services.CloseGuard;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Scheduled;
using Heimdall.App.ViewModels.Session;
using Heimdall.App.ViewModels.Shell;
using Heimdall.App.ViewModels.Sidebar;
using Heimdall.App.ViewModels.ToolsTab;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly ISessionRestoreCoordinator _sessionRestore;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly WorkspaceLockService _workspaceLock;

    private bool _disposed;
    private bool _isSettingLocalizedApplicationStatus;
    private string? _localizedApplicationStatusKey;
    private bool _statusTextIsSessionDerived;
    private SessionTabViewModel? _observedSession;
    private PropertyChangedEventHandler? _observedSessionHandler;
    private Action? _onConfigurationChanged;
    private Action<string, string, Core.Models.ToolContext>? _onToolSessionRequested;
    private Action<string>? _onStatusMessageRequested;
    private System.ComponentModel.PropertyChangedEventHandler? _connectionPropertyChangedHandler;

    private AppSettings? _currentSettings;

    /// <summary>Split/merge service handling all pane operations.</summary>
    public SplitService Split { get; }

    /// <summary>Owns the close protocol's bookkeeping; shared by every close path.</summary>
    private readonly IPaneCloseArbiter _closeArbiter;

    /// <summary>Runs the close protocol for a single pane; see <see cref="PaneCloseCoordinator"/>.</summary>
    private readonly PaneCloseCoordinator _paneCloseCoordinator;

    /// <summary>
    /// The one arbiter every close path shares, so a clearance obtained in one gesture is honoured
    /// wherever that gesture continues - including in a detached window.
    /// </summary>
    internal IPaneCloseArbiter CloseArbiter => _closeArbiter;

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

    partial void OnStatusTextChanged(string value)
    {
        if (!_isSettingLocalizedApplicationStatus)
        {
            _localizedApplicationStatusKey = null;
            _statusTextIsSessionDerived = false;
        }
    }

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
    private string _selectedTab = ShellTab.Sessions;

    [ObservableProperty]
    private SessionTabViewModel? _dragDisplaySession;

    private string _previousTab = ShellTab.Sessions;
    private bool _suppressTabChangeGuard;

    /// <summary>True when the Sessions tab is selected.</summary>
    public bool IsSessionsTabSelected => string.Equals(SelectedTab, ShellTab.Sessions, StringComparison.Ordinal);

    /// <summary>
    /// Session currently rendered in the main content area. During a tab drag,
    /// this freezes on the original merge target so the visible content does
    /// not follow WPF's transient tab selection changes.
    /// </summary>
    public SessionTabViewModel? DisplayedSession => DragDisplaySession ?? Connection.ActiveSession;

    /// <summary>True when the Tunnels tab is selected.</summary>
    public bool IsTunnelsTabSelected => string.Equals(SelectedTab, ShellTab.Tunnels, StringComparison.Ordinal);

    /// <summary>True when the Scheduled tab is selected.</summary>
    public bool IsScheduledTabSelected => string.Equals(SelectedTab, ShellTab.Scheduled, StringComparison.Ordinal);

    /// <summary>True when the Settings tab is selected.</summary>
    public bool IsSettingsTabSelected => string.Equals(SelectedTab, ShellTab.Settings, StringComparison.Ordinal);

    /// <summary>True when the Tools tab is selected.</summary>
    public bool IsToolsTabSelected => string.Equals(SelectedTab, ShellTab.Tools, StringComparison.Ordinal);

    /// <summary>True when the About tab is selected.</summary>
    public bool IsAboutTabSelected => string.Equals(SelectedTab, ShellTab.About, StringComparison.Ordinal);

    partial void OnSelectedTabChanged(string value)
    {
        Heimdall.Core.Logging.FileLogger.Info($"MainViewModel SelectedTab changed to {value}");

        // Guard: prompt to save unsaved settings when leaving Settings tab.
        // Revert immediately and handle the dialog asynchronously to avoid
        // blocking the WPF dispatcher (the previous .GetAwaiter().GetResult()
        // pattern caused deadlocks when the dialog posted back to the UI thread).
        ShellTabNavigationDecision decision = ShellTabNavigation.Decide(
            _previousTab,
            value,
            Settings.IsDirty,
            _suppressTabChangeGuard);

        if (decision == ShellTabNavigationDecision.PromptBeforeLeavingSettings)
        {
            _suppressTabChangeGuard = true;
            SelectedTab = ShellTab.Settings;
            _suppressTabChangeGuard = false;
            HandleUnsavedSettingsGuardAsync(value).SafeFireAndForget();
            return;
        }

        ApplyTabChange(value);
    }

    partial void OnDragDisplaySessionChanged(SessionTabViewModel? value)
    {
        OnPropertyChanged(nameof(DisplayedSession));
        ObserveDisplayedSession();
    }

    /// <summary>
    /// Shows the save/discard/cancel dialog asynchronously when leaving the
    /// Settings tab with unsaved changes, then navigates to the target tab
    /// if the user chose Discard or the requested save completed successfully.
    /// </summary>
    private async Task HandleUnsavedSettingsGuardAsync(string targetTab)
    {
        if (!await ResolveUnsavedSettingsAsync())
        {
            return;
        }

        // Navigate to the originally intended tab
        _suppressTabChangeGuard = true;
        SelectedTab = targetTab;
        _suppressTabChangeGuard = false;
    }

    /// <summary>
    /// Asks what to do about unsaved settings, acts on the answer, and reports whether
    /// the caller may now navigate away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted so the two ways of leaving the Settings tab cannot answer the question
    /// differently. They already did: this method's caller has always shown a three-way
    /// save / discard / cancel dialog, while the shell's own tab buttons went through a
    /// second copy in the code-behind that asked a yes/no question titled "Apply them
    /// before leaving?" and threw the changes away on yes. Two surfaces owning one rule
    /// is how that survived: each was correct on its own terms and nothing compared them.
    /// </para>
    /// <para>
    /// Returns <see langword="true" /> when the user chose Discard, or chose Save and the
    /// save succeeded; <see langword="false" /> on Cancel, and on a save that failed - in
    /// which case the user has been told why they are being held.
    /// </para>
    /// </remarks>
    internal async Task<bool> ResolveUnsavedSettingsAsync()
    {
        var title = _localizer["SettingsUnsavedWarningTitle"];
        var message = _localizer["SettingsUnsavedWarning"];
        var result = await _dialogService.ShowSaveDiscardCancelAsync(title, message);

        if (result is null)
        {
            // Cancel: stay on Settings (already reverted)
            return false;
        }

        if (result == true)
        {
            // Save
            bool settingsSaved = await Settings.TrySaveAsync();
            if (!settingsSaved)
            {
                // Holding the user on the Settings tab. Without this they are held there
                // with no statement of why, and every further attempt to leave repeats
                // the same silent refusal.
                _dialogService.ShowWarning(
                    Localize("SettingsCloseSaveFailedTitle"),
                    Localize("SettingsCloseSaveFailedMessage"));
                return false;
            }

            return true;
        }

        // Discard
        await Settings.DiscardChangesAsync();
        return true;
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

    /// <summary>Recently used tool IDs (most recent first, bounded).</summary>
    private readonly RecentToolList _recentTools = new();

    /// <summary>Returns the current list of recently used tool IDs.</summary>
    internal IReadOnlyList<string> RecentToolIds => _recentTools.Ids;

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

        FavoriteToolToggle toggle = FavoriteToolSet.Toggle(_currentSettings.FavoriteToolIds, toolId);

        // Persisted before anything in memory changes. MergeSettingAsync reloads from disk, writes,
        // and only then raises SettingsChanged, which is what replaces _currentSettings with the
        // stored clone. Editing the live list first - as this did - left every surface that reads
        // FavoriteToolIds describing a toggle that had not reached disk, and kept describing it
        // when the write failed.
        await _configManager.MergeSettingAsync(
            s => s.FavoriteToolIds = new List<string>(toggle.Favorites));

        Heimdall.Core.Logging.FileLogger.Debug(
            $"Favorite tools updated: {toggle.NormalizedId} => {(toggle.Added ? "added" : "removed")}");
        FavoritesChanged?.Invoke(toggle.NormalizedId);
    }

    public MainViewModel(
        IConfigManager configManager,
        LocalizationManager localizer,
        ApplicationStatusMachine appStatus,
        HostKeyStore hostKeyStore,
        IDialogService dialogService,
        IEmbeddedSessionManager embeddedSessionManager,
        HeimdallThemeService themeService,
        ISessionRestoreCoordinator sessionRestore,
        IPostConnectSequenceRunner postConnectSequenceRunner,
        IPostConnectStepResolver postConnectStepResolver,
        ToolRegistry toolRegistry,
        SplitService splitService,
        ToolsTabPopulationService toolsTabPopulation,
        IToolContextProvider toolContextProvider,
        IUiDispatcher uiDispatcher,
        WorkspaceLockService workspaceLock,
        ServerListViewModel serverList,
        ConnectionViewModel connection,
        SettingsViewModel settings,
        UpdateBannerViewModel update,
        IPaneCloseArbiter closeArbiter,
        ICommandPaletteViewModelFactory commandPaletteFactory,
        ITunnelsViewModelFactory tunnelsFactory)
    {
        _closeArbiter = closeArbiter ?? throw new ArgumentNullException(nameof(closeArbiter));
        _configManager = configManager;
        _workspaceLock = workspaceLock;
        _localizer = localizer;
        _appStatus = appStatus;
        _hostKeyStore = hostKeyStore;
        _dialogService = dialogService;
        _embeddedSessionManager = embeddedSessionManager;
        _themeService = themeService;
        _sessionRestore = sessionRestore;
        _uiDispatcher = uiDispatcher;
        ToolRegistry = toolRegistry;
        Split = splitService;

        // Built from the SAME arbiter instance every other close path uses: a clearance obtained in
        // one gesture has to be honoured wherever that gesture continues.
        _paneCloseCoordinator = new PaneCloseCoordinator(
            splitService.ClosePane,
            _closeArbiter,
            dialogService,
            localizer);
        ServerList = serverList;
        Connection = connection;
        Settings = settings;
        Update = update;
        Sidebar = new SidebarViewModel(this, localizer, configManager, toolsTabPopulation, toolContextProvider, _uiDispatcher);
        ToolsTab = new ToolsTabViewModel(this, localizer, toolContextProvider);
        CommandPalette = commandPaletteFactory.Create(this);
        Tunnels = tunnelsFactory.Create(this);
        Scheduled = new ScheduledTasksViewModel(this, localizer, dialogService, configManager, _uiDispatcher);
        Session = new SessionCoordinator(this, localizer, configManager, embeddedSessionManager, postConnectSequenceRunner, postConnectStepResolver, _uiDispatcher);

        // Workspace lock: mask the session host + show the re-unlock overlay on lock,
        // disconnect sessions on lock when the policy requires it (D3).
        IsWorkspaceLocked = _workspaceLock.IsWorkspaceLocked;
        _workspaceLock.SetSessionDisconnector(DisconnectAllSessionsForLock);
        _workspaceLock.LockStateChanged += OnWorkspaceLockStateChanged;

        _appStatus.StatusChanged += OnApplicationStatusChanged;
        _localizer.LocaleChanged += OnLocaleChanged;

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
            OpenToolTabAsync(toolId, title, ctx).SafeFireAndForget();
        };
        ServerList.ToolSessionRequested += _onToolSessionRequested;
        _onStatusMessageRequested = message => StatusText = message;
        ServerList.StatusMessageRequested += _onStatusMessageRequested;

        _connectionPropertyChangedHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ConnectionViewModel.ActiveSession), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(DisplayedSession));
                ObserveDisplayedSession();
            }
        };
        Connection.PropertyChanged += _connectionPropertyChangedHandler;

        SetLocalizedApplicationStatus("StatusReady");
    }

    // ── Workspace lock ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionHostVisible))]
    private bool _isWorkspaceLocked;

    /// <summary>
    /// Whether the embedded session host is visible. Hidden while locked so its
    /// Win32 (RDP ActiveX) and WebView2 (terminal/VNC) child HWNDs are masked - a
    /// WPF overlay alone cannot cover those, so the host must be collapsed.
    /// </summary>
    public bool IsSessionHostVisible => !IsWorkspaceLocked;

    /// <summary>The re-unlock ViewModel bound by the lock overlay (null while unlocked).</summary>
    [ObservableProperty]
    private VaultUnlockDialogViewModel? _lockOverlayViewModel;

    [RelayCommand(CanExecute = nameof(CanLockWorkspace))]
    private void LockWorkspace() => _workspaceLock.Lock();

    private bool CanLockWorkspace() => _workspaceLock.CanLock;

    private void OnWorkspaceLockStateChanged()
    {
        _uiDispatcher.Invoke(() =>
        {
            IsWorkspaceLocked = _workspaceLock.IsWorkspaceLocked;
            LockWorkspaceCommand.NotifyCanExecuteChanged();

            if (IsWorkspaceLocked)
            {
                // Reuse the unlock VM for the overlay; in-memory (non-persisted) lockout.
                LockOverlayViewModel = CreateLockOverlayViewModel();
            }
            else
            {
                LockOverlayViewModel = null;
                Session.ResumeDeferredReconnects();
            }
        });
    }

    private VaultUnlockDialogViewModel CreateLockOverlayViewModel()
    {
        bool showHelloUnlock = _currentSettings is not null
            && VaultHelloUnlockOfferPolicy.ShouldOfferHelloUnlock(_currentSettings, DateTimeOffset.UtcNow);

        return new VaultUnlockDialogViewModel(
            password => _workspaceLock.UnlockAsync(password),
            new PinManager(),
            _localizer,
            migrationInProgress: false,
            () => _workspaceLock.UnlockWithHelloAsync(),
            showHelloUnlock,
            () => _dialogService.ShowConfirmAsync(
                _localizer["VaultHelloReenrollTitle"],
                _localizer["VaultHelloReenrollPrompt"],
                "warning"),
            () => _workspaceLock.EnrollHelloAsync());
    }

    /// <summary>
    /// Tears every session down because the workspace locked.
    /// </summary>
    /// <remarks>
    /// Silent: a lock must actually lock. Prompting would leave a dialog on a workspace the user
    /// just secured, and a guard withholding a session would leave it open and connected behind
    /// the lock screen - the opposite of what locking is for.
    /// </remarks>
    private void DisconnectAllSessionsForLock()
    {
        foreach (var tab in Connection.ActiveSessions.ToList())
        {
            Connection.CloseSessionAsync(
                tab,
                DisconnectReason.UserAction,
                confirm: false,
                CloseIntent.Silent)
                .SafeFireAndForget();
        }
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
            await Settings.RefreshVaultStatusAsync();
            Scheduled.Load(settings);

            await _sessionRestore.RestoreAsync(ServerList, cancellationToken);

            // OperationScope.Dispose() handles the Ready transition
            SetLocalizedApplicationStatus("StatusReady");
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
        => SessionSnapshotProjection.FromSessions(Connection.ActiveSessions);

    private void OnSettingsChanged(AppSettings settings)
    {
        _currentSettings = settings;
        SleepPrevention.Enabled = settings.PreventSleepDuringSession;

        // The settings panel seeds its gateway buffer once and nothing reseeds it on the way in,
        // so a gateway created from a session dialog was on disk and in the tree while the panel
        // still read "no gateway configured". Absorbing rather than reloading is deliberate: the
        // panel buffers so that Cancel can discard, and a wholesale reseed here would throw away
        // edits in progress. Marshalled because Gateways is bound to the UI, and not awaited
        // because the raiser runs this inline for every subscriber - blocking it on the UI
        // thread would make an observer's cost the writer's problem.
        _ = _uiDispatcher.InvokeAsync(() => Settings.AbsorbExternallyCreatedGateways(settings));
    }

    private void OnApplicationStatusChanged(ApplicationStatus previous, ApplicationStatus current)
    {
        var metadata = ApplicationStatusMachine.GetMetadata(current);
        SetLocalizedApplicationStatus(metadata.DisplayKey);
        IsBusy = !metadata.AllowsUserAction;
    }

    private void OnLocaleChanged(string locale)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _ = _uiDispatcher.InvokeAsync(RefreshLocalizedShellState);
            return;
        }

        RefreshLocalizedShellState();
    }

    private void RefreshLocalizedShellState()
    {
        string? statusKey = _localizedApplicationStatusKey;
        if (statusKey is not null)
        {
            SetLocalizedApplicationStatus(statusKey);
        }
        else if (_statusTextIsSessionDerived)
        {
            RefreshSessionStatusText();
        }

        OnPropertyChanged(nameof(DropToMergeText));
    }

    /// <summary>
    /// Follows whichever session is on screen, so the status bar can be re-derived when its
    /// condition changes rather than when someone remembers to say so.
    /// </summary>
    /// <remarks>
    /// The bar used to be written once, at connection, and never revised. A session that
    /// dropped left the sentence "Connected to: X" standing over a panel already announcing
    /// the disconnection. Adding one more write on the VNC drop path would have fixed VNC
    /// and left every other path exactly as wrong.
    /// </remarks>
    private void ObserveDisplayedSession()
    {
        SessionTabViewModel? session = DisplayedSession;
        if (ReferenceEquals(session, _observedSession))
        {
            return;
        }

        if (_observedSession is not null && _observedSessionHandler is not null)
        {
            _observedSession.PropertyChanged -= _observedSessionHandler;
        }

        _observedSession = session;

        if (session is not null)
        {
            _observedSessionHandler ??= (_, e) =>
            {
                if (string.Equals(e.PropertyName, nameof(SessionTabViewModel.Status), StringComparison.Ordinal)
                    || string.Equals(e.PropertyName, nameof(SessionTabViewModel.Title), StringComparison.Ordinal))
                {
                    RefreshSessionStatusText();
                }
            };
            session.PropertyChanged += _observedSessionHandler;
        }

        RefreshSessionStatusText();
    }

    /// <summary>
    /// States the displayed session's condition in the status bar, from the session itself.
    /// </summary>
    /// <remarks>
    /// An established session keeps the wording it has always had. Any other condition is
    /// named with the same short label the tab header uses, through the shared resolver, so
    /// the two surfaces cannot drift apart. A pane carrying a free-form failure reason shows
    /// that reason, because hiding a diagnostic behind "Error" helps nobody.
    /// </remarks>
    private void RefreshSessionStatusText()
    {
        SessionTabViewModel? session = DisplayedSession;
        if (session is null)
        {
            SetLocalizedApplicationStatus("StatusReady");
            return;
        }

        // Title, not DisplayTitle: the latter carries suffixes that would change the
        // sentence this bar has always shown for a connected session.
        string displayName = session.Title ?? string.Empty;
        string text;

        if (SessionStatusDisplay.IsEstablished(session.Status))
        {
            text = _localizer.Format("StatusConnected", displayName);
        }
        else
        {
            string? key = SessionStatusDisplay.ResolveKey(session.Status);
            string condition = key is null ? session.Status ?? string.Empty : _localizer[key];
            text = _localizer.Format("StatusSessionState", displayName, condition);
        }

        _isSettingLocalizedApplicationStatus = true;
        try
        {
            StatusText = text;
            _localizedApplicationStatusKey = null;
            _statusTextIsSessionDerived = true;
        }
        finally
        {
            _isSettingLocalizedApplicationStatus = false;
        }
    }

    private void SetLocalizedApplicationStatus(string localizationKey)
    {
        _isSettingLocalizedApplicationStatus = true;
        try
        {
            StatusText = _localizer[localizationKey];
            _localizedApplicationStatusKey = localizationKey;
        }
        finally
        {
            _isSettingLocalizedApplicationStatus = false;
        }
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
        _localizer.LocaleChanged -= OnLocaleChanged;
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

    // ── Split operations (delegated to SplitService) ─────────────────

    public async Task SplitSessionWithServerAsync(
        SessionTabViewModel session,
        string serverId,
        Core.Models.SplitOrientation orientation,
        string? paneId = null)
    {
        await Split.SplitSessionWithServerAsync(session, serverId, orientation, paneId);
    }

    // --- Connect as... (transient ad-hoc protocol switch) ---

    /// <summary>
    /// Connects the given server's host using a chosen protocol as a transient
    /// ad-hoc session, carrying the username only (no password is propagated across
    /// protocols; credential autofill / external vault / prompt resolve the rest).
    /// Mirrors the Command Palette ad-hoc connect flow without persisting a profile.
    /// </summary>
    public async Task ConnectServerAsProtocolAsync(ServerItemViewModel server, string protocol)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return;
        }

        var dto = BuildTransientProfile(server, protocol);
        var connType = dto.ConnectionType;
        var settings = await _configManager.LoadSettingsAsync();

        // Credential-safe logging: host + protocol only, never username/password.
        Core.Logging.FileLogger.Info($"Connect as {connType} to host={dto.RemoteServer}");

        var connectionService = ServerList.ConnectionService;
        ConnectionResult result = connType switch
        {
            "RDP" => await connectionService.ConnectRdpAsync(dto, settings),
            "SFTP" => await connectionService.ConnectSftpAsync(dto, settings),
            "VNC" => await connectionService.ConnectVncAsync(dto, settings),
            "TELNET" => await connectionService.ConnectTelnetAsync(dto, settings),
            _ => await connectionService.ConnectSshAsync(dto, settings),
        };

        if (result.Success && result.Session is not null)
        {
            var tab = Connection.AddSession(
                dto.Id,
                dto.DisplayName,
                connType,
                settings.MaxEmbeddedSessions);
            if (tab is null)
            {
                SessionCoordinator.SafeDisposeSessionResult(result.Session);
                return;
            }

            tab.MarkAsAdHoc(dto);

            tab.HostControl = _embeddedSessionManager.CreateHostControl(
                tab, dto.DisplayName, connType, result.Session, settings);
            if (tab.HostControl is EmbeddedRdpView rdpView)
            {
                rdpView.SetOwningPane(tab.PrimaryPane);
            }

            tab.Status = SessionStatusTokens.Connected;
            StatusText = _localizer.Format("StatusConnected",
                !string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.DisplayName : dto.RemoteServer);
        }
        else if (result.Success)
        {
            // External mode: a process was launched; keep a lightweight ad-hoc tab.
            var tab = Connection.AddSession(dto.Id, dto.DisplayName, connType);
            tab.MarkAsAdHoc(dto);
            tab.Status = SessionStatusTokens.LaunchedExternalClient;
            StatusText = _localizer["StatusLaunchedExternalClient"];
        }
        else
        {
            StatusText = result.ErrorMessage ?? _localizer["ErrorConnectionFailed"];
        }
    }

    /// <summary>
    /// Builds a transient (never persisted) <see cref="ServerProfileDto"/> that connects
    /// the server's host with the chosen protocol. Carries the host and protocol default
    /// port, plus the username when that protocol supports a profile username. No password
    /// is set. The Id is prefixed <c>adhoc-</c> so the session is treated as ad-hoc. Pure helper (no
    /// connection) for unit testing.
    /// </summary>
    internal static ServerProfileDto BuildTransientProfile(ServerItemViewModel server, string protocol)
    {
        var connType = protocol.ToUpperInvariant();
        var host = server.RemoteServer ?? string.Empty;
        var username = server.Username ?? string.Empty;
        var displayName = !string.IsNullOrWhiteSpace(host) ? host : server.DisplayName;

        var dto = new ServerProfileDto
        {
            Id = "adhoc-" + Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            RemoteServer = host,
            ConnectionType = connType,
        };

        switch (connType)
        {
            case "RDP":
                dto.RemotePort = DefaultPorts.Rdp;
                dto.RdpUsername = username;
                break;
            case "SFTP":
                dto.SshPort = DefaultPorts.Sftp;
                dto.SshUsername = username;
                break;
            case "VNC":
                // VNC has no username field; it authenticates with a password resolved at connect.
                dto.VncPort = DefaultPorts.Vnc;
                break;
            case "TELNET":
                dto.TelnetPort = DefaultPorts.Telnet;
                break;
            default: // SSH
                dto.SshPort = DefaultPorts.Ssh;
                dto.SshUsername = username;
                break;
        }

        return dto;
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
    private async Task CloseSecondaryPane(SessionTabViewModel? session)
    {
        if (session is null || !session.IsSplit) return;

        SessionPaneModel? secondaryPane = session.SecondaryPaneOrNull;
        if (secondaryPane is not null)
        {
            await ClosePaneAsync(session, secondaryPane.PaneId);
        }
    }

    /// <summary>
    /// Closes one pane, honouring its close guard.
    /// </summary>
    /// <remarks>
    /// The protocol itself lives in <see cref="PaneCloseCoordinator"/>, beside the arbiter it
    /// negotiates with. This stays as the shell's entry point because the views call it.
    /// </remarks>
    public Task<PaneCloseResult> ClosePaneAsync(
        SessionTabViewModel session,
        string paneId,
        DisconnectReason reason = DisconnectReason.UserAction,
        CloseIntent intent = CloseIntent.Interactive)
        => _paneCloseCoordinator.ClosePaneAsync(session, paneId, reason, intent);

    [RelayCommand]
    private async Task ReconnectSecondaryAsync(SessionTabViewModel? session)
    {
        if (session is null || !session.IsSplit) return;

        SessionPaneModel? secondaryPane = session.SecondaryPaneOrNull;
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
    public void TrackRecentTool(string toolId) => _recentTools.Track(toolId);

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
        await Settings.RefreshVaultStatusAsync();
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

                // Bridge case: an already-open Command Library tab is focused
                // rather than re-created, so apply the requested preselection to
                // the live instance (a fresh open handles it via Initialize).
                if (!string.IsNullOrWhiteSpace(context?.InitialActionId)
                    && existing.HostControl is Views.Tools.CommandLibraryView commandLibraryView)
                {
                    commandLibraryView.PreselectAction(context.InitialActionId!);
                }

                return;
            }
        }

        var sessionId = $"tool-{toolId.ToLowerInvariant()}-{Guid.NewGuid():N}";

        var tab = Connection.AddSession(sessionId, title, connectionType);
        try
        {
            tab.HostControl = _embeddedSessionManager.CreateToolControl(
                tab, toolId, context, _currentSettings);
            // Deliberately free text rather than a state token: a tool pane is not a session, and
            // the connected-session census must not count it as one.
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
