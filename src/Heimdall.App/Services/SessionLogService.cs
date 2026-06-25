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
using Timer = System.Threading.Timer;
using Heimdall.Core.Security;
using Heimdall.Terminal.Logging;
using Microsoft.Extensions.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Writes per-session plain-text transcripts under a fixed root directory. Each active session
/// owns its own <see cref="StreamWriter"/>, streaming UTF-8 decoder, ANSI stripper, and buffer;
/// a single shared timer flushes all of them. Output is decoded and stripped before buffering,
/// files roll over on a size cap, and each file is hardened with a restrictive ACL on first write.
/// </summary>
public sealed class SessionLogService : ISessionLogService
{
    private const string LogFileExtension = ".log";

    /// <summary>Backoff schedule (ms) for transient IO failures; mirrors <c>FileLogger</c>.</summary>
    private static readonly int[] RetryDelaysMs = [10, 50, 200];

    private readonly string _rootDirectory;
    private readonly SessionLogOptions _options;
    private readonly ILogger<SessionLogService> _logger;
    private readonly Func<string, string> _localize;
    private readonly ConcurrentDictionary<string, SessionLogWriter> _writers = new(StringComparer.Ordinal);
    private readonly Timer _flushTimer;
    private readonly object _startLock = new();
    private bool _disposed;

    /// <summary>
    /// Creates the service. Nothing is written until <see cref="StartSession"/> is called.
    /// </summary>
    /// <param name="rootDirectory">Directory that receives the log files (created on demand).</param>
    /// <param name="options">Size cap and flush interval. Both values must be strictly positive.</param>
    /// <param name="logger">Diagnostic sink for non-fatal failures (rollover notices, IO errors).</param>
    /// <param name="localize">Resolves i18n keys to format strings for header, footer, and diagnostics.</param>
    public SessionLogService(
        string rootDirectory,
        SessionLogOptions options,
        ILogger<SessionLogService> logger,
        Func<string, string> localize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FlushIntervalMs);

        _rootDirectory = rootDirectory;
        _options = options;
        _logger = logger;
        _localize = localize;

        TimeSpan interval = TimeSpan.FromMilliseconds(options.FlushIntervalMs);
        _flushTimer = new Timer(_ => FlushAll(), null, interval, interval);
    }

    /// <inheritdoc />
    public string? StartSession(SessionLogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SessionKey);

        if (_disposed)
        {
            return null;
        }

        // Serialize starts so file-name disambiguation and writer registration are atomic: two
        // sessions to the same host within the same second must not resolve to the same file.
        lock (_startLock)
        {
            try
            {
                Directory.CreateDirectory(_rootDirectory);

                // Replace any pre-existing writer for this key (restart semantics, e.g. reconnect).
                // Close the previous segment with a proper footer so its transcript is not left
                // header-only — keeping the audit trail symmetric across reconnected sessions.
                if (_writers.TryRemove(context.SessionKey, out SessionLogWriter? existing))
                {
                    existing.Close(BuildFooter(existing.StartedUtc));
                    existing.Dispose();
                }

                string fullPath = ResolveUniquePath(BuildFileName(context));
                string header = BuildHeader(context);

                SessionLogWriter writer = new SessionLogWriter(fullPath, _options.MaxFileBytes, _logger, _localize);

                // Materialize the file with its header now so callers observe a ready transcript.
                if (!writer.Open(header))
                {
                    writer.Dispose();
                    return null;
                }

                _writers[context.SessionKey] = writer;
                return fullPath;
            }
            catch (Exception ex)
            {
                WarnDiagnostic("LogSessionLogWriteError", context.SessionKey, ex.Message);
                return null;
            }
        }
    }

    // Resolves a collision-free path under the root directory. Disambiguates against both existing
    // files on disk and the paths held by currently active writers, appending "_N" before ".log".
    private string ResolveUniquePath(string fileName)
    {
        string stem = fileName.EndsWith(LogFileExtension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^LogFileExtension.Length]
            : fileName;

        string candidate = Path.Combine(_rootDirectory, $"{stem}{LogFileExtension}");
        int suffix = 1;
        while (IsPathTaken(candidate))
        {
            candidate = Path.Combine(
                _rootDirectory,
                $"{stem}_{suffix.ToString(CultureInfo.InvariantCulture)}{LogFileExtension}");
            suffix++;
        }

        return candidate;
    }

    private bool IsPathTaken(string candidatePath)
    {
        if (File.Exists(candidatePath))
        {
            return true;
        }

        foreach (SessionLogWriter writer in _writers.Values)
        {
            if (string.Equals(writer.BasePath, candidatePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void WriteOutput(string sessionKey, ReadOnlySpan<byte> data)
    {
        if (_disposed || string.IsNullOrEmpty(sessionKey) || data.IsEmpty)
        {
            return;
        }

        if (_writers.TryGetValue(sessionKey, out SessionLogWriter? writer))
        {
            writer.Append(data);
        }
    }

    /// <inheritdoc />
    public void StopSession(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            return;
        }

        if (_writers.TryRemove(sessionKey, out SessionLogWriter? writer))
        {
            string footer = BuildFooter(writer.StartedUtc);
            writer.Close(footer);
            writer.Dispose();
        }
    }

    /// <inheritdoc />
    public bool IsSessionActive(string sessionKey)
    {
        return !string.IsNullOrEmpty(sessionKey) && _writers.ContainsKey(sessionKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Stop the timer first so no flush races with the final teardown, then drain every writer.
        _flushTimer.Dispose();
        _disposed = true;

        foreach (KeyValuePair<string, SessionLogWriter> entry in _writers.ToArray())
        {
            if (_writers.TryRemove(entry.Key, out SessionLogWriter? writer))
            {
                string footer = BuildFooter(writer.StartedUtc);
                writer.Close(footer);
                writer.Dispose();
            }
        }
    }

    private void FlushAll()
    {
        if (_disposed)
        {
            return;
        }

        foreach (SessionLogWriter writer in _writers.Values)
        {
            writer.Flush();
        }
    }

    private string BuildHeader(SessionLogContext context)
    {
        string template = _localize("SessionLogHeader");
        string body = string.Format(
            CultureInfo.InvariantCulture,
            template,
            FormatTimestamp(context.StartedUtc),
            context.Protocol,
            context.Host,
            context.DisplayTitle);
        return body + Environment.NewLine;
    }

    private string BuildFooter(DateTime startedUtc)
    {
        DateTime stoppedUtc = DateTime.UtcNow;
        TimeSpan duration = stoppedUtc - startedUtc;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        string template = _localize("SessionLogFooter");
        string body = string.Format(
            CultureInfo.InvariantCulture,
            template,
            FormatTimestamp(stoppedUtc),
            duration.ToString("c", CultureInfo.InvariantCulture));
        return body + Environment.NewLine;
    }

    private void WarnDiagnostic(string key, params object[] arguments)
    {
        string message = string.Format(CultureInfo.InvariantCulture, _localize(key), arguments);
        // Pass the already-formatted text as a value so user-derived braces cannot be mis-parsed
        // as a logging message template.
        _logger.LogWarning("{SessionLogDiagnostic}", message);
    }

    private static string FormatTimestamp(DateTime utc)
    {
        DateTime asUtc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return asUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string BuildFileName(SessionLogContext context)
    {
        string timestamp = (context.StartedUtc.Kind == DateTimeKind.Utc
            ? context.StartedUtc
            : context.StartedUtc.ToUniversalTime())
            .ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        string raw = $"{context.Protocol}_{context.Host}_{timestamp}{LogFileExtension}";
        return Sanitize(raw);
    }

    private static string Sanitize(string fileName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    /// <summary>
    /// One session's writer: a single output file (with size-based rollover continuations), its own
    /// stateful decoder/stripper, and a buffer. All state is guarded by <c>_lock</c> because the
    /// streaming helpers are not thread-safe and flush runs on a pool thread concurrently with
    /// terminal-thread appends.
    /// </summary>
    private sealed class SessionLogWriter : IDisposable
    {
        private readonly object _lock = new();
        private readonly string _basePath;
        private readonly long _maxBytes;
        private readonly ILogger _logger;
        private readonly Func<string, string> _localize;
        private readonly StreamingUtf8Decoder _decoder = new StreamingUtf8Decoder();
        private readonly StreamingAnsiStripper _stripper = new StreamingAnsiStripper();
        private readonly ConcurrentQueue<string> _queue = new();

        private StreamWriter? _stream;
        private string _currentPath;
        private long _currentBytes;
        private int _rolloverIndex;
        private bool _writeErrorLogged;
        private bool _closed;
        private bool _disposed;

        internal SessionLogWriter(string basePath, long maxBytes, ILogger logger, Func<string, string> localize)
        {
            _basePath = basePath;
            _currentPath = basePath;
            _maxBytes = maxBytes;
            _logger = logger;
            _localize = localize;
            StartedUtc = DateTime.UtcNow;
        }

        internal DateTime StartedUtc { get; }

        /// <summary>The first (pre-rollover) file path; used by the service for collision detection.</summary>
        internal string BasePath => _basePath;

        /// <summary>Opens the first file and writes the header. Returns false if the file cannot be created.</summary>
        internal bool Open(string header)
        {
            lock (_lock)
            {
                _queue.Enqueue(header);
                FlushLocked();
                return _stream is not null;
            }
        }

        /// <summary>Decodes, strips, and buffers a chunk of raw output bytes. Never throws.</summary>
        internal void Append(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                if (_closed || _disposed)
                {
                    return;
                }

                string decoded = _decoder.DecodeChunk(data);
                string clean = _stripper.Strip(decoded);
                if (clean.Length > 0)
                {
                    _queue.Enqueue(clean);
                }
            }
        }

        /// <summary>Flushes the buffer to disk. Never throws.</summary>
        internal void Flush()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                FlushLocked();
            }
        }

        /// <summary>Flushes any decoder residue, appends the footer (if any), and flushes a last time.</summary>
        internal void Close(string? footer)
        {
            lock (_lock)
            {
                if (_closed)
                {
                    return;
                }

                string residue = _decoder.Flush();
                if (residue.Length > 0)
                {
                    string clean = _stripper.Strip(residue);
                    if (clean.Length > 0)
                    {
                        _queue.Enqueue(clean);
                    }
                }

                if (footer is not null)
                {
                    _queue.Enqueue(footer);
                }

                FlushLocked();
                _closed = true;

                if (_stream is not null)
                {
                    try
                    {
                        _stream.Flush();
                        _stream.Dispose();
                    }
                    catch (IOException)
                    {
                        // Best-effort close; nothing actionable beyond the prior diagnostics.
                    }

                    _stream = null;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_stream is not null)
                {
                    try
                    {
                        _stream.Flush();
                        _stream.Dispose();
                    }
                    catch (IOException)
                    {
                        // Already torn down or unwritable; ignore.
                    }

                    _stream = null;
                }

                _disposed = true;
            }
        }

        // Assumes _lock is held. Drains the buffer to disk with bounded retry on transient IO faults.
        private void FlushLocked()
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
                        WriteEntry(batch[written]);
                    }

                    _stream?.Flush();
                    return;
                }
                catch (IOException) when (attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
                catch (IOException ex)
                {
                    // Persistent failure: preserve the unwritten remainder for the next flush and
                    // emit a single diagnostic so the hot path is never crashed or spammed.
                    for (int i = written; i < batch.Count; i++)
                    {
                        _queue.Enqueue(batch[i]);
                    }

                    LogWriteErrorOnce(ex.Message);
                    return;
                }
                catch
                {
                    // Output logging must never propagate; drop the batch rather than crash a session.
                    return;
                }
            }
        }

        // Assumes _lock is held. Writes one entry, rolling the file over first if the cap is reached.
        private void WriteEntry(string text)
        {
            long byteCount = Encoding.UTF8.GetByteCount(text);

            if (_stream is not null && _currentBytes > 0 && _currentBytes + byteCount > _maxBytes)
            {
                RollOver();
            }

            EnsureStream();
            _stream!.Write(text);
            _currentBytes += byteCount;
        }

        // Assumes _lock is held. Opens the current file (append, UTF-8), hardening it on first creation.
        private void EnsureStream()
        {
            if (_stream is not null)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(_currentPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_currentPath))
            {
                // Create the file closed, then apply the restrictive ACL before any data is written.
                // Mirrors FileLogger: SetAccessControl runs on a handle-free file so it cannot hit a
                // sharing violation against our own still-open StreamWriter.
                using (FileStream created = new FileStream(_currentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                if (OperatingSystem.IsWindows())
                {
                    AclEnforcer.SetFileAcl(_currentPath);
                }
            }

            _stream = new StreamWriter(_currentPath, append: true, Encoding.UTF8)
            {
                AutoFlush = false
            };
            _currentBytes = new FileInfo(_currentPath).Length;
        }

        // Assumes _lock is held. Closes the current file and switches to the next ".N.log" continuation.
        private void RollOver()
        {
            if (_stream is not null)
            {
                try
                {
                    _stream.Flush();
                    _stream.Dispose();
                }
                catch (IOException)
                {
                    // Continue regardless; the continuation file is the recovery path.
                }

                _stream = null;
            }

            _rolloverIndex++;
            string baseWithoutExtension = _basePath.EndsWith(LogFileExtension, StringComparison.OrdinalIgnoreCase)
                ? _basePath[..^LogFileExtension.Length]
                : _basePath;
            _currentPath = $"{baseWithoutExtension}.{_rolloverIndex.ToString(CultureInfo.InvariantCulture)}{LogFileExtension}";
            _currentBytes = 0;

            LogRollover(_currentPath);
        }

        private void LogRollover(string continuationPath)
        {
            string message = string.Format(
                CultureInfo.InvariantCulture,
                _localize("LogSessionLogRollover"),
                continuationPath);
            _logger.LogWarning("{SessionLogDiagnostic}", message);
        }

        private void LogWriteErrorOnce(string detail)
        {
            if (_writeErrorLogged)
            {
                return;
            }

            _writeErrorLogged = true;
            string message = string.Format(
                CultureInfo.InvariantCulture,
                _localize("LogSessionLogWriteError"),
                _currentPath,
                detail);
            _logger.LogWarning("{SessionLogDiagnostic}", message);
        }
    }
}
