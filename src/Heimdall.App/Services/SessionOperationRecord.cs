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

namespace Heimdall.App.Services;

/// <summary>
/// The kind of file-transfer operation recorded by <see cref="ISessionOperationLog"/>.
/// Serialized to its lowercase name ("upload" / "download" / ...) in the NDJSON operations log.
/// </summary>
public enum SessionOperationKind
{
    /// <summary>A local-to-remote file upload.</summary>
    Upload,

    /// <summary>A remote-to-local file download.</summary>
    Download,

    /// <summary>A remote file or directory deletion.</summary>
    Delete,

    /// <summary>A remote rename / move.</summary>
    Rename,

    /// <summary>A remote directory creation.</summary>
    Mkdir,

    /// <summary>A same-server remote copy (source to destination).</summary>
    Copy,

    /// <summary>A remote permission change.</summary>
    Chmod
}

/// <summary>
/// The outcome of a recorded file-transfer operation. Serialized to its lowercase name
/// ("success" / "error" / "cancelled") in the NDJSON operations log.
/// </summary>
public enum SessionOperationResult
{
    /// <summary>The operation completed successfully.</summary>
    Success,

    /// <summary>The operation failed; <see cref="SessionOperationRecord.ErrorCategory"/> carries the class of failure.</summary>
    Error,

    /// <summary>The operation was cancelled by the user; no error category applies.</summary>
    Cancelled
}

/// <summary>
/// Immutable description of a single SFTP / FTP file-transfer operation (one upload, download,
/// delete, rename, mkdir, or copy). Construct exclusively through the per-operation factories
/// (<see cref="Upload"/>, <see cref="Download"/>, <see cref="Delete"/>, <see cref="Rename"/>,
/// <see cref="Mkdir"/>, <see cref="Copy"/>) so the operation-specific fields can never be set in
/// incoherent combinations: <see cref="Bytes"/> and <see cref="LocalPath"/> only on transfers,
/// <see cref="RemotePathTo"/> only on rename and copy, and <see cref="ErrorCategory"/> only when
/// <see cref="Result"/> is <see cref="SessionOperationResult.Error"/>.
/// </summary>
/// <remarks>
/// <see cref="Host"/> is expected to be pre-sanitized by the caller; this record additionally strips
/// any leading "user@" prefix as defense in depth, and the operations-log writer strips it once more
/// before persisting. This record never carries credentials.
/// </remarks>
public sealed record SessionOperationRecord
{
    private SessionOperationRecord(
        DateTime timestampUtc,
        string protocol,
        SessionOperationKind op,
        string host,
        string remotePath,
        string? remotePathTo,
        string? localPath,
        long? bytes,
        long durationMs,
        SessionOperationResult result,
        string? errorCategory,
        bool privileged)
    {
        TimestampUtc = timestampUtc;
        Protocol = protocol;
        Op = op;
        Host = StripUserPrefix(host);
        RemotePath = remotePath;
        RemotePathTo = remotePathTo;
        LocalPath = localPath;
        Bytes = bytes;
        DurationMs = durationMs < 0 ? 0 : durationMs;
        Result = result;
        ErrorCategory = errorCategory;
        Privileged = privileged;
    }

    /// <summary>UTC instant the operation completed.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>Transfer protocol label: "SFTP" or "FTP".</summary>
    public string Protocol { get; }

    /// <summary>Which file operation this record describes.</summary>
    public SessionOperationKind Op { get; }

    /// <summary>Target host (pre-sanitized; "user@" stripped at construction and again at write time).</summary>
    public string Host { get; }

    /// <summary>Remote path the operation acted on (the source path for a rename).</summary>
    public string RemotePath { get; }

    /// <summary>Destination remote path for a rename or copy; null for every other operation.</summary>
    public string? RemotePathTo { get; }

    /// <summary>Local path for a transfer (upload source / download target); null for delete/rename/mkdir.</summary>
    public string? LocalPath { get; }

    /// <summary>Byte count for a successful transfer; null for delete/rename/mkdir and for failed/cancelled transfers.</summary>
    public long? Bytes { get; }

    /// <summary>Wall-clock duration of the operation in milliseconds (negatives clamp to zero).</summary>
    public long DurationMs { get; }

    /// <summary>The outcome: success, error, or cancelled.</summary>
    public SessionOperationResult Result { get; }

    /// <summary>Failure category when <see cref="Result"/> is <see cref="SessionOperationResult.Error"/>; otherwise null.</summary>
    public string? ErrorCategory { get; }

    /// <summary>True when the operation ran through a privileged (sudo) fallback; serialized only when true.</summary>
    public bool Privileged { get; }

    /// <summary>Factories for upload operations (local source to remote target, byte-counted on success).</summary>
    public static class Upload
    {
        /// <summary>Builds a successful upload record carrying the transferred byte count.</summary>
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, string localPath, long bytes, long durationMs,
            bool privileged = false)
            => CreateTransfer(
                SessionOperationKind.Upload, protocol, host, remotePath, localPath,
                bytes, durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        /// <summary>Builds a failed upload record tagged with an error category.</summary>
        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, string localPath, long durationMs, string errorCategory,
            bool privileged = false)
            => CreateTransfer(
                SessionOperationKind.Upload, protocol, host, remotePath, localPath,
                bytes: null, durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        /// <summary>Builds a cancelled upload record.</summary>
        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, string localPath, long durationMs,
            bool privileged = false)
            => CreateTransfer(
                SessionOperationKind.Upload, protocol, host, remotePath, localPath,
                bytes: null, durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    /// <summary>Factories for download operations (remote source to local target, byte-counted on success).</summary>
    public static class Download
    {
        /// <summary>Builds a successful download record carrying the transferred byte count.</summary>
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, string localPath, long bytes, long durationMs,
            bool privileged = false)
            => CreateTransfer(
                SessionOperationKind.Download, protocol, host, remotePath, localPath,
                bytes, durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        /// <summary>Builds a failed download record tagged with an error category.</summary>
        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, string localPath, long durationMs, string errorCategory,
            bool privileged = false)
            => CreateTransfer(
                SessionOperationKind.Download, protocol, host, remotePath, localPath,
                bytes: null, durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        /// <summary>Builds a cancelled download record.</summary>
        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, string localPath, long durationMs,
            bool privileged = false)
            => CreateTransfer(
                SessionOperationKind.Download, protocol, host, remotePath, localPath,
                bytes: null, durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    /// <summary>Factories for delete operations (single remote path, no bytes, no local path).</summary>
    public static class Delete
    {
        /// <summary>Builds a successful delete record.</summary>
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, long durationMs, bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Delete, protocol, host, remotePath,
                durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        /// <summary>Builds a failed delete record tagged with an error category.</summary>
        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, long durationMs, string errorCategory,
            bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Delete, protocol, host, remotePath,
                durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        /// <summary>Builds a cancelled delete record.</summary>
        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, long durationMs, bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Delete, protocol, host, remotePath,
                durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    /// <summary>Factories for mkdir operations (single remote path, no bytes, no local path).</summary>
    public static class Mkdir
    {
        /// <summary>Builds a successful mkdir record.</summary>
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, long durationMs, bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Mkdir, protocol, host, remotePath,
                durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        /// <summary>Builds a failed mkdir record tagged with an error category.</summary>
        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, long durationMs, string errorCategory,
            bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Mkdir, protocol, host, remotePath,
                durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        /// <summary>Builds a cancelled mkdir record.</summary>
        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, long durationMs, bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Mkdir, protocol, host, remotePath,
                durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    /// <summary>Factories for chmod operations (remote path only).</summary>
    /// <remarks>
    /// A chmod left no record at all, privileged or not, while every other mutation of the
    /// server did; an operations log that says what was deleted but not what was made
    /// world-writable is not the log its reader expects.
    /// </remarks>
    public static class Chmod
    {
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, long durationMs, bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Chmod, protocol, host, remotePath,
                durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, long durationMs, string errorCategory,
            bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Chmod, protocol, host, remotePath,
                durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, long durationMs, bool privileged = false)
            => CreatePathOnly(
                SessionOperationKind.Chmod, protocol, host, remotePath,
                durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    /// <summary>Factories for rename operations (source plus destination remote path).</summary>
    public static class Rename
    {
        /// <summary>Builds a successful rename record carrying the destination path.</summary>
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, string remotePathTo, long durationMs,
            bool privileged = false)
            => CreateRename(
                protocol, host, remotePath, remotePathTo,
                durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        /// <summary>Builds a failed rename record tagged with an error category.</summary>
        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, string remotePathTo, long durationMs, string errorCategory,
            bool privileged = false)
            => CreateRename(
                protocol, host, remotePath, remotePathTo,
                durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        /// <summary>Builds a cancelled rename record.</summary>
        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, string remotePathTo, long durationMs,
            bool privileged = false)
            => CreateRename(
                protocol, host, remotePath, remotePathTo,
                durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    /// <summary>Factories for copy operations (source plus destination remote path on the same server).</summary>
    public static class Copy
    {
        /// <summary>Builds a successful copy record carrying the destination path.</summary>
        public static SessionOperationRecord Success(
            string protocol, string host, string remotePath, string remotePathTo, long durationMs,
            bool privileged = false)
            => CreateCopy(
                protocol, host, remotePath, remotePathTo,
                durationMs, SessionOperationResult.Success, errorCategory: null, privileged);

        /// <summary>Builds a failed copy record tagged with an error category.</summary>
        public static SessionOperationRecord Error(
            string protocol, string host, string remotePath, string remotePathTo, long durationMs, string errorCategory,
            bool privileged = false)
            => CreateCopy(
                protocol, host, remotePath, remotePathTo,
                durationMs, SessionOperationResult.Error, RequireCategory(errorCategory), privileged);

        /// <summary>Builds a cancelled copy record.</summary>
        public static SessionOperationRecord Cancelled(
            string protocol, string host, string remotePath, string remotePathTo, long durationMs,
            bool privileged = false)
            => CreateCopy(
                protocol, host, remotePath, remotePathTo,
                durationMs, SessionOperationResult.Cancelled, errorCategory: null, privileged);
    }

    private static SessionOperationRecord CreateTransfer(
        SessionOperationKind op,
        string protocol,
        string host,
        string remotePath,
        string localPath,
        long? bytes,
        long durationMs,
        SessionOperationResult result,
        string? errorCategory,
        bool privileged)
    {
        ValidateCommon(protocol, host, remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        return new SessionOperationRecord(
            DateTime.UtcNow, protocol, op, host, remotePath,
            remotePathTo: null, localPath, bytes, durationMs, result, errorCategory, privileged);
    }

    private static SessionOperationRecord CreatePathOnly(
        SessionOperationKind op,
        string protocol,
        string host,
        string remotePath,
        long durationMs,
        SessionOperationResult result,
        string? errorCategory,
        bool privileged)
    {
        ValidateCommon(protocol, host, remotePath);

        return new SessionOperationRecord(
            DateTime.UtcNow, protocol, op, host, remotePath,
            remotePathTo: null, localPath: null, bytes: null, durationMs, result, errorCategory, privileged);
    }

    private static SessionOperationRecord CreateRename(
        string protocol,
        string host,
        string remotePath,
        string remotePathTo,
        long durationMs,
        SessionOperationResult result,
        string? errorCategory,
        bool privileged)
    {
        ValidateCommon(protocol, host, remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePathTo);

        return new SessionOperationRecord(
            DateTime.UtcNow, protocol, SessionOperationKind.Rename, host, remotePath,
            remotePathTo, localPath: null, bytes: null, durationMs, result, errorCategory, privileged);
    }

    private static SessionOperationRecord CreateCopy(
        string protocol,
        string host,
        string remotePath,
        string remotePathTo,
        long durationMs,
        SessionOperationResult result,
        string? errorCategory,
        bool privileged)
    {
        ValidateCommon(protocol, host, remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePathTo);

        return new SessionOperationRecord(
            DateTime.UtcNow, protocol, SessionOperationKind.Copy, host, remotePath,
            remotePathTo, localPath: null, bytes: null, durationMs, result, errorCategory, privileged);
    }

    private static void ValidateCommon(string protocol, string host, string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
    }

    private static string RequireCategory(string errorCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCategory);
        return errorCategory;
    }

    // Strips a leading "user@" so a "user@host" endpoint cannot leak an identity into the log.
    // Splits on the first '@'; values without one pass through.
    private static string StripUserPrefix(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return host;
        }

        int at = host.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? host[(at + 1)..] : host;
    }
}
