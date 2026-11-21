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
using System.Text;
using Heimdall.Core.Security;

namespace Heimdall.Core.Logging;

/// <summary>
/// Simple file logger that writes timestamped messages to a log file.
/// Thread-safe via a producer-consumer queue flushed periodically.
/// </summary>
public sealed class FileLogger : IDisposable
{
    /// <summary>How many days a diagnostic log file is kept after it was last written.</summary>
    internal const int LogRetentionDays = 30;

    /// <summary>The size, in megabytes, past which a day's log continues in a numbered file.</summary>
    private const int MaxLogFileMegabytes = 10;

    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>The size cap of one log file, in bytes.</summary>
    internal const long MaxLogFileBytes = MaxLogFileMegabytes * BytesPerMegabyte;

    /// <summary>
    /// The search pattern for the files this logger writes: <c>heimdall_</c>, the day as eight
    /// characters, then anything up to <c>.log</c> (the <c>.N</c> of an overflow file).
    /// </summary>
    /// <remarks>
    /// Not strict on Windows: there a <c>?</c> may match no character at all, so
    /// <c>heimdall_notes.log</c> matches it. It only narrows the enumeration;
    /// <see cref="OwnLogFileName"/> decides.
    /// </remarks>
    private const string LogFileSearchPattern = "heimdall_????????*.log";

    /// <summary>
    /// The exact shape of a file this logger writes: the day's file or one of its numbered
    /// continuations. Session transcripts and the event and operation logs share the folder and
    /// must never match it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex OwnLogFileName = new(
        @"^heimdall_\d{8}(?:\.\d+)?\.log\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private const string LogFilePrefix = "heimdall_";
    private const string LogFileDateFormat = "yyyyMMdd";
    private const string LogFileExtension = ".log";

    /// <summary>
    /// How many entries the queue keeps while the file cannot be written. Past it the oldest are
    /// dropped and counted, so a full disk or a held file cannot grow memory without bound.
    /// </summary>
    internal const int DefaultMaxQueuedEntries = 10_000;

    /// <summary>The line written ahead of the next successful batch to say entries were lost.</summary>
    private const string DroppedSummaryFormat = "[{0:yyyy-MM-dd HH:mm:ss}] [WARN] [FileLogger] {1} entries dropped while the log could not be written";

    /// <summary>Milliseconds before the first timed flush.</summary>
    internal const int DefaultInitialFlushDelayMs = 1000;

    /// <summary>The queue triggers an early flush when it reaches the cap divided by this.</summary>
    private const int EarlyFlushDivisor = 2;

    /// <summary>Test seam: runs when a batch could not be written, just before it is re-queued.</summary>
    internal static Action? WriteFailedForTest;

    /// <summary>Test seam: restricts a new log file to its owner. Replaced only by tests.</summary>
    internal static Action<string> AclApplier = RestrictToOwner;

    /// <summary>The production restriction of a log file to its owner (Windows only).</summary>
    internal static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AclEnforcer.SetFileAcl(path);
        }
    }

    private static FileLogger? _instance;
    private readonly int _flushIntervalMs;
    private readonly bool _earlyFlush;
    private readonly int _maxQueued;
    private int _queuedCount;
    private long _dropped;
    private readonly string _logPath;
    private readonly long _maxFileBytes;
    private int _overflowIndex;
    private string? _aclAppliedPath;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();
    private static volatile bool _enabled = true;
    private bool _disposed;

    private FileLogger(
        string logDirectory,
        int flushIntervalMs,
        long maxFileBytes,
        int retentionDays,
        int maxQueuedEntries,
        int initialFlushDelayMs,
        bool earlyFlush)
    {
        Directory.CreateDirectory(logDirectory);
        _maxFileBytes = maxFileBytes;
        _maxQueued = maxQueuedEntries;
        _flushIntervalMs = flushIntervalMs;
        _earlyFlush = earlyFlush;
        _logPath = Path.Combine(logDirectory, $"{LogFilePrefix}{DateTime.Now.ToString(LogFileDateFormat, System.Globalization.CultureInfo.InvariantCulture)}{LogFileExtension}");
        PurgeExpiredLogs(logDirectory, retentionDays, DateTime.UtcNow, _logPath);
        var interval = TimeSpan.FromMilliseconds(flushIntervalMs);
        _flushTimer = new Timer(_ => FlushInternal(), null, TimeSpan.FromMilliseconds(initialFlushDelayMs), interval);
    }

    /// <summary>
    /// Initialize the global logger instance.
    /// </summary>
    public static void Initialize(string logDirectory, int flushIntervalMs = 2000)
        => Initialize(logDirectory, flushIntervalMs, MaxLogFileBytes, LogRetentionDays);

    /// <summary>
    /// Initialize with explicit bounds, so a test can use a size cap of a few bytes.
    /// </summary>
    internal static void Initialize(
        string logDirectory,
        int flushIntervalMs,
        long maxFileBytes,
        int retentionDays,
        int maxQueuedEntries = DefaultMaxQueuedEntries,
        int initialFlushDelayMs = DefaultInitialFlushDelayMs,
        bool earlyFlush = true)
    {
        _instance?.Dispose();
        _instance = new FileLogger(logDirectory, flushIntervalMs, maxFileBytes, retentionDays, maxQueuedEntries, initialFlushDelayMs, earlyFlush);
    }

    /// <summary>
    /// Deletes the diagnostic log files of <paramref name="logDirectory"/> last written more
    /// than <paramref name="retentionDays"/> days before <paramref name="nowUtc"/>, and returns
    /// how many it deleted.
    /// </summary>
    /// <remarks>
    /// Only this logger's own files are candidates: a name searched with
    /// <see cref="LogFileSearchPattern"/> in the folder itself (no sub-folder) that also has the
    /// exact shape of <see cref="OwnLogFileName"/>. A session transcript, the event log or
    /// anything the operator keeps there is never touched, and neither is
    /// <paramref name="activePath"/>. Best effort: a file that is held or protected stays for
    /// the next start.
    /// </remarks>
    internal static int PurgeExpiredLogs(string logDirectory, int retentionDays, DateTime nowUtc, string? activePath = null)
    {
        if (retentionDays <= 0 || !Directory.Exists(logDirectory))
        {
            return 0;
        }

        var cutoff = nowUtc.AddDays(-retentionDays);
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, LogFileSearchPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase)
                        || !OwnLogFileName.IsMatch(Path.GetFileName(path))
                        || File.GetLastWriteTimeUtc(path) >= cutoff)
                    {
                        continue;
                    }

                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held or protected: it stays for the next start.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort; the logger must start whatever the folder holds.
        }

        return deleted;
    }

    /// <summary>
    /// Enables or disables logging at runtime. When disabled, messages are
    /// silently discarded. Thread-safe.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// True while log messages are being kept. A caller that switches logging off or on for a
    /// while reads this first, so it can put the previous state back.
    /// </summary>
    public static bool IsEnabled => _enabled;

    /// <summary>
    /// Log a message at the given level.
    /// </summary>
    public static void Log(string level, string message)
    {
        if (!_enabled) return;

        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        _instance?.EnqueueBounded(entry);

        // Also write to debug output for development
        System.Diagnostics.Debug.WriteLine(entry);
    }

    /// <summary>
    /// Adds an entry without taking a lock, then drops the oldest ones while the queue is over
    /// its cap. The count is kept by hand because ConcurrentQueue.Count is not cheap.
    /// </summary>
    private void EnqueueBounded(string entry)
    {
        _queue.Enqueue(entry);
        var queued = Interlocked.Increment(ref _queuedCount);
        if (_earlyFlush && queued == _maxQueued / EarlyFlushDivisor)
        {
            RequestEarlyFlush();
        }

        while (Volatile.Read(ref _queuedCount) > _maxQueued && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queuedCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Asks the timer for a flush right now, on its own thread, so Log never waits for the file.
    /// The cap keeps a burst from losing entries a prompt write would have kept.
    /// </summary>
    private void RequestEarlyFlush()
    {
        try
        {
            _flushTimer.Change(0, _flushIntervalMs);
        }
        catch (ObjectDisposedException)
        {
            // The logger is shutting down; the final flush takes what is queued.
        }
    }

    public static void Debug(string message) => Log("DEBUG", message);
    public static void Debug(string message, Exception ex) => Log("DEBUG", $"{message}: {ex.Message}");
    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Error(string message, Exception ex) => Log("ERROR", $"{message}: {ex.Message}");

    /// <summary>
    /// Records an exception with everything a reader needs to diagnose it: its type,
    /// its stack, and the whole inner-exception chain.
    /// </summary>
    /// <remarks>
    /// <see cref="Error(string, Exception)"/> keeps only <see cref="Exception.Message"/>,
    /// which on a crash path is the same sentence the user just read in the dialog. The
    /// log is the one artefact they can send when reporting the failure, so it has to
    /// hold more than the dialog did. Reserved for that case: it is far more verbose
    /// than the ordinary error line and would drown a routine log.
    /// </remarks>
    public static void ErrorDetailed(string message, Exception ex) => Log("ERROR", $"{message}: {ex}");

    /// <summary>
    /// Records a contained fault with the exception's type, its stack, and the inner chain.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="ErrorDetailed"/> for a fault that was caught and worked
    /// around rather than fatal. The session teardown at exit logged only
    /// <see cref="Exception.Message"/>, and a NullReferenceException's message names no
    /// reference: a whole shutdown's worth of that line could not say which one was null.
    /// Reserved, like its counterpart, for a catch whose message alone has already proven
    /// insufficient.
    /// </remarks>
    public static void WarnDetailed(string message, Exception ex) => Log("WARN", $"{message}: {ex}");

    /// <summary>Flushes all queued log entries to disk immediately.</summary>
    public static void Flush() => _instance?.FlushInternal();

    private void FlushInternal()
    {
        if (_disposed || _queue.IsEmpty) return;

        lock (_writeLock)
        {
            if (_disposed || _queue.IsEmpty) return;

            List<string> entries = [];
            var sb = new StringBuilder();
            while (_queue.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _queuedCount);
                entries.Add(entry);
                sb.AppendLine(entry);
            }

            if (sb.Length == 0) return;

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
            {
                sb.Insert(0, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    DroppedSummaryFormat,
                    DateTime.Now,
                    dropped) + Environment.NewLine);
            }

            var text = sb.ToString();
            int[] retryDelaysMs = [10, 50, 200];
            string? writtenPath = null;
            for (var attempt = 0; attempt <= retryDelaysMs.Length && writtenPath is null; attempt++)
            {
                try
                {
                    var path = ResolveWritablePath(Encoding.UTF8.GetByteCount(text));
                    File.AppendAllText(path, text, Encoding.UTF8);
                    writtenPath = path;
                }
                catch (IOException) when (attempt < retryDelaysMs.Length)
                {
                    Thread.Sleep(retryDelaysMs[attempt]);
                }
                catch (IOException)
                {
                    WriteFailedForTest?.Invoke();
                    RequeueNewest(entries, dropped);
                    return;
                }
                catch
                {
                    Interlocked.Add(ref _dropped, dropped + entries.Count);
                    return;
                }
            }

            if (writtenPath is not null)
            {
                ApplyAclOnce(writtenPath);
            }
        }
    }

    /// <summary>
    /// Restricts a newly written file to its owner. Runs after the batch is on disk and touches
    /// neither the queue nor the dropped count: a failure here must never make the batch be
    /// written twice. A path whose restriction failed is tried again on the next flush.
    /// </summary>
    private void ApplyAclOnce(string path)
    {
        if (string.Equals(_aclAppliedPath, path, StringComparison.Ordinal) || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            AclApplier(path);
            _aclAppliedPath = path;
        }
        catch
        {
            // Best effort, as before: the log itself must keep working.
        }
    }

    /// <summary>
    /// Puts back the newest entries of a failed batch that fit under the cap and counts the rest
    /// as dropped, together with the count the failed write was carrying. Re-enqueued entries
    /// land after any entry logged meanwhile, so the order is not strictly chronological.
    /// </summary>
    private void RequeueNewest(List<string> entries, long carriedDropped)
    {
        var room = Math.Max(0, _maxQueued - Volatile.Read(ref _queuedCount));
        var keep = Math.Min(entries.Count, room);
        Interlocked.Add(ref _dropped, carriedDropped + (entries.Count - keep));
        for (var i = entries.Count - keep; i < entries.Count; i++)
        {
            _queue.Enqueue(entries[i]);
            Interlocked.Increment(ref _queuedCount);
        }
    }

    /// <summary>
    /// The file the next <paramref name="incomingBytes"/> go to: the day's file, or the first
    /// numbered continuation (<c>heimdall_20261027.1.log</c>) with room for them. A file that
    /// is empty takes whatever arrives, so a single oversized batch is never refused. Called
    /// with the write lock held.
    /// </summary>
    private string ResolveWritablePath(long incomingBytes)
    {
        while (true)
        {
            var path = _overflowIndex == 0
                ? _logPath
                : Path.ChangeExtension(_logPath, null) + "." + _overflowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + LogFileExtension;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length + incomingBytes <= _maxFileBytes)
            {
                return path;
            }

            _overflowIndex++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Order matters: stop timer first, then flush remaining entries,
        // then mark disposed. Setting _disposed before Flush() would
        // cause the final flush to skip (Flush checks _disposed).
        _flushTimer.Dispose();
        FlushInternal();
        _disposed = true;
    }
}
