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

using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Persists split layout preferences so that when a user reconnects to
/// a server, previously paired servers are suggested at the top of the palette.
/// Thread-safe: all public methods are synchronized via lock.
/// </summary>
public sealed class SplitLayoutMemory
{
    private const int MaxEntries = 50;
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "split-layouts.json";

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<SplitLayoutEntry> _entries = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SplitLayoutMemory(string configDir)
    {
        _filePath = Path.Combine(configDir, FileName);
        lock (_lock)
        {
            Load();
        }
    }

    /// <summary>
    /// Records that two servers were split together with the given orientation and ratio.
    /// Updates existing entries or creates new ones, evicting oldest when at capacity.
    /// </summary>
    public void Record(string primaryServerId, string secondaryServerId,
        SplitOrientation orientation, double ratio = 0.5)
    {
        if (string.IsNullOrEmpty(primaryServerId) || string.IsNullOrEmpty(secondaryServerId))
            return;

        lock (_lock)
        {
            // Remove any existing entry for this pair (either direction)
            _entries.RemoveAll(e =>
                (string.Equals(e.PrimaryServerId, primaryServerId, StringComparison.Ordinal)
                 && string.Equals(e.SecondaryServerId, secondaryServerId, StringComparison.Ordinal))
                || (string.Equals(e.PrimaryServerId, secondaryServerId, StringComparison.Ordinal)
                    && string.Equals(e.SecondaryServerId, primaryServerId, StringComparison.Ordinal)));

            _entries.Insert(0, new SplitLayoutEntry
            {
                PrimaryServerId = primaryServerId,
                SecondaryServerId = secondaryServerId,
                Orientation = orientation,
                Ratio = ratio,
                LastUsed = DateTime.UtcNow
            });

            // Trim to max
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

            Save();
        }
    }

    /// <summary>
    /// Finds the most recent split partner for a given server ID.
    /// Returns null if no history is found.
    /// </summary>
    public SplitLayoutEntry? FindPartner(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return null;

        lock (_lock)
        {
            return _entries.FirstOrDefault(e =>
                string.Equals(e.PrimaryServerId, serverId, StringComparison.Ordinal)
                || string.Equals(e.SecondaryServerId, serverId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The ratio last used for this exact pair, seen from <paramref name="firstServerId"/>'s side:
    /// a pair recorded the other way round gives the mirrored share. Null when the pair is unknown.
    /// </summary>
    public double? FindRatio(string firstServerId, string secondServerId)
    {
        if (string.IsNullOrEmpty(firstServerId) || string.IsNullOrEmpty(secondServerId)) return null;

        lock (_lock)
        {
            foreach (SplitLayoutEntry entry in _entries)
            {
                if (string.Equals(entry.PrimaryServerId, firstServerId, StringComparison.Ordinal)
                    && string.Equals(entry.SecondaryServerId, secondServerId, StringComparison.Ordinal))
                {
                    return entry.Ratio;
                }

                if (string.Equals(entry.PrimaryServerId, secondServerId, StringComparison.Ordinal)
                    && string.Equals(entry.SecondaryServerId, firstServerId, StringComparison.Ordinal))
                {
                    return 1 - entry.Ratio;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Returns all known split partners for a given server ID, ordered by most recent.
    /// </summary>
    public IReadOnlyList<SplitLayoutEntry> FindAllPartners(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return [];

        lock (_lock)
        {
            return _entries.Where(e =>
                string.Equals(e.PrimaryServerId, serverId, StringComparison.Ordinal)
                || string.Equals(e.SecondaryServerId, serverId, StringComparison.Ordinal))
                .ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            string json = File.ReadAllText(_filePath);

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Legacy format: bare array without version wrapper
                _entries = JsonSerializer.Deserialize<List<SplitLayoutEntry>>(json, JsonOptions) ?? [];
            }
            else
            {
                SplitLayoutFile? wrapper = JsonSerializer.Deserialize<SplitLayoutFile>(json, JsonOptions);
                _entries = wrapper?.Entries ?? [];
            }
        }
        catch (Exception ex)
        {
            Logging.FileLogger.Warn($"Failed to load split layout memory: {ex.Message}");
            _entries = [];
        }
    }

    /// <summary>
    /// Atomic save via unique-temp-then-rename to prevent corruption on crash
    /// or concurrent writes from multiple processes.
    /// </summary>
    private void Save()
    {
        string? tempPath = null;
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            SplitLayoutFile wrapper = new SplitLayoutFile
            {
                Version = CurrentSchemaVersion,
                Entries = _entries
            };
            string json = JsonSerializer.Serialize(wrapper, JsonOptions);
            tempPath = Path.Combine(dir ?? ".", $"split-layouts.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
            tempPath = null;
        }
        catch (Exception ex)
        {
            Logging.FileLogger.Warn($"Failed to save split layout memory: {ex.Message}");
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch { /* best-effort cleanup */ }
            }
        }
    }
}

/// <summary>
/// Versioned file wrapper for split layout persistence.
/// Enables future schema migrations without data loss.
/// </summary>
internal sealed class SplitLayoutFile
{
    public int Version { get; set; } = 1;
    public List<SplitLayoutEntry> Entries { get; set; } = [];
}

/// <summary>
/// A recorded split layout pairing between two servers.
/// </summary>
public sealed class SplitLayoutEntry
{
    public string PrimaryServerId { get; set; } = "";
    public string SecondaryServerId { get; set; } = "";
    public SplitOrientation Orientation { get; set; }
    public double Ratio { get; set; } = 0.5;
    public DateTime LastUsed { get; set; }
}
