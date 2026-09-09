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

using System.Security.Cryptography;
using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>One-level undo containing only organization fields, never connection credentials.</summary>
public sealed class TreeOrganizationHistory(IConfigManager config)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UndoEntry? _entry;
    public bool CanUndo => _entry is not null;

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation,
        Func<T, FolderRenamePlan?>? reverseFolder = null,
        IReadOnlyCollection<string>? serverIds = null)
    {
        await _gate.WaitAsync();
        try
        {
            Dictionary<string, Row> before = Snapshot(await config.LoadServersAsync());
            T result;
            try { result = await operation(); }
            catch { _entry = null; throw; }
            try
            {
                Dictionary<string, Row> after = Snapshot(await config.LoadServersAsync());
                FolderRenamePlan? reverse = reverseFolder?.Invoke(result);
                HashSet<string> scope = new(serverIds ?? [], StringComparer.Ordinal);
                Dictionary<string, Row> changed = before
                    .Where(pair => scope.Contains(pair.Key) && after.TryGetValue(pair.Key, out Row? value) && pair.Value != value)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (reverse is not null || changed.Count > 0)
                {
                    _entry = new(changed, after, reverse,
                        reverse is null ? "" : MetadataFingerprint(await config.LoadSettingsAsync()));
                }
            }
            catch (Exception ex)
            {
                // The operation already committed. Failure to offer undo must not report it as failed.
                _entry = null;
                Core.Logging.FileLogger.Error("Could not record tree organization undo", ex);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> UndoAsync()
    {
        await _gate.WaitAsync();
        try
        {
            UndoEntry? entry = _entry;
            if (entry is null) return false;
            _entry = null;
            if (entry.ReverseFolder is FolderRenamePlan reverse)
            {
                Dictionary<string, Row> current = Snapshot(await config.LoadServersAsync());
                if (!SameRows(current, entry.After)
                    || MetadataFingerprint(await config.LoadSettingsAsync()) != entry.Metadata)
                    return false;

                await FolderRenameService.MigrateAsync(config, reverse);
                return true;
            }

            return await config.MutateServersAsync(inventory =>
            {
                Dictionary<string, ServerProfileDto> byId = inventory.ToDictionary(row => row.Id, StringComparer.Ordinal);
                foreach ((string id, Row before) in entry.Before)
                {
                    if (!byId.TryGetValue(id, out ServerProfileDto? dto)
                        || !MatchesChangedFields(Row.From(dto), entry.After[id], before)) return false;
                }
                foreach ((string id, Row before) in entry.Before)
                {
                    ServerProfileDto dto = byId[id];
                    Row after = entry.After[id];
                    if (before.Name != after.Name) dto.DisplayName = before.Name;
                    if (before.Group != after.Group) dto.Group = before.Group;
                    if (before.Order != after.Order) dto.SortOrder = before.Order;
                }
                return true;
            });
        }
        finally { _gate.Release(); }
    }

    private static bool MatchesChangedFields(Row current, Row after, Row before) =>
        (before.Name == after.Name || current.Name == after.Name)
        && (before.Group == after.Group || current.Group == after.Group)
        && (before.Order == after.Order || current.Order == after.Order);

    private static Dictionary<string, Row> Snapshot(IEnumerable<ServerProfileDto> rows) =>
        rows.ToDictionary(row => row.Id, Row.From, StringComparer.Ordinal);

    private static bool SameRows(Dictionary<string, Row> left, Dictionary<string, Row> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out Row? value) && pair.Value == value);

    private static string MetadataFingerprint(AppSettings settings) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Folders = settings.EmptyGroups.Order(StringComparer.Ordinal).ToArray(),
            Defaults = settings.GroupDefaults.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        })));

    private sealed record Row(string Name, string? Group, int Order)
    {
        public static Row From(ServerProfileDto dto) => new(dto.DisplayName, dto.Group, dto.SortOrder);
    }
    private sealed record UndoEntry(Dictionary<string, Row> Before, Dictionary<string, Row> After,
        FolderRenamePlan? ReverseFolder, string Metadata);
}
