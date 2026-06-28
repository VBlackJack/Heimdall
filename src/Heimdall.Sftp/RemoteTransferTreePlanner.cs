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

/// <summary>The kind of operation a <see cref="RemoteTransferOp"/> represents.</summary>
public enum RemoteTransferOpKind
{
    /// <summary>Create a destination remote directory.</summary>
    MakeDirectory,

    /// <summary>Transfer a single source remote file to a destination remote path.</summary>
    TransferFile,
}

/// <summary>
/// A single ordered step of a cross-endpoint remote transfer plan.
/// </summary>
/// <param name="Kind">Whether this step creates a directory or transfers a file.</param>
/// <param name="SourceRemotePath">
/// The source remote path. For <see cref="RemoteTransferOpKind.TransferFile"/> this is the file to
/// download; for <see cref="RemoteTransferOpKind.MakeDirectory"/> it is the originating source
/// directory, informational only.
/// </param>
/// <param name="DestinationRemotePath">The destination remote path.</param>
public sealed record RemoteTransferOp(
    RemoteTransferOpKind Kind,
    string SourceRemotePath,
    string DestinationRemotePath);

/// <summary>
/// Pure planner that flattens remote source files and directories into ordered cross-endpoint
/// transfer operations. All source listing I/O is delegated through the <c>listDirectory</c> callback,
/// so the planner is testable with an in-memory tree and works for SFTP and FTP browsers alike.
/// </summary>
public static class RemoteTransferTreePlanner
{
    /// <summary>Hard cap on remote source recursion depth.</summary>
    public const int MaxTransferDepth = RemoteUploadTreePlanner.MaxUploadDepth;

    /// <summary>
    /// Builds the ordered cross-endpoint transfer plan for <paramref name="roots"/>.
    /// </summary>
    /// <param name="roots">The captured source entries.</param>
    /// <param name="targetRemoteDir">The destination directory.</param>
    /// <param name="listDirectory">Lists immediate children of a remote source directory.</param>
    /// <exception cref="IOException">An entry name is unsafe, or recursion exceeds <see cref="MaxTransferDepth"/>.</exception>
    public static async Task<IReadOnlyList<RemoteTransferOp>> PlanAsync(
        IReadOnlyList<SftpFileInfo> roots,
        string targetRemoteDir,
        Func<string, CancellationToken, Task<IReadOnlyList<SftpFileInfo>>> listDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemoteDir);
        ArgumentNullException.ThrowIfNull(listDirectory);

        var ops = new List<RemoteTransferOp>();
        foreach (SftpFileInfo root in roots)
        {
            await AppendEntryAsync(root, targetRemoteDir, listDirectory, depth: 0, ops, ct)
                .ConfigureAwait(false);
        }

        return ops;
    }

    private static async Task AppendEntryAsync(
        SftpFileInfo entry,
        string parentRemoteDir,
        Func<string, CancellationToken, Task<IReadOnlyList<SftpFileInfo>>> listDirectory,
        int depth,
        List<RemoteTransferOp> ops,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (depth > MaxTransferDepth)
        {
            throw new IOException(
                $"Refused to transfer '{entry.FullPath}': remote directory depth exceeds {MaxTransferDepth}.");
        }

        if (!SftpPathGuard.IsValidChildName(entry.Name))
        {
            throw new IOException(
                $"Refused to transfer entry with unsafe name '{entry.Name}' into '{parentRemoteDir}'.");
        }

        string remotePath = RemoteCopyPlanner.JoinRemote(parentRemoteDir, entry.Name);
        if (!entry.IsDirectory)
        {
            ops.Add(new RemoteTransferOp(RemoteTransferOpKind.TransferFile, entry.FullPath, remotePath));
            return;
        }

        ops.Add(new RemoteTransferOp(RemoteTransferOpKind.MakeDirectory, entry.FullPath, remotePath));

        IReadOnlyList<SftpFileInfo> children = await listDirectory(entry.FullPath, ct).ConfigureAwait(false);
        foreach (SftpFileInfo child in children)
        {
            await AppendEntryAsync(child, remotePath, listDirectory, depth + 1, ops, ct)
                .ConfigureAwait(false);
        }
    }
}
