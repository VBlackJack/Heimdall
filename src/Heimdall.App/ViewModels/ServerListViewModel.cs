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

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Network;
using Heimdall.Core.Security;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Core.SessionHealth;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Microsoft.Win32;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel for the server list with filtering, sorting, and connection actions.
/// </summary>
public partial class ServerListViewModel : ObservableObject, IDisposable, ISessionRestoreHost
{
    internal static readonly TimeSpan SearchFilterDebounceDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Default SSH port; omitted from a generated <c>ssh</c> command line.</summary>
    private const int DefaultSshPort = 22;

    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly ConnectionService _connectionService;
    private readonly IProfileImportService _profileImportService;
    private readonly PuttySessionImporter _puttySessionImporter;
    private readonly KnownHostsImporter _knownHostsImporter;

    internal ConnectionService ConnectionService => _connectionService;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Shared with the settings panel: both create a gateway from outside that panel, and
    /// one owner for that sequence is the point.
    /// </summary>
    private IGatewayCreationService? _gatewayCreation;
    private bool _disposed;

    private List<ServerItemViewModel> _allServers = [];

    /// <summary>
    /// Every saved profile, whatever the sidebar is currently showing.
    /// </summary>
    /// <remarks>
    /// <para><c>Servers</c> is the FILTERED view: applying a filter replaces its contents with the
    /// visible results. Anything that answers a question about the inventory rather than about the
    /// list on screen must read this instead, or the answer changes when the user types in the
    /// search box.</para>
    /// <para>That is not hypothetical. The scheduler resolved a task's profile from the filtered
    /// view, so with a filter active it could not see the profile the task names - and then found
    /// a DIFFERENT profile of the same display name among the visible ones, and connected to it.
    /// No deletion and no stale reference required; a filter was enough.</para>
    /// </remarks>
    internal IReadOnlyList<ServerItemViewModel> AllServers => _allServers;
    private readonly Dictionary<string, ServerItemViewModel> _healthServerById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastHealthGenerationByServerId =
        new(StringComparer.Ordinal);
    private List<ProjectTarget> _projectTargets = [];
    private readonly Dictionary<string, Dictionary<string, SessionStateRevision>> _sessionStatesByInventoryId =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalSessionRevision> _lastTerminalSessionRevisionByInventoryId =
        new(StringComparer.Ordinal);
    private AppSettings? _currentSettings;
    private readonly HashSet<string> _expandedNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _expandSaveSync = new();
    private ITimer? _searchFilterTimer;
    private System.Threading.Timer? _expandSaveTimer;
    private Task _expandSaveTask = Task.CompletedTask;
    private long _expandSaveVersion;
    private bool _expandStateSavePending;
    private bool _expandStateFlushInProgress;
    private int _searchFilterVersion;
    private static readonly TimeSpan ExpandStateSaveDelay = TimeSpan.FromMilliseconds(500);

    [ObservableProperty]
    private ObservableCollection<ServerItemViewModel> _servers = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _filteredCount;

    [ObservableProperty]
    private ObservableCollection<FolderViewModel> _groupedServers = [];

    [ObservableProperty]
    private ObservableCollection<string> _projects = [];

    [ObservableProperty]
    private ObservableCollection<string> _groups = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedProject = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ServerItemViewModel? _selectedServer;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    /// <summary>
    /// True when the server list contains no entries, used to show the empty state overlay.
    /// </summary>
    public bool IsEmpty => FilteredCount == 0;

    /// <summary>
    /// True when a server is selected in the TreeView, used to toggle the detail panel.
    /// </summary>
    public bool HasSelection => SelectionCount > 0;

    /// <summary>
    /// Raised BEFORE the protocol-specific connect call, so subscribers can
    /// mount a placeholder tab in a "Connecting" state. Today only the SSH
    /// path fires this; other protocols still rely solely on
    /// <see cref="SessionReady"/>. The event payload intentionally omits
    /// <c>ISessionResult</c> because no session exists yet, but includes the
    /// linked cancellation source that can abort the in-flight SSH connect.
    /// </summary>
    public event Action<string, string, string, string, ServerProfileDto, AppSettings, CancellationTokenSource>? SessionStarting;

    /// <summary>
    /// Raised when a connection result is ready and a session tab should be created.
    /// Parameters: sessionId, originalServerId, displayName, connectionType, session result.
    /// </summary>
    public event Action<string, string, string, string, Core.Models.ISessionResult?, RdpModeOverride>? SessionReady;

    /// <summary>
    /// Raised when a connect that previously fired
    /// <see cref="SessionStarting"/> fails (any reason: result.Success false,
    /// exception, cancellation). Subscribers should remove the placeholder tab.
    /// </summary>
    public event Action<string>? SessionStartFailed;

    /// <summary>
    /// Raised when a connection fails with structured diagnostics and should surface a failed tab.
    /// Parameters: sessionId, originalServerId, displayName, connectionType, user-facing status text, diagnostic payload.
    /// </summary>
    public event Action<string, string, string, string, string, SessionDiagnostic>? SessionFailed;

    /// <summary>
    /// Raised when a TOOL:* entry is double-clicked. MainViewModel handles opening the tool tab.
    /// Parameters: (toolId, displayName, context).
    /// </summary>
    public event Action<string, string, Core.Models.ToolContext>? ToolSessionRequested;

    /// <summary>
    /// Raised when a non-modal status message should be surfaced in the shell.
    /// </summary>
    public event Action<string>? StatusMessageRequested;

    public ServerListViewModel(
        IConfigManager configManager,
        LocalizationManager localizer,
        IUiDispatcher uiDispatcher,
        ConnectionStateMachine connectionSm,
        ConnectionService connectionService,
        IDialogService dialogService,
        IRdpImportService rdpImportService,
        PuttySessionImporter puttySessionImporter,
        KnownHostsImporter knownHostsImporter,
        IRecentConnectionTracker? recentConnections = null,
        IProfileImportService? profileImportService = null,
        SessionHealthMonitor? healthMonitor = null,
        ICredentialProviderFactory? credentialProviderFactory = null,
        IWindowsHelloService? windowsHelloService = null,
        TimeProvider? timeProvider = null,
        ICredentialGuardService? credentialGuardService = null)
    {
        _configManager = configManager;
        _localizer = localizer;
        _uiDispatcher = uiDispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionSm = connectionSm;
        _connectionService = connectionService;
        _dialogService = dialogService;
        _profileImportService = profileImportService
            ?? new ProfileImportService(configManager, localizer, dialogService, rdpImportService);
        _puttySessionImporter = puttySessionImporter;
        _knownHostsImporter = knownHostsImporter;
        _recentConnections = recentConnections ?? new RecentConnectionTracker();
        _healthMonitor = healthMonitor;
        _credentialProviderFactory = credentialProviderFactory ?? new CredentialProviderFactory();
        _windowsHelloService = windowsHelloService ?? new WindowsHelloService();
        _credentialGuardService = credentialGuardService ?? new CredentialGuardService();

        InitializeFilterOptions();
        InitializeSelectionModel();
        _connectionSm.StateChanged += OnConnectionStateChanged;
        if (_healthMonitor is not null)
        {
            _healthMonitor.StatusChanged += OnServerHealthChanged;
        }
    }

    private readonly SessionHealthMonitor? _healthMonitor;

    /// <summary>
    /// Routes a health probe verdict back to the corresponding
    /// <see cref="ServerItemViewModel"/>. Always marshals to the UI thread
    /// because <see cref="SessionHealthMonitor.StatusChanged"/> fires from a
    /// background scheduler thread.
    /// </summary>
    private void OnServerHealthChanged(HealthStateChange change)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            ApplyServerHealthChange(change);
        });
    }

    internal int HealthServerIndexCount => _healthServerById.Count;

    internal bool ApplyServerHealthChange(HealthStateChange change)
    {
        if (_lastHealthGenerationByServerId.TryGetValue(
                change.ServerId,
                out long lastGeneration)
            && change.Generation < lastGeneration)
        {
            return false;
        }

        _lastHealthGenerationByServerId[change.ServerId] = change.Generation;
        if (!_healthServerById.TryGetValue(change.ServerId, out ServerItemViewModel? vm))
        {
            return false;
        }

        vm.HealthState = change.State;
        return true;
    }

    private void RebuildHealthServerIndex()
    {
        _healthServerById.Clear();
        foreach (ServerItemViewModel server in _allServers)
        {
            if (!string.IsNullOrEmpty(server.Id))
            {
                _healthServerById[server.Id] = server;
            }
        }

        // Generation entries deliberately outlive an index entry so a health
        // event queued before removal cannot regress a later VM with the same ID.
    }

    private readonly IRecentConnectionTracker _recentConnections;

    internal int ActiveSessionAggregationEntryCount => _sessionStatesByInventoryId.Count;

    private readonly ICredentialProviderFactory _credentialProviderFactory;

    private readonly IWindowsHelloService _windowsHelloService;

    private readonly ICredentialGuardService _credentialGuardService;

    /// <summary>
    /// In-memory timestamp of the last successful Windows Hello verification. Used to honor
    /// the grace window so the user is not prompted on every connect. Not persisted across
    /// app restarts by design.
    /// </summary>
    private DateTimeOffset? _lastWindowsHelloVerifiedAt;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachStableTreeFolderEvents();
        _searchFilterTimer?.Dispose();
        lock (_expandSaveSync)
        {
            _expandSaveVersion++;
            _expandSaveTimer?.Dispose();
            _expandSaveTimer = null;
            _expandStateSavePending = false;
        }

        _connectionSm.StateChanged -= OnConnectionStateChanged;
        if (_healthMonitor is not null)
        {
            _healthMonitor.StatusChanged -= OnServerHealthChanged;
        }
    }

    /// <summary>
    /// Confirms and best-effort persists trust for a profile carrying local-execution payload.
    /// </summary>
    internal async Task<bool> ConfirmAndTrustExecutionAsync(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string executable = string.IsNullOrWhiteSpace(profile.LocalShellExecutable)
            ? "powershell.exe"
            : profile.LocalShellExecutable;
        string body = _localizer.Format(
            "ConfirmExecutionImportedBody",
            profile.DisplayName,
            executable);
        bool confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmExecutionImportedTitle"],
            body,
            "warning");

        if (!confirmed)
        {
            return false;
        }

        profile.ExecutionConfirmed = true;
        await PersistExecutionTrustAsync(profile);

        return true;
    }

    /// <summary>
    /// Confirms and best-effort persists trust for imported post-connect commands.
    /// </summary>
    internal async Task<bool> ConfirmAndTrustPostConnectAsync(ServerProfileDto profile, int commandCount)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string body = _localizer.Format(
            "ConfirmPostConnectImportedBody",
            profile.DisplayName,
            commandCount);
        bool confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmPostConnectImportedTitle"],
            body,
            "warning");

        if (!confirmed)
        {
            return false;
        }

        profile.ExecutionConfirmed = true;
        await PersistExecutionTrustAsync(profile);

        return true;
    }

    private async Task PersistExecutionTrustAsync(ServerProfileDto profile)
    {
        try
        {
            await _configManager.MutateServersAsync(servers =>
            {
                // The profile this session belongs to, as the profile itself records it. Read
                // from the shared property rather than re-derived here: the certificate question
                // applies the same rule from a different layer, and the two agreeing by
                // resemblance is how one of them shipped inverting the mint unconditionally.
                //
                // Deliberately not narrowed by `servers`. Reading the inventory looks like the
                // more careful answer and is the weaker one: a profile deleted while this
                // connection was still running is absent from it, and treating absence as
                // evidence of a mint is exactly what filed one profile's approval under another.
                string inventoryId = profile.InventoryProfileId;

                ServerProfileDto? stored = servers.FirstOrDefault(
                    server => string.Equals(server.Id, inventoryId, StringComparison.Ordinal));

                if (stored is not null)
                {
                    stored.ExecutionConfirmed = true;
                }

                return stored is not null;
            });
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"Failed to persist execution-trust for '{profile.DisplayName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Populates the server list from loaded DTOs and settings.
    /// </summary>
    public void LoadServers(List<ServerProfileDto> serverDtos, AppSettings settings)
    {
        _currentSettings = settings;
        var selectedServerId = SelectedServer?.Id;
        var projectMap = BuildProjectMap(settings);

        // Restore expand state from settings
        _expandedNodes.Clear();
        if (settings.TreeExpandedNodes.Count > 0)
        {
            foreach (var node in settings.TreeExpandedNodes)
            {
                _expandedNodes.Add(node);
            }
        }

        var gatewayMap = BuildGatewayMap(settings);
        _allServers = serverDtos
            .Select(dto => ServerItemViewModel.FromDto(
                dto,
                ResolveProject(projectMap, dto.ProjectId),
                _connectionSm.GetState(dto.Id).ToString(),
                gatewayMap,
                _localizer))
            .ToList();
        RebuildHealthServerIndex();

        RefreshLookupCollections(settings);
        IsSidebarVisible = !settings.SidebarCollapsed;
        RebuildStableTreeProjection();
        ApplyFilter(selectedServerId);
    }

    public IReadOnlyList<ProjectTarget> GetProjectTargets(bool includeNoProject)
    {
        var targets = new List<ProjectTarget>();

        if (includeNoProject)
        {
            targets.Add(new ProjectTarget(
                string.Empty,
                _localizer["TreeNodeNoProject"],
                string.Empty,
                IsVirtualProject: true));
        }

        targets.AddRange(_projectTargets);
        return targets;
    }

    internal void RefreshLocalizedState()
    {
        foreach (ServerItemViewModel server in _allServers)
        {
            server.RefreshLocalizedState();
        }

        foreach (StableFolderNode folder in EnumerateStableFolders(_stableTreeRoot))
        {
            folder.ViewModel!.RefreshLocalizedState();
        }
    }

    /// <summary>
    /// Every folder a server can be moved into.
    /// </summary>
    /// <remarks>
    /// This used to take a project id and return only the folders already used by servers in that
    /// project. Nothing on screen said so, so "Move to folder" quietly omitted folders the user
    /// could see in the tree, with no way to tell why. That was the one behavioural effect Project
    /// ever had, and it was invisible - which is a large part of why nobody could say what a
    /// project was for. The concept is gone; the narrowing goes with it.
    /// </remarks>
    public IReadOnlyList<GroupTarget> GetGroupTargets(bool includeNoGroup)
    {
        var targets = new List<GroupTarget>();

        if (includeNoGroup)
        {
            targets.Add(new GroupTarget(
                string.Empty,
                _localizer["TreeNodeNoGroup"],
                IsVirtualGroup: true));
        }

        // One row per folder, from both sources at once. A folder created in the tree is written
        // to EmptyGroups and stays there once a session moves into it, so the two sources overlap
        // and appending one after the other listed the same path twice, in two interchangeable
        // rows with nothing to tell them apart. Distinct keeps the first spelling seen, which is a
        // session's own - the one the tree displays - and sorting the union rather than only the
        // in-use half stops empty folders from piling up at the bottom in settings order.
        var groupTargets = _allServers
            .Select(server => server.Group)
            .Concat(_currentSettings?.EmptyGroups ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new GroupTarget(path, path));

        targets.AddRange(groupTargets);

        return targets;
    }

    public async Task ImportRdpFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (paths.Length == 0)
        {
            return;
        }

        var result = await _profileImportService.ImportFromPathsAsync(paths, cancellationToken);
        if (result.IsFailure)
        {
            _dialogService.ShowError(
                _localizer["DialogImportRdpTitle"],
                result.ErrorMessage ?? _localizer["StatusImportFailed"]);
            return;
        }

        if (!result.HasChanges)
        {
            return;
        }

        var settings = await _configManager.LoadSettingsAsync();
        var servers = await _configManager.LoadServersAsync();
        LoadServers(servers, settings);

        if (!string.IsNullOrWhiteSpace(result.UserMessage))
        {
            StatusMessageRequested?.Invoke(result.UserMessage);
        }
    }

    public async Task ImportOpenSshConfigAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string contents;
        try
        {
            contents = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            _dialogService.ShowError(
                _localizer["DialogTitleImportOpenSshConfig"],
                _localizer.Format("ErrorImportOpenSshConfigFileUnreadable", filePath));
            return;
        }
        catch (IOException)
        {
            _dialogService.ShowError(
                _localizer["DialogTitleImportOpenSshConfig"],
                _localizer.Format("ErrorImportOpenSshConfigFileUnreadable", filePath));
            return;
        }

        var parseResult = OpenSshConfigParser.Parse(contents);
        if (parseResult.Candidates.Count == 0 && parseResult.Diagnostics.Count == 0)
        {
            _dialogService.ShowInfo(
                _localizer["DialogTitleImportOpenSshConfig"],
                _localizer["ErrorImportOpenSshConfigEmpty"]);
            return;
        }

        var outcome = await _dialogService.ShowImportOpenSshConfigAsync(parseResult);
        if (outcome is null)
        {
            return;
        }

        var settings = await _configManager.LoadSettingsAsync();
        var servers = await _configManager.LoadServersAsync();
        LoadServers(servers, settings);

        var summary = _localizer.Format(
            "ToastImportOpenSshResult",
            outcome.ImportedCount,
            outcome.SkippedDuplicates,
            outcome.WarningCount);

        if (outcome.ImportedCount > 0)
        {
            _dialogService.ShowInfo(_localizer["DialogTitleImportOpenSshConfig"], summary);
            StatusMessageRequested?.Invoke(summary);
        }
        else
        {
            _dialogService.ShowWarning(_localizer["DialogTitleImportOpenSshConfig"], summary);
        }
    }

    public async Task ImportPuttySessionsAsync(CancellationToken cancellationToken = default)
    {
        var parseResult = await _puttySessionImporter.ReadAndParseAsync(cancellationToken);
        if (parseResult.Candidates.Count == 0)
        {
            _dialogService.ShowInfo(
                _localizer["DialogTitleImportPuttySessions"],
                _localizer["InfoImportPuttyNoSessionsFound"]);
            return;
        }

        var outcome = await _dialogService.ShowImportPuttySessionsAsync(parseResult);
        if (outcome is null)
        {
            return;
        }

        var settings = await _configManager.LoadSettingsAsync();
        var servers = await _configManager.LoadServersAsync();
        LoadServers(servers, settings);

        var summary = _localizer.Format(
            "ToastImportPuttyResult",
            outcome.ImportedCount,
            outcome.SkippedDuplicates,
            outcome.SkippedInvalid,
            outcome.WarningCount);

        if (outcome.ImportedCount > 0)
        {
            _dialogService.ShowInfo(_localizer["DialogTitleImportPuttySessions"], summary);
            StatusMessageRequested?.Invoke(summary);
        }
        else
        {
            _dialogService.ShowWarning(_localizer["DialogTitleImportPuttySessions"], summary);
        }
    }

    [RelayCommand]
    private async Task ImportKnownHostsAsync(CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDirectory = string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, ".ssh");
        var dialog = new OpenFileDialog
        {
            Title = _localizer["DialogTitlePickKnownHostsFile"],
            Filter = "known_hosts (known_hosts)|known_hosts|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            FileName = "known_hosts"
        };

        if (!string.IsNullOrWhiteSpace(sshDirectory) && Directory.Exists(sshDirectory))
        {
            dialog.InitialDirectory = sshDirectory;
        }
        else if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
        {
            dialog.InitialDirectory = userProfile;
        }

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        KnownHostsParseResult parsed;
        try
        {
            parsed = await _knownHostsImporter.ParseFileAsync(dialog.FileName, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            _dialogService.ShowError(
                _localizer["DialogTitleImportKnownHosts"],
                _localizer.Format("ErrorImportKnownHostsReadFailed", Path.GetFileName(dialog.FileName)));
            return;
        }
        catch (IOException)
        {
            _dialogService.ShowError(
                _localizer["DialogTitleImportKnownHosts"],
                _localizer.Format("ErrorImportKnownHostsReadFailed", Path.GetFileName(dialog.FileName)));
            return;
        }

        if (parsed.Entries.Count == 0 && parsed.Diagnostics.Count == 0)
        {
            _dialogService.ShowInfo(
                _localizer["DialogTitleImportKnownHosts"],
                _localizer["ErrorImportKnownHostsNoEntries"]);
            return;
        }

        var preview = await _knownHostsImporter.BuildPreviewAsync(parsed, cancellationToken);
        var outcome = await _dialogService.ShowImportKnownHostsAsync(preview);
        if (outcome is null)
        {
            return;
        }

        var warningCount = preview.Diagnostics.Count(diagnostic => diagnostic.Level == KnownHostsDiagnosticLevel.Warning);
        var summary = _localizer.Format(
            "ToastImportKnownHostsResult",
            outcome.Imported,
            outcome.SkippedExisting,
            outcome.SkippedConflict,
            warningCount);
        StatusMessageRequested?.Invoke(summary);
    }

    private readonly HashSet<string> _connectingServerIds = new(StringComparer.Ordinal);

    [RelayCommand]
    private async Task ConnectAsync(ServerItemViewModel? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }

        await ConnectCoreAsync(server, cancellationToken);
    }

    [RelayCommand]
    private async Task ConnectEmbeddedAsync(ServerItemViewModel? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }

        await ConnectCoreAsync(server, cancellationToken, RdpModeOverride.ForceEmbedded);
    }

    [RelayCommand]
    private async Task ConnectExternalAsync(ServerItemViewModel? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }

        await ConnectCoreAsync(server, cancellationToken, RdpModeOverride.ForceExternal);
    }

    /// <summary>
    /// Restores a server session by stable inventory ID using the standard connection pipeline.
    /// Returns false when the server no longer exists or the connection fails.
    /// </summary>
    internal async Task<bool> RestoreServerAsync(string originalServerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalServerId);

        var server = Servers.FirstOrDefault(
            candidate => string.Equals(candidate.Id, originalServerId, StringComparison.OrdinalIgnoreCase));

        if (server is null)
        {
            Core.Logging.FileLogger.Warn(
                $"RestoreServerAsync could not find server with id={originalServerId}.");
            return false;
        }

        return await ConnectCoreAsync(
            server,
            cancellationToken,
            RdpModeOverride.UseProfile,
            allowCredentialPrompt: false);
    }

    /// <inheritdoc />
    IEnumerable<ServerItemViewModel> ISessionRestoreHost.RestorableServers => Servers;

    /// <inheritdoc />
    Task<bool> ISessionRestoreHost.RestoreServerAsync(
        string originalServerId,
        CancellationToken cancellationToken)
        => RestoreServerAsync(originalServerId, cancellationToken);

    /// <param name="allowCredentialPrompt">
    /// Whether this connection may put a credential question to the user. True for a
    /// connection someone just asked for; false when the application is reconnecting on
    /// its own, where a modal would arrive unbidden - and, on a restore of several
    /// sessions, arrive several times.
    /// </param>
    private async Task<bool> ConnectCoreAsync(
        ServerItemViewModel server,
        CancellationToken cancellationToken,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile,
        bool allowCredentialPrompt = true)
    {
        // Prevent duplicate connections from rapid double-clicks
        if (!_connectingServerIds.Add(server.Id))
        {
            return false;
        }

        try
        {

            var servers = await _configManager.LoadServersAsync();
            var serverDto = servers.FirstOrDefault(
                s => string.Equals(s.Id, server.Id, StringComparison.Ordinal));

            if (serverDto is null)
            {
                return false;
            }

            // Only a connection someone asked for may ask something back. A restore
            // reconnecting on its own must fail quietly rather than raise a modal - or
            // several, one per restored session. The copy is detached and never saved.
            serverDto.AllowCredentialPrompt = allowCredentialPrompt;

            var settings = await _configManager.LoadSettingsAsync();

            if (!await EnforceCredentialGuardAsync(
                    serverDto,
                    settings,
                    rdpModeOverride,
                    cancellationToken,
                    showMessage: true))
            {
                return false;
            }

            // Windows Hello gate: when enabled, require a successful biometric/PIN
            // verification before any stored credentials are resolved or used.
            if (!await EnsureWindowsHelloAsync(settings, cancellationToken))
            {
                return false;
            }

            // Apply group-level inherited defaults (gateway, SSH username, key path)
            // before preflight and connection. Server's own values take priority.
            if (settings.GroupDefaults.Count > 0 && !string.IsNullOrEmpty(serverDto.Group))
            {
                var groupDefaults = Core.Configuration.GroupDefaultsDto.Resolve(
                    serverDto.Group, settings.GroupDefaults);
                groupDefaults.ApplyTo(serverDto);
            }

            // Resolve credentials from external provider if configured and server
            // has no stored password. The retrieved password is DPAPI-encrypted into
            // the DTO so all downstream code (ConnectionService, EmbeddedRdpView) works
            // without modification.
            _ = await TryResolveExternalCredentialsAsync(
                serverDto,
                settings,
                cancellationToken,
                skipOnFailure: false);

            // Generate a unique session ID so duplicate connections to the same server
            // get independent state tracking (tunnel lifecycle, error recovery)
            string sessionId = SessionIdCodec.Create(server.Id);

            Core.Logging.FileLogger.Info($"ConnectAsync: {server.DisplayName} type={serverDto.ConnectionType} gateway={serverDto.SshGatewayId} sessionId={sessionId}");

            var originalId = serverDto.Id;

            // Tool entries bypass the connection pipeline entirely
            if (ConnectionTypeCatalog.IsToolConnectionType(serverDto.ConnectionType))
            {
                serverDto.Id = originalId;
                _connectionSm.TryTransition(sessionId, Core.Models.ConnectionState.Initializing);
                try
                {
                    var toolId = ConnectionTypeCatalog.StripToolPrefix(serverDto.ConnectionType);
                    var context = new Core.Models.ToolContext(
                        TargetHost: serverDto.RemoteServer,
                        TargetPort: serverDto.RemotePort > 0 ? serverDto.RemotePort : null,
                        Argument: serverDto.RemoteServer,
                        DisplayName: serverDto.DisplayName,
                        Username: serverDto.SshUsername ?? serverDto.RdpUsername,
                        ConnectionType: serverDto.ConnectionType,
                        ProjectName: server.ProjectName);
                    ToolSessionRequested?.Invoke(toolId, server.DisplayName, context);
                    return true;
                }
                finally
                {
                    _connectionSm.Teardown(sessionId);
                }
            }

            var outcome = await RunConnectionPipelineAsync(
                serverDto,
                settings,
                sessionId,
                originalId,
                server,
                cancellationToken,
                rdpModeOverride);

            return outcome.Status switch
            {
                BulkConnectOutcomeStatus.Success => true,
                BulkConnectOutcomeStatus.PreflightFailed => PublishFailureAndShowError(
                    serverDto,
                    sessionId,
                    originalId,
                    server,
                    outcome,
                    _localizer["ErrorPreflightTitle"],
                    outcome.ErrorMessage ?? _localizer["ErrorPreflightFailed"]),
                BulkConnectOutcomeStatus.ConnectionFailed => PublishFailureAndShowError(
                    serverDto,
                    sessionId,
                    originalId,
                    server,
                    outcome,
                    _localizer["ErrorConnectionTitle"],
                    outcome.ErrorMessage ?? _localizer["ErrorConnectionFailed"]),
                BulkConnectOutcomeStatus.Cancelled => false,
                BulkConnectOutcomeStatus.UnsupportedType => false,
                _ => false
            };
        }
        finally
        {
            _connectingServerIds.Remove(server.Id);
        }
    }

    internal async Task<bool> EnforceCredentialGuardAsync(
        ServerProfileDto server,
        AppSettings settings,
        RdpModeOverride rdpModeOverride,
        CancellationToken cancellationToken,
        bool showMessage)
    {
        if (!settings.RequireCredentialGuard
            || !string.Equals(server.ConnectionType, "RDP", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                RdpHandler.ResolveEffectiveMode(server, rdpModeOverride),
                "Embedded",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        CredentialGuardStatus credentialGuard =
            await _credentialGuardService.GetStatusAsync(cancellationToken);
        if (credentialGuard.State is CredentialGuardState.Active)
        {
            return true;
        }

        if (credentialGuard.State is CredentialGuardState.Indeterminate)
        {
            Core.Logging.FileLogger.Warn(
                _localizer.Format(
                    "LogCredentialGuardCheckFailed",
                    credentialGuard.FailureReason ?? "unknown error"));
        }

        Core.Logging.FileLogger.Warn(
            _localizer.Format("LogEmbeddedCredentialGuardBlocked", server.DisplayName));
        if (showMessage)
        {
            _dialogService.ShowError(
                _localizer["ErrorConnectionTitle"],
                _localizer["ErrorEmbeddedCredentialGuardRequired"]);
        }

        return false;
    }

    internal async Task<BulkConnectOutcome> RunConnectionPipelineAsync(
        ServerProfileDto serverDto,
        AppSettings settings,
        string sessionId,
        string originalId,
        ServerItemViewModel server,
        CancellationToken cancellationToken,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        var preflight = _connectionService.RunPreflight(serverDto, settings);
        if (!preflight.Success)
        {
            server.ConnectionState = "Error";
            return new BulkConnectOutcome(
                BulkConnectOutcomeStatus.PreflightFailed,
                preflight.Message ?? _localizer["ErrorPreflightFailed"],
                string.Equals(serverDto.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                    ? SshSessionDiagnosticFactory.FromPreflight(preflight)
                    : null);
        }

        // Through AdoptSessionIdentity: the profile this connection belongs to is recorded here,
        // where it is still known, instead of being read back out of the key's text later.
        serverDto.AdoptSessionIdentity(sessionId);
        var sessionStartFired = false;
        CancellationTokenSource? sessionStartCts = null;
        var lifecycleHandedOff = false;

        try
        {
            _connectionSm.TryTransition(sessionId, Core.Models.ConnectionState.Initializing);
            ConnectionResult result;

            switch (serverDto.ConnectionType?.ToUpperInvariant())
            {
                case "RDP":
                    result = await _connectionService.ConnectRdpAsync(
                        serverDto, settings, cancellationToken, rdpModeOverride);
                    break;

                case "SSH":
                    CancellationToken sshCancellationToken = cancellationToken;
                    if (!string.Equals(serverDto.SshMode, "External", StringComparison.OrdinalIgnoreCase))
                    {
                        sessionStartCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        sessionStartFired = true;
                        SessionStarting?.Invoke(sessionId, originalId, server.DisplayName,
                            "SSH", serverDto, settings, sessionStartCts);
                        sshCancellationToken = sessionStartCts.Token;
                    }

                    result = await _connectionService.ConnectSshAsync(
                        serverDto, settings, sshCancellationToken);
                    break;

                case "SFTP":
                    result = await _connectionService.ConnectSftpAsync(
                        serverDto, settings, cancellationToken);
                    break;

                case "FTP":
                    result = await _connectionService.ConnectFtpAsync(
                        serverDto, settings, cancellationToken);
                    break;

                case "LOCAL":
                    result = await _connectionService.ConnectLocalShellAsync(
                        serverDto, settings, cancellationToken);
                    break;

                case "WINRM":
                    result = await _connectionService.ConnectWinRmAsync(
                        serverDto, settings, cancellationToken);
                    break;

                case "CITRIX":
                    result = await _connectionService.ConnectCitrixAsync(
                        serverDto, settings, cancellationToken);
                    break;

                case "VNC":
                    result = await _connectionService.ConnectVncAsync(
                        serverDto, settings, cancellationToken);
                    break;

                case "TELNET":
                    result = await _connectionService.ConnectTelnetAsync(
                        serverDto, settings, cancellationToken);
                    break;

                default:
                    var unsupportedMessage = _localizer.Format(
                        "ErrorUnsupportedConnectionType",
                        serverDto.ConnectionType ?? "");
                    _connectionSm.SetError(sessionId, unsupportedMessage);
                    server.ConnectionState = "Error";
                    return new BulkConnectOutcome(
                        BulkConnectOutcomeStatus.UnsupportedType,
                        unsupportedMessage);
            }

            if (result.Success)
            {
                SessionReady?.Invoke(
                    sessionId,
                    originalId,
                    server.DisplayName,
                    serverDto.ConnectionType,
                    result.Session,
                    rdpModeOverride);
                lifecycleHandedOff = result.Session is not null;
                return new BulkConnectOutcome(BulkConnectOutcomeStatus.Success, null);
            }

            server.ConnectionState = "Error";
            if (sessionStartFired)
            {
                SessionStartFailed?.Invoke(sessionId);
            }

            return new BulkConnectOutcome(
                BulkConnectOutcomeStatus.ConnectionFailed,
                result.ErrorMessage ?? _localizer["ErrorConnectionFailed"],
                result.Failure);
        }
        catch (OperationCanceledException)
        {
            if (sessionStartFired)
            {
                SessionStartFailed?.Invoke(sessionId);
            }

            return new BulkConnectOutcome(BulkConnectOutcomeStatus.Cancelled, null);
        }
        catch (Exception ex)
        {
            var failure = Ssh.FailureClassifier.Classify(ex);
            if (sessionStartFired)
            {
                SessionStartFailed?.Invoke(sessionId);
            }

            _connectionSm.SetError(sessionId, failure.Message);
            server.ConnectionState = "Error";
            return new BulkConnectOutcome(
                BulkConnectOutcomeStatus.ConnectionFailed,
                failure.Message,
                string.Equals(serverDto.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                    ? SshSessionDiagnosticFactory.FromClassifiedFailure(failure)
                    : null);
        }
        finally
        {
            sessionStartCts?.Dispose();
            serverDto.Id = originalId;
            if (!lifecycleHandedOff)
            {
                _connectionSm.Teardown(sessionId);
            }
        }
    }

    internal static CredentialTarget? GetCredentialTarget(ServerProfileDto dto)
    {
        var connType = dto.ConnectionType?.ToUpperInvariant();

        if (connType is "SSH" or "SFTP" && string.IsNullOrEmpty(dto.SshPasswordEncrypted))
        {
            return new CredentialTarget(
                dto.SshPort, dto.SshUsername,
                encrypted => dto.SshPasswordEncrypted = encrypted,
                username => { if (string.IsNullOrEmpty(dto.SshUsername)) dto.SshUsername = username; });
        }

        if (connType is "RDP" && string.IsNullOrEmpty(dto.RdpPasswordEncrypted))
        {
            return new CredentialTarget(
                dto.RemotePort, dto.RdpUsername,
                encrypted => dto.RdpPasswordEncrypted = encrypted,
                username => { if (string.IsNullOrEmpty(dto.RdpUsername)) dto.RdpUsername = username; });
        }

        if (connType is "WINRM"
            && dto.WinRmIdentityMode == Core.Configuration.WinRmIdentityMode.Credential
            && string.IsNullOrEmpty(dto.WinRmPasswordEncrypted))
        {
            return new CredentialTarget(
                dto.WinRmPort, dto.WinRmUsername,
                encrypted => dto.WinRmPasswordEncrypted = encrypted,
                username => { if (string.IsNullOrEmpty(dto.WinRmUsername)) dto.WinRmUsername = username; });
        }

        if (connType is "FTP" && string.IsNullOrEmpty(dto.FtpPasswordEncrypted))
        {
            return new CredentialTarget(
                dto.FtpPort, dto.FtpUsername,
                encrypted => dto.FtpPasswordEncrypted = encrypted,
                username => { if (string.IsNullOrEmpty(dto.FtpUsername)) dto.FtpUsername = username; });
        }

        if (connType is "VNC" && string.IsNullOrEmpty(dto.VncPassword))
        {
            // VncPassword is DPAPI-encrypted despite the name; VNC has no username field.
            return new CredentialTarget(
                dto.VncPort, null,
                encrypted => dto.VncPassword = encrypted,
                _ => { });
        }

        return null;
    }

    internal readonly record struct CredentialTarget(
        int Port,
        string? Username,
        Action<string> SetPassword,
        Action<string> SetUsernameIfEmpty);

    /// <summary>
    /// Enforces the optional Windows Hello gate before any stored credentials are used.
    /// Returns true to allow the connect, false to abort it. Fail-closed: when the gate is
    /// enabled but Hello is unavailable / not enrolled, the connect is blocked. A successful
    /// verification is remembered for <see cref="AppSettings.WindowsHelloGraceMinutes"/>
    /// minutes (in-memory) so the user is not prompted on every connect.
    /// </summary>
    internal async Task<bool> EnsureWindowsHelloAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.RequireWindowsHelloOnConnect)
        {
            return true;
        }

        // Grace window: a recent successful verification still counts. 0 minutes = always re-verify.
        if (_lastWindowsHelloVerifiedAt is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(settings.WindowsHelloGraceMinutes))
        {
            return true;
        }

        if (!await _windowsHelloService.IsAvailableAsync().ConfigureAwait(true))
        {
            Core.Logging.FileLogger.Warn(
                "Windows Hello required but unavailable or not enrolled; blocking connect.");
            StatusMessageRequested?.Invoke(_localizer["ErrorWindowsHelloUnavailable"]);
            return false;
        }

        try
        {
            bool verified = await _windowsHelloService
                .VerifyAsync(_localizer["WindowsHelloVerifyReason"], ct)
                .ConfigureAwait(true);

            if (verified)
            {
                _lastWindowsHelloVerifiedAt = DateTimeOffset.UtcNow;
                return true;
            }

            StatusMessageRequested?.Invoke(_localizer["WarnWindowsHelloFailed"]);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Clean abort via the status-text channel (no modal, no crash).
            StatusMessageRequested?.Invoke(_localizer["WarnWindowsHelloCancelled"]);
            return false;
        }
    }

    /// <summary>
    /// If the external credential provider is enabled and the server has no stored
    /// password for its connection type, executes the configured command to retrieve
    /// the password and injects it (DPAPI-encrypted) into the DTO. This allows all
    /// downstream code to work unchanged.
    /// </summary>
    internal async Task<bool> TryResolveExternalCredentialsAsync(
        ServerProfileDto serverDto,
        AppSettings settings,
        CancellationToken ct,
        bool skipOnFailure)
    {
        if (!settings.UseExternalCredentialProvider)
        {
            return false;
        }

        var provider = _credentialProviderFactory.Create(settings);

        if (!provider.IsAvailable)
        {
            return false;
        }

        var credTarget = GetCredentialTarget(serverDto);
        if (credTarget is null)
        {
            return false;
        }

        try
        {
            var vaultTitle = string.IsNullOrWhiteSpace(serverDto.VaultEntryName)
                ? serverDto.DisplayName
                : serverDto.VaultEntryName;

            var credential = await provider.GetCredentialAsync(
                serverDto.RemoteServer, credTarget.Value.Port,
                credTarget.Value.Username, vaultTitle, ct);

            if (credential is null)
            {
                Core.Logging.FileLogger.Warn(
                    $"External credential provider returned no result for {serverDto.DisplayName}");
                if (skipOnFailure)
                {
                    return true;
                }

                _dialogService.ShowWarning(
                    _localizer["ErrorConnectionTitle"],
                    _localizer.Format("WarnCredentialProviderNoResult", serverDto.DisplayName));
                return false;
            }

            var encrypted = CredentialProtector.Protect(credential.Password);
            credTarget.Value.SetPassword(encrypted);

            if (!string.IsNullOrEmpty(credential.Username))
            {
                credTarget.Value.SetUsernameIfEmpty(credential.Username);
            }

            Core.Logging.FileLogger.Info(
                $"External credential provider resolved password for {serverDto.DisplayName}");
            return false;
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation
            throw;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"External credential provider failed for {serverDto.DisplayName}: {ex.Message}");
            if (skipOnFailure)
            {
                return true;
            }

            _dialogService.ShowError(
                _localizer["ErrorConnectionTitle"],
                _localizer.Format("ErrorCredentialProviderFailed", ex.Message));
            return false;
        }
    }

    private bool ShowConnectionError(string title, string message)
    {
        _dialogService.ShowError(title, message);
        return false;
    }

    private bool PublishFailureAndShowError(
        ServerProfileDto serverDto,
        string sessionId,
        string originalId,
        ServerItemViewModel server,
        BulkConnectOutcome outcome,
        string title,
        string message)
    {
        PublishFailedSession(serverDto, sessionId, originalId, server, outcome);
        return ShowConnectionError(title, message);
    }

    private void PublishFailedSession(
        ServerProfileDto serverDto,
        string sessionId,
        string originalId,
        ServerItemViewModel server,
        BulkConnectOutcome outcome)
    {
        if (outcome.Failure is null)
        {
            return;
        }

        SessionFailed?.Invoke(
            sessionId,
            originalId,
            server.DisplayName,
            serverDto.ConnectionType,
            outcome.ErrorMessage ?? _localizer["ErrorConnectionFailed"],
            outcome.Failure);
    }

    [RelayCommand]
    private async Task AddServerAsync(ServerDialogSeed? seed, CancellationToken cancellationToken)
    {
        var dialogVm = new ServerDialogViewModel
        {
            DialogTitle = _localizer["DialogTitleAddServer"],
            Group = seed?.GroupName ?? string.Empty,
            SelectedProjectId = seed?.ProjectId ?? string.Empty
        };

        var settings = await _configManager.LoadSettingsAsync();
        dialogVm.SshMode = settings.SshDefaultMode;
        PopulateServerDialogOptions(dialogVm, settings);
        dialogVm.Settings = settings;

        // Reset dirty state after initialization (gateway pre-selection is not a user change)
        dialogVm.IsDirty = false;

        var result = await _dialogService.ShowServerDialogAsync(dialogVm);

        if (result is not { Saved: true })
        {
            return;
        }

        settings = await RereadSettingsAfterDialogAsync();

        // Persist the selected gateway as last-used for future Add Server dialogs
        var savedGatewayId = result.Server.SshGatewayId;
        if (!string.Equals(settings.LastUsedGatewayId, savedGatewayId, StringComparison.Ordinal))
        {
            await _configManager.MergeSettingAsync(s => s.LastUsedGatewayId = savedGatewayId);
        }

        result.Server.Id = Guid.NewGuid().ToString();
        result.Server.Origin = ProfileOrigin.Manual;
        await _configManager.MutateServersAsync(servers =>
        {
            servers.Add(result.Server);
            return result.Server;
        });

        _allServers.Add(ServerItemViewModel.FromDto(
            result.Server,
            ResolveProject(BuildProjectMap(settings), result.Server.ProjectId),
            _connectionSm.GetState(result.Server.Id).ToString(),
            BuildGatewayMap(settings),
            _localizer));
        RebuildHealthServerIndex();

        RefreshLookupCollections(settings);
        RebuildStableTreeProjection();
        ApplyFilter(result.Server.Id);
        OnPropertyChanged(nameof(Servers));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task SaveAdHocAsProfileAsync(ServerProfileDto? template, CancellationToken cancellationToken)
    {
        if (template is null)
        {
            return;
        }

        var dialogVm = ServerDialogViewModel.FromDto(template);
        dialogVm.DialogTitle = _localizer["DialogTitleAddServer"];
        dialogVm.IsEditMode = false;
        dialogVm.Origin = ProfileOrigin.Manual;

        var settings = await _configManager.LoadSettingsAsync();
        PopulateServerDialogOptions(dialogVm, settings);
        dialogVm.Settings = settings;

        // Reset dirty state after initialization so saving is an explicit user choice.
        dialogVm.IsDirty = false;

        var result = await _dialogService.ShowServerDialogAsync(dialogVm);

        if (result is not { Saved: true })
        {
            return;
        }

        settings = await RereadSettingsAfterDialogAsync();

        var savedGatewayId = result.Server.SshGatewayId;
        if (!string.Equals(settings.LastUsedGatewayId, savedGatewayId, StringComparison.Ordinal))
        {
            await _configManager.MergeSettingAsync(s => s.LastUsedGatewayId = savedGatewayId);
        }

        result.Server.Id = Guid.NewGuid().ToString();
        result.Server.Origin = ProfileOrigin.Manual;
        await _configManager.MutateServersAsync(servers =>
        {
            servers.Add(result.Server);
            return result.Server;
        });

        _allServers.Add(ServerItemViewModel.FromDto(
            result.Server,
            ResolveProject(BuildProjectMap(settings), result.Server.ProjectId),
            _connectionSm.GetState(result.Server.Id).ToString(),
            BuildGatewayMap(settings),
            _localizer));
        RebuildHealthServerIndex();

        RefreshLookupCollections(settings);
        RebuildStableTreeProjection();
        ApplyFilter(result.Server.Id);
        OnPropertyChanged(nameof(Servers));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task EditServerAsync(ServerItemViewModel? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }

        var servers = await _configManager.LoadServersAsync();
        var serverDto = servers.FirstOrDefault(
            s => string.Equals(s.Id, server.Id, StringComparison.Ordinal));

        if (serverDto is null)
        {
            return;
        }

        // Tools use a simplified edit flow (name + host) instead of the full ServerDialog
        if (ConnectionTypeCatalog.IsToolConnectionType(serverDto.ConnectionType))
        {
            var newName = await _dialogService.ShowInputAsync(
                _localizer["AddToolDialogTitle"],
                _localizer["AddToolDialogName"],
                serverDto.DisplayName);
            if (string.IsNullOrWhiteSpace(newName)) return;

            var newHost = await _dialogService.ShowInputAsync(
                _localizer["AddToolDialogTitle"],
                _localizer["AddToolDialogHost"],
                serverDto.RemoteServer ?? "");

            string updatedName = newName.Trim();
            string updatedHost = newHost?.Trim() ?? "";
            bool updated = await _configManager.MutateServersAsync(inventory =>
            {
                ServerProfileDto? persisted = inventory.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, serverDto.Id, StringComparison.Ordinal));
                if (persisted is null)
                {
                    return false;
                }

                persisted.DisplayName = updatedName;
                persisted.RemoteServer = updatedHost;
                return true;
            });

            if (!updated)
            {
                return;
            }

            server.DisplayName = updatedName;
            server.RemoteServer = updatedHost;
            ResortStableTreeProjection();
            ApplyFilter(server.Id);
            return;
        }

        var dialogVm = ServerDialogViewModel.FromDto(serverDto);
        dialogVm.DialogTitle = _localizer["DialogTitleEditServer"];

        var settings = await _configManager.LoadSettingsAsync();
        PopulateServerDialogOptions(dialogVm, settings);
        dialogVm.Settings = settings;

        var result = await _dialogService.ShowServerDialogAsync(dialogVm);

        if (result is not { Saved: true })
        {
            return;
        }

        result.Server.Id = serverDto.Id;

        settings = await RereadSettingsAfterDialogAsync();

        // Persist the selected gateway as last-used for future Add Server dialogs
        var savedGatewayId = result.Server.SshGatewayId;
        if (!string.Equals(settings.LastUsedGatewayId, savedGatewayId, StringComparison.Ordinal))
        {
            await _configManager.MergeSettingAsync(s => s.LastUsedGatewayId = savedGatewayId);
        }

        bool saved = await _configManager.MutateServersAsync(inventory =>
        {
            int index = inventory.FindIndex(
                candidate => string.Equals(candidate.Id, serverDto.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            inventory[index] = result.Server;
            return true;
        });

        if (!saved)
        {
            return;
        }

        server.UpdateFromDto(
            result.Server,
            ResolveProject(BuildProjectMap(settings), result.Server.ProjectId),
            BuildGatewayMap(settings),
            _localizer);

        RefreshLookupCollections(settings);
        RebuildStableTreeProjection();
        ApplyFilter(server.Id);
    }

    internal async Task<bool> EditServerByIdAsync(string serverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var server = _allServers.FirstOrDefault(
            candidate => string.Equals(candidate.Id, serverId, StringComparison.Ordinal));

        if (server is null)
        {
            return false;
        }

        await EditServerAsync(server, cancellationToken);
        return true;
    }

    [RelayCommand]
    private async Task DeleteServerAsync(ServerItemViewModel? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }

        // A tool entry is not a connection profile, and the menu item that gets here says
        // "Remove", not "Delete". Asking whether to delete a session names something the user
        // did not click and cannot see, which reads as having hit the wrong entry.
        bool isTool = ConnectionTypeCatalog.IsToolConnectionType(server.ConnectionType);
        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer[isTool ? "DialogTitleRemoveTool" : "DialogTitleDeleteServer"],
            _localizer.Format(
                isTool ? "ConfirmRemoveTool" : "ConfirmDeleteServer",
                server.DisplayName),
            "danger");

        if (!confirmed)
        {
            return;
        }

        await DeleteServersCoreAsync([server], cancellationToken);
    }

    [RelayCommand]
    private async Task DuplicateServerAsync(ServerItemViewModel? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }

        await DuplicateServersCoreAsync([server], cancellationToken);
    }

    [RelayCommand]
    private async Task MoveToProjectAsync(ServerMoveToProjectRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        await MoveServerToProjectCoreAsync(request.Server, request.ProjectId, cancellationToken);
    }

    [RelayCommand]
    private async Task MoveToGroupAsync(ServerMoveToGroupRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        await MoveServerToGroupCoreAsync(request.Server, request.GroupName, cancellationToken);
    }

    /// <summary>
    /// Moves a server to the specified project, persists the change, and refreshes
    /// the filtered tree in place without rebuilding the backing view-model
    /// instances. No-op when the server is already in the target project. The
    /// caller is responsible for any status text surfaced after a successful move.
    /// </summary>
    /// <param name="server">The server view model being moved.</param>
    /// <param name="targetProjectId">Destination project identifier (null or whitespace = no project).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns><c>true</c> if the server was moved and the model rebuilt; <c>false</c> otherwise.</returns>
    public async Task<bool> MoveServerToProjectAsync(
        ServerItemViewModel server,
        string? targetProjectId,
        CancellationToken cancellationToken = default)
    {
        return await MoveServerToProjectCoreAsync(server, targetProjectId, cancellationToken);
    }

    /// <summary>
    /// Moves a server to the specified group (folder path), persists the change,
    /// and refreshes the filtered tree in place without rebuilding the backing
    /// <see cref="ServerItemViewModel"/> instances. No-op when the server is
    /// already in the target group (case-insensitive). The caller is responsible
    /// for any status text surfaced after a successful move.
    /// </summary>
    /// <param name="server">The server view model being moved.</param>
    /// <param name="targetGroup">Destination folder path (null or whitespace = root).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns><c>true</c> if the server was moved and the model rebuilt; <c>false</c> otherwise.</returns>
    public async Task<bool> MoveServerToGroupAsync(
        ServerItemViewModel server,
        string? targetGroup,
        CancellationToken cancellationToken = default)
    {
        return await MoveServerToGroupCoreAsync(server, targetGroup, cancellationToken);
    }

    [RelayCommand]
    private void CopyHostname(ServerItemViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            return;
        }

        Clipboard.SetText(server.RemoteServer);
        StatusMessageRequested?.Invoke(
            _localizer.Format("StatusCopiedToClipboard", server.RemoteServer));
    }

    [RelayCommand]
    private void CopyUsername(ServerItemViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(server.Username))
        {
            return;
        }

        Clipboard.SetText(server.Username);
        StatusMessageRequested?.Invoke(
            _localizer.Format("StatusCopiedToClipboard", server.Username));
    }

    [RelayCommand]
    private void CopyAddress(ServerItemViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            return;
        }

        var address = BuildAddress(server);
        Clipboard.SetText(address);
        StatusMessageRequested?.Invoke(
            _localizer.Format("StatusCopiedToClipboard", address));
    }

    [RelayCommand]
    private void CopySshCommand(ServerItemViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            return;
        }

        var command = BuildSshCommand(server);
        Clipboard.SetText(command);
        StatusMessageRequested?.Invoke(
            _localizer.Format("StatusCopiedToClipboard", command));
    }

    [RelayCommand]
    private async Task TestReachabilityAsync(ServerItemViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            return;
        }

        var host = server.RemoteServer;
        var port = server.RemotePort;

        // Do not guess a port: a server without one cannot be probed deterministically.
        if (port <= 0)
        {
            StatusMessageRequested?.Invoke(
                _localizer.Format("StatusReachabilityNoPort", host));
            return;
        }

        StatusMessageRequested?.Invoke(
            _localizer.Format("StatusReachabilityTesting", host, port));

        var result = await TcpReachabilityProbe.ProbeAsync(
            host, port, TcpReachabilityProbe.DefaultTimeoutMs).ConfigureAwait(true);

        StatusMessageRequested?.Invoke(result.Reachable
            ? _localizer.Format("StatusReachabilitySuccess", host, port, result.LatencyMs.ToString("F0"))
            : _localizer.Format("StatusReachabilityFailed", host, port, result.Error ?? string.Empty));
    }

    /// <summary>
    /// Formats a server address as <c>host</c> when no port is configured, otherwise
    /// <c>host:port</c>. Pure helper (no clipboard) so it can be unit tested directly.
    /// </summary>
    internal static string BuildAddress(ServerItemViewModel server)
    {
        return server.RemotePort > 0
            ? $"{server.RemoteServer}:{server.RemotePort}"
            : server.RemoteServer;
    }

    /// <summary>
    /// Builds an <c>ssh</c> command line for a server: prefixes the username when present
    /// (<c>user@host</c>) and appends <c>-p port</c> only for a non-default port. Pure
    /// helper (no clipboard) so it can be unit tested directly.
    /// </summary>
    internal static string BuildSshCommand(ServerItemViewModel server)
    {
        var target = string.IsNullOrWhiteSpace(server.Username)
            ? server.RemoteServer
            : $"{server.Username}@{server.RemoteServer}";

        var portSuffix = server.RemotePort > 0 && server.RemotePort != DefaultSshPort
            ? $" -p {server.RemotePort}"
            : string.Empty;

        return $"ssh {target}{portSuffix}";
    }

    /// <summary>
    /// Single implementation for moving a server between groups from the tree UX.
    /// Persists the DTO update once, mutates the existing view-model instance in
    /// place, then rebuilds the filtered projections without calling <see cref="LoadServers"/>.
    /// </summary>
    private async Task<bool> MoveServerToGroupCoreAsync(
        ServerItemViewModel server,
        string? targetGroup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return await MoveServersToGroupCoreAsync([server], targetGroup, cancellationToken);
    }

    private async Task<bool> MoveServerToProjectCoreAsync(
        ServerItemViewModel server,
        string? targetProjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return await MoveServersToProjectCoreAsync([server], targetProjectId, cancellationToken) > 0;
    }

    private static string? NormalizeGroupForPersistence(string? groupName)
    {
        return string.IsNullOrWhiteSpace(groupName) ? null : groupName;
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
    }

    /// <summary>
    /// Tracks expand/collapse state changes on folder nodes and schedules
    /// a debounced save of TreeExpandedNodes to settings. Only a toggle the
    /// user made with no filter on screen reaches that save.
    /// </summary>
    private void OnFolderExpandedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FolderViewModel.IsExpanded) || sender is not FolderViewModel folder)
        {
            return;
        }

        if (_applyingFilterExpansion)
        {
            // The filter pass opened or restored this branch. It ends with its own selection
            // synchronisation, and none of what it did belongs in the saved state.
            return;
        }

        var key = folder.ExpansionKey;
        if (!folder.IsExpanded)
        {
            SynchronizeSelection(null);
        }

        if (AppliedFilterSpec.IsActive)
        {
            // Every branch on screen is open because the filter opened it, so closing one says
            // something about the filtered view, not about the tree the user returns to. Honour it
            // until the filter clears, then forget it.
            if (folder.IsExpanded)
            {
                _filterCollapsedFolders.Remove(key);
            }
            else
            {
                _filterCollapsedFolders.Add(key);
            }

            return;
        }

        if (folder.IsExpanded)
        {
            _expandedNodes.Add(key);
        }
        else
        {
            _expandedNodes.Remove(key);
        }

        ScheduleExpandStateSave();
    }

    /// <summary>
    /// Debounced save of TreeExpandedNodes - waits 500ms after last toggle
    /// before writing to disk, to avoid spamming settings.json on rapid clicks.
    /// </summary>
    private void ScheduleExpandStateSave()
    {
        ImmutableArray<string> expandedNodes = [.. _expandedNodes];
        lock (_expandSaveSync)
        {
            long version = ++_expandSaveVersion;
            _expandStateSavePending = true;
            _expandSaveTimer?.Dispose();
            _expandSaveTimer = null;

            if (_expandStateFlushInProgress)
            {
                return;
            }

            _expandSaveTimer = new System.Threading.Timer(
                _ => StartExpandStateSave(version, expandedNodes),
                null,
                ExpandStateSaveDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void StartExpandStateSave(long version, ImmutableArray<string> expandedNodes)
    {
        lock (_expandSaveSync)
        {
            if (version != _expandSaveVersion || _expandStateFlushInProgress)
            {
                return;
            }

            _expandSaveTimer?.Dispose();
            _expandSaveTimer = null;
            _expandStateSavePending = false;
            _expandSaveTask = SaveExpandStateAfterAsync(_expandSaveTask, expandedNodes);
        }
    }

    private async Task SaveExpandStateAfterAsync(
        Task precedingSave,
        ImmutableArray<string> expandedNodes)
    {
        try
        {
            await precedingSave.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error(
                $"Previous tree expand-state persistence failed: {ex.Message}");
        }

        try
        {
            await SaveExpandStateCoreAsync(expandedNodes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error(
                $"Queued tree expand-state persistence failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels the expand-state debounce, drains any active persistence callback,
    /// and best-effort persists the latest pending snapshot before interactive close.
    /// </summary>
    internal async Task FlushExpandStateForCloseAsync()
    {
        lock (_expandSaveSync)
        {
            _expandStateFlushInProgress = true;
            _expandSaveVersion++;
            _expandSaveTimer?.Dispose();
            _expandSaveTimer = null;
        }

        while (true)
        {
            Task activeSave;
            bool hasPendingSnapshot;
            ImmutableArray<string> expandedNodes = [];
            lock (_expandSaveSync)
            {
                activeSave = _expandSaveTask;
                hasPendingSnapshot = _expandStateSavePending;
                if (hasPendingSnapshot)
                {
                    expandedNodes = [.. _expandedNodes];
                    _expandStateSavePending = false;
                }
            }

            await activeSave.ConfigureAwait(false);
            if (hasPendingSnapshot)
            {
                await SaveExpandStateBestEffortAsync(expandedNodes).ConfigureAwait(false);
            }

            lock (_expandSaveSync)
            {
                if (!_expandStateSavePending && _expandSaveTask.IsCompleted)
                {
                    _expandStateFlushInProgress = false;
                    return;
                }
            }
        }
    }

    private async Task SaveExpandStateCoreAsync(ImmutableArray<string> expandedNodes)
    {
        await _configManager.MergeSettingAsync(
            settings => settings.TreeExpandedNodes = [.. expandedNodes]).ConfigureAwait(false);
    }

    private async Task SaveExpandStateBestEffortAsync(ImmutableArray<string> expandedNodes)
    {
        try
        {
            await SaveExpandStateCoreAsync(expandedNodes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Failed to save tree expand state: {ex.Message}");
        }
    }

    private void RefreshLookupCollections(AppSettings settings)
    {
        _projectTargets = settings.Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Name))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => new ProjectTarget(project.Id, project.Name, project.Color ?? string.Empty))
            .ToList();

        Projects = new ObservableCollection<string>(_projectTargets.Select(project => project.Name));

        Groups = new ObservableCollection<string>(
            _allServers
                .Where(server => !string.IsNullOrWhiteSpace(server.Group))
                .Select(server => server.Group)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Re-reads settings after a server dialog closes, because the dialog may have changed them.
    /// </summary>
    /// <remarks>
    /// The snapshot taken to POPULATE the dialog stops describing the configuration the moment
    /// the dialog can write to it, and its Network tab has been able to create a gateway since
    /// 2026-08-25. Everything rebuilt afterwards resolves ids against that snapshot, so a gateway
    /// chosen inside the dialog resolved to nothing and the row read "gateway missing (guid)"
    /// over data that was intact on disk. Found in a live session the same day.
    ///
    /// This guards DISPLAY only. The writes on these paths go through MergeSettingAsync and
    /// MutateServersAsync, which re-read under a lock and were never at risk - which is why the
    /// defect lost nothing and still looked like data loss.
    ///
    /// It exists as one named method rather than three inline reloads so the three dialog paths
    /// share the decision instead of three copies of it.
    ///
    /// It refreshes <see cref="_currentSettings"/> as well as returning the fresh instance, and
    /// that second half is not incidental. Reloading only into a local would fix the row the
    /// dialog just saved and leave the field a session-long copy of the pre-dialog state, because
    /// this view model does not subscribe to <c>SettingsChanged</c>. Renaming that same server
    /// inline reads the field (<c>ApplyInlineServerRename</c>), so the badge would come back on
    /// the next F2 - a fix that looks right on the path it was tested on and reappears elsewhere.
    /// The folder rename path and the bulk path already assign the field; only the server rename
    /// path read one nobody refreshed.
    /// </remarks>
    private async Task<AppSettings> RereadSettingsAfterDialogAsync()
    {
        AppSettings settings = await _configManager.LoadSettingsAsync();
        _currentSettings = settings;
        return settings;
    }

    private void PopulateServerDialogOptions(ServerDialogViewModel dialogVm, AppSettings settings)
    {
        dialogVm.AvailableGateways = new(BuildGatewayOptions(settings.SshGateways));

        // The tab that CHOOSES a gateway can now create one. The dialog owns no
        // configuration stack, so the shell hands it the same creation path the Add menu
        // and the tree context menu use - one owner for the sequence, not a third copy.
        dialogVm.CreateGatewayRequested = async () =>
        {
            _gatewayCreation ??= new GatewayCreationService(_configManager, _dialogService);
            SshGatewayDto? created = await _gatewayCreation.CreateAsync();
            if (created is null)
            {
                return null;
            }

            AppSettings refreshed = await _configManager.LoadSettingsAsync();
            return BuildGatewayOptions(refreshed.SshGateways)
                .FirstOrDefault(option =>
                    string.Equals(option.Id, created.Id, StringComparison.OrdinalIgnoreCase));
        };

        // Pre-select the last-used gateway for new servers (not edit mode)
        if (!dialogVm.IsEditMode
            && string.IsNullOrWhiteSpace(dialogVm.SelectedGatewayId)
            && !string.IsNullOrWhiteSpace(settings.LastUsedGatewayId)
            && dialogVm.AvailableGateways.Any(gw =>
                string.Equals(gw.Id, settings.LastUsedGatewayId, StringComparison.OrdinalIgnoreCase)))
        {
            dialogVm.SelectedGatewayId = settings.LastUsedGatewayId;
        }
    }

    private void SynchronizeSelection(string? preferredSelectedServerId)
    {
        List<ServerItemViewModel> visibleLeaves = SelectionHelpers
            .EnumerateVisibleLeaves(GroupedServers)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredSelectedServerId))
        {
            ServerItemViewModel? preferred = visibleLeaves.FirstOrDefault(
                server => string.Equals(server.Id, preferredSelectedServerId, StringComparison.Ordinal));

            if (preferred is not null)
            {
                SelectSingle(preferred);
                return;
            }
        }

        var visibleSelection = SelectedItems
            .Where(visibleLeaves.Contains)
            .ToList();

        if (visibleSelection.Count == 0)
        {
            ClearSelection();
            return;
        }

        var primary = SelectedServer is not null && visibleSelection.Contains(SelectedServer)
            ? SelectedServer
            : visibleSelection.LastOrDefault();
        var anchor = _selectionAnchor is not null && visibleSelection.Contains(_selectionAnchor)
            ? _selectionAnchor
            : primary;

        ApplySelection(visibleSelection, primary, anchor, updateSelectedServer: true);
    }

    private static Dictionary<string, ProjectDto> BuildProjectMap(AppSettings settings)
    {
        return settings.Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Id))
            .GroupBy(project => project.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    private static Dictionary<string, SshGatewayDto> BuildGatewayMap(AppSettings settings)
    {
        return settings.SshGateways
            .Where(gw => !string.IsNullOrWhiteSpace(gw.Id))
            .GroupBy(gw => gw.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<GatewayOption> BuildGatewayOptions(IEnumerable<SshGatewayDto> gateways)
    {
        var gatewayList = gateways.ToList();
        var gatewayMap = gatewayList
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id))
            .GroupBy(gateway => gateway.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return gatewayList.Select(gateway => new GatewayOption(
            gateway.Id,
            FormatGatewayDisplayText(gateway),
            gateway.Name,
            gateway.Host,
            gateway.Port,
            BuildGatewayRouteText(gateway, gatewayMap)));
    }

    private static string BuildGatewayRouteText(
        SshGatewayDto gateway,
        IReadOnlyDictionary<string, SshGatewayDto> gatewayMap)
    {
        var route = new List<string>();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        var current = gateway;

        while (current is not null && !string.IsNullOrWhiteSpace(current.Id) && visited.Add(current.Id))
        {
            route.Insert(0, FormatGatewayDisplayText(current));

            if (string.IsNullOrWhiteSpace(current.ParentGatewayId) ||
                !gatewayMap.TryGetValue(current.ParentGatewayId, out current))
            {
                break;
            }
        }

        return string.Join(" -> ", route);
    }

    private static string FormatGatewayDisplayText(SshGatewayDto gateway)
    {
        return $"{gateway.Name} ({gateway.Host}:{gateway.Port})";
    }

    private static ProjectDto? ResolveProject(IReadOnlyDictionary<string, ProjectDto> projectMap, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return projectMap.TryGetValue(projectId, out var project) ? project : null;
    }

    private static void ApplyProjectMetadata(ServerItemViewModel server, ProjectDto? project)
    {
        server.ProjectName = project?.Name ?? string.Empty;
        server.ProjectColor = project?.Color ?? string.Empty;
    }

    private static string ResolveProjectNodeName(
        IGrouping<string, ServerItemViewModel> projectGroup,
        string noProjectLabel)
    {
        var projectName = projectGroup.FirstOrDefault()?.ProjectName;
        return string.IsNullOrWhiteSpace(projectName) ? noProjectLabel : projectName;
    }

    private static string ResolveGroupNodeName(
        IGrouping<string, ServerItemViewModel> group,
        string noGroupLabel)
    {
        var groupName = group.FirstOrDefault()?.Group;
        return string.IsNullOrWhiteSpace(groupName) ? noGroupLabel : groupName;
    }

    private void OnConnectionStateChanged(ConnectionStateChange change)
    {
        // State machine events may fire from background threads; marshal to UI thread.
        // Always queued; direct fast-path would re-enter selection / binding updates.
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            string serverId = change.ServerId;
            var server = _allServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
            string inventoryId = serverId;

            // A raw inventory ID takes precedence because a valid profile ID may
            // itself end with an underscore and eight hexadecimal characters.
            if (server is null)
            {
                var trackedProfile = _sessionStatesByInventoryId.FirstOrDefault(entry =>
                    entry.Value.ContainsKey(serverId));
                if (trackedProfile.Value is not null)
                {
                    inventoryId = trackedProfile.Key;
                }
                else if (SessionIdCodec.TryGetInventoryId(
                             serverId,
                             out string decodedInventoryId))
                {
                    inventoryId = decodedInventoryId;
                }

                server = _allServers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, inventoryId, StringComparison.Ordinal));
            }

            StateAggregationResult aggregation = UpdateProfileSessionState(
                inventoryId,
                serverId,
                change.NewState,
                change.Revision);

            if (!aggregation.Applied)
            {
                return;
            }

            if (server is null)
            {
                return;
            }

            bool wasConnected = ConnectionStateSets.IsConnected(server.ConnectionState);
            server.ConnectionState = aggregation.AggregateState.ToString();
            bool isConnected = ConnectionStateSets.IsConnected(aggregation.AggregateState);

            // Refresh the stable projection exactly once when this server
            // crosses the connected-filter boundary. Other state changes only
            // update the row's visual state.
            if (ConnectedFilterEnabled && wasConnected != isConnected)
            {
                ConnectedMembershipRefreshCount++;
                ApplyFilter();
            }

            // Record successful reach: feeds RDP-DISC-04 (palette protocol bias)
            // and RDP-DISC-05 (Recents). External launch and remote handoff count
            // because the user-initiated session reached its successful boundary.
            if (ConnectionStateSets.IsConnected(change.NewState))
            {
                _recentConnections.Record(server.RemoteServer, server.ConnectionType);
            }
        });
    }

    private StateAggregationResult UpdateProfileSessionState(
        string inventoryId,
        string sessionId,
        ConnectionState newState,
        long revision)
    {
        bool isTeardown =
            newState is ConnectionState.Disconnected or ConnectionState.Error;

        _sessionStatesByInventoryId.TryGetValue(
            inventoryId,
            out Dictionary<string, SessionStateRevision>? sessionStates);

        if (sessionStates is not null
            && sessionStates.TryGetValue(sessionId, out SessionStateRevision current)
            && revision <= current.Revision)
        {
            return StateAggregationResult.Stale;
        }

        if (_lastTerminalSessionRevisionByInventoryId.TryGetValue(
                inventoryId,
                out TerminalSessionRevision lastTerminalRevision)
            && string.Equals(lastTerminalRevision.SessionId, sessionId, StringComparison.Ordinal)
            && revision <= lastTerminalRevision.Revision)
        {
            return StateAggregationResult.Stale;
        }

        if (isTeardown)
        {
            sessionStates?.Remove(sessionId);
            _lastTerminalSessionRevisionByInventoryId[inventoryId] =
                new TerminalSessionRevision(sessionId, revision);
        }
        else
        {
            if (sessionStates is null)
            {
                sessionStates = new Dictionary<string, SessionStateRevision>(StringComparer.Ordinal);
                _sessionStatesByInventoryId.Add(inventoryId, sessionStates);
            }

            sessionStates[sessionId] = new SessionStateRevision(newState, revision);
            if (_lastTerminalSessionRevisionByInventoryId.TryGetValue(
                    inventoryId,
                    out TerminalSessionRevision terminalRevision)
                && string.Equals(terminalRevision.SessionId, sessionId, StringComparison.Ordinal))
            {
                _lastTerminalSessionRevisionByInventoryId.Remove(inventoryId);
            }
        }

        if (sessionStates is null || sessionStates.Count == 0)
        {
            _sessionStatesByInventoryId.Remove(inventoryId);
            return new StateAggregationResult(true, newState);
        }

        foreach (SessionStateRevision sessionState in sessionStates.Values)
        {
            if (ConnectionStateSets.IsConnected(sessionState.State))
            {
                return new StateAggregationResult(true, sessionState.State);
            }
        }

        if (sessionStates.TryGetValue(sessionId, out SessionStateRevision currentSessionState))
        {
            return new StateAggregationResult(true, currentSessionState.State);
        }

        return new StateAggregationResult(true, sessionStates.Values.First().State);
    }

    private readonly record struct SessionStateRevision(ConnectionState State, long Revision);

    private readonly record struct TerminalSessionRevision(string SessionId, long Revision);

    private readonly record struct StateAggregationResult(bool Applied, ConnectionState AggregateState)
    {
        public static StateAggregationResult Stale { get; } =
            new(false, ConnectionState.Disconnected);
    }
}

public sealed record ServerDialogSeed(string? ProjectId, string? GroupName);

public sealed record ServerMoveToProjectRequest(ServerItemViewModel Server, string? ProjectId);

public sealed record ServerMoveToGroupRequest(ServerItemViewModel Server, string? GroupName);

public sealed record ProjectTarget(
    string Id,
    string Name,
    string Color,
    bool IsVirtualProject = false);

public sealed record GroupTarget(
    string GroupName,
    string DisplayName,
    bool IsVirtualGroup = false);

internal enum BulkConnectOutcomeStatus
{
    Success,
    PreflightFailed,
    ConnectionFailed,
    Cancelled,
    UnsupportedType
}

internal readonly record struct BulkConnectOutcome(
    BulkConnectOutcomeStatus Status,
    string? ErrorMessage,
    SessionDiagnostic? Failure = null);
