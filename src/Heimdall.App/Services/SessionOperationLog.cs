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

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Heimdall.App.Services;

/// <summary>
/// Records SFTP / FTP file-transfer operations (upload / download / delete / rename / mkdir) to a
/// single, shared, append-only log. Recording is gated by the global session-logging toggle and is
/// non-blocking for callers.
/// </summary>
public interface ISessionOperationLog : IDisposable
{
    /// <summary>
    /// Queues one file-transfer operation for asynchronous append to the shared operations log.
    /// Never blocks the caller and never throws into it. No-op only after disposal. This sink is a
    /// dumb writer: whether an operation should be recorded at all is decided by the caller through
    /// <see cref="SessionOperationGatePolicy"/> against the LIVE global toggle, so the toggle takes
    /// effect without a restart.
    /// </summary>
    /// <param name="record">The operation to record.</param>
    void LogOperation(SessionOperationRecord record);
}

/// <summary>
/// Single shared append-only NDJSON writer for SFTP / FTP file-transfer operations. One JSON object
/// per line, with language-neutral English property names so the file is machine-readable without
/// any locale dependency. The writer mechanics (buffered queue, background drain, retry, size
/// rollover, lazy ACL-hardened creation) live in <see cref="NdjsonAppendLog{TRecord}"/>; this type
/// supplies the fixed file name and the per-operation serializer.
/// </summary>
public sealed class SessionOperationLog : NdjsonAppendLog<SessionOperationRecord>, ISessionOperationLog
{
    /// <summary>Fixed name of the shared operations log under the session-log root directory.</summary>
    private const string OperationLogFileName = "session-operations.log";

    /// <summary>
    /// Creates the operations log. Nothing is written until the first <see cref="LogOperation"/> call;
    /// the directory and file are materialized lazily on the first real write, so when no caller logs
    /// no file appears.
    /// </summary>
    /// <param name="rootDirectory">Directory that receives the shared operations log (created on demand).</param>
    /// <param name="maxBytes">Size cap in bytes before rolling over to a ".N.log" continuation. Must be strictly positive.</param>
    /// <param name="flushIntervalMs">Drain interval in milliseconds. Must be strictly positive.</param>
    public SessionOperationLog(string rootDirectory, long maxBytes, int flushIntervalMs)
        : base(rootDirectory, OperationLogFileName, maxBytes, flushIntervalMs)
    {
    }

    /// <inheritdoc />
    protected override string DiagnosticName => "Session operation log";

    /// <inheritdoc />
    public void LogOperation(SessionOperationRecord record) => Enqueue(record);

    // Serializes a record to a single NDJSON object. Property names are stable, language-neutral
    // English; the timestamp is ISO-8601 round-trip UTC. Op and result are lowercase strings. Null
    // optional fields (remotePathTo, localPath, bytes, errorCategory) are OMITTED rather than emitted
    // as null, and "privileged" is written only when true, so each line is compact and unambiguous.
    // The host has any leading "user@" prefix stripped here, defensively, so a credential-bearing
    // endpoint never lands on disk.
    /// <inheritdoc />
    protected override string ToNdjsonLine(SessionOperationRecord record)
    {
        using MemoryStream buffer = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", FormatTimestamp(record.TimestampUtc));
            writer.WriteString("protocol", record.Protocol);
            writer.WriteString("op", ToLowerToken(record.Op));
            writer.WriteString("host", StripUserPrefix(record.Host));
            writer.WriteString("remotePath", record.RemotePath);

            if (record.RemotePathTo is not null)
            {
                writer.WriteString("remotePathTo", record.RemotePathTo);
            }

            if (record.LocalPath is not null)
            {
                writer.WriteString("localPath", record.LocalPath);
            }

            if (record.Bytes is not null)
            {
                writer.WriteNumber("bytes", record.Bytes.Value);
            }

            writer.WriteNumber("durationMs", record.DurationMs);
            writer.WriteString("result", ToLowerToken(record.Result));

            if (record.ErrorCategory is not null)
            {
                writer.WriteString("errorCategory", record.ErrorCategory);
            }

            if (record.Privileged)
            {
                writer.WriteBoolean("privileged", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ToLowerToken(SessionOperationKind op) =>
        op.ToString().ToLowerInvariant();

    private static string ToLowerToken(SessionOperationResult result) =>
        result.ToString().ToLowerInvariant();

    private static string FormatTimestamp(DateTime utc)
    {
        DateTime asUtc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return asUtc.ToString("o", CultureInfo.InvariantCulture);
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
