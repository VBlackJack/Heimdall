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

using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The sessions of the shared sources in the tree: one root per enabled source, below the local
/// folders, and the read-only rule every mutation applies to them.
/// </summary>
/// <remarks>
/// <para>A shared row joins <see cref="_allServers"/> like any other, so connecting, the health
/// dot, the filter and the search treat it as they treat a local one. Its folders do not join the
/// local folders: they are keyed apart, under a reserved key that names the source, so a local
/// folder spelled like a shared one never merges with it, and no folder operation addressed by
/// path can reach a shared folder.</para>
/// <para>Every write path refuses a shared row before it reads the inventory, with a status message
/// rather than silently: the configuration manager's doors would refuse the identifier anyway, but
/// a refusal found there reads as a failure, not as a rule.</para>
/// </remarks>
public partial class ServerListViewModel
{
    /// <summary>The key prefix of shared folders; no local folder path is written with it.</summary>
    internal const string SharedFolderKeyPrefix = "::shared:";

    private const char SharedFolderSeparator = '/';

    private readonly Dictionary<string, StableFolderNode> _sharedFoldersByKey =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _sharedSessionsSubscribed;

    /// <summary>
    /// The shared session the previous run left selected, when the tree was loaded before its
    /// source was read; selected by the first refresh of the shared rows, then dropped.
    /// </summary>
    private string? _pendingSharedSelectionId;

    /// <summary>
    /// What the shared sources were last read with: the sources and the MobaXterm variables. Null
    /// until the inventory is first loaded.
    /// </summary>
    private string? _sharedSourcesSignature;

    /// <summary>The last reload a settings change started; tests await it.</summary>
    internal Task SharedSessionReload { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The part of <paramref name="settings"/> the shared sources are read from, as one comparable
    /// string: the sources, and the variables their user fields resolve against.
    /// </summary>
    internal static string SharedSourcesSignature(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Sources = (settings.SharedSessionSources ?? [])
                .Where(source => source is not null)
                .Select(source => new { source.Id, source.Name, source.Path, source.Enabled }),
            Variables = (settings.MobaXtermUserVariables ?? [])
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, pair.Value }),
        });
    }

    /// <summary>
    /// Reads the shared sources again when a settings change touched what they are read from: a
    /// save of the settings card, an import, a reset. Anything else leaves them alone, so an
    /// unrelated save does not reread files on a network share.
    /// </summary>
    private void ReloadSharedSessionsIfSourcesChanged(AppSettings settings)
    {
        string signature = SharedSourcesSignature(settings);
        if (string.Equals(signature, _sharedSourcesSignature, StringComparison.Ordinal))
        {
            return;
        }

        _sharedSourcesSignature = signature;
        SharedSessionReload = ReloadSharedSessionsAsync(settings);
    }

    private async Task ReloadSharedSessionsAsync(AppSettings settings)
    {
        try
        {
            // The provider raises Changed when it is done; the tree follows from there.
            await _profileLookup.SharedSessions.LoadAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Reloading the shared session sources failed: {ex.Message}");
        }
    }

    /// <summary>The key a shared folder is filed under, and its <see cref="FolderViewModel.FullPath"/>.</summary>
    /// <param name="sourceId">The source the folder belongs to.</param>
    /// <param name="sharedPath">The folder path inside the source; empty for the source root.</param>
    internal static string SharedFolderKey(string sourceId, string sharedPath) =>
        string.IsNullOrEmpty(sharedPath)
            ? SharedFolderKeyPrefix + sourceId
            : SharedFolderKeyPrefix + sourceId + SharedFolderSeparator + sharedPath;

    /// <summary>Whether <paramref name="folderPath"/> is the key of a shared folder.</summary>
    internal static bool IsSharedFolderKey(string? folderPath) =>
        folderPath is not null && folderPath.StartsWith(SharedFolderKeyPrefix, StringComparison.Ordinal);

    /// <summary>The shared folder nodes of the tree, roots included.</summary>
    internal IReadOnlyList<FolderViewModel> SharedFolders =>
        [.. _sharedFoldersByKey.Values.Select(node => node.ViewModel!)];

    /// <summary>Whether the current selection holds at least one shared row.</summary>
    public bool SelectionHasShared => SelectedItems.Any(item => item.IsShared);

    /// <summary>
    /// Listens to the shared provider, so a reload from the settings card refreshes the tree
    /// without a restart. Idempotent.
    /// </summary>
    private void SubscribeToSharedSessions()
    {
        if (_sharedSessionsSubscribed)
        {
            return;
        }

        _profileLookup.SharedSessions.Changed += OnSharedSessionsChanged;
        _profileLookup.SharedSessions.SourceReloaded += OnSharedSourceReloaded;
        _sharedSessionsSubscribed = true;
    }

    private void UnsubscribeFromSharedSessions()
    {
        if (!_sharedSessionsSubscribed)
        {
            return;
        }

        _profileLookup.SharedSessions.Changed -= OnSharedSessionsChanged;
        _profileLookup.SharedSessions.SourceReloaded -= OnSharedSourceReloaded;
        _sharedSessionsSubscribed = false;
    }

    private void OnSharedSessionsChanged()
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || _currentSettings is null)
            {
                return;
            }

            RefreshSharedSessions();
        });
    }

    /// <summary>
    /// Says on the status line what a reload of one source after its file changed came to: the
    /// sessions it holds now, or why the previous ones were kept.
    /// </summary>
    private void OnSharedSourceReloaded(SharedSessionSourceReload reload)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                StatusMessageRequested?.Invoke(DescribeSharedSourceReload(reload));
            }
        });
    }

    /// <summary>
    /// The status line of a reload of one shared source: the sessions it holds now or why they
    /// were kept, then the issue count of its import report when the reload changed it.
    /// </summary>
    internal string DescribeSharedSourceReload(SharedSessionSourceReload reload)
    {
        ArgumentNullException.ThrowIfNull(reload);

        SharedSessionSourceState state = reload.State;
        string name = string.IsNullOrWhiteSpace(state.Name) ? state.SourceId : state.Name;
        string? problem = state.Status == SharedSessionSourceStatus.Error ? state.ErrorMessage : state.Notice;
        string message = problem is null
            ? _localizer.FormatCount(
                state.ProfileCount,
                "StatusSharedSourceUpdatedOne",
                "StatusSharedSourceUpdated",
                name,
                state.ProfileCount)
            : _localizer.Format("StatusSharedSourceNotUpdated", name, problem);
        return reload.IssueCountChanged
            ? _localizer.Format(
                "StatusSharedSourceWithIssues",
                message,
                _localizer.FormatCount(
                    state.IssueCount,
                    "StatusSharedSourceIssuesChangedOne",
                    "StatusSharedSourceIssuesChanged",
                    state.IssueCount,
                    reload.PreviousIssueCount))
            : message;
    }

    /// <summary>Puts the plain text of an import report on the clipboard; replaced by tests.</summary>
    internal Services.IClipboardService ReportClipboard { get; set; } = new Services.WpfClipboardService();

    /// <summary>Asks where to export an import report as CSV; replaced by tests.</summary>
    internal Func<string, string?>? ReportCsvPathPicker { get; set; }

    /// <summary>
    /// Shows the import report of <paramref name="sourceId"/> as of its last successful read. A
    /// source that was never read shows an empty report.
    /// </summary>
    internal Task ShowSharedImportReportAsync(string sourceId)
    {
        SharedSessionSourceState? state = _profileLookup.SharedSessions.Sources
            .FirstOrDefault(source => string.Equals(source.SourceId, sourceId, StringComparison.Ordinal));
        string name = string.IsNullOrWhiteSpace(state?.Name) ? sourceId : state!.Name;
        var viewModel = new ViewModels.Dialogs.SharedSessionImportReportDialogViewModel(
            state?.Report ?? SharedSessionImportReport.Empty(sourceId, name),
            _localizer,
            ReportClipboard,
            ReportCsvPathPicker
                ?? (proposed => ViewModels.Dialogs.SharedSessionImportReportDialogViewModel.PickCsvPathWithDialog(_localizer, proposed)));
        return _dialogService.ShowSharedSessionImportReportAsync(viewModel);
    }

    /// <summary>How many shared sessions the tree lists.</summary>
    public int SharedServerCount => _allServers.Count(server => server.IsShared);

    /// <summary>
    /// Brings the shared rows in line with what the provider holds now and rebuilds the tree; the
    /// local rows are left as they are.
    /// </summary>
    /// <remarks>
    /// A session still held keeps its row: the instance is updated in place, so it stays selected,
    /// keeps its health dot and its session state, and the folders keep their expansion, which is
    /// filed under keys made of the source and the path. Only a session gone from its source loses
    /// its row; a session open on it is not touched.
    /// </remarks>
    internal void RefreshSharedSessions()
    {
        AppSettings settings = _currentSettings ?? new AppSettings();
        Dictionary<string, ServerItemViewModel> previous = _allServers
            .Where(server => server.IsShared)
            .GroupBy(server => server.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        _allServers.RemoveAll(server => server.IsShared);
        HashSet<string> localIds = _allServers
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        _allServers.AddRange(BuildSharedRows(localIds, settings, previous));

        RebuildHealthServerIndex();
        RebuildStableTreeProjection();
        string? selection = EffectiveSelectedServerId ?? _pendingSharedSelectionId;
        _pendingSharedSelectionId = null;
        ApplyFilter(selection);
        OnPropertyChanged(nameof(HasNoInventory));
        OnPropertyChanged(nameof(SharedServerCount));
    }

    /// <summary>
    /// The rows of every shared profile the provider holds, except one whose identifier a local
    /// profile already uses (the door refuses that, so it can only be an older inventory).
    /// </summary>
    private IEnumerable<ServerItemViewModel> BuildSharedRows(
        IReadOnlySet<string> localIds,
        AppSettings settings,
        IReadOnlyDictionary<string, ServerItemViewModel>? reuse = null)
    {
        ISharedSessionProvider provider = _profileLookup.SharedSessions;

        // Only the sources the tree shows a root for: a row with no root to sit under would be
        // listed by the search and reachable by the keyboard while no branch of the tree held it.
        Dictionary<string, string> sourceNames = provider.Sources
            .Where(source => source.Status == SharedSessionSourceStatus.Loaded)
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        SessionGatewayContext gateways = SharedGatewayContext(settings);

        foreach (ServerProfileDto profile in provider.Profiles)
        {
            if (localIds.Contains(profile.Id)
                || SharedSessionIds.SourceIdOf(profile.Id) is not { } sourceId
                || !sourceNames.TryGetValue(sourceId, out string? sourceName))
            {
                continue;
            }

            ServerItemViewModel row;
            if (reuse is not null && reuse.TryGetValue(profile.Id, out ServerItemViewModel? existing))
            {
                existing.UpdateFromDto(profile, project: null, gateways, _localizer);
                row = existing;
            }
            else
            {
                row = ServerItemViewModel.FromDto(
                    profile,
                    project: null,
                    _connectionSm.GetState(profile.Id).ToString(),
                    gateways,
                    _localizer);
            }

            row.SharedSourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceId : sourceName;
            yield return row;
        }
    }

    /// <summary>
    /// The gateways a shared row resolves its badge against: the canonical ones and the shared
    /// ones, as the connection itself will see them.
    /// </summary>
    private SessionGatewayContext SharedGatewayContext(AppSettings settings) =>
        SessionGatewayContext.FromSettings(
            SharedSessionSettings.WithSharedGateways(settings, _profileLookup.SharedSessions.Gateways));

    /// <summary>
    /// Adds one root per enabled source after the local folders, then files every shared row under
    /// its source. A source in error keeps its root, carrying the reason, and gets no children.
    /// </summary>
    private void ProjectSharedSources()
    {
        _sharedFoldersByKey.Clear();
        int order = 0;
        foreach (SharedSessionSourceState source in _profileLookup.SharedSessions.Sources)
        {
            if (source.Status == SharedSessionSourceStatus.Disabled
                || !SharedSessionIds.IsValidSourceId(source.SourceId))
            {
                continue;
            }

            StableFolderNode root = CreateSharedFolderNode(
                source.SourceId,
                source.Name,
                sharedPath: "",
                name: string.IsNullOrWhiteSpace(source.Name) ? source.SourceId : source.Name,
                isRoot: true);
            root.SharedOrder = order++;
            if (source.Status == SharedSessionSourceStatus.Error)
            {
                root.ViewModel!.ErrorMessage = string.IsNullOrWhiteSpace(source.ErrorMessage)
                    ? _localizer["SharedSessionSourceErrorUnknown"]
                    : source.ErrorMessage;
            }
            else if (source.IssueCount > 0)
            {
                root.ViewModel!.IssueCount = source.IssueCount;
                root.ViewModel.IssueBadgeText = _localizer.FormatCount(
                    source.IssueCount,
                    "SessionTreeSharedRootIssuesOne",
                    "SessionTreeSharedRootIssues",
                    source.IssueCount);
            }

            if (source.Status != SharedSessionSourceStatus.Error && source.Notice is not null)
            {
                root.ViewModel!.Notice = source.Notice;
                root.ViewModel.NoticeBadgeText = source.OfflineCopyFromUtc is { } takenAtUtc
                    ? _localizer.Format(
                        "SessionTreeSharedRootOfflineBadge",
                        takenAtUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))
                    : _localizer["SessionTreeSharedRootStaleBadge"];
            }

            _stableTreeRoot.Children.Add(root);
        }

        foreach (ServerItemViewModel server in _allServers)
        {
            if (!server.IsShared
                || SharedSessionIds.SourceIdOf(server.Id) is not { } sourceId
                || !_sharedFoldersByKey.TryGetValue(SharedFolderKey(sourceId, ""), out StableFolderNode? root)
                || root.ViewModel!.HasError)
            {
                continue;
            }

            EnsureSharedFolderPath(root, sourceId, NormalizeFolderPath(server.Group)).Servers.Add(server);
        }
    }

    private StableFolderNode EnsureSharedFolderPath(StableFolderNode root, string sourceId, string path)
    {
        string currentPath = "";
        StableFolderNode parent = root;
        foreach (string segment in path.Split(SharedFolderSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            currentPath = currentPath.Length == 0 ? trimmed : currentPath + SharedFolderSeparator + trimmed;
            if (_sharedFoldersByKey.TryGetValue(SharedFolderKey(sourceId, currentPath), out StableFolderNode? existing))
            {
                parent = existing;
                continue;
            }

            StableFolderNode created = CreateSharedFolderNode(
                sourceId,
                root.ViewModel!.SharedSourceName,
                currentPath,
                trimmed,
                isRoot: false);
            parent.Children.Add(created);
            parent = created;
        }

        return parent;
    }

    private StableFolderNode CreateSharedFolderNode(
        string sourceId,
        string sourceName,
        string sharedPath,
        string name,
        bool isRoot)
    {
        string key = SharedFolderKey(sourceId, sharedPath);
        var viewModel = new FolderViewModel(_localizer)
        {
            Name = name,
            FullPath = key,
            IsShared = true,
            IsSharedRoot = isRoot,
            SharedSourceId = sourceId,
            SharedSourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceId : sourceName,
            SharedPath = sharedPath,
            IsExpanded = _expandedNodes.Contains(key),
        };
        viewModel.PropertyChanged += OnFolderExpandedChanged;

        var node = new StableFolderNode(viewModel, isNoGroup: false);
        _sharedFoldersByKey[key] = node;
        return node;
    }

    /// <summary>The shared folder a shared row is filed under, as the tree shows it; null otherwise.</summary>
    private FolderViewModel? ResolveSharedFolderOf(ServerItemViewModel server)
    {
        if (!server.IsShared || SharedSessionIds.SourceIdOf(server.Id) is not { } sourceId)
        {
            return null;
        }

        string path = string.Join(
            SharedFolderSeparator,
            NormalizeFolderPath(server.Group)
                .Split(SharedFolderSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0));
        return _sharedFoldersByKey.TryGetValue(SharedFolderKey(sourceId, path), out StableFolderNode? node)
            ? node.ViewModel
            : null;
    }

    /// <summary>
    /// Refuses a change to rows that include a shared one, saying so on the status line.
    /// </summary>
    /// <param name="rows">The rows the change would write.</param>
    /// <returns>True when every row is local and the change may go ahead.</returns>
    /// <remarks>
    /// A selection that mixes local and shared rows is refused whole rather than trimmed: the
    /// operation's own confirmation and result would otherwise count rows the user did not see
    /// left out, and its status message would overwrite the one saying why.
    /// </remarks>
    internal bool EnsureNoSharedRows(IReadOnlyCollection<ServerItemViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int shared = rows.Count(row => row.IsShared);
        if (shared == 0)
        {
            return true;
        }

        StatusMessageRequested?.Invoke(DescribeSharedRefusal(shared, rows.Count));
        return false;
    }

    /// <summary>The status line a refused change to shared rows shows.</summary>
    /// <param name="sharedCount">How many of the rows are shared.</param>
    /// <param name="totalCount">How many rows the change was asked for.</param>
    internal string DescribeSharedRefusal(int sharedCount, int totalCount) =>
        totalCount <= 1
            ? _localizer["StatusSharedSessionReadOnly"]
            : _localizer.FormatCount(
                sharedCount,
                "StatusSharedSelectionReadOnlyOne",
                "StatusSharedSelectionReadOnly",
                sharedCount);

    /// <summary>Says on the status line that a shared folder cannot be changed.</summary>
    internal void ReportSharedFolderReadOnly() =>
        StatusMessageRequested?.Invoke(_localizer["StatusSharedFolderReadOnly"]);
}
