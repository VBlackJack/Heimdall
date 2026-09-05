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

        List<ServerItemViewModel> moving = NormalizeSelection(servers);
        if (moving.Count == 0 || moving.Contains(anchor))
        {
            return false;
        }

        // The tree's order, not the selection's: a set picked bottom-up lands top-down.
        moving.Sort((left, right) => _stableServerOrder.IndexOf(left).CompareTo(_stableServerOrder.IndexOf(right)));

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
        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);
        return written;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> dtos)
        {
            Dictionary<string, ServerProfileDto> dtoById = dtos.ToDictionary(dto => dto.Id, StringComparer.Ordinal);
            List<(ServerItemViewModel OldVm, ServerProfileDto NewDto)> updates = [];
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
                statusArgs);
        }
    }
}
