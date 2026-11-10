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

namespace Heimdall.App.ViewModels;

/// <summary>
/// How the manual order of local folders ranks a folder among its siblings.
/// </summary>
/// <remarks>
/// The mirror of <see cref="SessionOrdering"/> for folders: a folder listed in the persisted
/// order ranks before every sibling that is not, in list order, and the unlisted siblings follow
/// alphabetically. An empty list ranks every folder the same, which leaves the alphabetical
/// order untouched. The list is flat and spans every parent; only the relative position of two
/// siblings matters, so one folder's entry never disturbs another parent's order.
/// </remarks>
public static class FolderOrdering
{
    /// <summary>The rank of a folder that is not in the persisted order: after every listed one.</summary>
    public const int Unordered = int.MaxValue;

    /// <summary>The rank each listed folder path sorts by: its position in the list.</summary>
    /// <remarks>Paths compare case-insensitively, as folder collisions do; a repeat keeps its first place.</remarks>
    public static IReadOnlyDictionary<string, int> RanksOf(IEnumerable<string>? order)
    {
        Dictionary<string, int> ranks = new(StringComparer.OrdinalIgnoreCase);
        if (order is null)
        {
            return ranks;
        }

        foreach (string path in order)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                ranks.TryAdd(path, ranks.Count);
            }
        }

        return ranks;
    }

    /// <summary>The rank <paramref name="path"/> sorts by: unlisted goes last.</summary>
    public static int RankOf(IReadOnlyDictionary<string, int> ranks, string path)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        ArgumentNullException.ThrowIfNull(path);
        return ranks.TryGetValue(path, out int rank) ? rank : Unordered;
    }

    /// <summary>
    /// The persisted order after moving <paramref name="path"/> one step among its siblings, or
    /// <see langword="null"/> when the folder cannot move that way.
    /// </summary>
    /// <param name="stored">The persisted order as it stands.</param>
    /// <param name="siblings">
    /// Every local sibling of the folder, itself included, in the order the tree shows them. The
    /// first move in a group writes all of them, so the move is relative to what the user sees.
    /// </param>
    /// <param name="path">The folder to move.</param>
    /// <param name="delta"><c>-1</c> to move up, <c>+1</c> to move down.</param>
    /// <param name="existing">
    /// Every local folder path that exists. Stored paths that name none of them are dropped, which
    /// is where a stale entry leaves the list.
    /// </param>
    public static List<string>? Nudge(
        IEnumerable<string>? stored,
        IReadOnlyList<string> siblings,
        string path,
        int delta,
        IReadOnlyCollection<string> existing)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(existing);

        int index = IndexOf(siblings, path);
        int target = index + delta;
        if (index < 0 || delta == 0 || target < 0 || target >= siblings.Count)
        {
            return null;
        }

        List<string> reordered = [.. siblings];
        string moved = reordered[index];
        reordered.RemoveAt(index);
        reordered.Insert(target, moved);

        HashSet<string> siblingSet = new(siblings, StringComparer.OrdinalIgnoreCase);
        HashSet<string> existingSet = new(existing, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string entry in stored ?? [])
        {
            if (!string.IsNullOrWhiteSpace(entry)
                && !siblingSet.Contains(entry)
                && existingSet.Contains(entry)
                && seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        result.AddRange(reordered);
        return result;
    }

    private static int IndexOf(IReadOnlyList<string> paths, string path)
    {
        for (int index = 0; index < paths.Count; index++)
        {
            if (string.Equals(paths[index], path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
