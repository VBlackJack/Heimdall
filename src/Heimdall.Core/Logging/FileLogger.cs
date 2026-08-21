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
    private static FileLogger? _instance;
    private readonly string _logPath;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();
    private static volatile bool _enabled = true;
    private bool _disposed;
    private bool _aclApplied;

    private FileLogger(string logDirectory, int flushIntervalMs)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"heimdall_{DateTime.Now:yyyyMMdd}.log");
        var interval = TimeSpan.FromMilliseconds(flushIntervalMs);
        _flushTimer = new Timer(_ => FlushInternal(), null, TimeSpan.FromSeconds(1), interval);
    }

    /// <summary>
    /// Initialize the global logger instance.
    /// </summary>
    public static void Initialize(string logDirectory, int flushIntervalMs = 2000)
    {
        _instance?.Dispose();
        _instance = new FileLogger(logDirectory, flushIntervalMs);
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
    /// Log a message at the given level.
    /// </summary>
    public static void Log(string level, string message)
    {
        if (!_enabled) return;

        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        _instance?._queue.Enqueue(entry);

        // Also write to debug output for development
        System.Diagnostics.Debug.WriteLine(entry);
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
                entries.Add(entry);
                sb.AppendLine(entry);
            }

            if (sb.Length == 0) return;

            int[] retryDelaysMs = [10, 50, 200];
            for (var attempt = 0; attempt <= retryDelaysMs.Length; attempt++)
            {
                try
                {
                    File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);

                    if (!_aclApplied && OperatingSystem.IsWindows())
                    {
                        AclEnforcer.SetFileAcl(_logPath);
                        _aclApplied = true;
                    }

                    return;
                }
                catch (IOException) when (attempt < retryDelaysMs.Length)
                {
                    Thread.Sleep(retryDelaysMs[attempt]);
                }
                catch (IOException)
                {
                    foreach (var originalEntry in entries)
                    {
                        _queue.Enqueue(originalEntry);
                    }

                    return;
                }
                catch
                {
                    return;
                }
            }
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
