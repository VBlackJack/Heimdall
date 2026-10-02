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
using System.Text;
using System.Text.Json;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// What the file browser keeps between runs: the bookmarks of each server and the folder the last
/// download went to.
/// </summary>
public interface ISftpBrowserStateStore
{
    /// <summary>Gets the bookmarked remote paths of one server.</summary>
    /// <param name="endpointKey">The normalized host, port and user of the server.</param>
    IReadOnlyList<string> LoadBookmarks(string endpointKey);

    /// <summary>Replaces the bookmarked remote paths of one server.</summary>
    void SaveBookmarks(string endpointKey, IReadOnlyList<string> bookmarks);

    /// <summary>Gets the folder the last download went to, or <see langword="null"/> when there is none.</summary>
    string? LoadLastDownloadFolder();

    /// <summary>Remembers the folder a download went to.</summary>
    void SaveLastDownloadFolder(string folder);
}

/// <summary>
/// File-backed <see cref="ISftpBrowserStateStore"/>: one small JSON document in the application's
/// data directory.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no parameterless constructor, for the reason the password-preset storage
/// states: the production location is resolved once, in the composition root, and a test cannot
/// reach the operator's own file by constructing the store with nothing.
/// </para>
/// <para>
/// Bookmarks are keyed by server, not by pane: they used to live in memory per pane, so they were
/// lost with the tab and not shared between two panes on the same server. A failed read or write
/// is logged and never raised, because losing a bookmark must not stop a browsing session.
/// </para>
/// </remarks>
public sealed class SftpBrowserStateStore : ISftpBrowserStateStore
{
    private const string FileName = "sftp-browser-state.json";
    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _filePath;

    /// <summary>Creates a store rooted in the supplied directory.</summary>
    /// <param name="directoryPath">Where the state file lives.</param>
    public SftpBrowserStateStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _filePath = Path.Combine(directoryPath, FileName);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> LoadBookmarks(string endpointKey)
    {
        if (string.IsNullOrWhiteSpace(endpointKey))
        {
            return [];
        }

        lock (_gate)
        {
            State state = Read();
            return state.Bookmarks.TryGetValue(endpointKey, out List<string>? bookmarks)
                ? [.. bookmarks]
                : [];
        }
    }

    /// <inheritdoc />
    public void SaveBookmarks(string endpointKey, IReadOnlyList<string> bookmarks)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        if (string.IsNullOrWhiteSpace(endpointKey))
        {
            return;
        }

        lock (_gate)
        {
            State state = Read();
            if (bookmarks.Count == 0)
            {
                state.Bookmarks.Remove(endpointKey);
            }
            else
            {
                state.Bookmarks[endpointKey] = [.. bookmarks];
            }

            Write(state);
        }
    }

    /// <inheritdoc />
    public string? LoadLastDownloadFolder()
    {
        lock (_gate)
        {
            return Read().LastDownloadFolder;
        }
    }

    /// <inheritdoc />
    public void SaveLastDownloadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        lock (_gate)
        {
            State state = Read();
            state.LastDownloadFolder = folder;
            Write(state);
        }
    }

    private State Read()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new State();
            }

            string json = File.ReadAllText(_filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.Warn($"[SftpBrowserStateStore] The saved state could not be read: {ex.Message}");
            return new State();
        }
    }

    private void Write(State state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside the file and moved over it, so a crash leaves the old document whole
            // rather than a truncated one.
            string tempPath = _filePath + TempSuffix;
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, SerializerOptions), new UTF8Encoding(false));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"[SftpBrowserStateStore] The state could not be saved: {ex.Message}");
        }
    }

    private sealed class State
    {
        public Dictionary<string, List<string>> Bookmarks { get; set; } = new(StringComparer.Ordinal);

        public string? LastDownloadFolder { get; set; }
    }
}
