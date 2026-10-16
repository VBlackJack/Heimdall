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
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel
{
    private const string NoGroupProjectionKey = "::projection:nogroup";

    private StableFolderNode _stableTreeRoot = StableFolderNode.CreateRoot();
    private readonly Dictionary<string, StableFolderNode> _stableFoldersByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private List<ServerItemViewModel> _stableServerOrder = [];

    /// <summary>
    /// Set while a filter pass drives <see cref="FolderViewModel.IsExpanded"/> itself, so the
    /// expand-state handler can tell a branch the filter opened from one the user toggled.
    /// </summary>
    private bool _applyingFilterExpansion;

    /// <summary>
    /// Branches the user closed by hand while a filter was on screen. Later passes of the same
    /// filter leave them closed instead of reopening them under the user's fingers; clearing the
    /// filter discards the set along with the rest of the filter-time expansion.
    /// </summary>
    private readonly HashSet<string> _filterCollapsedFolders =
        new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProtocolFilterOptionViewModel> ProtocolFilters { get; } = [];

    [ObservableProperty]
    private bool _favoriteFilterEnabled;

    [ObservableProperty]
    private bool _connectedFilterEnabled;

    /// <summary>Keeps only the sessions routed through an SSH gateway, present or missing.</summary>
    [ObservableProperty]
    private bool _gatewayFilterEnabled;

    /// <summary>
    /// Whether rows show the "via gateway" badge. Persisted, and pushed to every row on each
    /// filter pass so a row created by any path carries it.
    /// </summary>
    [ObservableProperty]
    private bool _showGatewayBadge = true;

    /// <summary>
    /// Whether the read-only properties of the selected session are docked under the tree.
    /// Persisted; off by default so the tree keeps its height for whoever never asked for it.
    /// </summary>
    [ObservableProperty]
    private bool _showPropertiesPanel;

    /// <summary>The height the properties panel was last dragged to, already clamped.</summary>
    /// <remarks>
    /// Raised, so a height arriving from the settings (an import, a reset) is laid out at once
    /// rather than on the next toggle of the panel.
    /// </remarks>
    public double PropertiesPanelHeight
    {
        get => _propertiesPanelHeight;
        private set => SetProperty(ref _propertiesPanelHeight, value);
    }

    private double _propertiesPanelHeight = Services.SessionPropertiesPanelLayout.DefaultHeight;

    /// <summary>Whether the external credential provider is on, pushed to every row.</summary>
    private bool _usesCredentialProvider;

    /// <summary>
    /// Set while a persisted value is being applied, so applying it does not write it back.
    /// </summary>
    private bool _applyingPersistedViewPreferences;

    /// <summary>The write of the last badge preference change; tests await it.</summary>
    internal Task ViewPreferencePersistence { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private bool _isFilterPending;

    public ServerFilterSpec AppliedFilterSpec { get; private set; } = ServerFilterSpec.Empty;

    public bool HasAppliedFilterResult =>
        AppliedFilterSpec.IsActive && !IsFilterPending;

    /// <summary>"3 / 12 sessions": the noun agrees with the total it follows.</summary>
    public string FilterResultCountText =>
        _localizer.FormatCount(
            _allServers.Count,
            "FilterResultCountOne",
            "FilterResultCount",
            FilteredCount,
            _allServers.Count);

    public bool HasActiveFacetFilter =>
        FavoriteFilterEnabled
        || ConnectedFilterEnabled
        || GatewayFilterEnabled
        || ProtocolFilters.Any(option => option.IsSelected);

    /// <summary>
    /// Whether a drag started in the tree is in progress; set by the view around its drag loop.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoGroupDropZone))]
    private bool _isTreeDragInProgress;

    /// <summary>Whether the "take it out of its folder" drop target is on screen.</summary>
    /// <remarks>
    /// It used to stand above the tree permanently, a second "(No Folder)" that read like a node
    /// of the tree and did nothing until something was dragged onto it. It now appears only
    /// while a drag is under way, when it is the thing the user is looking for.
    /// </remarks>
    public bool ShowNoGroupDropZone =>
        IsTreeDragInProgress
        && (!AppliedFilterSpec.IsActive
            || _stableTreeRoot.Children.Any(node =>
                node.IsNoGroup && node.ViewModel!.Servers.Count > 0));

    internal int StableTreeBuildCount { get; private set; }

    internal int FilterPassApplicationCount { get; private set; }

    internal int ConnectedMembershipRefreshCount { get; private set; }

    internal TimeSpan LastFilterPassDuration { get; private set; }

    /// <summary>
    /// Counts every inventory entry in a folder or one of its descendants,
    /// independently from the current visible filter projection.
    /// </summary>
    internal int GetCanonicalFolderEntryCount(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        return _allServers.Count(server =>
            !server.IsShared
            && server.Group is string group
            && FolderPath.IsSelfOrDescendant(group, folderPath));
    }

    private void InitializeFilterOptions()
    {
        foreach (string protocol in _connectionService.RegisteredProtocols)
        {
            // One name, two channels. The accessible name quotes what the row shows, so a
            // voice-control user speaking the visible label reaches the control they meant.
            string? displayKey = ConnectionTypeCatalog.GetDisplayNameKey(protocol);
            string displayName = displayKey is null ? protocol : _localizer[displayKey];

            ProtocolFilters.Add(new ProtocolFilterOptionViewModel(
                protocol,
                displayName,
                _localizer.Format("FilterProtocolAutomationName", displayName),
                ApplyDiscreteFilter));
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_resettingTreeFilters) return;
        RefreshTreeFilterChips();
        if (string.IsNullOrWhiteSpace(value))
        {
            CancelSearchFilterDebounce();
            ApplyFilter();
            return;
        }

        IsFilterPending = true;
        OnPropertyChanged(nameof(HasAppliedFilterResult));
        OnPropertyChanged(nameof(HasNoTreeResults));
        ScheduleSearchFilter();
    }

    partial void OnSelectedProjectChanged(string value) => ApplyDiscreteFilter();

    partial void OnFavoriteFilterEnabledChanged(bool value) => ApplyDiscreteFilter();

    partial void OnConnectedFilterEnabledChanged(bool value) => ApplyDiscreteFilter();

    partial void OnGatewayFilterEnabledChanged(bool value) => ApplyDiscreteFilter();

    partial void OnShowGatewayBadgeChanged(bool value)
    {
        ApplyGatewayBadgePreference();
        if (_applyingPersistedViewPreferences)
        {
            return;
        }

        ViewPreferencePersistence = PersistShowGatewayBadgeAsync(value);
    }

    partial void OnShowPropertiesPanelChanged(bool value)
    {
        if (_applyingPersistedViewPreferences)
        {
            return;
        }

        ViewPreferencePersistence = PersistViewPreferenceAsync(
            settings => settings.ShowSessionPropertiesPanel = value,
            "properties panel visibility");
    }

    /// <summary>
    /// Records the height the user dragged the properties panel to and saves it.
    /// </summary>
    public void CommitPropertiesPanelHeight(double height)
    {
        // Rounded here, not only on disk: the save raises a settings change that re-applies the
        // stored whole number, and the two must not disagree.
        int persisted = (int)Math.Round(
            Services.SessionPropertiesPanelLayout.ClampHeight(height),
            MidpointRounding.AwayFromZero);
        if (persisted == PropertiesPanelHeight)
        {
            return;
        }

        PropertiesPanelHeight = persisted;
        ViewPreferencePersistence = PersistViewPreferenceAsync(
            settings => settings.SessionPropertiesPanelHeight = persisted,
            "properties panel height");
    }

    /// <summary>Takes the view preferences a settings object carries without writing them back.</summary>
    private void ApplyPersistedViewPreferences(AppSettings settings)
    {
        _applyingPersistedViewPreferences = true;
        try
        {
            ShowGatewayBadge = settings.ShowGatewayBadge;
            ShowPropertiesPanel = settings.ShowSessionPropertiesPanel;
            PropertiesPanelHeight = Services.SessionPropertiesPanelLayout.ClampHeight(
                settings.SessionPropertiesPanelHeight);
            _usesCredentialProvider = settings.UseExternalCredentialProvider;
            ApplyGatewayBadgePreference();
        }
        finally
        {
            _applyingPersistedViewPreferences = false;
        }
    }

    /// <summary>Pushes the list-wide row preferences to every row.</summary>
    private void ApplyGatewayBadgePreference()
    {
        foreach (ServerItemViewModel server in _allServers)
        {
            server.ShowGatewayBadge = ShowGatewayBadge;
            server.UsesCredentialProvider = _usesCredentialProvider;
        }
    }

    private Task PersistShowGatewayBadgeAsync(bool value) =>
        PersistViewPreferenceAsync(settings => settings.ShowGatewayBadge = value, "gateway badge preference");

    private async Task PersistViewPreferenceAsync(Action<AppSettings> apply, string what)
    {
        try
        {
            await _configManager.MergeSettingAsync(apply).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Failed to save the {what}: {ex.Message}");
        }
    }

    private void ApplyDiscreteFilter()
    {
        if (_resettingTreeFilters) return;
        RefreshTreeFilterChips();
        OnPropertyChanged(nameof(HasActiveFacetFilter));
        CancelSearchFilterDebounce();
        ApplyFilter();
    }

    /// <summary>
    /// Applies a search the debounce is still holding back, so a key that acts on the results -
    /// Enter in the filter box - acts on the results of what has been typed.
    /// </summary>
    public void ApplyPendingSearchNow()
    {
        if (IsFilterPending)
        {
            CancelSearchFilterDebounce();
            ApplyFilter();
        }
    }

    private void ScheduleSearchFilter()
    {
        int version = System.Threading.Interlocked.Increment(ref _searchFilterVersion);
        _searchFilterTimer?.Dispose();
        _searchFilterTimer = _timeProvider.CreateTimer(
            _ => ApplySearchFilterFromTimer(version),
            null,
            SearchFilterDebounceDelay,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelSearchFilterDebounce()
    {
        System.Threading.Interlocked.Increment(ref _searchFilterVersion);
        _searchFilterTimer?.Dispose();
        _searchFilterTimer = null;
    }

    private void ApplySearchFilterFromTimer(int version)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (!IsCurrentFilterVersion(version))
            {
                return;
            }

            ApplyFilterPass(BuildFilterSpec(), version);
        });
    }

    private void ApplyFilter(string? preferredSelectedServerId = null)
    {
        int version = System.Threading.Interlocked.Increment(ref _searchFilterVersion);
        _searchFilterTimer?.Dispose();
        _searchFilterTimer = null;
        ApplyFilterPass(BuildFilterSpec(), version, preferredSelectedServerId);
    }

    private ServerFilterSpec BuildFilterSpec()
    {
        return ServerFilterSpec.Create(
            SearchText,
            ProtocolFilters
                .Where(option => option.IsSelected)
                .Select(option => option.Protocol),
            FavoriteFilterEnabled,
            ConnectedFilterEnabled,
            SelectedProject,
            GatewayFilterEnabled);
    }

    private bool ApplyFilterPass(
        ServerFilterSpec spec,
        int version,
        string? preferredSelectedServerId = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!IsCurrentFilterVersion(version))
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        ApplyGatewayBadgePreference();
        var filteredServers = new List<ServerItemViewModel>(_stableServerOrder.Count);
        foreach (ServerItemViewModel server in _stableServerOrder)
        {
            if (spec.Matches(server))
            {
                filteredServers.Add(server);
            }
        }

        if (!IsCurrentFilterVersion(version))
        {
            return false;
        }

        var matches = new HashSet<ServerItemViewModel>(
            filteredServers,
            ReferenceEqualityComparer.Instance);
        bool wasFilterActive = AppliedFilterSpec.IsActive;
        var visibleRootFolders = new List<FolderViewModel>(_stableTreeRoot.Children.Count);
        _applyingFilterExpansion = true;
        try
        {
            foreach (StableFolderNode child in _stableTreeRoot.Children)
            {
                int descendantCount = ApplyFolderMembership(child, matches, spec.IsActive, out _);
                if (!spec.IsActive || descendantCount > 0)
                {
                    visibleRootFolders.Add(child.ViewModel!);
                }
            }

            if (!spec.IsActive)
            {
                _filterCollapsedFolders.Clear();
                if (wasFilterActive)
                {
                    RestoreUserExpansionState();
                }
            }
        }
        finally
        {
            _applyingFilterExpansion = false;
        }

        SynchronizeCollection(GroupedServers, visibleRootFolders);
        SynchronizeCollection(Servers, filteredServers);

        AppliedFilterSpec = spec;
        FilteredCount = filteredServers.Count;
        IsFilterPending = false;
        FilterPassApplicationCount++;
        SynchronizeSelection(preferredSelectedServerId);

        stopwatch.Stop();
        LastFilterPassDuration = stopwatch.Elapsed;
        OnPropertyChanged(nameof(FilterResultCountText));
        OnPropertyChanged(nameof(HasAppliedFilterResult));
        OnPropertyChanged(nameof(ShowNoGroupDropZone));
        OnPropertyChanged(nameof(HasNoTreeResults));
        OnPropertyChanged(nameof(HasNoInventory));
        RefreshPropertiesSummary();
        return true;
    }

    private bool IsCurrentFilterVersion(int version) =>
        !_disposed
        && version == System.Threading.Volatile.Read(ref _searchFilterVersion);

    private int ApplyFolderMembership(
        StableFolderNode node,
        HashSet<ServerItemViewModel> matches,
        bool filterActive,
        out int totalCount)
    {
        var visibleServers = node.Servers
            .Where(matches.Contains)
            .ToList();
        var visibleFolders = new List<FolderViewModel>(node.Children.Count);
        int descendantCount = visibleServers.Count;
        totalCount = node.Servers.Count;

        foreach (StableFolderNode child in node.Children)
        {
            int childCount = ApplyFolderMembership(child, matches, filterActive, out int childTotal);
            descendantCount += childCount;
            totalCount += childTotal;
            if (!filterActive || childCount > 0)
            {
                visibleFolders.Add(child.ViewModel!);
            }
        }

        FolderViewModel viewModel = node.ViewModel!;
        viewModel.SynchronizeVisibleChildren(visibleFolders, visibleServers);
        viewModel.SetFilteredTotal(filterActive ? totalCount : null);

        // A branch that survives the filter holds a match, and a match inside a closed branch is
        // a result the user is told about but cannot see. Opening every surviving branch needs no
        // ceiling: whatever the inventory size, a filter only ever leaves its own results standing.
        if (filterActive
            && descendantCount > 0
            && !_filterCollapsedFolders.Contains(viewModel.ExpansionKey))
        {
            viewModel.IsExpanded = true;
        }

        return descendantCount;
    }

    /// <summary>
    /// Puts every branch back to the arrangement the user last chose with no filter on screen.
    /// </summary>
    /// <remarks>
    /// Filter-time expansion never reaches the persisted expand-state set, so that set still holds
    /// the arrangement the filter interrupted and is the only thing worth restoring from.
    /// </remarks>
    private void RestoreUserExpansionState()
    {
        foreach (StableFolderNode node in EnumerateStableFolders(_stableTreeRoot))
        {
            FolderViewModel folder = node.ViewModel!;
            folder.IsExpanded = _expandedNodes.Contains(folder.ExpansionKey);
        }
    }

    /// <summary>
    /// The colour a folder icon takes: its own, else the nearest ancestor's, else none.
    /// </summary>
    private string ResolveFolderColor(string folderPath)
    {
        Dictionary<string, GroupDefaultsDto>? defaults = _currentSettings?.GroupDefaults;
        if (defaults is null || defaults.Count == 0)
        {
            return "";
        }

        string? path = folderPath;
        while (!string.IsNullOrEmpty(path))
        {
            if (defaults.TryGetValue(path, out GroupDefaultsDto? entry)
                && !string.IsNullOrWhiteSpace(entry.Color))
            {
                return entry.Color;
            }

            int separator = path.LastIndexOf('/');
            path = separator > 0 ? path[..separator] : null;
        }

        return "";
    }

    /// <summary>Re-reads every folder's colour from the current settings.</summary>
    private void RefreshFolderColors()
    {
        foreach (StableFolderNode node in EnumerateStableFolders(_stableTreeRoot))
        {
            // A shared folder takes no colour: the colours are filed by local path.
            if (!node.IsNoGroup && !node.IsShared)
            {
                node.ViewModel!.Color = ResolveFolderColor(node.ViewModel.FullPath);
            }
        }

        FolderLayoutChanged?.Invoke();
    }

    /// <summary>
    /// Raised after the folders, or what they look like, changed: a profile moved, a folder
    /// renamed, recoloured, created or removed. The session tab groups follow the tree on it.
    /// </summary>
    public event Action? FolderLayoutChanged;

    /// <summary>
    /// The folder a profile is filed under, as the tree shows it: the folder's own spelling,
    /// name and resolved colour. Null when the profile is in no folder or not in the inventory.
    /// </summary>
    /// <remarks>
    /// Read from the tree's folder nodes rather than recomputed, so a tab group can never show a
    /// name or a colour the tree does not.
    /// </remarks>
    internal SessionTabGroupInfo? ResolveTabGroup(string profileId)
    {
        ServerItemViewModel? server = _allServers.FirstOrDefault(
            candidate => string.Equals(candidate.Id, profileId, StringComparison.Ordinal));
        if (server is null)
        {
            return null;
        }

        // A shared session groups under its own source's folder, never under a local folder that
        // happens to be spelled the same.
        if (server.IsShared)
        {
            return ResolveSharedFolderOf(server) is { } shared
                ? new SessionTabGroupInfo(shared.FullPath, shared.Name, shared.Color)
                : null;
        }

        string path = string.Join('/', NormalizeFolderPath(server.Group)
            .Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (path.Length == 0
            || !_stableFoldersByPath.TryGetValue(path, out StableFolderNode? node)
            || node.IsNoGroup)
        {
            return null;
        }

        FolderViewModel folder = node.ViewModel!;
        return new SessionTabGroupInfo(folder.FullPath, folder.Name, folder.Color);
    }

    /// <summary>
    /// Persists a folder's icon colour and shows it at once.
    /// </summary>
    /// <param name="folderPath">The folder's full path.</param>
    /// <param name="color">A palette hex string, or null to return to the themed default.</param>
    /// <remarks>
    /// The settings change the write raises re-reads every folder, so a sub-folder that inherited
    /// the old colour follows without a rebuild; the direct assignment only spares the user the
    /// dispatcher hop between the click and the icon.
    /// </remarks>
    public async Task SetFolderColorAsync(string folderPath, string? color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (IsSharedFolderKey(folderPath))
        {
            ReportSharedFolderReadOnly();
            return;
        }

        string? normalized = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        if (_stableFoldersByPath.TryGetValue(folderPath, out StableFolderNode? node))
        {
            node.ViewModel!.Color = normalized ?? ResolveFolderColor(ParentPath(folderPath));
        }

        try
        {
            await _configManager.MergeSettingAsync(settings =>
            {
                if (!settings.GroupDefaults.TryGetValue(folderPath, out GroupDefaultsDto? entry))
                {
                    entry = new GroupDefaultsDto();
                    settings.GroupDefaults[folderPath] = entry;
                }

                entry.Color = normalized;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Failed to save the colour of folder '{folderPath}': {ex.Message}");
        }
    }

    private static string ParentPath(string folderPath)
    {
        int separator = folderPath.LastIndexOf('/');
        return separator > 0 ? folderPath[..separator] : "";
    }

    /// <summary>
    /// Rebuilds canonical folder metadata only after inventory structure
    /// changes. Filter passes never call this method.
    /// </summary>
    private void RebuildStableTreeProjection()
    {
        DetachStableTreeFolderEvents();

        _stableTreeRoot = StableFolderNode.CreateRoot();
        _stableFoldersByPath.Clear();

        StableFolderNode? noGroupNode = null;
        foreach (ServerItemViewModel server in _allServers)
        {
            // Filed by ProjectSharedSources, under the source's own root.
            if (server.IsShared)
            {
                continue;
            }

            string folderPath = NormalizeFolderPath(server.Group);
            StableFolderNode target;
            if (folderPath.Length == 0)
            {
                noGroupNode ??= CreateNoGroupNode();
                target = noGroupNode;
            }
            else
            {
                target = EnsureStableFolderPath(folderPath);
            }

            target.Servers.Add(server);
        }

        if (_currentSettings?.EmptyGroups is not null)
        {
            foreach (string path in _currentSettings.EmptyGroups)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    EnsureStableFolderPath(NormalizeFolderPath(path));
                }
            }
        }

        ProjectSharedSources();
        SortStableTree(_stableTreeRoot);
        _stableServerOrder = _allServers
            .OrderBy(server => SessionOrdering.RankOf(server.SortOrder))
            .ThenBy(server => server.DisplayName, DisplayNameOrdering.Comparer)
            .ToList();
        StableTreeBuildCount++;
        FolderLayoutChanged?.Invoke();
    }

    private void DetachStableTreeFolderEvents()
    {
        foreach (StableFolderNode existing in EnumerateStableFolders(_stableTreeRoot))
        {
            existing.ViewModel!.PropertyChanged -= OnFolderExpandedChanged;
        }
    }

    private StableFolderNode CreateNoGroupNode()
    {
        var viewModel = new FolderViewModel(_localizer)
        {
            Name = _localizer["TreeNodeNoGroup"],
            FullPath = "",
            IsExpanded = _expandedNodes.Contains("::nogroup")
        };
        viewModel.PropertyChanged += OnFolderExpandedChanged;

        var node = new StableFolderNode(viewModel, isNoGroup: true);
        _stableTreeRoot.Children.Add(node);
        _stableFoldersByPath.Add(NoGroupProjectionKey, node);
        return node;
    }

    private StableFolderNode EnsureStableFolderPath(string path)
    {
        string currentPath = "";
        StableFolderNode parent = _stableTreeRoot;
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = currentPath.Length == 0
                ? segment
                : $"{currentPath}/{segment}";
            if (_stableFoldersByPath.TryGetValue(currentPath, out StableFolderNode? existing))
            {
                parent = existing;
                continue;
            }

            var viewModel = new FolderViewModel(_localizer)
            {
                Name = segment,
                FullPath = currentPath,
                IsExpanded = _expandedNodes.Contains(currentPath),
                Color = ResolveFolderColor(currentPath)
            };
            viewModel.PropertyChanged += OnFolderExpandedChanged;

            var created = new StableFolderNode(viewModel, isNoGroup: false);
            parent.Children.Add(created);
            _stableFoldersByPath.Add(currentPath, created);
            parent = created;
        }

        return parent;
    }

    private void ResortStableTreeProjection()
    {
        SortStableTree(_stableTreeRoot);
        _stableServerOrder = _allServers
            .OrderBy(server => SessionOrdering.RankOf(server.SortOrder))
            .ThenBy(server => server.DisplayName, DisplayNameOrdering.Comparer)
            .ToList();

        _stableFoldersByPath.Clear();
        foreach (StableFolderNode node in EnumerateStableFolders(_stableTreeRoot))
        {
            // Shared folders keep their own index; a local path must never resolve to one.
            if (node.IsShared)
            {
                continue;
            }

            string key = node.IsNoGroup
                ? NoGroupProjectionKey
                : node.ViewModel!.FullPath;
            _stableFoldersByPath[key] = node;
        }

        // A rename rekeys the folders only here, after the colour pass already told the tabs.
        FolderLayoutChanged?.Invoke();
    }

    private static void SortStableTree(StableFolderNode node) =>
        SortStableTree(node, DisplayNameOrdering.Comparer);

    private static void SortStableTree(StableFolderNode node, StringComparer names)
    {
        node.Children.Sort((left, right) =>
        {
            // Local folders first, then "no folder", then the shared sources in the order the
            // settings list them.
            int rank = left.SortRank.CompareTo(right.SortRank);
            if (rank != 0)
            {
                return rank;
            }

            if (left.ViewModel!.IsSharedRoot)
            {
                return left.SharedOrder.CompareTo(right.SharedOrder);
            }

            return names.Compare(
                left.ViewModel!.Name,
                right.ViewModel!.Name);
        });
        node.Servers.Sort((left, right) =>
        {
            int order = SessionOrdering.RankOf(left.SortOrder).CompareTo(SessionOrdering.RankOf(right.SortOrder));
            return order != 0
                ? order
                : names.Compare(left.DisplayName, right.DisplayName);
        });

        foreach (StableFolderNode child in node.Children)
        {
            SortStableTree(child, names);
        }
    }

    private static IEnumerable<StableFolderNode> EnumerateStableFolders(
        StableFolderNode root)
    {
        foreach (StableFolderNode child in root.Children)
        {
            yield return child;
            foreach (StableFolderNode descendant in EnumerateStableFolders(child))
            {
                yield return descendant;
            }
        }
    }

    private static string NormalizeFolderPath(string? path) =>
        (path?.Trim() ?? "").Replace('\\', '/');

    private static void SynchronizeCollection<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> target)
        where T : class
    {
        var targetSet = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        for (int index = collection.Count - 1; index >= 0; index--)
        {
            if (!targetSet.Contains(collection[index]))
            {
                collection.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < target.Count; targetIndex++)
        {
            T targetItem = target[targetIndex];
            if (targetIndex < collection.Count
                && ReferenceEquals(collection[targetIndex], targetItem))
            {
                continue;
            }

            int existingIndex = collection.IndexOf(targetItem);
            if (existingIndex >= 0)
            {
                collection.Move(existingIndex, targetIndex);
            }
            else
            {
                collection.Insert(targetIndex, targetItem);
            }
        }
    }

    private sealed class StableFolderNode
    {
        public StableFolderNode(FolderViewModel? viewModel, bool isNoGroup)
        {
            ViewModel = viewModel;
            IsNoGroup = isNoGroup;
        }

        public FolderViewModel? ViewModel { get; }

        public bool IsNoGroup { get; }

        /// <summary>Whether the node is a shared source root or one of its folders.</summary>
        public bool IsShared => ViewModel?.IsShared == true;

        /// <summary>Where a shared source root stands among the others, in settings order.</summary>
        public int SharedOrder { get; set; }

        /// <summary>The band a node sorts in among its siblings: local, "no folder", shared root.</summary>
        public int SortRank => ViewModel?.IsSharedRoot == true ? 2 : IsNoGroup ? 1 : 0;

        public List<StableFolderNode> Children { get; } = [];

        public List<ServerItemViewModel> Servers { get; } = [];

        public static StableFolderNode CreateRoot() => new(null, isNoGroup: false);
    }
}
