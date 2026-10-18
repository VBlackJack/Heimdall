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

using System.IO;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Watches the file of every enabled shared source and reports a source once its file has
/// stayed quiet for the debounce delay.
/// </summary>
/// <remarks>
/// <para>The pattern of the remote file editor: the folder is watched, filtered on the file name,
/// for a change, a creation, a rename and a deletion. Editors and sync clients (OneDrive,
/// SharePoint) save by writing a temporary file and renaming it over the original, or by deleting
/// and recreating it; a watch on the change alone misses those saves.</para>
/// <para>Every event re-arms the source's timer, so a save made of several writes is read once,
/// after the last one.</para>
/// <para>A watch that cannot start or stops working is tried again every
/// <see cref="RewatchInterval"/>: a folder that does not exist yet (a share mounted later, a sync
/// client still downloading), or a watcher the system dropped (a network disconnection, an
/// overflowing buffer), which only reports an error once. Once the watch is back the source is
/// read again, since what changed in the meantime was not seen.</para>
/// </remarks>
public sealed class SharedSessionSourceWatcher : IDisposable
{
    /// <summary>
    /// How often a source that is not watched is tried again. Long enough that a share out of
    /// reach for a day costs nothing worth measuring; short enough that a share mounted after the
    /// start is followed within the time a user takes to notice.
    /// </summary>
    internal static readonly TimeSpan RewatchInterval = TimeSpan.FromSeconds(30);

    private readonly Func<string, Task> _onSourceChanged;
    private readonly TimeProvider _timeProvider;
    private readonly bool _watchFileSystem;
    private readonly object _gate = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private TimeSpan _debounce;
    private bool _disposed;

    /// <param name="onSourceChanged">Called with the source identifier once its file is quiet.</param>
    /// <param name="timeProvider">The clock of the debounce timers.</param>
    /// <param name="watchFileSystem">
    /// False to keep the timers without opening file system watchers; a test then reports events
    /// through <see cref="NotifyChanged"/>.
    /// </param>
    public SharedSessionSourceWatcher(
        Func<string, Task> onSourceChanged,
        TimeProvider? timeProvider = null,
        bool watchFileSystem = true)
    {
        _onSourceChanged = onSourceChanged ?? throw new ArgumentNullException(nameof(onSourceChanged));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _watchFileSystem = watchFileSystem;
    }

    /// <summary>The sources watched now, with the file each one is watched for.</summary>
    public IReadOnlyDictionary<string, string> WatchedPaths => PathsWhere(watching: true);

    /// <summary>The sources not watched yet or any more, tried again every <see cref="RewatchInterval"/>.</summary>
    internal IReadOnlyDictionary<string, string> PendingPaths => PathsWhere(watching: false);

    private Dictionary<string, string> PathsWhere(bool watching)
    {
        lock (_gate)
        {
            return _watches
                .Where(pair => IsWatching(pair.Value) == watching)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.Ordinal);
        }
    }

    /// <summary>Whether a watch is live; without file system watchers every watch counts as live.</summary>
    private bool IsWatching(Watch watch) => !_watchFileSystem || watch.FileWatcher is not null;

    /// <summary>
    /// Watches exactly <paramref name="sources"/>: a source no longer listed, or listed with
    /// another file, stops being watched; a new one starts.
    /// </summary>
    /// <param name="sources">The enabled sources and their files, environment variables expanded.</param>
    /// <param name="debounce">How long a file must stay quiet before it is reported.</param>
    public void Update(IReadOnlyDictionary<string, string> sources, TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(sources);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce = debounce;
            foreach (string sourceId in _watches.Keys.ToList())
            {
                if (!sources.TryGetValue(sourceId, out string? path)
                    || !string.Equals(path, _watches[sourceId].Path, StringComparison.OrdinalIgnoreCase))
                {
                    _watches[sourceId].Dispose();
                    _watches.Remove(sourceId);
                }
            }

            foreach ((string sourceId, string path) in sources)
            {
                if (!_watches.ContainsKey(sourceId) && TryCreate(sourceId, path) is { } watch)
                {
                    _watches[sourceId] = watch;
                }
            }
        }
    }

    /// <summary>How long a file must stay quiet before it is reported.</summary>
    internal TimeSpan Debounce
    {
        get
        {
            lock (_gate)
            {
                return _debounce;
            }
        }
    }

    /// <summary>
    /// Changes how long a file must stay quiet before it is reported, for the changes that come
    /// next; the watches themselves are left as they are.
    /// </summary>
    public void SetDebounce(TimeSpan debounce)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _debounce = debounce;
            }
        }
    }

    /// <summary>
    /// Re-arms the timer of <paramref name="sourceId"/>: the source is reported once nothing else
    /// happened to its file for the debounce delay.
    /// </summary>
    internal void NotifyChanged(string sourceId)
    {
        lock (_gate)
        {
            if (!_disposed && _watches.TryGetValue(sourceId, out Watch? watch))
            {
                watch.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Watch watch in _watches.Values)
            {
                watch.Dispose();
            }

            _watches.Clear();
        }
    }

    private Watch? TryCreate(string sourceId, string path)
    {
        string? directory;
        string fileName;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(path));
            fileName = Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            FileLogger.Warn($"Shared sessions: source {sourceId} is not watched, its path is not usable: {ex.Message}");
            return null;
        }

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        ITimer timer = _timeProvider.CreateTimer(
            _ => Report(sourceId),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        ITimer rewatchTimer = _timeProvider.CreateTimer(
            _ => Rewatch(sourceId),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        var watch = new Watch(path, directory, fileName, timer, rewatchTimer);
        if (_watchFileSystem && !TryStartFileWatcher(sourceId, watch))
        {
            watch.RewatchTimer.Change(RewatchInterval, RewatchInterval);
        }

        return watch;
    }

    /// <summary>Opens the file system watcher of <paramref name="watch"/>; false when it cannot be opened now.</summary>
    /// <param name="sourceId">The source watched.</param>
    /// <param name="watch">Its watch.</param>
    /// <param name="retry">Whether this is a periodic retry, which does not log a folder still missing.</param>
    /// <remarks>Called under <see cref="_gate"/>.</remarks>
    private bool TryStartFileWatcher(string sourceId, Watch watch, bool retry = false)
    {
        if (!Directory.Exists(watch.Directory))
        {
            if (retry)
            {
                return false;
            }

            FileLogger.Info(
                $"Shared sessions: the folder of source {sourceId} does not exist yet ({watch.Directory}); "
                + $"tried again every {RewatchInterval.TotalSeconds:0} s.");
            return false;
        }

        FileSystemWatcher? fileWatcher = null;
        try
        {
            fileWatcher = new FileSystemWatcher(watch.Directory, watch.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            fileWatcher.Changed += (_, _) => NotifyChanged(sourceId);
            fileWatcher.Created += (_, _) => NotifyChanged(sourceId);
            fileWatcher.Renamed += (_, _) => NotifyChanged(sourceId);
            fileWatcher.Deleted += (_, _) => NotifyChanged(sourceId);
            FileSystemWatcher raising = fileWatcher;
            fileWatcher.Error += (_, e) => OnWatchFailed(sourceId, raising, e.GetException().Message);
            fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            fileWatcher?.Dispose();
            FileLogger.Warn($"Shared sessions: source {sourceId} could not be watched: {ex.Message}");
            return false;
        }

        watch.FileWatcher = fileWatcher;
        return true;
    }

    /// <summary>
    /// Drops a file system watcher the system reported an error on and tries again every
    /// <see cref="RewatchInterval"/>: a watcher that failed raises nothing more, so the source
    /// would otherwise stop following its file without a word.
    /// </summary>
    /// <param name="sourceId">The source whose watch failed.</param>
    /// <param name="failed">The watcher that reported the error; a newer one is left alone.</param>
    /// <param name="reason">What the system said.</param>
    internal void OnWatchFailed(string sourceId, FileSystemWatcher? failed, string reason)
    {
        FileLogger.Warn(
            $"Shared sessions: the watch on source {sourceId} reported an error ({reason}); "
            + $"tried again every {RewatchInterval.TotalSeconds:0} s.");
        lock (_gate)
        {
            if (_disposed
                || !_watches.TryGetValue(sourceId, out Watch? watch)
                || (failed is not null && !ReferenceEquals(watch.FileWatcher, failed)))
            {
                return;
            }

            watch.FileWatcher?.Dispose();
            watch.FileWatcher = null;
            watch.RewatchTimer.Change(RewatchInterval, RewatchInterval);
        }
    }

    /// <summary>
    /// Tries again to watch a source that is not watched; once it is, reads it again, since what
    /// happened to its file in the meantime was not seen.
    /// </summary>
    private void Rewatch(string sourceId)
    {
        lock (_gate)
        {
            if (_disposed
                || !_watches.TryGetValue(sourceId, out Watch? watch)
                || watch.FileWatcher is not null
                || !TryStartFileWatcher(sourceId, watch, retry: true))
            {
                return;
            }

            watch.RewatchTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            FileLogger.Info($"Shared sessions: source {sourceId} is watched again; it is read again.");
        }

        NotifyChanged(sourceId);
    }

    private void Report(string sourceId)
    {
        lock (_gate)
        {
            if (_disposed || !_watches.ContainsKey(sourceId))
            {
                return;
            }
        }

        _ = ReportAsync(sourceId);
    }

    private async Task ReportAsync(string sourceId)
    {
        try
        {
            await _onSourceChanged(sourceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Shared sessions: reloading source {sourceId} after a change failed: {ex.Message}");
        }
    }

    private sealed class Watch(string path, string directory, string fileName, ITimer timer, ITimer rewatchTimer) : IDisposable
    {
        public string Path { get; } = path;

        public string Directory { get; } = directory;

        public string FileName { get; } = fileName;

        /// <summary>The debounce timer: fires once the file has been quiet long enough.</summary>
        public ITimer Timer { get; } = timer;

        /// <summary>Fires every <see cref="RewatchInterval"/> while <see cref="FileWatcher"/> is null.</summary>
        public ITimer RewatchTimer { get; } = rewatchTimer;

        /// <summary>The live watcher; null while the folder cannot be watched. Changed under the gate.</summary>
        public FileSystemWatcher? FileWatcher { get; set; }

        public void Dispose()
        {
            FileWatcher?.Dispose();
            FileWatcher = null;
            Timer.Dispose();
            RewatchTimer.Dispose();
        }
    }
}
