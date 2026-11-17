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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Arranges what an import adds so the sessions tree shows it in the order of the source file.
/// </summary>
/// <remarks>
/// The tree orders sessions by their manual rank then name, and folders by their place in the
/// persisted folder order then name. An import that writes neither shows alphabetically. These
/// helpers write a rank or a folder order only where the wanted order differs from what the tree
/// would show anyway, so an untouched folder stays alphabetical. Existing items keep their visible
/// order and the imported ones follow them. Every path comparison ignores case, as the tree does.
/// </remarks>
internal static class ImportSourceOrder
{
    private const char Separator = '/';

    /// <summary>
    /// Gives the imported sessions their canonical group and, in every folder that receives some, the
    /// manual order that shows them after the existing sessions in file order.
    /// </summary>
    /// <param name="existing">
    /// The inventory the import was merged into. A row that is also in <paramref name="imported"/>
    /// (the same instance) is not counted as existing.
    /// </param>
    /// <param name="imported">The rows the import added, in file order.</param>
    public static void ArrangeSessions(
        IReadOnlyList<ServerProfileDto> existing,
        IReadOnlyList<ServerProfileDto> imported)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(imported);

        Dictionary<string, List<ServerProfileDto>> importedByKey = new(StringComparer.OrdinalIgnoreCase);
        HashSet<ServerProfileDto> importedSet = new(ReferenceEqualityComparer.Instance);
        foreach (ServerProfileDto row in imported)
        {
            string key = FolderPath.Canonicalize(row.Group);
            row.Group = key.Length == 0 ? null : key;
            importedSet.Add(row);
            AddRow(importedByKey, key, row);
        }

        Dictionary<string, List<ServerProfileDto>> existingByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (ServerProfileDto row in existing)
        {
            if (importedSet.Contains(row))
            {
                continue;
            }

            string key = FolderPath.Canonicalize(row.Group);
            if (importedByKey.ContainsKey(key))
            {
                AddRow(existingByKey, key, row);
            }
        }

        StringComparer names = DisplayNameOrdering.Comparer;
        long unorderedRank = SessionOrdering.RankOf(SessionOrdering.Unordered);
        long RankFor(ServerProfileDto row) =>
            importedSet.Contains(row) ? unorderedRank : SessionOrdering.RankOf(row.SortOrder);
        foreach ((string key, List<ServerProfileDto> added) in importedByKey)
        {
            List<ServerProfileDto> desired = [];
            if (existingByKey.TryGetValue(key, out List<ServerProfileDto>? current))
            {
                desired.AddRange(current
                    .OrderBy(row => SessionOrdering.RankOf(row.SortOrder))
                    .ThenBy(row => row.DisplayName, names));
            }

            desired.AddRange(added);
            if (desired.Count <= 1)
            {
                continue;
            }

            // Until this write the tree ranks an imported row as unordered, so its default place is
            // by name among the unordered rows. Writing is only worth it when that differs.
            List<ServerProfileDto> byDefault = [.. desired
                .OrderBy(RankFor)
                .ThenBy(row => row.DisplayName, names)];
            // Two rows of the same rank whose names compare equal may show in either order: the
            // tree sorts with an unstable sort, so the file order is written for them.
            bool hasTie = false;
            for (int index = 1; index < byDefault.Count && !hasTie; index++)
            {
                hasTie = RankFor(byDefault[index]) == RankFor(byDefault[index - 1])
                    && names.Compare(byDefault[index].DisplayName, byDefault[index - 1].DisplayName) == 0;
            }

            if (!hasTie && desired.SequenceEqual(byDefault, ReferenceEqualityComparer.Instance))
            {
                continue;
            }

            for (int index = 0; index < desired.Count; index++)
            {
                desired[index].SortOrder = SessionOrdering.OrderAt(index);
            }
        }
    }

    /// <summary>
    /// The canonical folder paths the rows' groups pass through, each once, in order of first
    /// appearance. A parent is listed before its children.
    /// </summary>
    public static IReadOnlyList<string> FolderPathsInFileOrder(IEnumerable<ServerProfileDto> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return PathsInFileOrder(rows.Select(row => row.Group));
    }

    /// <summary>
    /// The canonical folder paths the given paths pass through, each once, in order of first
    /// appearance. A parent is listed before its children.
    /// </summary>
    public static IReadOnlyList<string> PathsInFileOrder(IEnumerable<string?> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = [];
        foreach (string? group in groups)
        {
            AddPrefixes(group, seen, ordered);
        }

        return ordered;
    }

    /// <summary>
    /// The containers of the file that become empty folders: canonical, not blank, not already a
    /// folder, each once (first spelling) and in file order.
    /// </summary>
    /// <param name="containersInFileOrder">Every container path of the file, parents first.</param>
    /// <param name="existingFolders">
    /// Every folder path already in use, compared without case: the prefixes of the existing
    /// session groups and of the stored empty folders.
    /// </param>
    public static IReadOnlyList<string> EmptyFoldersToCreate(
        IReadOnlyList<string> containersInFileOrder,
        IReadOnlySet<string> existingFolders)
    {
        ArgumentNullException.ThrowIfNull(containersInFileOrder);
        ArgumentNullException.ThrowIfNull(existingFolders);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> toCreate = [];
        foreach (string container in containersInFileOrder)
        {
            string path = FolderPath.Canonicalize(container);
            if (path.Length > 0 && !existingFolders.Contains(path) && seen.Add(path))
            {
                toCreate.Add(path);
            }
        }

        return toCreate;
    }

    /// <summary>Every canonical folder path the groups pass through, as a case-insensitive set.</summary>
    public static HashSet<string> FolderPathsOf(IEnumerable<string?> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? group in groups)
        {
            AddPrefixes(group, seen, null);
        }

        return seen;
    }

    /// <summary>
    /// The folder order to persist so the imported folders show in file order, or
    /// <see langword="null"/> when nothing needs writing.
    /// </summary>
    /// <param name="stored">The persisted folder order as it stands.</param>
    /// <param name="before">Every folder path that existed before the import.</param>
    /// <param name="importedInFileOrder">The imported folder paths in file order, parents first.</param>
    public static List<string>? MergeFolderOrder(
        IReadOnlyList<string> stored,
        IReadOnlySet<string> before,
        IReadOnlyList<string> importedInFileOrder)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(importedInFileOrder);

        HashSet<string> newPaths = new(StringComparer.OrdinalIgnoreCase);
        List<string> newInFileOrder = [];
        foreach (string path in importedInFileOrder)
        {
            if (path.Length > 0 && !before.Contains(path) && newPaths.Add(path))
            {
                newInFileOrder.Add(path);
            }
        }

        if (newInFileOrder.Count == 0)
        {
            return null;
        }

        Dictionary<string, List<string>> beforeByParent = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in before)
        {
            AddRow(beforeByParent, FolderPath.ParentOf(path), path);
        }

        Dictionary<string, List<string>> newByParent = new(StringComparer.OrdinalIgnoreCase);
        List<string> parents = [];
        foreach (string path in newInFileOrder)
        {
            string parent = FolderPath.ParentOf(path);
            if (!newByParent.ContainsKey(parent))
            {
                parents.Add(parent);
            }

            AddRow(newByParent, parent, path);
        }

        IReadOnlyDictionary<string, int> ranks = FolderOrdering.RanksOf(stored);
        StringComparer names = DisplayNameOrdering.Comparer;
        int RankFor(string path) =>
            newPaths.Contains(path) ? FolderOrdering.Unordered : FolderOrdering.RankOf(ranks, path);
        List<List<string>> rewritten = [];
        HashSet<string> rewrittenSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (string parent in parents)
        {
            List<string> desired = [];
            if (beforeByParent.TryGetValue(parent, out List<string>? current))
            {
                desired.AddRange(current
                    .OrderBy(path => FolderOrdering.RankOf(ranks, path))
                    .ThenBy(FolderPath.LeafOf, names));
            }

            desired.AddRange(newByParent[parent]);
            if (desired.Count <= 1)
            {
                continue;
            }

            List<string> byDefault = [.. desired
                .OrderBy(RankFor)
                .ThenBy(FolderPath.LeafOf, names)];
            // Siblings of one rank whose leaf names compare equal may show in either order.
            bool hasTie = false;
            for (int index = 1; index < byDefault.Count && !hasTie; index++)
            {
                hasTie = RankFor(byDefault[index]) == RankFor(byDefault[index - 1])
                    && names.Compare(FolderPath.LeafOf(byDefault[index]), FolderPath.LeafOf(byDefault[index - 1])) == 0;
            }

            if (!hasTie && desired.SequenceEqual(byDefault, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            rewritten.Add(desired);
            rewrittenSet.UnionWith(desired);
        }

        if (rewritten.Count == 0)
        {
            return null;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string entry in stored)
        {
            if (!string.IsNullOrWhiteSpace(entry)
                && !rewrittenSet.Contains(entry)
                && (before.Contains(entry) || newPaths.Contains(entry))
                && seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        foreach (List<string> siblings in rewritten)
        {
            result.AddRange(siblings);
        }

        return result;
    }

    private static void AddPrefixes(string? group, HashSet<string> seen, List<string>? ordered)
    {
        string path = FolderPath.Canonicalize(group);
        int end = -1;
        while (path.Length > 0)
        {
            end = path.IndexOf(Separator, end + 1);
            string prefix = end < 0 ? path : path[..end];
            if (seen.Add(prefix))
            {
                ordered?.Add(prefix);
            }

            if (end < 0)
            {
                break;
            }
        }
    }

    private static void AddRow<T>(Dictionary<string, List<T>> index, string key, T value)
    {
        if (!index.TryGetValue(key, out List<T>? values))
        {
            values = [];
            index.Add(key, values);
        }

        values.Add(value);
    }
}
