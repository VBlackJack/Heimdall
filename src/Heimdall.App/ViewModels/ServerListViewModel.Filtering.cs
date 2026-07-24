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

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel
{
    private const string NoGroupProjectionKey = "::projection:nogroup";

    private StableFolderNode _stableTreeRoot = StableFolderNode.CreateRoot();
    private readonly Dictionary<string, StableFolderNode> _stableFoldersByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private List<ServerItemViewModel> _stableServerOrder = [];

    public ObservableCollection<ProtocolFilterOptionViewModel> ProtocolFilters { get; } = [];

    [ObservableProperty]
    private bool _favoriteFilterEnabled;

    [ObservableProperty]
    private bool _connectedFilterEnabled;

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
        || ProtocolFilters.Any(option => option.IsSelected);

    public bool ShowNoGroupDropZone =>
        !AppliedFilterSpec.IsActive
        || _stableTreeRoot.Children.Any(node =>
            node.IsNoGroup && node.ViewModel!.Servers.Count > 0);

    internal int StableTreeBuildCount { get; private set; }

    internal int FilterPassApplicationCount { get; private set; }

    internal int ConnectedMembershipRefreshCount { get; private set; }

    internal TimeSpan LastFilterPassDuration { get; private set; }

    private void InitializeFilterOptions()
    {
        foreach (string protocol in _connectionService.RegisteredProtocols)
        {
            ProtocolFilters.Add(new ProtocolFilterOptionViewModel(
                protocol,
                _localizer.Format("FilterProtocolAutomationName", protocol),
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
            SelectedProject);
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
        var visibleRootFolders = new List<FolderViewModel>(_stableTreeRoot.Children.Count);
        foreach (StableFolderNode child in _stableTreeRoot.Children)
        {
            int descendantCount = ApplyFolderMembership(child, matches, spec.IsActive);
            if (!spec.IsActive || descendantCount > 0)
            {
                visibleRootFolders.Add(child.ViewModel!);
            }
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

    private static int ApplyFolderMembership(
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

        node.ViewModel!.SynchronizeVisibleChildren(visibleFolders, visibleServers);
        return descendantCount;
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
            .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
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
        var viewModel = new FolderViewModel
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

            var viewModel = new FolderViewModel
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
            .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
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

    private static void SortStableTree(StableFolderNode node)
    {
        node.Children.Sort((left, right) =>
        {
            if (left.IsNoGroup != right.IsNoGroup)
            {
                return left.IsNoGroup ? 1 : -1;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(
                left.ViewModel!.Name,
                right.ViewModel!.Name);
        });
        node.Servers.Sort((left, right) =>
        {
            int order = left.SortOrder.CompareTo(right.SortOrder);
            return order != 0
                ? order
                : StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        });

        foreach (StableFolderNode child in node.Children)
        {
            SortStableTree(child);
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
