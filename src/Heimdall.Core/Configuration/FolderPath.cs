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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Describes why a requested folder rename is invalid.
/// </summary>
public enum FolderRenameValidationError
{
    None,
    InvalidSegment,
    SiblingCollision
}

/// <summary>
/// Immutable mapping from one folder path to another.
/// </summary>
public sealed record FolderRenamePlan(string OldPath, string NewPath)
{
    /// <summary>
    /// Rewrites the source folder or one of its descendants while leaving prefix
    /// siblings unchanged.
    /// </summary>
    public string Rewrite(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FolderPath.IsSelfOrDescendant(path, OldPath)
            ? NewPath + path[OldPath.Length..]
            : path;
    }
}

/// <summary>
/// Canonical rules for path-based server folders.
/// </summary>
public static class FolderPath
{
    private const char Separator = '/';
    private const char AlternateSeparator = '\\';

    /// <summary>
    /// Validates a single-segment leaf name and creates the corresponding path
    /// migration plan.
    /// </summary>
    public static bool TryCreateRename(
        string oldPath,
        string? newLeafName,
        IEnumerable<string?> existingPaths,
        out FolderRenamePlan? plan,
        out FolderRenameValidationError error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentNullException.ThrowIfNull(existingPaths);

        string segment = newLeafName?.Trim() ?? string.Empty;
        if (segment.Length == 0 ||
            segment.IndexOfAny([Separator, AlternateSeparator]) >= 0 ||
            segment.Any(char.IsControl))
        {
            plan = null;
            error = FolderRenameValidationError.InvalidSegment;
            return false;
        }

        int lastSeparator = oldPath.LastIndexOf(Separator);
        string parentPath = lastSeparator >= 0 ? oldPath[..lastSeparator] : string.Empty;
        string newPath = parentPath.Length == 0
            ? segment
            : $"{parentPath}{Separator}{segment}";

        foreach (string? candidate in existingPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                IsSelfOrDescendant(candidate, oldPath))
            {
                continue;
            }

            if (IsSelfOrDescendant(candidate, newPath))
            {
                plan = null;
                error = FolderRenameValidationError.SiblingCollision;
                return false;
            }
        }

        plan = new FolderRenamePlan(oldPath, newPath);
        error = FolderRenameValidationError.None;
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> is the supplied folder or one of
    /// its descendants. Segment boundaries make the comparison prefix-safe.
    /// </summary>
    public static bool IsSelfOrDescendant(string path, string ancestorPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(ancestorPath);

        return path.Equals(ancestorPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(
                   $"{ancestorPath}{Separator}",
                   StringComparison.OrdinalIgnoreCase);
    }
}
