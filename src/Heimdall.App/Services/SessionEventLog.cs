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
/// Records graphical-protocol (RDP / VNC / Citrix) connect/disconnect events to a single,
/// shared, append-only log. Recording is gated by the global session-logging toggle and is
/// non-blocking for callers (events fire on the UI thread).
/// </summary>
public interface ISessionEventLog : IDisposable
{
    /// <summary>
    /// Queues one connect/disconnect event for asynchronous append to the shared event log.
    /// Never blocks the caller and never throws into it. No-op only after disposal. This sink is a
    /// dumb writer: whether an event should be recorded at all is decided by the caller through
    /// <see cref="SessionEventGatePolicy"/> against the LIVE global toggle, so the toggle takes
    /// effect without a restart.
    /// </summary>
    /// <param name="record">The event to record.</param>
    void LogEvent(SessionEventRecord record);
}

/// <summary>
/// Single shared append-only NDJSON writer for graphical-protocol session events. One JSON
/// object per line, with language-neutral English property names so the file is machine-readable
/// without any locale dependency. The writer mechanics (buffered queue, background drain, retry,
/// size rollover, lazy ACL-hardened creation) live in <see cref="NdjsonAppendLog{TRecord}"/>; this
/// type supplies the fixed file name and the per-event serializer.
/// </summary>
public sealed class SessionEventLog : NdjsonAppendLog<SessionEventRecord>, ISessionEventLog
{
    /// <summary>Fixed name of the shared event log under the session-log root directory.</summary>
    private const string EventLogFileName = "session-events.log";

    /// <summary>
    /// Creates the event log. Nothing is written until the first <see cref="LogEvent"/> call; the
    /// directory and file are materialized lazily on the first real write, so when no caller logs no
    /// file appears.
    /// </summary>
    /// <param name="rootDirectory">Directory that receives the shared event log (created on demand).</param>
    /// <param name="maxBytes">Size cap in bytes before rolling over to a ".N.log" continuation. Must be strictly positive.</param>
    /// <param name="flushIntervalMs">Drain interval in milliseconds. Must be strictly positive.</param>
    public SessionEventLog(string rootDirectory, long maxBytes, int flushIntervalMs)
        : base(rootDirectory, EventLogFileName, maxBytes, flushIntervalMs)
    {
    }

    /// <inheritdoc />
    protected override string DiagnosticName => "Session event log";

    /// <inheritdoc />
    public void LogEvent(SessionEventRecord record) => Enqueue(record);

    // Serializes a record to a single NDJSON object. Property names are stable, language-neutral
    // English; the timestamp is ISO-8601 round-trip UTC. Null optional fields are OMITTED (not
    // emitted as null) to keep connect lines compact and unambiguous. The host has any leading
    // "user@" prefix stripped here, defensively, so a credential-bearing endpoint never lands on disk.
    /// <inheritdoc />
    protected override string ToNdjsonLine(SessionEventRecord record)
    {
        using MemoryStream buffer = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", FormatTimestamp(record.TimestampUtc));
            writer.WriteString("protocol", record.Protocol);
            writer.WriteString("event", record.Kind.ToString());
            writer.WriteString("host", StripUserPrefix(record.Host));

            if (record.Title is not null)
            {
                writer.WriteString("title", record.Title);
            }

            if (record.ReasonKey is not null)
            {
                writer.WriteString("reason", record.ReasonKey);
            }

            if (record.ReasonCode is not null)
            {
                writer.WriteNumber("reasonCode", record.ReasonCode.Value);
            }

            if (record.DurationMs is not null)
            {
                writer.WriteNumber("durationMs", record.DurationMs.Value);
            }

            if (record.EndTrigger is not null)
            {
                writer.WriteString("endTrigger", record.EndTrigger);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string FormatTimestamp(DateTime utc)
    {
        DateTime asUtc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return asUtc.ToString("o", CultureInfo.InvariantCulture);
    }

    // Strips a leading "user@" so a "user@host" endpoint (VNC display / Citrix StoreFront) cannot
    // leak an identity into the event log. Splits on the first '@'; values without one pass through.
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
