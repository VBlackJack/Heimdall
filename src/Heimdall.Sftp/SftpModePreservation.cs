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
/// Decides how an SFTP replacement preserves the target's complete POSIX permission mode.
/// </summary>
public static class SftpModePreservation
{
    internal const uint SetUserIdBit = 0x800;
    internal const uint SetGroupIdBit = 0x400;
    internal const uint StickyBit = 0x200;
    internal const uint OwnerReadBit = 0x100;
    internal const uint OwnerWriteBit = 0x080;
    internal const uint OwnerExecuteBit = 0x040;
    internal const uint GroupReadBit = 0x020;
    internal const uint GroupWriteBit = 0x010;
    internal const uint GroupExecuteBit = 0x008;
    internal const uint OthersReadBit = 0x004;
    internal const uint OthersWriteBit = 0x002;
    internal const uint OthersExecuteBit = 0x001;

    private const uint PermissionModeMask = 0x0FFF;

    /// <summary>
    /// Returns the target mode to apply to the temporary file, or <see langword="null"/>
    /// when both complete permission modes already match.
    /// </summary>
    public static uint? ResolveModeToApply(uint targetPermissions, uint tempPermissions)
    {
        uint targetMode = GetMode(targetPermissions);
        uint tempMode = GetMode(tempPermissions);

        return targetMode == tempMode ? null : targetMode;
    }

    /// <summary>
    /// Returns whether a failed mode application must refuse the commit because the temporary
    /// file would not retain the target's exact permission and special-mode bits.
    /// </summary>
    public static bool ShouldRefuseCommitAfterApplyFailure(
        uint targetPermissions,
        uint tempPermissions)
    {
        uint targetMode = GetMode(targetPermissions);
        uint tempMode = GetMode(tempPermissions);

        return targetMode != tempMode;
    }

    internal static uint GetMode(uint permissions)
    {
        return permissions & PermissionModeMask;
    }

    /// <summary>
    /// The mode a temporary upload carries while its content is being written: owner read and
    /// write, nothing else, every special bit cleared.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the target's mode. A target of 0644 would leave the staged content
    /// world-readable for the whole duration of the copy, which is exactly the window this staging
    /// exists to close - the point is that nobody but the owner can read the bytes until the file
    /// is published.
    /// </remarks>
    internal const uint StagingMode = OwnerReadBit | OwnerWriteBit;

    /// <summary>
    /// The only file mode the staging open may be performed with.
    /// </summary>
    /// <remarks>
    /// <see cref="FileMode.CreateNew"/> maps to the SFTP flags <c>SSH_FXF_CREAT | SSH_FXF_EXCL</c>,
    /// so the server refuses the open when the path already exists. Anything that can create *or*
    /// open, such as <see cref="FileMode.Create"/> or <see cref="FileMode.OpenOrCreate"/>, silently
    /// adopts or truncates a staging file somebody else made, which is the failure this whole staging
    /// design exists to prevent. The value is passed to the open seam as an argument, so the mode
    /// actually requested is observable and cannot drift from this declaration unnoticed.
    /// </remarks>
    internal const FileMode ExclusiveStagingFileMode = FileMode.CreateNew;

    /// <summary>
    /// Everything the staged upload needs from the remote file system, as delegates, so the order
    /// it performs them in can be observed without a server.
    /// </summary>
    /// <param name="OpenTempExclusive">
    /// Reserves the temporary file and returns the stream the bytes are written through. One call,
    /// one handle: a separate create-then-reopen pair would discard the handle that proved
    /// exclusivity and re-resolve the path by name, so the reservation would guarantee nothing about
    /// what the writes land in. Receives the mode and access to use so a test can capture them.
    /// </param>
    /// <param name="ReadTempMode">Reads the temporary file's current permission bits.</param>
    /// <param name="ApplyTempMode">Sets the temporary file's permission bits.</param>
    /// <param name="ReadTargetAttributesAfterUpload">
    /// Re-reads the destination without following symlinks and validates its type, returning the
    /// mode and timestamps it currently carries, or null when no regular file is there to inherit
    /// from. Throwing here refuses the commit, which is how the existing type validation keeps its
    /// effect.
    /// </param>
    /// <param name="ApplyPublicationAttributes">Applies the mode and timestamps to the temporary file.</param>
    /// <param name="ReadTempAttributesAfterApply">Re-reads the temporary file to confirm what landed.</param>
    /// <param name="Commit">Publishes the temporary file over the destination.</param>
    internal sealed record StagedUploadOperations(
        Func<FileMode, FileAccess, Stream> OpenTempExclusive,
        Func<uint> ReadTempMode,
        Action<uint> ApplyTempMode,
        Func<SftpPublicationAttributes?> ReadTargetAttributesAfterUpload,
        Action<SftpPublicationAttributes> ApplyPublicationAttributes,
        Func<SftpPublicationAttributes> ReadTempAttributesAfterApply,
        Action Commit);

    /// <summary>
    /// The mode and timestamps a published file must end up carrying.
    /// </summary>
    /// <remarks>
    /// UTC on both ends, deliberately. SSH.NET exposes a local-time and a UTC property for each
    /// stamp; reading one while writing the other shifts every timestamp by the client's offset,
    /// which is the kind of corruption that only surfaces on a machine in another zone.
    /// </remarks>
    internal readonly record struct SftpPublicationAttributes(
        uint Mode,
        DateTime? LastAccessTimeUtc,
        DateTime? LastWriteTimeUtc);

    /// <summary>
    /// Uploads through a temporary file that is private for the whole time it holds content.
    /// </summary>
    /// <remarks>
    /// The order is the contract. The temporary is created empty, its server-assigned mode is
    /// captured, it is tightened to <see cref="StagingMode"/>, and that tightening is READ BACK
    /// before a single byte is written - a server that silently ignores the chmod must stop the
    /// upload, not receive the content anyway. The remote stream is closed before the publication
    /// mode is applied and before the commit, so nothing is published while a write may still be
    /// buffered.
    /// <para>
    /// The published mode is the destination's own when it is being replaced, and the mode the
    /// server originally assigned when the file is new - never <see cref="StagingMode"/>, which
    /// would quietly make every newly uploaded file owner-only.
    /// </para>
    /// </remarks>
    internal static void RunStagedUpload(
        StagedUploadOperations operations,
        Stream source,
        Action<long> reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reportProgress);

        cancellationToken.ThrowIfCancellationRequested();

        // One exclusive open, and the handle it returns is the handle the bytes go through. The mode
        // travels as an argument rather than being baked into the wiring so a test can capture what
        // was actually requested: CreateNew is the whole guarantee, and a staging path that another
        // party already created must make this throw instead of being truncated into.
        uint serverAssignedMode;
        long written = 0;
        using (Stream destination = operations.OpenTempExclusive(
            ExclusiveStagingFileMode,
            FileAccess.Write))
        {
            // Captured before the file is tightened: it is the only record of what the server would
            // have given a brand-new file, and a new upload has to end up with exactly that.
            serverAssignedMode = GetMode(operations.ReadTempMode());

            operations.ApplyTempMode(StagingMode);

            // Read back before the first byte, so no content exists while the file is still readable
            // by anyone the server's default mode allowed. The read-back is by path: the handle-level
            // route (FSTAT/FSETSTAT) is unreachable because SSH.NET keeps its sftp session internal.
            // It therefore proves the name carries 0600, not that this handle's inode does.
            uint stagedMode = GetMode(operations.ReadTempMode());
            if (stagedMode != StagingMode)
            {
                throw new IOException(
                    "Refusing to stage the upload: the temporary file reports mode "
                    + $"0{Convert.ToString(stagedMode, 8)} instead of 0{Convert.ToString(StagingMode, 8)}, "
                    + "so its contents would not be private while copying.");
            }

            byte[] buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                written += read;

                // Cumulative, and never ahead of what has actually reached the stream.
                reportProgress(written);
            }

            cancellationToken.ThrowIfCancellationRequested();
            destination.Flush();
        }

        // Read after the stream is closed, so the destination is observed as late as the protocol
        // allows and the values used are the target's own, never the temporary's.
        SftpPublicationAttributes? target = operations.ReadTargetAttributesAfterUpload();
        if (target is not { } existing)
        {
            // Creation. There is nothing to inherit, so the file keeps the mode the server gave a
            // new file and whatever timestamps the write produced; inventing a target's stamps
            // here would date a file that never existed.
            operations.ApplyPublicationAttributes(new SftpPublicationAttributes(
                serverAssignedMode,
                LastAccessTimeUtc: null,
                LastWriteTimeUtc: null));
            operations.Commit();
            return;
        }

        SftpPublicationAttributes desired = existing with { Mode = GetMode(existing.Mode) };
        operations.ApplyPublicationAttributes(desired);

        // Read back what actually landed. A server may silently ignore a timestamp write, and a
        // replacement that reports success while leaving the file dated today has not preserved
        // anything. The commit is refused rather than published on an unverified claim.
        SftpPublicationAttributes applied = operations.ReadTempAttributesAfterApply();
        if (GetMode(applied.Mode) != desired.Mode
            || applied.LastWriteTimeUtc != desired.LastWriteTimeUtc
            || applied.LastAccessTimeUtc != desired.LastAccessTimeUtc)
        {
            throw new IOException(
                "Refusing to publish the upload: the staged file reports mode "
                + $"0{Convert.ToString(GetMode(applied.Mode), 8)} with write time "
                + $"{applied.LastWriteTimeUtc:O} and access time {applied.LastAccessTimeUtc:O}, "
                + $"but the destination carried mode 0{Convert.ToString(desired.Mode, 8)} with "
                + $"write time {desired.LastWriteTimeUtc:O} and access time "
                + $"{desired.LastAccessTimeUtc:O}, so the replacement would not preserve them.");
        }

        operations.Commit();
    }
}
