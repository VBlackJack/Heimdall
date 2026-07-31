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

/// <summary>The kind of destination represented by a conflict-planning item.</summary>
public enum FileConflictItemKind
{
    /// <summary>A file destination.</summary>
    File,

    /// <summary>A directory destination.</summary>
    Directory,
}

/// <summary>The operation semantics used to classify destination-kind collisions.</summary>
public enum FileConflictPolicy
{
    /// <summary>Transfer semantics, where an existing directory may accept a directory merge.</summary>
    Transfer,

    /// <summary>Rename semantics, where the destination is always the exact target entry.</summary>
    Rename,
}

/// <summary>The conflict resolutions that may be selected for an analyzed item.</summary>
[Flags]
public enum FileConflictResolutionActions
{
    /// <summary>No resolution is applicable.</summary>
    None = 0,

    /// <summary>The item may be skipped.</summary>
    Skip = 1,

    /// <summary>The existing destination may be replaced.</summary>
    Replace = 2,

    /// <summary>The item may be transferred under a derived free name.</summary>
    AutoRename = 4,

    /// <summary>Every file-conflict resolution is available.</summary>
    All = Skip | Replace | AutoRename,
}

/// <summary>A source item and its intended destination before conflict resolution.</summary>
/// <param name="SourceIdentity">Stable identity used by the transfer caller.</param>
/// <param name="TargetPath">Intended destination path.</param>
/// <param name="Kind">Destination kind; defaults to file for download compatibility.</param>
public sealed record FileConflictPlanItem(
    string SourceIdentity,
    string TargetPath,
    FileConflictItemKind Kind = FileConflictItemKind.File);

/// <summary>A planned item annotated with its pre-transfer conflict state.</summary>
/// <param name="Index">Zero-based position in the original ordered batch.</param>
/// <param name="SourceIdentity">Stable identity used by the transfer caller.</param>
/// <param name="TargetPath">Intended destination path.</param>
/// <param name="HasConflict">Whether the target exists or duplicates an earlier batch target.</param>
/// <param name="PlannedKind">Kind of the planned destination.</param>
/// <param name="ExistingTargetKind">Kind already occupying the target, or null when absent.</param>
/// <param name="AllowedActions">Resolutions permitted by the kind matrix.</param>
public sealed record FileConflictAnalysisItem(
    int Index,
    string SourceIdentity,
    string TargetPath,
    bool HasConflict,
    FileConflictItemKind PlannedKind = FileConflictItemKind.File,
    FileConflictItemKind? ExistingTargetKind = null,
    FileConflictResolutionActions AllowedActions = FileConflictResolutionActions.All);

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
        StringComparer pathComparer,
        FileConflictPolicy policy = FileConflictPolicy.Transfer)
    {
        ArgumentNullException.ThrowIfNull(targetExists);

        return Analyze(
            items,
            targetPath => targetExists(targetPath)
                ? FileConflictItemKind.File
                : (FileConflictItemKind?)null,
            pathComparer,
            policy);
    }

    /// <summary>
    /// Detects external and intra-batch collisions while preserving the kind of each destination.
    /// </summary>
    public static IReadOnlyList<FileConflictAnalysisItem> Analyze(
        IReadOnlyList<FileConflictPlanItem> items,
        Func<string, FileConflictItemKind?> getExistingTargetKind,
        StringComparer pathComparer,
        FileConflictPolicy policy = FileConflictPolicy.Transfer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(getExistingTargetKind);
        ArgumentNullException.ThrowIfNull(pathComparer);

        Dictionary<string, FileConflictItemKind> claimedTargets = new(pathComparer);
        List<FileConflictAnalysisItem> analysis = new(items.Count);

        for (int index = 0; index < items.Count; index++)
        {
            FileConflictPlanItem item = items[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.TargetPath);

            FileConflictItemKind? externalKind = getExistingTargetKind(item.TargetPath);
            bool hasClaimedTarget = claimedTargets.TryGetValue(
                item.TargetPath,
                out FileConflictItemKind claimedKind);
            FileConflictResolutionActions allowedActions = FileConflictResolutionActions.All;
            FileConflictItemKind? collisionKind = null;
            bool hasConflict = false;

            ApplyKindCollision(
                item.Kind,
                externalKind,
                ref hasConflict,
                ref allowedActions,
                ref collisionKind,
                policy);
            ApplyKindCollision(
                item.Kind,
                hasClaimedTarget ? claimedKind : null,
                ref hasConflict,
                ref allowedActions,
                ref collisionKind,
                policy);

            claimedTargets.TryAdd(item.TargetPath, item.Kind);
            analysis.Add(new FileConflictAnalysisItem(
                index,
                item.SourceIdentity,
                item.TargetPath,
                hasConflict,
                item.Kind,
                collisionKind ?? externalKind ?? (hasClaimedTarget ? claimedKind : null),
                hasConflict ? allowedActions : FileConflictResolutionActions.None));
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
        HashSet<string> reservedTargets = new(
            analysis.Select(item => item.TargetPath),
            pathComparer);
        List<FileConflictResolvedItem> resolved = new(analysis.Count);
        List<IReadOnlyList<string>> skippedDirectorySegments = [];

        foreach (FileConflictDecision decision in decisions)
        {
            FileConflictAnalysisItem? item = analysis.FirstOrDefault(
                candidate => candidate.Index == decision.ItemIndex);
            if (item is null || !item.HasConflict)
            {
                throw new ArgumentException(
                    $"Decision targets a non-conflicting or unknown item {decision.ItemIndex}.",
                    nameof(decisions));
            }

            if (!Allows(item.AllowedActions, decision.Choice))
            {
                throw new ArgumentException(
                    $"Resolution '{decision.Choice}' is not allowed for conflicting item {item.Index}.",
                    nameof(decisions));
            }

            if (item.PlannedKind == FileConflictItemKind.Directory
                && decision.Choice == FileConflictResolutionChoice.Skip)
            {
                skippedDirectorySegments.Add(GetPathSegments(item.TargetPath));
            }
        }

        foreach (FileConflictAnalysisItem item in analysis)
        {
            if (IsDescendantOfSkippedDirectory(
                item.TargetPath,
                skippedDirectorySegments,
                pathComparer))
            {
                resolved.Add(ToResolved(item, item.TargetPath, FileConflictEffectiveAction.Skip));
                continue;
            }

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

    /// <summary>
    /// Enumerates derived sibling targets in the same order used by
    /// <see cref="Resolve(IReadOnlyList{FileConflictAnalysisItem}, IReadOnlyList{FileConflictDecision}, Func{string, bool}, StringComparer)"/>.
    /// </summary>
    /// <param name="originalTargetPath">Original occupied target path.</param>
    /// <returns>An unbounded sequence beginning with the unnumbered copy suffix.</returns>
    public static IEnumerable<string> EnumerateAutoRenameTargets(string originalTargetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalTargetPath);

        (string directory, string leafName) = SplitTarget(originalTargetPath);
        (string stem, string extension) = SplitNameAndExtension(leafName);

        yield return CombineTarget(directory, $"{stem} (copy){extension}");
        for (int counter = 2; ; counter++)
        {
            yield return CombineTarget(directory, $"{stem} (copy {counter}){extension}");
        }
    }

    private static void ApplyKindCollision(
        FileConflictItemKind plannedKind,
        FileConflictItemKind? existingKind,
        ref bool hasConflict,
        ref FileConflictResolutionActions allowedActions,
        ref FileConflictItemKind? collisionKind,
        FileConflictPolicy policy)
    {
        if (existingKind is null)
        {
            return;
        }

        FileConflictResolutionActions actions = GetAllowedActions(
            plannedKind,
            existingKind.Value,
            policy);
        if (actions == FileConflictResolutionActions.None)
        {
            return;
        }

        hasConflict = true;
        allowedActions &= actions;
        collisionKind ??= existingKind;
    }

    private static FileConflictResolutionActions GetAllowedActions(
        FileConflictItemKind plannedKind,
        FileConflictItemKind existingKind,
        FileConflictPolicy policy)
        => (policy, plannedKind, existingKind) switch
        {
            (FileConflictPolicy.Transfer, FileConflictItemKind.File, FileConflictItemKind.File) =>
                FileConflictResolutionActions.All,
            (FileConflictPolicy.Rename, FileConflictItemKind.File, FileConflictItemKind.File) =>
                FileConflictResolutionActions.All,
            (FileConflictPolicy.Transfer, FileConflictItemKind.Directory, FileConflictItemKind.Directory) =>
                FileConflictResolutionActions.None,
            (FileConflictPolicy.Transfer, FileConflictItemKind.File, FileConflictItemKind.Directory) =>
                FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename,
            (FileConflictPolicy.Transfer, FileConflictItemKind.Directory, FileConflictItemKind.File) =>
                FileConflictResolutionActions.Skip,
            (FileConflictPolicy.Rename, _, _) =>
                FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

    private static bool Allows(
        FileConflictResolutionActions allowedActions,
        FileConflictResolutionChoice choice)
    {
        FileConflictResolutionActions requestedAction = choice switch
        {
            FileConflictResolutionChoice.Skip => FileConflictResolutionActions.Skip,
            FileConflictResolutionChoice.Replace => FileConflictResolutionActions.Replace,
            FileConflictResolutionChoice.AutoRename => FileConflictResolutionActions.AutoRename,
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown conflict resolution."),
        };

        return (allowedActions & requestedAction) == requestedAction;
    }

    private static bool IsDescendantOfSkippedDirectory(
        string targetPath,
        IReadOnlyList<IReadOnlyList<string>> skippedDirectorySegments,
        StringComparer pathComparer)
    {
        IReadOnlyList<string> targetSegments = GetPathSegments(targetPath);
        foreach (IReadOnlyList<string> directorySegments in skippedDirectorySegments)
        {
            if (targetSegments.Count <= directorySegments.Count)
            {
                continue;
            }

            bool segmentsMatch = true;
            for (int index = 0; index < directorySegments.Count; index++)
            {
                if (!pathComparer.Equals(targetSegments[index], directorySegments[index]))
                {
                    segmentsMatch = false;
                    break;
                }
            }

            if (segmentsMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetPathSegments(string path)
        => path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

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
        foreach (string candidate in EnumerateAutoRenameTargets(originalTargetPath))
        {
            if (IsAvailable(candidate, reservedTargets, targetExists))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The auto-rename candidate sequence ended unexpectedly.");
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
