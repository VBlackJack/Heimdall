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

    public string FilterResultCountText =>
        _localizer.Format("FilterResultCount", FilteredCount, _allServers.Count);

    public bool HasActiveFacetFilter =>
        FavoriteFilterEnabled
        || ConnectedFilterEnabled
        || GatewayFilterEnabled
        || ProtocolFilters.Any(option => option.IsSelected);

    public bool ShowNoGroupDropZone =>
        !AppliedFilterSpec.IsActive
        || _stableTreeRoot.Children.Any(node =>
            node.IsNoGroup && node.ViewModel!.Servers.Count > 0);

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
            server.Group is string group
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
        if (string.IsNullOrWhiteSpace(value))
        {
            CancelSearchFilterDebounce();
            ApplyFilter();
            return;
        }

        IsFilterPending = true;
        OnPropertyChanged(nameof(HasAppliedFilterResult));
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

    /// <summary>Takes the view preferences a settings object carries without writing them back.</summary>
    private void ApplyPersistedViewPreferences(AppSettings settings)
    {
        _applyingPersistedViewPreferences = true;
        try
        {
            ShowGatewayBadge = settings.ShowGatewayBadge;
        }
        finally
        {
            _applyingPersistedViewPreferences = false;
        }
    }

    private void ApplyGatewayBadgePreference()
    {
        foreach (ServerItemViewModel server in _allServers)
        {
            server.ShowGatewayBadge = ShowGatewayBadge;
        }
    }

    private async Task PersistShowGatewayBadgeAsync(bool value)
    {
        try
        {
            await _configManager.MergeSettingAsync(
                settings => settings.ShowGatewayBadge = value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Failed to save the gateway badge preference: {ex.Message}");
        }
    }

    private void ApplyDiscreteFilter()
    {
        OnPropertyChanged(nameof(HasActiveFacetFilter));
        CancelSearchFilterDebounce();
        ApplyFilter();
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
                int descendantCount = ApplyFolderMembership(child, matches, spec.IsActive);
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
        return true;
    }

    private bool IsCurrentFilterVersion(int version) =>
        !_disposed
        && version == System.Threading.Volatile.Read(ref _searchFilterVersion);

    private int ApplyFolderMembership(
        StableFolderNode node,
        HashSet<ServerItemViewModel> matches,
        bool filterActive)
    {
        var visibleServers = node.Servers
            .Where(matches.Contains)
            .ToList();
        var visibleFolders = new List<FolderViewModel>(node.Children.Count);
        int descendantCount = visibleServers.Count;

        foreach (StableFolderNode child in node.Children)
        {
            int childCount = ApplyFolderMembership(child, matches, filterActive);
            descendantCount += childCount;
            if (!filterActive || childCount > 0)
            {
                visibleFolders.Add(child.ViewModel!);
            }
        }

        FolderViewModel viewModel = node.ViewModel!;
        viewModel.SynchronizeVisibleChildren(visibleFolders, visibleServers);

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

        SortStableTree(_stableTreeRoot);
        _stableServerOrder = _allServers
            .OrderBy(server => server.SortOrder)
            .ThenBy(server => server.DisplayName, DisplayNameOrdering.Comparer)
            .ToList();
        StableTreeBuildCount++;
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
                IsExpanded = _expandedNodes.Contains(currentPath)
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
            .OrderBy(server => server.SortOrder)
            .ThenBy(server => server.DisplayName, DisplayNameOrdering.Comparer)
            .ToList();

        _stableFoldersByPath.Clear();
        foreach (StableFolderNode node in EnumerateStableFolders(_stableTreeRoot))
        {
            string key = node.IsNoGroup
                ? NoGroupProjectionKey
                : node.ViewModel!.FullPath;
            _stableFoldersByPath[key] = node;
        }
    }

    private static void SortStableTree(StableFolderNode node) =>
        SortStableTree(node, DisplayNameOrdering.Comparer);

    private static void SortStableTree(StableFolderNode node, StringComparer names)
    {
        node.Children.Sort((left, right) =>
        {
            if (left.IsNoGroup != right.IsNoGroup)
            {
                return left.IsNoGroup ? 1 : -1;
            }

            return names.Compare(
                left.ViewModel!.Name,
                right.ViewModel!.Name);
        });
        node.Servers.Sort((left, right) =>
        {
            int order = left.SortOrder.CompareTo(right.SortOrder);
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

        public List<StableFolderNode> Children { get; } = [];

        public List<ServerItemViewModel> Servers { get; } = [];

        public static StableFolderNode CreateRoot() => new(null, isNoGroup: false);
    }
}
