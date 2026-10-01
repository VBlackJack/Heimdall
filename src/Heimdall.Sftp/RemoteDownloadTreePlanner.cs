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

/// <summary>The kind of operation a <see cref="RemoteDownloadOp"/> represents.</summary>
public enum RemoteDownloadOpKind
{
    /// <summary>Create a local directory (parents are always emitted before their children).</summary>
    MakeDirectory,

    /// <summary>Download a single remote file to a local path.</summary>
    DownloadFile,
}

/// <summary>
/// A single ordered step of an outgoing (download) tree, produced by <see cref="RemoteDownloadTreePlanner"/>.
/// </summary>
/// <param name="Kind">Whether this op creates a directory or downloads a file.</param>
/// <param name="RemotePath">The remote source path (the originating directory, informational, for a directory).</param>
/// <param name="LocalPath">The local destination path (file path for downloads, directory path for mkdir).</param>
/// <param name="Size">The remote file size in bytes; zero for a directory.</param>
/// <param name="LastModified">The remote modification time of a file, so a collision can be judged by date.</param>
public sealed record RemoteDownloadOp(
    RemoteDownloadOpKind Kind,
    string RemotePath,
    string LocalPath,
    long Size,
    DateTime LastModified = default);

/// <summary>
/// The ordered operations of a recursive download and what the walk deliberately left out.
/// </summary>
/// <param name="Ops">The ordered local operations.</param>
/// <param name="SkippedUnsupportedPaths">Remote paths that are neither files nor directories (links, devices, pipes).</param>
/// <param name="SkippedUnsafeNames">Remote paths whose name cannot be written safely below the destination.</param>
public sealed record RemoteDownloadPlan(
    IReadOnlyList<RemoteDownloadOp> Ops,
    IReadOnlyList<string> SkippedUnsupportedPaths,
    IReadOnlyList<string> SkippedUnsafeNames);

/// <summary>
/// Pure planner that flattens remote files and directories into ordered local operations for a
/// recursive download. It mirrors <see cref="RemoteUploadTreePlanner"/>: every remote listing is
/// delegated through a callback and the local containment rule through another, so the planner
/// performs no I/O of its own and is testable with an in-memory tree.
/// </summary>
/// <remarks>
/// Symbolic links are never followed: a link is reported as unsupported, which both keeps a link
/// cycle from recursing and keeps the download confined to the tree the user selected. Directories
/// are always emitted before any of their children.
/// </remarks>
public static class RemoteDownloadTreePlanner
{
    /// <summary>Hard cap on download recursion depth, shared with the upload and cross-endpoint planners.</summary>
    public const int MaxDownloadDepth = RemoteUploadTreePlanner.MaxUploadDepth;

    /// <summary>
    /// Whether one remote entry can be downloaded: a regular file, or a directory with its content.
    /// </summary>
    /// <remarks>
    /// The single answer to that question, shared by the walk and by the menu that offers the
    /// action, so the offer and the outcome cannot drift apart. Links, pipes, sockets and devices
    /// are not byte-addressable (and a link may loop), so they are never downloaded.
    /// </remarks>
    public static bool IsDownloadable(SftpFileInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind is RemoteEntryKind.File or RemoteEntryKind.Directory;
    }

    /// <summary>Builds the ordered download plan for <paramref name="roots"/> into <paramref name="targetLocalDirectory"/>.</summary>
    /// <param name="roots">The selected remote entries.</param>
    /// <param name="targetLocalDirectory">The local folder the selection lands in.</param>
    /// <param name="listDirectory">Lists the immediate children of a remote directory.</param>
    /// <param name="resolveLocalChild">
    /// Returns the local path of a child named <c>childName</c> directly below
    /// <c>parentLocalDirectory</c>, or <see langword="null"/> when that name cannot be written
    /// safely there. This is where the local containment rule lives.
    /// </param>
    /// <param name="onEntryPlanned">Optional report of how many entries the walk has planned so far.</param>
    /// <param name="ct">Observed before every step, so a deep or slow tree stays interruptible.</param>
    /// <exception cref="IOException">Recursion exceeds <see cref="MaxDownloadDepth"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<RemoteDownloadPlan> PlanAsync(
        IReadOnlyList<SftpFileInfo> roots,
        string targetLocalDirectory,
        Func<string, CancellationToken, Task<IReadOnlyList<SftpFileInfo>>> listDirectory,
        Func<string, string, string?> resolveLocalChild,
        Action<int>? onEntryPlanned = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocalDirectory);
        ArgumentNullException.ThrowIfNull(listDirectory);
        ArgumentNullException.ThrowIfNull(resolveLocalChild);

        List<RemoteDownloadOp> ops = [];
        List<string> skippedUnsupported = [];
        List<string> skippedUnsafe = [];

        foreach (SftpFileInfo root in roots)
        {
            await AppendEntryAsync(
                    root,
                    targetLocalDirectory,
                    listDirectory,
                    resolveLocalChild,
                    depth: 0,
                    ops,
                    skippedUnsupported,
                    skippedUnsafe,
                    onEntryPlanned,
                    ct)
                .ConfigureAwait(false);
        }

        return new RemoteDownloadPlan(ops, skippedUnsupported, skippedUnsafe);
    }

    private static async Task AppendEntryAsync(
        SftpFileInfo entry,
        string parentLocalDirectory,
        Func<string, CancellationToken, Task<IReadOnlyList<SftpFileInfo>>> listDirectory,
        Func<string, string, string?> resolveLocalChild,
        int depth,
        List<RemoteDownloadOp> ops,
        List<string> skippedUnsupported,
        List<string> skippedUnsafe,
        Action<int>? onEntryPlanned,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsDownloadable(entry))
        {
            skippedUnsupported.Add(entry.FullPath);
            return;
        }

        if (depth > MaxDownloadDepth)
        {
            throw new IOException(
                $"Refused to download '{entry.FullPath}': remote directory depth exceeds {MaxDownloadDepth}.");
        }

        string? localPath = SftpPathGuard.IsValidChildName(entry.Name)
            ? resolveLocalChild(parentLocalDirectory, entry.Name)
            : null;
        if (localPath is null)
        {
            skippedUnsafe.Add(entry.FullPath);
            return;
        }

        if (!entry.IsDirectory)
        {
            ops.Add(new RemoteDownloadOp(
                RemoteDownloadOpKind.DownloadFile,
                entry.FullPath,
                localPath,
                entry.Size,
                entry.LastModified));
            onEntryPlanned?.Invoke(ops.Count);
            return;
        }

        ops.Add(new RemoteDownloadOp(RemoteDownloadOpKind.MakeDirectory, entry.FullPath, localPath, 0));
        onEntryPlanned?.Invoke(ops.Count);

        IReadOnlyList<SftpFileInfo> children = await listDirectory(entry.FullPath, ct).ConfigureAwait(false);
        foreach (SftpFileInfo child in children)
        {
            await AppendEntryAsync(
                    child,
                    localPath,
                    listDirectory,
                    resolveLocalChild,
                    depth + 1,
                    ops,
                    skippedUnsupported,
                    skippedUnsafe,
                    onEntryPlanned,
                    ct)
                .ConfigureAwait(false);
        }
    }
}
