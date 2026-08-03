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

using System.IO;

namespace Heimdall.App.Services;

/// <summary>The kind of operation a <see cref="LocalPasteOp"/> represents.</summary>
public enum LocalPasteOpKind
{
    /// <summary>Create a local directory before processing its children.</summary>
    CreateDirectory,

    /// <summary>Copy a single local file to its target path.</summary>
    CopyFile,
}

/// <summary>One ordered step of a local paste.</summary>
/// <param name="Kind">Whether this operation creates a directory or copies a file.</param>
/// <param name="SourcePath">The full path of the source entry.</param>
/// <param name="TargetPath">The full path where the entry will be created or copied.</param>
public sealed record LocalPasteOp(LocalPasteOpKind Kind, string SourcePath, string TargetPath);

/// <summary>A local entry: a pasted root, or a child discovered while walking a directory.</summary>
/// <param name="FullPath">The full path of the source entry.</param>
/// <param name="Name">The leaf name used to build the target path.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="IsReparsePoint">Whether the entry is a filesystem reparse point.</param>
public sealed record LocalPasteEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    bool IsReparsePoint = false);

/// <summary>
/// Pure planner that flattens pasted local entries into ordered operations without accessing the filesystem.
/// </summary>
public static class LocalPasteTreePlanner
{
    /// <summary>Maximum accepted entry depth before planning aborts to contain possible junction loops.</summary>
    public const int MaxCopyDepth = 256;

    /// <summary>
    /// Determines lexically whether a target directory is the source path or one of its descendants,
    /// as required by product decision 7.8. Link aliases are outside the scope of this check.
    /// </summary>
    /// <param name="sourcePath">The source directory path.</param>
    /// <param name="targetDirectory">The candidate target directory path.</param>
    /// <returns><see langword="true"/> when the normalized target is the source or its descendant.</returns>
    public static bool IsSameOrDescendantPath(string sourcePath, string targetDirectory)
    {
        string normalizedSource = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedTarget = Path.GetFullPath(targetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string sourcePrefix = normalizedSource + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds an ordered, depth-first local paste plan with directories emitted before their children.</summary>
    /// <param name="roots">The pasted top-level entries, processed in their supplied order.</param>
    /// <param name="targetDirectory">The directory into which the roots will be pasted.</param>
    /// <param name="enumerateChildren">Returns the immediate children of a source directory.</param>
    /// <returns>The complete ordered paste plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roots"/> or <paramref name="enumerateChildren"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetDirectory"/> is null, empty, or whitespace.</exception>
    /// <exception cref="IOException">The entry depth reaches <see cref="MaxCopyDepth"/>.</exception>
    public static IReadOnlyList<LocalPasteOp> Plan(
        IReadOnlyList<LocalPasteEntry> roots,
        string targetDirectory,
        Func<string, IReadOnlyList<LocalPasteEntry>> enumerateChildren)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(enumerateChildren);

        List<LocalPasteOp> operations = [];

        foreach (LocalPasteEntry root in roots)
        {
            AppendEntry(root, targetDirectory, enumerateChildren, depth: 0, isRoot: true, operations);
        }

        return operations;
    }

    private static void AppendEntry(
        LocalPasteEntry entry,
        string parentTargetDirectory,
        Func<string, IReadOnlyList<LocalPasteEntry>> enumerateChildren,
        int depth,
        bool isRoot,
        List<LocalPasteOp> operations)
    {
        // Root reparse points intentionally remain traversable to preserve current behavior.
        // SFTP-015 owns root-level containment and must resolve this known asymmetry.
        if (!isRoot && entry.IsDirectory && entry.IsReparsePoint)
        {
            return;
        }

        if (depth >= MaxCopyDepth)
        {
            throw new IOException(
                $"Directory copy aborted: nesting exceeds {MaxCopyDepth} levels (possible junction loop).");
        }

        string targetPath = Path.Combine(parentTargetDirectory, entry.Name);

        if (!entry.IsDirectory)
        {
            operations.Add(new LocalPasteOp(LocalPasteOpKind.CopyFile, entry.FullPath, targetPath));
            return;
        }

        operations.Add(new LocalPasteOp(LocalPasteOpKind.CreateDirectory, entry.FullPath, targetPath));

        foreach (LocalPasteEntry child in enumerateChildren(entry.FullPath))
        {
            AppendEntry(child, targetPath, enumerateChildren, depth + 1, isRoot: false, operations);
        }
    }
}
