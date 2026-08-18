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

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Heimdall.Core.Logging;

namespace Heimdall.Core.Security;

/// <summary>
/// Writes sensitive data to files while minimizing the window where plaintext
/// exists in memory. All intermediate char[] and byte[] buffers are zeroed
/// in finally blocks (CWE-316 prevention).
/// </summary>
public static class SecureFileWriter
{
    /// <summary>
    /// UTF-8 encoding without BOM (matches legacy PS 5.1 behavior that avoids BOM corruption).
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes text to a file that is created with restrictive ACL from the start,
    /// eliminating the TOCTOU window between file creation and permission enforcement.
    /// Only the current user, Administrators, and SYSTEM can access the file.
    /// </summary>
    /// <param name="filePath">The target file path.</param>
    /// <param name="text">The text content to write.</param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void WriteAndProtect(string filePath, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        FileSecurity security = BuildRestrictedSecurity();
        FileInfo fileInfo = new(filePath);

        // CREATE_ALWAYS keeps a pre-existing file's ACL, so the restrictive security descriptor would not be
        // applied to a file that already exists (e.g. pre-created by another process). Remove any existing
        // file first, then CreateNew so the SD is always applied - and if a racing process re-creates the
        // path between the delete and the create, CreateNew fails closed instead of writing the secret into
        // a foreign file.
        fileInfo.Delete();
        using FileStream stream = fileInfo.Create(FileMode.CreateNew, FileSystemRights.WriteData,
            FileShare.None, 4096, FileOptions.None, security);

        byte[] bytes = Utf8NoBom.GetBytes(text ?? string.Empty);
        try
        {
            stream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// Creates a new file with a restrictive ACL applied atomically and returns
    /// an exclusive write stream. The path must not already exist.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static FileStream CreateWriteAndProtect(
        string filePath,
        int bufferSize = 81920,
        FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        FileSecurity security = BuildRestrictedSecurity();
        FileInfo fileInfo = new(filePath);
        return fileInfo.Create(
            FileMode.CreateNew,
            FileSystemRights.Write,
            FileShare.None,
            bufferSize,
            options,
            security);
    }

    /// <summary>
    /// Creates a new directory whose ACL grants access only to the current user,
    /// Administrators, and SYSTEM. Inheritance is disabled at creation time.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void CreateRestrictedDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var directory = new DirectoryInfo(directoryPath);
        if (directory.Exists)
        {
            throw new IOException($"The restricted directory already exists: {directoryPath}");
        }

        directory.Create(BuildRestrictedDirectorySecurity());
    }

    /// <summary>
    /// Async variant of <see cref="WriteAndProtect"/>. Same TOCTOU-free guarantee:
    /// the restrictive ACL is applied atomically when the file is created, before
    /// any data is written, so an observer never sees the file with a permissive
    /// inherited ACL.
    /// </summary>
    /// <param name="filePath">The target file path.</param>
    /// <param name="text">The text content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task WriteAndProtectAsync(
        string filePath,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        FileSecurity security = BuildRestrictedSecurity();
        FileInfo fileInfo = new(filePath);

        // See WriteAndProtect: CreateNew (after deleting any stale file) guarantees the restrictive ACL is
        // applied to a freshly created file and fails closed on a creation race.
        fileInfo.Delete();
        await using FileStream stream = fileInfo.Create(FileMode.CreateNew, FileSystemRights.WriteData,
            FileShare.None, 4096, FileOptions.Asynchronous, security);

        byte[] bytes = Utf8NoBom.GetBytes(text ?? string.Empty);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// Atomically replaces a file with new text, durable against a crash mid-write.
    /// The content is written to a uniquely-named temp file IN THE SAME DIRECTORY
    /// (same volume) with the restrictive ACL applied at create (current user +
    /// Administrators + SYSTEM, inheritance disabled), then the target is replaced
    /// by an atomic same-volume rename (<see cref="File.Move(string, string, bool)"/>
    /// -> MoveFileEx MOVEFILE_REPLACE_EXISTING). The renamed file carries the temp's
    /// restrictive ACL, so the final file ends up restricted without a separate
    /// post-write ACL pass and without a TOCTOU window.
    /// </summary>
    /// <remarks>
    /// On ANY failure the ORIGINAL target is left untouched and the error is surfaced, never
    /// swallowed. Deleting the temp is attempted on a best-effort basis: the cleanup itself absorbs
    /// its own failures, so a stray temp can survive a failed write. It holds only staged data.
    /// If the volume does not support the
    /// secure ACL create (e.g. FAT/exFAT/odd network shares), the method falls back
    /// once to a same-directory temp write + best-effort ACL + atomic rename,
    /// logging a single warning. Default Windows (NTFS) always takes the
    /// restrictive-ACL staging path.
    /// </remarks>
    /// <param name="targetPath">The final file path.</param>
    /// <param name="content">The text content to write (UTF-8, no BOM).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Task WriteAllTextAtomicAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken = default)
        => WriteAllTextAtomicAsync(
            targetPath,
            content,
            SystemAtomicFileOperations.Instance,
            cancellationToken);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task WriteAllTextAtomicAsync(
        string targetPath,
        string content,
        IAtomicFileOperations fileOperations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        ArgumentNullException.ThrowIfNull(fileOperations);

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Target path must include a directory.", nameof(targetPath));
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(targetPath) + ".tmp" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // Stage the content into the temp file with the restrictive ACL applied
            // at create (TOCTOU-free), reusing the secure-create path.
            await fileOperations.WriteWithRestrictedAclAsync(tempPath, content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AclCreationNotSupportedException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the ACL-aware create is allowed to select this path. Generic I/O
            // failures during staging must propagate so the live target stays intact.
            TryDeleteTemp(tempPath, fileOperations);
            FileLogger.Warn($"Restrictive ACL create unavailable; using atomic post-ACL fallback: {ex.Message}");
            await WriteWithPostAclFallbackAsync(
                tempPath,
                targetPath,
                content,
                fileOperations,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch
        {
            TryDeleteTemp(tempPath, fileOperations);
            throw;
        }

        try
        {
            // Atomic same-volume replace. The temp's restrictive ACL travels with
            // the renamed file, so the final target is restricted.
            fileOperations.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath, fileOperations);
            throw; // original target left untouched
        }
    }

    /// <summary>
    /// Atomic fallback used only when the secure ACL create is unsupported on the
    /// volume: write a same-directory temp, apply the ACL best-effort, then rename.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WriteWithPostAclFallbackAsync(
        string tempPath,
        string targetPath,
        string content,
        IAtomicFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        try
        {
            await fileOperations.WriteWithoutAclAsync(tempPath, content, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                fileOperations.ApplyRestrictedAcl(tempPath);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"Post-write ACL application skipped (non-NTFS or restricted): {ex.Message}");
            }

            fileOperations.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath, fileOperations);
            throw;
        }
    }

    private static void TryDeleteTemp(string tempPath, IAtomicFileOperations fileOperations)
    {
        try
        {
            fileOperations.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup; a stray temp contains only staged data.
        }
    }

    /// <summary>
    /// Atomically replaces <paramref name="targetPath"/> with exactly the given bytes.
    /// </summary>
    /// <remarks>
    /// The text overload carries no byte-identity contract: it takes a string, so what lands on disk is
    /// whatever the encoder produces. Restoring a captured baseline needs the opposite guarantee, the
    /// bytes back exactly as they were, byte-order mark and all, so it goes through this overload rather
    /// than through a decode-then-re-encode round trip.
    /// <para>
    /// Same protocol as the text path: a same-directory temporary is created with the restrictive ACL
    /// applied at creation, then renamed over the target. Every failure leaves the existing target
    /// untouched and propagates the original error; removing the temporary is only attempted on a
    /// best-effort basis, so a stray temporary can remain.
    /// </para>
    /// <para>
    /// The buffer belongs to the caller. This method never clears it; a caller holding sensitive bytes
    /// is responsible for zeroing its own snapshot once it is done with it.
    /// </para>
    /// </remarks>
    /// <param name="targetPath">File to replace.</param>
    /// <param name="content">Exact bytes to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static Task WriteAllBytesAtomicAsync(
        string targetPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
        => WriteAllBytesAtomicAsync(
            targetPath,
            content,
            SystemAtomicFileOperations.Instance,
            cancellationToken);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task WriteAllBytesAtomicAsync(
        string targetPath,
        ReadOnlyMemory<byte> content,
        IAtomicFileOperations fileOperations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        ArgumentNullException.ThrowIfNull(fileOperations);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Target path must include a directory.", nameof(targetPath));
        }

        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(
            directory,
            Path.GetFileName(targetPath) + ".tmp" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await fileOperations.WriteBytesWithRestrictedAclAsync(tempPath, content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDeleteTemp(tempPath, fileOperations);
            throw;
        }

        try
        {
            fileOperations.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath, fileOperations);
            throw;
        }
    }

    internal interface IAtomicFileOperations
    {
        Task WriteWithRestrictedAclAsync(
            string path,
            string content,
            CancellationToken cancellationToken);

        /// <summary>
        /// Writes exactly the given bytes, creating the file with the restrictive ACL.
        /// </summary>
        /// <remarks>
        /// The buffer belongs to the caller: this method never clears it, and never keeps a reference
        /// beyond the call. It creates no internal copy, so it has nothing of its own to clear.
        /// </remarks>
        Task WriteBytesWithRestrictedAclAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken);

        Task WriteWithoutAclAsync(
            string path,
            string content,
            CancellationToken cancellationToken);

        void ApplyRestrictedAcl(string path);

        void Move(string sourcePath, string destinationPath, bool overwrite);

        void Delete(string path);
    }

    internal sealed class AclCreationNotSupportedException : Exception
    {
        internal AclCreationNotSupportedException(Exception innerException)
            : base("The volume does not support restrictive ACL creation.", innerException)
        {
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class SystemAtomicFileOperations : IAtomicFileOperations
    {
        internal static SystemAtomicFileOperations Instance { get; } = new();

        private SystemAtomicFileOperations()
        {
        }

        public async Task WriteWithRestrictedAclAsync(
            string path,
            string content,
            CancellationToken cancellationToken)
        {
            FileSecurity security = BuildRestrictedSecurity();
            FileInfo fileInfo = new(path);
            fileInfo.Delete();

            FileStream stream;
            try
            {
                stream = fileInfo.Create(
                    FileMode.CreateNew,
                    FileSystemRights.WriteData,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous,
                    security);
            }
            catch (NotSupportedException ex)
            {
                throw new AclCreationNotSupportedException(ex);
            }

            await using (stream)
            {
                byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
                try
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Array.Clear(bytes);
                }
            }
        }

        public async Task WriteBytesWithRestrictedAclAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            FileSecurity security = BuildRestrictedSecurity();
            FileInfo fileInfo = new(path);
            fileInfo.Delete();

            FileStream stream;
            try
            {
                stream = fileInfo.Create(
                    FileMode.CreateNew,
                    FileSystemRights.WriteData,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous,
                    security);
            }
            catch (NotSupportedException ex)
            {
                throw new AclCreationNotSupportedException(ex);
            }

            await using (stream)
            {
                // The caller's buffer is written straight through. No copy is made, so there is
                // nothing here to clear, and clearing the caller's memory would be a bug.
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task WriteWithoutAclAsync(
            string path,
            string content,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
            try
            {
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }

        public void ApplyRestrictedAcl(string path)
            => new FileInfo(path).SetAccessControl(BuildRestrictedSecurity());

        public void Move(string sourcePath, string destinationPath, bool overwrite)
            => File.Move(sourcePath, destinationPath, overwrite);

        public void Delete(string path)
            => File.Delete(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSecurity BuildRestrictedSecurity()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine current user SID.");

        FileSecurity security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DirectorySecurity BuildRestrictedDirectorySecurity()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine current user SID.");

        DirectorySecurity security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const PropagationFlags propagation = PropagationFlags.None;

        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            inheritance,
            propagation,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            propagation,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            propagation,
            AccessControlType.Allow));
        return security;
    }

}
