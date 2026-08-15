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
using System.Text.Json.Serialization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.SessionSnapshot;

/// <summary>
/// File-based session snapshot persistence used by the restore-on-launch flow.
/// </summary>
public sealed class SessionSnapshotService(IConfigManager configManager) : ISessionSnapshotService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfigManager _configManager = configManager;

    public string SnapshotPath => Path.Combine(_configManager.ConfigPath, "session-snapshot.json");

    public async Task SaveAsync(IReadOnlyList<SessionSnapshotEntry> sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var snapshot = new SessionSnapshotFile
        {
            SchemaVersion = SessionSnapshotFile.CurrentSchemaVersion,
            SavedAtUtc = DateTime.UtcNow,
            Sessions = sessions
                .OrderBy(session => session.Order)
                .Select(CloneEntry)
                .ToList(),
        };

        var json = JsonSerializer.Serialize(snapshot, WriteOptions);
        var snapshotDirectory = Path.GetDirectoryName(SnapshotPath);
        if (!string.IsNullOrEmpty(snapshotDirectory))
        {
            Directory.CreateDirectory(snapshotDirectory);
        }

        var tempPath = $"{SnapshotPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, json, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            if (File.Exists(SnapshotPath))
            {
                File.Replace(tempPath, SnapshotPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, SnapshotPath);
            }

            FileLogger.Info($"Session snapshot saved: {snapshot.Sessions.Count} session(s)");
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    public async Task<SessionSnapshotFile?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SnapshotPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(SnapshotPath, cancellationToken).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<SessionSnapshotFile>(json, ReadOptions);
            if (snapshot is null)
            {
                return null;
            }

            var sessions = snapshot.Sessions ?? [];
            var originalCount = sessions.Count;
            snapshot.Sessions = sessions
                .Where(IsValidEntry)
                .OrderBy(session => session.Order)
                .ToList();

            var skippedCount = originalCount - snapshot.Sessions.Count;
            if (skippedCount > 0)
            {
                FileLogger.Warn($"Session snapshot skipped {skippedCount} invalid entrie(s).");
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.Warn($"Session snapshot load failed: {ex.Message}");
            return null;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(SnapshotPath))
            {
                File.Delete(SnapshotPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"Session snapshot delete failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static SessionSnapshotEntry CloneEntry(SessionSnapshotEntry entry)
    {
        return new SessionSnapshotEntry
        {
            ServerId = entry.ServerId,
            ConnectionType = entry.ConnectionType,
            Order = entry.Order,
            ExtraProperties = entry.ExtraProperties is null
                ? null
                : new Dictionary<string, JsonElement>(entry.ExtraProperties),
        };
    }

    private static bool IsValidEntry(SessionSnapshotEntry entry)
    {
        if (entry is null)
        {
            return false;
        }

        if (!Guid.TryParse(entry.ServerId, out _))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.ConnectionType))
        {
            return false;
        }

        return !ConnectionTypeCatalog.IsToolConnectionType(entry.ConnectionType);
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
