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
/// Coordinates FTP temp-path uploads and recoverable replacement without depending on a live server.
/// </summary>
/// <remarks>
/// Recoverable, not non-destructive: an existing destination is moved aside, replaced, and its
/// backup deleted once the commit succeeds. The backup exists so a failed commit can be undone,
/// not so the previous file survives a successful one. Replacement is intentional here; the copy
/// path, which must never overwrite, cannot be built on this and refuses instead.
/// </remarks>
public static class FtpAtomicUpload
{
    /// <summary>
    /// Replaces the final remote path with the uploaded temp path while preserving an existing target
    /// through a recoverable backup move.
    /// </summary>
    /// <param name="onExistingTargetReplaced">
    /// Raised exactly once, and only once an existing destination has actually been replaced: after
    /// the commit move succeeded, and never when the destination was absent, when the backup move
    /// failed, or when the commit failed and the backup was restored. The callback reports a
    /// completed fact, so it is non-blocking by contract - an exception thrown by a subscriber is
    /// logged and contained, never allowed to fail a replacement that already succeeded.
    /// </param>
    public static async Task CommitRenameAsync(
        string tempRemotePath,
        string finalRemotePath,
        Func<string, CancellationToken, Task<bool>> remoteExistsAsync,
        Func<string, string, CancellationToken, Task<bool>> moveRemoteAsync,
        Func<string, CancellationToken, Task> deleteRemoteAsync,
        CancellationToken ct = default,
        Action? onExistingTargetReplaced = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRemotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);
        ArgumentNullException.ThrowIfNull(remoteExistsAsync);
        ArgumentNullException.ThrowIfNull(moveRemoteAsync);
        ArgumentNullException.ThrowIfNull(deleteRemoteAsync);

        string? backupRemotePath = null;
        if (await remoteExistsAsync(finalRemotePath, ct).ConfigureAwait(false))
        {
            backupRemotePath = CreateRemoteBackupPath(finalRemotePath);
            Heimdall.Core.Logging.FileLogger.Warn(
                $"FTP replacement for '{finalRemotePath}' is not atomic; moving the existing target "
                + $"to backup '{backupRemotePath}' before commit.");
            bool backupMoved = await moveRemoteAsync(finalRemotePath, backupRemotePath, ct)
                .ConfigureAwait(false);
            if (!backupMoved)
            {
                throw new IOException(
                    $"FTP upload backup move returned false for '{finalRemotePath}'.");
            }
        }

        try
        {
            bool tempMoved = await moveRemoteAsync(tempRemotePath, finalRemotePath, ct)
                .ConfigureAwait(false);
            if (!tempMoved)
            {
                throw new IOException(
                    $"FTP upload commit move returned false for '{finalRemotePath}'.");
            }
        }
        catch (Exception renameEx)
        {
            await RestoreBackupAsync(
                finalRemotePath,
                backupRemotePath,
                moveRemoteAsync,
                remoteExistsAsync,
                deleteRemoteAsync,
                renameEx).ConfigureAwait(false);
            throw;
        }

        // Only here is the replacement a fact. Anything earlier would announce a destruction that
        // a failed backup move never started, or that a failed commit has just undone.
        if (backupRemotePath is not null)
        {
            RaiseExistingTargetReplaced(onExistingTargetReplaced, finalRemotePath);
        }

        await CleanupBackupAsync(backupRemotePath, remoteExistsAsync, deleteRemoteAsync)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Invokes the replacement notification without ever letting a subscriber alter the outcome.
    /// </summary>
    /// <remarks>
    /// The destination already holds the new content when this runs. Propagating a subscriber's
    /// exception would report a successful replacement as a failure, and in
    /// <c>FtpBrowser.UploadFileAsync</c> it would additionally trigger the temp-file rollback for
    /// an upload that has just been published, so it is logged and contained instead.
    /// </remarks>
    private static void RaiseExistingTargetReplaced(
        Action? onExistingTargetReplaced,
        string finalRemotePath)
    {
        if (onExistingTargetReplaced is null)
        {
            return;
        }

        try
        {
            onExistingTargetReplaced();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"FTP replacement warning subscriber threw for '{finalRemotePath}': {ex.Message}");
        }
    }

    private static string CreateRemoteBackupPath(string finalRemotePath)
    {
        return $"{finalRemotePath}.{Guid.NewGuid():N}.bak";
    }

    private static async Task RestoreBackupAsync(
        string finalRemotePath,
        string? backupRemotePath,
        Func<string, string, CancellationToken, Task<bool>> moveRemoteAsync,
        Func<string, CancellationToken, Task<bool>> remoteExistsAsync,
        Func<string, CancellationToken, Task> deleteRemoteAsync,
        Exception renameException)
    {
        if (backupRemotePath is null)
        {
            return;
        }

        try
        {
            if (await remoteExistsAsync(finalRemotePath, CancellationToken.None).ConfigureAwait(false))
            {
                await deleteRemoteAsync(finalRemotePath, CancellationToken.None).ConfigureAwait(false);
            }

            bool backupRestored = await moveRemoteAsync(
                backupRemotePath,
                finalRemotePath,
                CancellationToken.None).ConfigureAwait(false);
            if (!backupRestored)
            {
                throw new IOException(
                    $"FTP upload backup restore returned false for '{finalRemotePath}'.");
            }
        }
        catch (Exception restoreEx)
        {
            throw new InvalidOperationException(
                $"FTP upload commit failed and restoring backup '{backupRemotePath}' failed.",
                new AggregateException(renameException, restoreEx));
        }
    }

    private static async Task CleanupBackupAsync(
        string? backupRemotePath,
        Func<string, CancellationToken, Task<bool>> remoteExistsAsync,
        Func<string, CancellationToken, Task> deleteRemoteAsync)
    {
        if (backupRemotePath is null)
        {
            return;
        }

        try
        {
            if (await remoteExistsAsync(backupRemotePath, CancellationToken.None).ConfigureAwait(false))
            {
                await deleteRemoteAsync(backupRemotePath, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"FTP upload backup cleanup failed for '{backupRemotePath}': {ex.Message}");
        }
    }
}
