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

/// <summary>
/// Transport primitives the <see cref="RemoteCopyPlanner"/> drives to perform a non-overwriting,
/// recursive remote copy. Each delegate is supplied by a concrete browser (SFTP or FTP) and wraps a
/// single locked transport call, so the planner itself stays pure and free of any I/O or locking.
/// </summary>
/// <param name="DestinationExistsAsync">Returns whether the destination path already exists.</param>
/// <param name="SourceIsDirectoryAsync">Returns whether the source path is a directory.</param>
/// <param name="ListChildNamesAsync">Lists the leaf names of a source directory's entries.</param>
/// <param name="CopyFileAsync">Copies a single file from source to destination.</param>
/// <param name="CreateDirectoryAsync">Creates a destination directory.</param>
internal sealed record RemoteCopyOps(
    Func<string, CancellationToken, Task<bool>> DestinationExistsAsync,
    Func<string, CancellationToken, Task<bool>> SourceIsDirectoryAsync,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>> ListChildNamesAsync,
    Func<string, string, CancellationToken, Task> CopyFileAsync,
    Func<string, CancellationToken, Task> CreateDirectoryAsync);

/// <summary>
/// Pure orchestrator for a same-server remote copy. Encodes the copy contract shared by
/// <see cref="SftpBrowser"/> and <see cref="FtpBrowser"/>: never overwrite an existing destination,
/// require <c>recursive</c> for directory sources, and fan a directory copy out child by child. All
/// transport work is delegated through <see cref="RemoteCopyOps"/>, so this type performs no I/O and
/// is fully unit-testable with in-memory fakes (mirrors <see cref="SftpAtomicUpload"/>).
/// </summary>
internal static class RemoteCopyPlanner
{
    /// <summary>
    /// Hard cap on copy recursion depth, so a corrupted or hostile remote tree cannot drive
    /// unbounded recursion.
    /// </summary>
    internal const int MaxCopyDepth = 256;

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/> on the same server.
    /// The destination must not already exist; a directory source requires <paramref name="recursive"/>.
    /// </summary>
    /// <exception cref="IOException">
    /// The destination is the source or its descendant, the destination already exists, or the source
    /// is a directory and <paramref name="recursive"/> is false.
    /// </exception>
    public static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        RemoteCopyOps ops,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(ops);

        ct.ThrowIfCancellationRequested();

        if (IsSameOrDescendantPath(sourcePath, destinationPath))
        {
            throw new IOException(
                $"Refused to copy: destination is the source path or its descendant: {destinationPath}");
        }

        if (await ops.DestinationExistsAsync(destinationPath, ct).ConfigureAwait(false))
        {
            throw new IOException($"Refused to copy: destination already exists: {destinationPath}");
        }

        await CopyNodeAsync(sourcePath, destinationPath, recursive, ops, depth: 0, ct)
            .ConfigureAwait(false);
    }

    private static async Task CopyNodeAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        RemoteCopyOps ops,
        int depth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (depth > MaxCopyDepth)
        {
            throw new InvalidOperationException(
                $"Refused to copy '{sourcePath}': remote directory depth exceeds {MaxCopyDepth}.");
        }

        bool isDirectory = await ops.SourceIsDirectoryAsync(sourcePath, ct).ConfigureAwait(false);
        if (!isDirectory)
        {
            await ops.CopyFileAsync(sourcePath, destinationPath, ct).ConfigureAwait(false);
            return;
        }

        if (!recursive)
        {
            throw new IOException("Source is a directory; set recursive=true.");
        }

        await ops.CreateDirectoryAsync(destinationPath, ct).ConfigureAwait(false);

        IReadOnlyList<string> children =
            await ops.ListChildNamesAsync(sourcePath, ct).ConfigureAwait(false);

        foreach (string childName in children)
        {
            ct.ThrowIfCancellationRequested();

            if (!SftpPathGuard.IsValidChildName(childName))
            {
                throw new IOException(
                    $"Refused to copy child with unsafe name under '{sourcePath}'.");
            }

            string childSource = JoinRemote(sourcePath, childName);
            string childDestination = JoinRemote(destinationPath, childName);

            await CopyNodeAsync(childSource, childDestination, recursive: true, ops, depth + 1, ct)
                .ConfigureAwait(false);
        }
    }

    internal static bool IsSameOrDescendantPath(string sourcePath, string destinationPath)
    {
        string normalizedSource = sourcePath.TrimEnd('/');
        string normalizedDestination = destinationPath.TrimEnd('/');

        // Trimming the remote root produces an empty string. Reject it explicitly because every
        // remote destination is within the server root.
        if (normalizedSource.Length == 0)
        {
            return true;
        }

        return string.Equals(normalizedDestination, normalizedSource, StringComparison.Ordinal)
            || normalizedDestination.StartsWith(
                $"{normalizedSource}/",
                StringComparison.Ordinal);
    }

    // Joins a remote directory path and a child leaf name using forward slashes, matching the
    // separator convention already used by SftpBrowser and FtpBrowser listings.
    internal static string JoinRemote(string directory, string childName)
        => $"{directory.TrimEnd('/')}/{childName}";
}

/// <summary>
/// Allocates and removes the throwaway local staging file used to copy a remote file by roundtrip
/// (remote download to local temp to remote upload). Kept separate from <see cref="RemoteCopyPlanner"/>
/// so the planner stays free of filesystem I/O.
/// </summary>
internal static class RemoteCopyLocalTemp
{
    /// <summary>Creates a unique local staging file path under the per-user temp directory.</summary>
    public static string Create()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Heimdall", "copy");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N"));
    }

    /// <summary>Best-effort deletion of a staging file; never throws into the copy path.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[RemoteCopyLocalTemp] failed to delete copy staging file '{path}': {ex.Message}");
        }
    }
}
