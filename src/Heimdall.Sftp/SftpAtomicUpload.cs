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

/// <summary>
/// Coordinates temp-path uploads and remote replacement without depending on a live SFTP server.
/// </summary>
public static class SftpAtomicUpload
{
    /// <summary>
    /// Creates a unique temporary remote path next to the final remote path.
    /// </summary>
    public static string CreateRemoteTempPath(string finalRemotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);

        return $"{finalRemotePath}.{Guid.NewGuid():N}.part";
    }

    /// <summary>
    /// Publishes the uploaded temp path only when the final remote path is absent.
    /// </summary>
    /// <remarks>
    /// The re-probe classifies the error message only and is deliberately not part of the data path; it can be
    /// wrong under concurrency, which is harmless because the rename has already decided the outcome.
    /// </remarks>
    /// <param name="tempRemotePath">Uploaded temporary path to publish.</param>
    /// <param name="finalRemotePath">Final path that must not be replaced.</param>
    /// <param name="plainRename">Plain SFTP rename operation used for the publish attempt.</param>
    /// <param name="remoteExists">Remote existence probe used only after a failed rename.</param>
    public static void CommitPublishIfAbsent(
        string tempRemotePath,
        string finalRemotePath,
        Action<string, string> plainRename,
        Func<string, bool> remoteExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRemotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);
        ArgumentNullException.ThrowIfNull(plainRename);
        ArgumentNullException.ThrowIfNull(remoteExists);

        try
        {
            plainRename(tempRemotePath, finalRemotePath);
        }
        catch (Exception renameException)
        {
            if (remoteExists(finalRemotePath))
            {
                throw new IOException(
                    $"Refused to copy: destination already exists: {finalRemotePath}",
                    renameException);
            }

            throw;
        }
    }

    /// <summary>
    /// Replaces the final remote path with the uploaded temp path.
    /// </summary>
    /// <remarks>
    /// Omitting <paramref name="canDemoteAtomicRenameFailure"/> preserves the historical behavior where
    /// every atomic-rename exception enters the fallback. Omitting <paramref name="isExistingTargetRegularFile"/>
    /// preserves the historical behavior where every existing target is eligible for replacement. Omitting
    /// <paramref name="onNonAtomicReplacement"/> preserves the historical behavior without a warning callback.
    /// </remarks>
    public static void CommitRename(
        string tempRemotePath,
        string finalRemotePath,
        Action<string, string> atomicRename,
        Action<string, string> plainRename,
        Func<string, bool> remoteExists,
        Action<string> deleteRemote,
        Func<Exception, bool>? canDemoteAtomicRenameFailure = null,
        Func<string, bool>? isExistingTargetRegularFile = null,
        Action? onNonAtomicReplacement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRemotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);
        ArgumentNullException.ThrowIfNull(atomicRename);
        ArgumentNullException.ThrowIfNull(plainRename);
        ArgumentNullException.ThrowIfNull(remoteExists);
        ArgumentNullException.ThrowIfNull(deleteRemote);

        try
        {
            atomicRename(tempRemotePath, finalRemotePath);
            return;
        }
        catch (Exception ex)
        {
            bool canDemote = canDemoteAtomicRenameFailure?.Invoke(ex) ?? true;
            if (!canDemote)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"SFTP atomic rename failed for '{finalRemotePath}' and fallback was not allowed "
                    + $"({ex.GetType().Name}): {ex.Message}");
                throw;
            }

            Heimdall.Core.Logging.FileLogger.Warn(
                $"SFTP atomic rename unavailable for '{finalRemotePath}', falling back to replace: {ex.Message}");
        }

        string? backupRemotePath = null;
        if (remoteExists(finalRemotePath))
        {
            if (isExistingTargetRegularFile is not null
                && !isExistingTargetRegularFile(finalRemotePath))
            {
                throw new InvalidOperationException(
                    $"SFTP fallback replacement refused for '{finalRemotePath}' because the existing target "
                    + "is not a regular file.");
            }

            backupRemotePath = CreateRemoteBackupPath(finalRemotePath);
            Heimdall.Core.Logging.FileLogger.Warn(
                $"SFTP replacement for '{finalRemotePath}' is not atomic; moving the existing target "
                + $"to backup '{backupRemotePath}' before commit.");
            // No warning is due when the final path is absent because no backup move opens a replacement window.
            onNonAtomicReplacement?.Invoke();
            plainRename(finalRemotePath, backupRemotePath);
        }

        try
        {
            plainRename(tempRemotePath, finalRemotePath);
        }
        catch (Exception renameEx)
        {
            RestoreBackup(finalRemotePath, backupRemotePath, plainRename, remoteExists, deleteRemote, renameEx);
            throw;
        }

        CleanupBackup(backupRemotePath, remoteExists, deleteRemote);
    }

    private static string CreateRemoteBackupPath(string finalRemotePath)
    {
        return $"{finalRemotePath}.{Guid.NewGuid():N}.bak";
    }

    private static void RestoreBackup(
        string finalRemotePath,
        string? backupRemotePath,
        Action<string, string> plainRename,
        Func<string, bool> remoteExists,
        Action<string> deleteRemote,
        Exception renameException)
    {
        if (backupRemotePath is null)
        {
            return;
        }

        try
        {
            if (remoteExists(finalRemotePath))
            {
                deleteRemote(finalRemotePath);
            }

            plainRename(backupRemotePath, finalRemotePath);
        }
        catch (Exception restoreEx)
        {
            throw new InvalidOperationException(
                $"SFTP fallback rename failed and restoring backup '{backupRemotePath}' failed.",
                new AggregateException(renameException, restoreEx));
        }
    }

    private static void CleanupBackup(
        string? backupRemotePath,
        Func<string, bool> remoteExists,
        Action<string> deleteRemote)
    {
        if (backupRemotePath is null)
        {
            return;
        }

        try
        {
            if (remoteExists(backupRemotePath))
            {
                deleteRemote(backupRemotePath);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"SFTP upload backup cleanup failed for '{backupRemotePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes an abandoned remote temp path without touching the final remote path.
    /// </summary>
    public static void Rollback(string tempRemotePath, Action<string> deleteTemp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRemotePath);
        ArgumentNullException.ThrowIfNull(deleteTemp);

        try
        {
            deleteTemp(tempRemotePath);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"SFTP temp upload rollback failed for '{tempRemotePath}': {ex.Message}");
        }
    }
}
