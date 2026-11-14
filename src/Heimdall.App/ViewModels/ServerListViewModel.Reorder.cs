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

using System.Runtime.ExceptionServices;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Manual ordering of sessions within a folder: a positioned drop, and a one-step nudge.
/// </summary>
/// <remarks>
/// Both end in the same write: the folder's sessions, in the order the gesture produced, are
/// renumbered by tens and saved in one inventory mutation, and the tree is rebuilt from the
/// result. A session dropped on a row of another folder changes folder and takes its position
/// in the same write.
/// </remarks>
public partial class ServerListViewModel
{
    /// <summary>
    /// Places <paramref name="servers"/> before or after <paramref name="anchor"/> among the
    /// anchor's siblings, moving them into the anchor's folder when they come from elsewhere.
    /// </summary>
    /// <returns><see langword="true"/> when an order was written.</returns>
    public async Task<bool> ReorderServersAsync(
        IReadOnlyList<ServerItemViewModel> servers,
        ServerItemViewModel anchor,
        bool placeAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(anchor);
        if (!EnsureNoSharedRows(servers))
        {
            return false;
        }

        // A place among shared rows is the file's to decide, not a drop target.
        if (anchor.IsShared)
        {
            StatusMessageRequested?.Invoke(_localizer["StatusSharedSessionMoveRefused"]);
            return false;
        }

        List<ServerItemViewModel> moving = NormalizeMutableSelection(servers);
        if (moving.Count == 0 || moving.Contains(anchor))
        {
            return false;
        }

        // The tree's order, not the selection's: a set picked bottom-up lands top-down.
        Dictionary<ServerItemViewModel, int> treeOrder = EnumerateCanonicalTreeServers(_stableTreeRoot)
            .Select((server, index) => (server, index))
            .ToDictionary(entry => entry.server, entry => entry.index);
        moving.Sort((left, right) => treeOrder[left].CompareTo(treeOrder[right]));

        List<ServerItemViewModel> siblings = GetStableSiblings(anchor.Group)
            .Where(sibling => !moving.Contains(sibling))
            .ToList();
        int index = siblings.IndexOf(anchor);
        if (index < 0)
        {
            return false;
        }

        siblings.InsertRange(placeAfter ? index + 1 : index, moving);

        string primaryId = SelectedServer is not null && moving.Contains(SelectedServer)
            ? SelectedServer.Id
            : moving[^1].Id;
        string folderName = string.IsNullOrWhiteSpace(anchor.Group)
            ? _localizer["TreeNodeNoGroup"]
            : FolderPath.LeafOf(anchor.Group);
        string statusKey = moving.Count == 1 ? "StatusSessionReordered" : "StatusSessionsReordered";
        object[] statusArgs = moving.Count == 1
            ? [moving[0].DisplayName, folderName]
            : [moving.Count, folderName];

        return await PersistOrderAsync(
            NormalizeGroupForPersistence(anchor.Group),
            siblings,
            [.. moving.Select(server => server.Id)],
            primaryId,
            statusKey,
            statusArgs,
            cancellationToken);
    }

    /// <summary>
    /// Moves <paramref name="server"/> one step up (<c>-1</c>) or down (<c>+1</c>) among its
    /// siblings, keeping the selection as it is.
    /// </summary>
    /// <returns><see langword="true"/> when the session moved.</returns>
    public async Task<bool> NudgeServerAsync(
        ServerItemViewModel server,
        int delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!EnsureNoSharedRows([server]))
        {
            return false;
        }

        List<ServerItemViewModel> siblings = GetStableSiblings(server.Group).ToList();
        int index = siblings.IndexOf(server);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= siblings.Count || delta == 0)
        {
            return false;
        }

        siblings.RemoveAt(index);
        siblings.Insert(target, server);

        return await PersistOrderAsync(
            NormalizeGroupForPersistence(server.Group),
            siblings,
            [.. SelectedItems.Select(selected => selected.Id)],
            SelectedServer?.Id,
            statusKey: null,
            statusArgs: null,
            cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="server"/> can move one step in the direction of
    /// <paramref name="delta"/> among its siblings: a local session that is not at that edge.
    /// </summary>
    public bool CanNudgeServer(ServerItemViewModel server, int delta)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.IsShared || delta == 0)
        {
            return false;
        }

        IReadOnlyList<ServerItemViewModel> siblings = GetStableSiblings(server.Group);
        int index = siblings.ToList().IndexOf(server);
        int target = index + delta;
        return index >= 0 && target >= 0 && target < siblings.Count;
    }

    /// <summary>
    /// Whether <paramref name="folder"/> can move one step in the direction of
    /// <paramref name="delta"/> among its local siblings: a local folder that is not at that edge.
    /// </summary>
    public bool CanNudgeFolder(FolderViewModel folder, int delta)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return delta != 0
            && !folder.IsShared
            && TryResolveFolderSiblings(folder.FullPath, delta, out _, out _);
    }

    /// <summary>
    /// Moves <paramref name="folder"/> one step up (<c>-1</c>) or down (<c>+1</c>) among the local
    /// folders of its parent, in one settings write.
    /// </summary>
    /// <returns><see langword="true"/> when the folder moved.</returns>
    /// <remarks>
    /// The first move in a group writes the order of every local sibling as the tree shows it, so
    /// the step is relative to what the user sees whatever the stored list held before. A shared
    /// folder is refused with the read-only status line, and an edge writes nothing.
    /// </remarks>
    public async Task<bool> NudgeFolderAsync(
        FolderViewModel folder,
        int delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.IsShared || IsSharedFolderKey(folder.FullPath))
        {
            ReportSharedFolderReadOnly();
            return false;
        }

        if (delta == 0
            || !TryResolveFolderSiblings(folder.FullPath, delta, out List<string> siblings, out _))
        {
            return false;
        }

        string path = folder.FullPath;
        List<string> existing = EnumerateStableFolders(_stableTreeRoot)
            .Where(node => !node.IsShared && !node.IsNoGroup)
            .Select(node => node.ViewModel!.FullPath)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();

        bool moved = false;
        await _configManager.MergeSettingAsync(settings =>
        {
            List<string>? reordered = FolderOrdering.Nudge(settings.FolderOrder, siblings, path, delta, existing);
            if (reordered is not null)
            {
                settings.FolderOrder = reordered;
                moved = true;
            }
        });

        if (!moved)
        {
            return false;
        }

        _currentSettings = await _configManager.LoadSettingsAsync();
        ResortStableTreeProjection();
        ApplyFilter();
        return true;
    }

    /// <summary>
    /// Whether "Sort A to Z" has something to undo on <paramref name="folder"/>'s own level: a local
    /// folder, or "(No Folder)", with a direct session that has a manual place or a direct
    /// sub-folder listed in the manual folder order. Only direct children count, so a hand
    /// arrangement deeper down never enables it, and it reads the stable tree, which is cheap.
    /// </summary>
    public bool CanSortFolderAlphabetically(FolderViewModel folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.IsShared || IsSharedFolderKey(folder.FullPath))
        {
            return false;
        }

        string key = string.IsNullOrEmpty(folder.FullPath) ? NoGroupProjectionKey : folder.FullPath;
        return _stableFoldersByPath.TryGetValue(key, out StableFolderNode? node)
            && !node.IsShared
            && HasHandArrangedLevel(node, includeFolders: !node.IsNoGroup);
    }

    /// <summary>
    /// Whether "Sort top-level folders A to Z" has something to undo: a top-level local folder is
    /// listed in the manual folder order.
    /// </summary>
    public bool CanSortTopLevelAlphabetically() =>
        HasHandArrangedLevel(_stableTreeRoot, includeFolders: true, includeSessions: false);

    /// <summary>
    /// Puts one level back in alphabetical order: the direct sessions of <paramref name="folder"/>
    /// lose their manual place and the folder order entries ranking its direct sub-folders are
    /// removed. "(No Folder)" clears its sessions only. Nothing deeper changes.
    /// </summary>
    /// <returns><see langword="true"/> when something was cleared.</returns>
    /// <remarks>
    /// A shared folder is refused with the read-only status line. The change is offered to the
    /// undo bar, which puts the session values and the removed entries back.
    /// </remarks>
    public async Task<bool> SortFolderAlphabeticallyAsync(
        FolderViewModel folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.IsShared || IsSharedFolderKey(folder.FullPath))
        {
            ReportSharedFolderReadOnly();
            return false;
        }

        string key = string.IsNullOrEmpty(folder.FullPath) ? NoGroupProjectionKey : folder.FullPath;
        if (!_stableFoldersByPath.TryGetValue(key, out StableFolderNode? node) || node.IsShared)
        {
            return false;
        }

        bool sorted = await SortLevelAlphabeticallyAsync(
            folder.FullPath,
            [.. node.Servers.Where(server => !server.IsShared)],
            clearFolderEntries: !node.IsNoGroup,
            cancellationToken);
        if (sorted)
        {
            StatusMessageRequested?.Invoke(_localizer.Format("StatusFolderSortedAlphabetically", folder.Name));
        }

        return sorted;
    }

    /// <summary>
    /// Puts the top-level folders back in alphabetical order by removing the folder order entries
    /// that have no parent. Sessions and deeper levels are left as they are.
    /// </summary>
    /// <returns><see langword="true"/> when an entry was cleared.</returns>
    public async Task<bool> SortTopLevelAlphabeticallyAsync(CancellationToken cancellationToken = default)
    {
        bool sorted = await SortLevelAlphabeticallyAsync(
            string.Empty,
            [],
            clearFolderEntries: true,
            cancellationToken);
        if (sorted)
        {
            StatusMessageRequested?.Invoke(_localizer["StatusTopLevelSortedAlphabetically"]);
        }

        return sorted;
    }

    private bool HasHandArrangedLevel(StableFolderNode node, bool includeFolders, bool includeSessions = true)
    {
        if (includeSessions
            && node.Servers.Any(server => !server.IsShared && server.SortOrder != SessionOrdering.Unordered))
        {
            return true;
        }

        if (!includeFolders || _currentSettings?.FolderOrder is not { Count: > 0 } order)
        {
            return false;
        }

        IReadOnlyDictionary<string, int> ranks = FolderOrdering.RanksOf(order);
        return node.Children.Any(child =>
            !child.IsShared && !child.IsNoGroup && ranks.ContainsKey(child.ViewModel!.FullPath));
    }

    /// <summary>
    /// The shared write of both sort commands. The folder order entries go first, then the
    /// sessions; if the second write fails the first is put back, and if that fails too the undo
    /// bar still offers to put the entries back, so no failure leaves a change nobody can reverse.
    /// </summary>
    private async Task<bool> SortLevelAlphabeticallyAsync(
        string levelPath,
        IReadOnlyList<ServerItemViewModel> directSessions,
        bool clearFolderEntries,
        CancellationToken cancellationToken)
    {
        // Nothing arranged on this level: no write, no undo entry.
        if (!(clearFolderEntries && FolderOrdering.HasChildEntry(_currentSettings?.FolderOrder, levelPath))
            && !directSessions.Any(server => server.SortOrder != SessionOrdering.Unordered))
        {
            return false;
        }

        HashSet<string> directIds = new(directSessions.Select(server => server.Id), StringComparer.Ordinal);
        Dictionary<string, ServerItemViewModel> directById = directSessions.ToDictionary(
            server => server.Id,
            StringComparer.Ordinal);
        List<string> removedEntries = [];
        bool rowsChanged = false;
        ExceptionDispatchInfo? failure = null;

        FolderOrderRestore? restore = await WithOrganizationUndoAsync(TreeOrganizationChange.FolderSort, async () =>
        {
            if (clearFolderEntries)
            {
                await _configManager.MergeSettingAsync(settings =>
                {
                    removedEntries = FolderOrdering.ChildEntries(settings.FolderOrder, levelPath);
                    if (removedEntries.Count > 0)
                    {
                        settings.FolderOrder = FolderOrdering.WithoutChildEntries(settings.FolderOrder, levelPath);
                    }
                });
            }

            try
            {
                if (directIds.Count > 0)
                {
                    await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // Any exception from the session write lands here, one thrown after the rows were
                // saved (the refresh steps) included. The folder entries are then put back while the
                // sessions are already cleared: the user is told the change failed, and the cleared
                // rows are still recorded by the history, so Undo is offered for them.
                failure = ExceptionDispatchInfo.Capture(ex);
                if (removedEntries.Count > 0 && await TryPutBackEntriesAsync(removedEntries))
                {
                    removedEntries = [];
                }
            }

            return removedEntries.Count > 0 ? new FolderOrderRestore(levelPath, removedEntries) : null;
        }, serverIds: directIds, restoreFolderOrder: removed => removed);

        if (!rowsChanged || failure is not null)
        {
            // Only the folder order changed, or nothing did: rebuild from what is now stored.
            try
            {
                _currentSettings = await _configManager.LoadSettingsAsync();
                ResortStableTreeProjection();
                ApplyFilter();
            }
            catch (Exception ex) when (failure is not null)
            {
                Core.Logging.FileLogger.Error("Refreshing the tree after a failed sort failed", ex);
            }
        }

        failure?.Throw();
        return restore is not null || rowsChanged;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> dtos)
        {
            List<(ServerItemViewModel OldVm, ServerProfileDto NewDto)> updates = [];
            foreach (ServerProfileDto dto in dtos)
            {
                // A row that left the folder since the tree was built is not this level's any more.
                if (dto.SortOrder != SessionOrdering.Unordered
                    && directById.TryGetValue(dto.Id, out ServerItemViewModel? server)
                    && string.Equals(FolderPath.Canonicalize(dto.Group), levelPath, StringComparison.OrdinalIgnoreCase))
                {
                    dto.SortOrder = SessionOrdering.Unordered;
                    updates.Add((server, dto));
                }
            }

            rowsChanged = updates.Count > 0;
            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                updates,
                Array.Empty<ServerProfileDto>(),
                null,
                null,
                null,
                null,
                null,
                OrderOnly: true);
        }
    }

    private async Task<bool> TryPutBackEntriesAsync(IReadOnlyList<string> entries)
    {
        try
        {
            await _configManager.MergeSettingAsync(settings =>
                settings.FolderOrder = FolderOrdering.WithEntriesAppended(settings.FolderOrder, entries));
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Putting the folder order back after a failed sort failed", ex);
            return false;
        }
    }

    /// <summary>
    /// The local folders sharing a parent with <paramref name="folderPath"/>, in the order the
    /// tree shows them, when the folder can move <paramref name="delta"/> steps.
    /// </summary>
    private bool TryResolveFolderSiblings(
        string folderPath,
        int delta,
        out List<string> siblingPaths,
        out int index)
    {
        siblingPaths = [];
        index = -1;
        if (string.IsNullOrEmpty(folderPath) || IsSharedFolderKey(folderPath))
        {
            return false;
        }

        string parentPath = FolderPath.ParentOf(folderPath);
        StableFolderNode? parent = parentPath.Length == 0
            ? _stableTreeRoot
            : _stableFoldersByPath.GetValueOrDefault(parentPath);
        if (parent is null || parent.IsShared)
        {
            return false;
        }

        siblingPaths = parent.Children
            .Where(child => !child.IsShared && !child.IsNoGroup)
            .Select(child => child.ViewModel!.FullPath)
            .ToList();
        index = siblingPaths.FindIndex(path => string.Equals(path, folderPath, StringComparison.Ordinal));
        int target = index + delta;
        return index >= 0 && target >= 0 && target < siblingPaths.Count;
    }

    /// <summary>Traverses folders before their direct sessions, as the tree renders them.</summary>
    private static IEnumerable<ServerItemViewModel> EnumerateCanonicalTreeServers(StableFolderNode node)
    {
        foreach (StableFolderNode child in node.Children)
        {
            foreach (ServerItemViewModel server in EnumerateCanonicalTreeServers(child))
            {
                yield return server;
            }
        }

        foreach (ServerItemViewModel server in node.Servers)
        {
            yield return server;
        }
    }

    /// <summary>Every session of a folder, in the order the tree shows them, filter or not.</summary>
    private IReadOnlyList<ServerItemViewModel> GetStableSiblings(string? group)
    {
        string key = string.IsNullOrWhiteSpace(group)
            ? NoGroupProjectionKey
            : NormalizeFolderPath(group);
        return _stableFoldersByPath.TryGetValue(key, out StableFolderNode? node)
            ? node.Servers
            : [];
    }

    /// <summary>
    /// Renumbers <paramref name="orderedSiblings"/> by tens under <paramref name="targetGroup"/>
    /// in one inventory write, then rebuilds the tree and restores the selection.
    /// </summary>
    private async Task<bool> PersistOrderAsync(
        string? targetGroup,
        IReadOnlyList<ServerItemViewModel> orderedSiblings,
        IReadOnlyList<string> selectionIds,
        string? primaryId,
        string? statusKey,
        object[]? statusArgs,
        CancellationToken cancellationToken)
    {
        bool written = false;
        await WithOrganizationUndoAsync(TreeOrganizationChange.Reorder, async () =>
        {
            await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);
            return written;
        }, serverIds: orderedSiblings.Select(server => server.Id).ToArray());
        return written;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> dtos)
        {
            Dictionary<string, ServerProfileDto> dtoById = dtos.ToDictionary(dto => dto.Id, StringComparer.Ordinal);
            List<(ServerItemViewModel OldVm, ServerProfileDto NewDto)> updates = [];
            bool everyRowKeptItsFolder = true;
            for (int index = 0; index < orderedSiblings.Count; index++)
            {
                ServerItemViewModel sibling = orderedSiblings[index];
                if (!dtoById.TryGetValue(sibling.Id, out ServerProfileDto? dto))
                {
                    continue;
                }

                int order = SessionOrdering.OrderAt(index);
                bool sameGroup = string.Equals(
                    NormalizeGroupForPersistence(dto.Group),
                    targetGroup,
                    StringComparison.OrdinalIgnoreCase);
                if (dto.SortOrder == order && sameGroup)
                {
                    continue;
                }

                everyRowKeptItsFolder &=
                    string.Equals(NormalizeGroupForPersistence(dto.Group), targetGroup, StringComparison.Ordinal)
                    && string.Equals(NormalizeGroupForPersistence(sibling.Group), targetGroup, StringComparison.Ordinal);
                dto.SortOrder = order;
                dto.Group = targetGroup;
                updates.Add((sibling, dto));
            }

            written = updates.Count > 0;
            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                updates,
                Array.Empty<ServerProfileDto>(),
                selectionIds,
                primaryId,
                primaryId,
                written ? statusKey : null,
                statusArgs,
                everyRowKeptItsFolder);
        }
    }
}
