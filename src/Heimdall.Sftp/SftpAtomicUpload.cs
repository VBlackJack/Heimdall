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
    /// Refuses an upload when the existing destination is an unsupported remote entry type.
    /// </summary>
    /// <param name="finalRemotePath">Final remote destination path.</param>
    /// <param name="existingDestinationKind">
    /// Existing destination kind, or <see langword="null"/> when the destination is absent.
    /// </param>
    public static void EnsureUploadTargetSupported(
        string finalRemotePath,
        RemoteEntryKind? existingDestinationKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);

        switch (existingDestinationKind)
        {
            case null:
            case RemoteEntryKind.File:
                return;

            // A directory used to be accepted here and refused only by the server, at the
            // rename, after the whole file had been staged. It is decidable up front.
            case RemoteEntryKind.Directory:
            case RemoteEntryKind.Unknown:
            case RemoteEntryKind.SymbolicLink:
            case RemoteEntryKind.Fifo:
            case RemoteEntryKind.Socket:
            case RemoteEntryKind.Device:
                throw new RemoteUploadTargetUnsupportedException(
                    finalRemotePath,
                    existingDestinationKind.Value);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(existingDestinationKind),
                    existingDestinationKind,
                    null);
        }
    }

    /// <summary>
    /// Creates a unique temporary remote path next to the final remote path.
    /// </summary>
    public static string CreateRemoteTempPath(string finalRemotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);

        return $"{finalRemotePath}.{Guid.NewGuid():N}.part";
    }

    // CommitPublishIfAbsent lived here. It published an uploaded temp path with a plain rename and
    // re-probed the destination only after that rename failed, which was the commit behind the remote
    // copy's best-effort fallback: on a server whose rename silently overwrites, it reported success
    // while destroying data. Both transports now refuse a copy they cannot make safe, so the primitive
    // has no consumer and is removed rather than left as a route back to that behaviour.

    /// <summary>
    /// Publishes the uploaded temp path at a destination the caller established was absent, with the
    /// SFTP version 3 rename, and turns a refusal caused by a destination that appeared since into
    /// <see cref="RemoteDestinationExistsException"/>.
    /// </summary>
    /// <remarks>
    /// The version 3 rename (<c>SSH_FXP_RENAME</c>, not the <c>posix-rename@openssh.com</c>
    /// extension) is specified to fail when the new name exists, and OpenSSH implements it with
    /// <c>link()</c>, which the kernel refuses on an existing name. It needs no exec channel and no
    /// extra connection, so it works where the exec-based publisher does not: chrooted
    /// <c>internal-sftp</c> accounts, Windows OpenSSH and SFTP gateways.
    /// <para>
    /// This is not the strict no-clobber the remote copy requires, and the copy does not use it: a
    /// server that departs from the specification and overwrites on this rename would replace a file
    /// created after the caller's listing. For an upload whose destination a listing just proved
    /// absent, that is the same residual race every transport without an exclusive commit carries,
    /// and it is never wider than an ordinary upload's.
    /// </para>
    /// <para>
    /// The existence probe runs only after the rename failed, and only to say why. A probe failure
    /// leaves the reason unknown, so the rename's own failure is what propagates.
    /// </para>
    /// </remarks>
    /// <param name="tempRemotePath">Uploaded temporary path to publish.</param>
    /// <param name="finalRemotePath">Final remote destination path, absent at the caller's listing.</param>
    /// <param name="plainRename">SFTP version 3 rename, which refuses an existing destination.</param>
    /// <param name="remoteExists">Existence probe, consulted only after the rename failed.</param>
    /// <exception cref="RemoteDestinationExistsException">The destination exists at commit time.</exception>
    public static void CommitCreate(
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
        catch (Exception renameFailure) when (renameFailure is not OperationCanceledException)
        {
            if (DestinationExistsAfterFailedCommit(finalRemotePath, remoteExists))
            {
                throw new RemoteDestinationExistsException(finalRemotePath, renameFailure);
            }

            throw;
        }
    }

    private static bool DestinationExistsAfterFailedCommit(
        string finalRemotePath,
        Func<string, bool> remoteExists)
    {
        try
        {
            return remoteExists(finalRemotePath);
        }
        catch (Exception probeFailure) when (probeFailure is not OperationCanceledException)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                "SFTP create commit failed and the destination could not be examined "
                + $"({probeFailure.GetType().Name}); reporting the commit failure.");
            return false;
        }
    }

    /// <summary>
    /// Replaces the final remote path with the uploaded temp path.
    /// </summary>
    /// <remarks>
    /// The atomic rename is the only operation allowed to replace an existing destination. When the server
    /// cannot perform it and <paramref name="canDemoteAtomicRenameFailure"/> accepts the failure, the plain
    /// rename fallback runs only once the destination is proven absent: an existing destination is refused
    /// instead of being replaced through a non-atomic sequence, and a failing existence probe is propagated
    /// so the fallback stays closed. Omitting <paramref name="canDemoteAtomicRenameFailure"/> preserves the
    /// historical behavior where every atomic-rename exception is eligible for the fallback.
    /// </remarks>
    /// <param name="tempRemotePath">Uploaded temporary path to publish.</param>
    /// <param name="finalRemotePath">Final remote destination path.</param>
    /// <param name="atomicRename">Atomic rename operation attempted first.</param>
    /// <param name="plainRename">Plain rename used only when the destination is absent.</param>
    /// <param name="remoteExists">Remote existence probe consulted only after a demotable atomic failure.</param>
    /// <param name="canDemoteAtomicRenameFailure">
    /// Predicate deciding whether an atomic-rename failure may enter the fallback.
    /// </param>
    public static void CommitRename(
        string tempRemotePath,
        string finalRemotePath,
        Action<string, string> atomicRename,
        Action<string, string> plainRename,
        Func<string, bool> remoteExists,
        Func<Exception, bool>? canDemoteAtomicRenameFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRemotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);
        ArgumentNullException.ThrowIfNull(atomicRename);
        ArgumentNullException.ThrowIfNull(plainRename);
        ArgumentNullException.ThrowIfNull(remoteExists);

        Exception demotedFailure;
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
                $"SFTP atomic rename unavailable for '{finalRemotePath}', probing the destination before "
                + $"the plain rename fallback: {ex.Message}");
            demotedFailure = ex;
        }

        if (remoteExists(finalRemotePath))
        {
            throw new InvalidOperationException(
                $"SFTP non-atomic replacement refused for '{finalRemotePath}': the destination already exists "
                + "and the server cannot rename atomically.",
                demotedFailure);
        }

        plainRename(tempRemotePath, finalRemotePath);
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
