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

namespace Heimdall.Sftp;

/// <summary>The kind of operation a <see cref="RemoteUploadOp"/> represents.</summary>
public enum RemoteUploadOpKind
{
    /// <summary>Create a remote directory (parents are always emitted before their children).</summary>
    MakeDirectory,

    /// <summary>Upload a single local file to a remote path.</summary>
    UploadFile,
}

/// <summary>
/// A single ordered step of an incoming upload tree, produced by <see cref="RemoteUploadTreePlanner"/>.
/// </summary>
/// <param name="Kind">Whether this op creates a directory or uploads a file.</param>
/// <param name="LocalPath">
/// The local source path. For <see cref="RemoteUploadOpKind.UploadFile"/> this is the file to read;
/// for <see cref="RemoteUploadOpKind.MakeDirectory"/> it is the originating local directory (informational).
/// </param>
/// <param name="RemotePath">The remote destination path (file path for uploads, directory path for mkdir).</param>
public sealed record RemoteUploadOp(RemoteUploadOpKind Kind, string LocalPath, string RemotePath);

/// <summary>
/// A local filesystem entry (a dropped root or a child discovered while walking a directory).
/// </summary>
/// <param name="FullPath">Absolute local path of the entry.</param>
/// <param name="Name">Leaf name of the entry, used as the remote child segment.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
public sealed record LocalUploadEntry(string FullPath, string Name, bool IsDirectory);

/// <summary>
/// Pure planner that flattens dropped local files and directories into an ordered list of remote
/// operations for an incoming (upload) transfer. Mirrors <see cref="RemoteCopyPlanner"/>: all
/// filesystem access is delegated through <paramref name="enumerateChildren"/>, so the planner itself
/// performs no I/O and is fully unit-testable with an in-memory tree.
/// </summary>
/// <remarks>
/// Each produced remote path is the target directory joined with the entry's path relative to the
/// drop, so dropping a folder <c>proj</c> onto target <c>T</c> yields <c>T/proj</c>, <c>T/proj/sub</c>,
/// <c>T/proj/sub/a.txt</c>, and so on. Directories are always emitted before any of their children.
/// </remarks>
public static class RemoteUploadTreePlanner
{
    /// <summary>
    /// Hard cap on upload recursion depth, mirroring <see cref="RemoteCopyPlanner.MaxCopyDepth"/>, so a
    /// pathological local tree cannot drive unbounded recursion.
    /// </summary>
    public const int MaxUploadDepth = 256;

    /// <summary>
    /// Builds the ordered upload plan for <paramref name="roots"/> into <paramref name="targetRemoteDir"/>.
    /// </summary>
    /// <param name="roots">The dropped top-level entries (files and/or directories).</param>
    /// <param name="targetRemoteDir">The remote directory the drop lands in.</param>
    /// <param name="enumerateChildren">
    /// Returns the immediate children of a local directory (by its <see cref="LocalUploadEntry.FullPath"/>).
    /// </param>
    /// <param name="ct">Observed before every recursion step, so a deep or slow tree stays interruptible.</param>
    /// <exception cref="IOException">A directory child carries an unsafe name, or recursion exceeds <see cref="MaxUploadDepth"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <remarks>
    /// The planner performs no offloading of its own: <paramref name="enumerateChildren"/> owns the
    /// decision to leave the calling thread, so this unit stays pure and testable in memory.
    /// </remarks>
    public static async Task<IReadOnlyList<RemoteUploadOp>> PlanAsync(
        IReadOnlyList<LocalUploadEntry> roots,
        string targetRemoteDir,
        Func<string, CancellationToken, Task<IReadOnlyList<LocalUploadEntry>>> enumerateChildren,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemoteDir);
        ArgumentNullException.ThrowIfNull(enumerateChildren);

        var ops = new List<RemoteUploadOp>();

        foreach (LocalUploadEntry root in roots)
        {
            await AppendEntryAsync(root, targetRemoteDir, enumerateChildren, depth: 0, ops, ct);
        }

        return ops;
    }

    private static async Task AppendEntryAsync(
        LocalUploadEntry entry,
        string parentRemoteDir,
        Func<string, CancellationToken, Task<IReadOnlyList<LocalUploadEntry>>> enumerateChildren,
        int depth,
        List<RemoteUploadOp> ops,
        CancellationToken ct)
    {
        // Observed before the depth cap so a pathological tree cannot outrun cancellation.
        ct.ThrowIfCancellationRequested();

        if (depth > MaxUploadDepth)
        {
            throw new IOException(
                $"Refused to upload '{entry.FullPath}': local directory depth exceeds {MaxUploadDepth}.");
        }

        if (!SftpPathGuard.IsValidChildName(entry.Name))
        {
            throw new IOException(
                $"Refused to upload entry with unsafe name '{entry.Name}' into '{parentRemoteDir}'.");
        }

        string remotePath = RemoteCopyPlanner.JoinRemote(parentRemoteDir, entry.Name);

        if (!entry.IsDirectory)
        {
            ops.Add(new RemoteUploadOp(RemoteUploadOpKind.UploadFile, entry.FullPath, remotePath));
            return;
        }

        // Parent directory first, then its children (depth-first, pre-order).
        ops.Add(new RemoteUploadOp(RemoteUploadOpKind.MakeDirectory, entry.FullPath, remotePath));

        foreach (LocalUploadEntry child in await enumerateChildren(entry.FullPath, ct))
        {
            await AppendEntryAsync(child, remotePath, enumerateChildren, depth + 1, ops, ct);
        }
    }
}
