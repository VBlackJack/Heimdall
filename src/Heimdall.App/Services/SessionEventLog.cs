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

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Timer = System.Threading.Timer;
using Heimdall.Core.Security;

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
/// without any locale dependency. Events are buffered on a lock-free queue and drained by one
/// background timer (the FileLogger / SessionLogService pattern), so the UI thread is never
/// blocked. The file is hardened with a restrictive ACL on first write and rolls over to a
/// ".N.log" continuation past a size cap.
/// </summary>
public sealed class SessionEventLog : ISessionEventLog
{
    /// <summary>Fixed name of the shared event log under the session-log root directory.</summary>
    private const string EventLogFileName = "session-events.log";

    private const string LogFileExtension = ".log";

    /// <summary>Newline terminator for each NDJSON record (platform-neutral, per the NDJSON spec).</summary>
    private const string LineTerminator = "\n";

    /// <summary>Backoff schedule (ms) for transient IO failures; mirrors <c>FileLogger</c>/<c>SessionLogService</c>.</summary>
    private static readonly int[] RetryDelaysMs = [10, 50, 200];

    private readonly string _rootDirectory;
    private readonly string _basePath;
    private readonly long _maxBytes;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();

    private string _currentPath;
    private long _currentBytes;
    private bool _currentBytesKnown;
    private int _rolloverIndex;
    private bool _writeErrorLogged;
    private bool _disposed;

    /// <summary>
    /// Creates the event log. Nothing is written until the first <see cref="LogEvent"/> call; the
    /// directory and file are materialized lazily on the first real write, so when no caller logs no
    /// file appears.
    /// </summary>
    /// <param name="rootDirectory">Directory that receives the shared event log (created on demand).</param>
    /// <param name="maxBytes">Size cap in bytes before rolling over to a ".N.log" continuation. Must be strictly positive.</param>
    /// <param name="flushIntervalMs">Drain interval in milliseconds. Must be strictly positive.</param>
    public SessionEventLog(string rootDirectory, long maxBytes, int flushIntervalMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushIntervalMs);

        _rootDirectory = rootDirectory;
        _maxBytes = maxBytes;
        _basePath = Path.Combine(rootDirectory, EventLogFileName);
        _currentPath = _basePath;

        TimeSpan interval = TimeSpan.FromMilliseconds(flushIntervalMs);
        _flushTimer = new Timer(_ => Flush(), null, interval, interval);
    }

    /// <inheritdoc />
    public void LogEvent(SessionEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(ToNdjsonLine(record));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Stop the timer first, then drain the remainder while still marked live (Flush short-circuits
        // once disposed would be true), then mark disposed. Mirrors FileLogger's teardown ordering.
        _flushTimer.Dispose();
        Flush();
        _disposed = true;
    }

    // Drains the queued NDJSON lines to disk with bounded retry on transient IO faults. Never throws.
    private void Flush()
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        lock (_writeLock)
        {
            if (_queue.IsEmpty)
            {
                return;
            }

            List<string> batch = [];
            while (_queue.TryDequeue(out string? entry))
            {
                batch.Add(entry);
            }

            int written = 0;
            for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
            {
                try
                {
                    for (; written < batch.Count; written++)
                    {
                        WriteLine(batch[written]);
                    }

                    return;
                }
                catch (IOException) when (attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
                catch (IOException ex)
                {
                    // Persistent failure: preserve the unwritten remainder for the next drain and emit
                    // a single diagnostic so the hot path is never crashed or spammed.
                    for (int i = written; i < batch.Count; i++)
                    {
                        _queue.Enqueue(batch[i]);
                    }

                    LogWriteErrorOnce(ex.Message);
                    return;
                }
                catch
                {
                    // Event logging must never propagate; drop the batch rather than crash shutdown.
                    return;
                }
            }
        }
    }

    // Assumes _writeLock is held. Appends one NDJSON line, rolling the file over first if the cap is reached.
    private void WriteLine(string jsonLine)
    {
        string text = jsonLine + LineTerminator;
        long byteCount = Encoding.UTF8.GetByteCount(text);

        EnsureCurrentBytesInitialized();
        if (_currentBytes > 0 && _currentBytes + byteCount > _maxBytes)
        {
            RollOver();
        }

        Directory.CreateDirectory(_rootDirectory);

        if (!File.Exists(_currentPath))
        {
            // Create the file closed, then apply the restrictive ACL before any data is written.
            // Mirrors SessionLogService/FileLogger: SetFileAcl runs on a handle-free file so it
            // cannot hit a sharing violation against an open writer.
            using (new FileStream(_currentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            if (OperatingSystem.IsWindows())
            {
                AclEnforcer.SetFileAcl(_currentPath);
            }
        }

        File.AppendAllText(_currentPath, text, Encoding.UTF8);
        _currentBytes += byteCount;
    }

    // Assumes _writeLock is held. Lazily seeds the current byte count from any pre-existing file so
    // that appends across app restarts still respect the size cap.
    private void EnsureCurrentBytesInitialized()
    {
        if (_currentBytesKnown)
        {
            return;
        }

        _currentBytes = File.Exists(_currentPath) ? new FileInfo(_currentPath).Length : 0;
        _currentBytesKnown = true;
    }

    // Assumes _writeLock is held. Switches to the next ".N.log" continuation file.
    private void RollOver()
    {
        _rolloverIndex++;
        string stem = _basePath.EndsWith(LogFileExtension, StringComparison.OrdinalIgnoreCase)
            ? _basePath[..^LogFileExtension.Length]
            : _basePath;
        _currentPath = $"{stem}.{_rolloverIndex.ToString(CultureInfo.InvariantCulture)}{LogFileExtension}";
        _currentBytes = 0;
        _currentBytesKnown = true;

        Core.Logging.FileLogger.Info($"Session event log reached its size cap, continuing in {_currentPath}");
    }

    private void LogWriteErrorOnce(string detail)
    {
        if (_writeErrorLogged)
        {
            return;
        }

        _writeErrorLogged = true;
        Core.Logging.FileLogger.Warn($"Session event log write failed for {_currentPath}: {detail}");
    }

    // Serializes a record to a single NDJSON object. Property names are stable, language-neutral
    // English; the timestamp is ISO-8601 round-trip UTC. Null optional fields are OMITTED (not
    // emitted as null) to keep connect lines compact and unambiguous. The host has any leading
    // "user@" prefix stripped here, defensively, so a credential-bearing endpoint never lands on disk.
    private static string ToNdjsonLine(SessionEventRecord record)
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
