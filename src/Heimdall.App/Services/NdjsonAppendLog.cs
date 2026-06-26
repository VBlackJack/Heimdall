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
using Heimdall.Core.Security;
using Timer = System.Threading.Timer;

namespace Heimdall.App.Services;

/// <summary>
/// Single shared append-only NDJSON writer base for session logs. One JSON object per line, with
/// language-neutral English property names so the file is machine-readable without any locale
/// dependency. Records are buffered on a lock-free queue and drained by one background timer (the
/// <c>FileLogger</c> / <c>SessionLogService</c> pattern), so the calling thread is never blocked.
/// The file is hardened with a restrictive ACL on first write and rolls over to a ".N.log"
/// continuation past a size cap. Concrete logs supply the file name and the per-record serializer.
/// </summary>
/// <typeparam name="TRecord">The immutable record type each line is serialized from.</typeparam>
/// <remarks>
/// Nothing is written until the first <see cref="Enqueue"/> call; the directory and file are
/// materialized lazily on the first real write, so when no caller logs no file appears. This sink is
/// a dumb writer: whether a record should be persisted at all is decided by the caller (the protocol +
/// LIVE-toggle gate), so the global toggle takes effect without a restart.
/// </remarks>
public abstract class NdjsonAppendLog<TRecord> : IDisposable
    where TRecord : class
{
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
    /// Creates the log. Nothing is written until the first <see cref="Enqueue"/> call.
    /// </summary>
    /// <param name="rootDirectory">Directory that receives the shared log file (created on demand).</param>
    /// <param name="fileName">Fixed name of the log file under <paramref name="rootDirectory"/>.</param>
    /// <param name="maxBytes">Size cap in bytes before rolling over to a ".N.log" continuation. Must be strictly positive.</param>
    /// <param name="flushIntervalMs">Drain interval in milliseconds. Must be strictly positive.</param>
    protected NdjsonAppendLog(string rootDirectory, string fileName, long maxBytes, int flushIntervalMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushIntervalMs);

        _rootDirectory = rootDirectory;
        _maxBytes = maxBytes;
        _basePath = Path.Combine(rootDirectory, fileName);
        _currentPath = _basePath;

        TimeSpan interval = TimeSpan.FromMilliseconds(flushIntervalMs);
        _flushTimer = new Timer(_ => Flush(), null, interval, interval);
    }

    /// <summary>
    /// Diagnostic label used in <c>FileLogger</c> rollover/error messages (e.g. "Session event log").
    /// Not part of the NDJSON output; purely for operator-facing diagnostics.
    /// </summary>
    protected abstract string DiagnosticName { get; }

    /// <summary>Serializes one record to a single NDJSON object (without the trailing newline).</summary>
    protected abstract string ToNdjsonLine(TRecord record);

    /// <summary>
    /// Queues one record for asynchronous append. Never blocks the caller and never throws into it
    /// (other than the null-argument guard). No-op only after disposal.
    /// </summary>
    /// <param name="record">The record to persist.</param>
    protected void Enqueue(TRecord record)
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
                    // Logging must never propagate; drop the batch rather than crash shutdown.
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

        Core.Logging.FileLogger.Info($"{DiagnosticName} reached its size cap, continuing in {_currentPath}");
    }

    private void LogWriteErrorOnce(string detail)
    {
        if (_writeErrorLogged)
        {
            return;
        }

        _writeErrorLogged = true;
        Core.Logging.FileLogger.Warn($"{DiagnosticName} write failed for {_currentPath}: {detail}");
    }
}
