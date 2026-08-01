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
/// Coordinates FTP temp-path uploads and non-destructive replacement without depending on a live server.
/// </summary>
public static class FtpAtomicUpload
{
    /// <summary>
    /// Replaces the final remote path with the uploaded temp path while preserving an existing target
    /// through a recoverable backup move.
    /// </summary>
    public static async Task CommitRenameAsync(
        string tempRemotePath,
        string finalRemotePath,
        Func<string, CancellationToken, Task<bool>> remoteExistsAsync,
        Func<string, string, CancellationToken, Task<bool>> moveRemoteAsync,
        Func<string, CancellationToken, Task> deleteRemoteAsync,
        CancellationToken ct = default)
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

        await CleanupBackupAsync(backupRemotePath, remoteExistsAsync, deleteRemoteAsync)
            .ConfigureAwait(false);
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
