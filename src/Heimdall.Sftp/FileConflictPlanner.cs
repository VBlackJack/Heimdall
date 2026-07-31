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

namespace Heimdall.Sftp;

/// <summary>A source item and its intended destination before conflict resolution.</summary>
/// <param name="SourceIdentity">Stable identity used by the transfer caller.</param>
/// <param name="TargetPath">Intended destination path.</param>
public sealed record FileConflictPlanItem(string SourceIdentity, string TargetPath);

/// <summary>A planned item annotated with its pre-transfer conflict state.</summary>
/// <param name="Index">Zero-based position in the original ordered batch.</param>
/// <param name="SourceIdentity">Stable identity used by the transfer caller.</param>
/// <param name="TargetPath">Intended destination path.</param>
/// <param name="HasConflict">Whether the target exists or duplicates an earlier batch target.</param>
public sealed record FileConflictAnalysisItem(
    int Index,
    string SourceIdentity,
    string TargetPath,
    bool HasConflict);

/// <summary>The user-selectable resolution for an item whose destination collides.</summary>
public enum FileConflictResolutionChoice
{
    /// <summary>Do not transfer this item.</summary>
    Skip,

    /// <summary>Proceed to the original target and allow the transfer primitive to replace it.</summary>
    Replace,

    /// <summary>Proceed to a derived target that is free in the destination and the batch.</summary>
    AutoRename,
}

/// <summary>A user's resolution for one analyzed item.</summary>
/// <param name="ItemIndex">Index from <see cref="FileConflictAnalysisItem.Index"/>.</param>
/// <param name="Choice">Resolution selected for the collision.</param>
public sealed record FileConflictDecision(int ItemIndex, FileConflictResolutionChoice Choice);

/// <summary>The effective transfer action after all conflicts have been resolved.</summary>
public enum FileConflictEffectiveAction
{
    /// <summary>Transfer to the original target.</summary>
    Proceed,

    /// <summary>Transfer to a newly derived target.</summary>
    ProceedToNewTarget,

    /// <summary>Do not transfer the item.</summary>
    Skip,
}

/// <summary>An item ready for transfer after applying the user's batch decisions.</summary>
/// <param name="Index">Zero-based position in the original ordered batch.</param>
/// <param name="SourceIdentity">Stable identity used by the transfer caller.</param>
/// <param name="OriginalTargetPath">Destination requested before conflict resolution.</param>
/// <param name="EffectiveTargetPath">Destination to use when the action transfers the item.</param>
/// <param name="Action">Effective transfer action.</param>
public sealed record FileConflictResolvedItem(
    int Index,
    string SourceIdentity,
    string OriginalTargetPath,
    string EffectiveTargetPath,
    FileConflictEffectiveAction Action);

/// <summary>
/// Pure batch conflict analyzer and resolver. All destination access is supplied by the caller, and
/// all path equivalence is controlled by the injected comparer.
/// </summary>
public static class FileConflictPlanner
{
    /// <summary>
    /// Detects destinations that already exist and destinations repeated inside the ordered batch.
    /// </summary>
    public static IReadOnlyList<FileConflictAnalysisItem> Analyze(
        IReadOnlyList<FileConflictPlanItem> items,
        Func<string, bool> targetExists,
        StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(targetExists);
        ArgumentNullException.ThrowIfNull(pathComparer);

        var claimedTargets = new HashSet<string>(pathComparer);
        var analysis = new List<FileConflictAnalysisItem>(items.Count);

        for (int index = 0; index < items.Count; index++)
        {
            FileConflictPlanItem item = items[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.TargetPath);

            bool alreadyClaimed = !claimedTargets.Add(item.TargetPath);
            bool hasConflict = alreadyClaimed || targetExists(item.TargetPath);
            analysis.Add(new FileConflictAnalysisItem(
                index,
                item.SourceIdentity,
                item.TargetPath,
                hasConflict));
        }

        return analysis;
    }

    /// <summary>
    /// Applies one decision per colliding item and returns the effective ordered transfer batch.
    /// </summary>
    public static IReadOnlyList<FileConflictResolvedItem> Resolve(
        IReadOnlyList<FileConflictAnalysisItem> analysis,
        IReadOnlyList<FileConflictDecision> decisions,
        Func<string, bool> targetExists,
        StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(targetExists);
        ArgumentNullException.ThrowIfNull(pathComparer);

        Dictionary<int, FileConflictResolutionChoice> choices = decisions.ToDictionary(
            decision => decision.ItemIndex,
            decision => decision.Choice);
        var reservedTargets = new HashSet<string>(
            analysis.Select(item => item.TargetPath),
            pathComparer);
        var resolved = new List<FileConflictResolvedItem>(analysis.Count);

        foreach (FileConflictAnalysisItem item in analysis)
        {
            if (!item.HasConflict)
            {
                resolved.Add(ToResolved(item, item.TargetPath, FileConflictEffectiveAction.Proceed));
                continue;
            }

            if (!choices.TryGetValue(item.Index, out FileConflictResolutionChoice choice))
            {
                throw new ArgumentException(
                    $"A resolution is required for conflicting item {item.Index}.",
                    nameof(decisions));
            }

            switch (choice)
            {
                case FileConflictResolutionChoice.Skip:
                    resolved.Add(ToResolved(item, item.TargetPath, FileConflictEffectiveAction.Skip));
                    break;
                case FileConflictResolutionChoice.Replace:
                    resolved.Add(ToResolved(item, item.TargetPath, FileConflictEffectiveAction.Proceed));
                    break;
                case FileConflictResolutionChoice.AutoRename:
                    string renamedTarget = BuildAvailableTarget(
                        item.TargetPath,
                        reservedTargets,
                        targetExists);
                    reservedTargets.Add(renamedTarget);
                    resolved.Add(ToResolved(
                        item,
                        renamedTarget,
                        FileConflictEffectiveAction.ProceedToNewTarget));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(decisions), choice, "Unknown conflict resolution.");
            }
        }

        return resolved;
    }

    private static FileConflictResolvedItem ToResolved(
        FileConflictAnalysisItem item,
        string effectiveTargetPath,
        FileConflictEffectiveAction action)
        => new(
            item.Index,
            item.SourceIdentity,
            item.TargetPath,
            effectiveTargetPath,
            action);

    private static string BuildAvailableTarget(
        string originalTargetPath,
        HashSet<string> reservedTargets,
        Func<string, bool> targetExists)
    {
        (string directory, string leafName) = SplitTarget(originalTargetPath);
        (string stem, string extension) = SplitNameAndExtension(leafName);

        string firstCandidate = CombineTarget(directory, $"{stem} (copy){extension}");
        if (IsAvailable(firstCandidate, reservedTargets, targetExists))
        {
            return firstCandidate;
        }

        for (int counter = 2; ; counter++)
        {
            string candidate = CombineTarget(directory, $"{stem} (copy {counter}){extension}");
            if (IsAvailable(candidate, reservedTargets, targetExists))
            {
                return candidate;
            }
        }
    }

    private static bool IsAvailable(
        string candidate,
        HashSet<string> reservedTargets,
        Func<string, bool> targetExists)
        => !reservedTargets.Contains(candidate) && !targetExists(candidate);

    private static (string Directory, string LeafName) SplitTarget(string targetPath)
    {
        int lastSlash = targetPath.LastIndexOf('/');
        int lastBackslash = targetPath.LastIndexOf('\\');
        int separatorIndex = Math.Max(lastSlash, lastBackslash);
        if (separatorIndex < 0)
        {
            return (string.Empty, targetPath);
        }

        return (targetPath[..(separatorIndex + 1)], targetPath[(separatorIndex + 1)..]);
    }

    private static string CombineTarget(string directory, string leafName) => directory + leafName;

    private static (string Stem, string Extension) SplitNameAndExtension(string name)
    {
        int lastDot = name.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == name.Length - 1)
        {
            return (name, string.Empty);
        }

        return (name[..lastDot], name[lastDot..]);
    }
}
