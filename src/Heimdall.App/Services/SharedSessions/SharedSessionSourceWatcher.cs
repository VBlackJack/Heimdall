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
/// after the last one. A source whose folder does not exist is not watched, and is tried again at
/// the next <see cref="Update"/>.</para>
/// </remarks>
public sealed class SharedSessionSourceWatcher : IDisposable
{
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
    public IReadOnlyDictionary<string, string> WatchedPaths
    {
        get
        {
            lock (_gate)
            {
                return _watches.ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.Ordinal);
            }
        }
    }

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

        FileSystemWatcher? fileWatcher = null;
        if (_watchFileSystem)
        {
            if (!Directory.Exists(directory))
            {
                FileLogger.Info($"Shared sessions: the folder of source {sourceId} does not exist yet ({directory}); it is not watched.");
                return null;
            }

            try
            {
                fileWatcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                fileWatcher.Changed += (_, _) => NotifyChanged(sourceId);
                fileWatcher.Created += (_, _) => NotifyChanged(sourceId);
                fileWatcher.Renamed += (_, _) => NotifyChanged(sourceId);
                fileWatcher.Deleted += (_, _) => NotifyChanged(sourceId);
                fileWatcher.Error += (_, e) => FileLogger.Warn(
                    $"Shared sessions: the watch on source {sourceId} reported an error: {e.GetException().Message}");
                fileWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                fileWatcher?.Dispose();
                FileLogger.Warn($"Shared sessions: source {sourceId} could not be watched: {ex.Message}");
                return null;
            }
        }

        ITimer timer = _timeProvider.CreateTimer(
            _ => Report(sourceId),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        return new Watch(path, fileWatcher, timer);
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

    private sealed class Watch(string path, FileSystemWatcher? fileWatcher, ITimer timer) : IDisposable
    {
        public string Path { get; } = path;

        public ITimer Timer { get; } = timer;

        public void Dispose()
        {
            fileWatcher?.Dispose();
            Timer.Dispose();
        }
    }
}
