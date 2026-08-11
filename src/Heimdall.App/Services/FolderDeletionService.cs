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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Persisted inventory and settings reloaded after a folder deletion.
/// </summary>
internal sealed record FolderDeletionResult(
    List<ServerProfileDto> Servers,
    AppSettings Settings);

/// <summary>
/// Moves a deleted folder's inventory entries to the root and removes its
/// path-keyed settings metadata.
/// </summary>
internal sealed class FolderDeletionService
{
    private readonly IConfigManager _configManager;

    internal FolderDeletionService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// Drains pending expansion persistence, applies the inventory and settings
    /// writes, then reloads both persisted files for projection. Inventory moves
    /// first deliberately: a later settings failure leaves recoverable orphaned
    /// metadata, whereas the reverse order could erase inherited defaults while
    /// servers remain in the folder. The two files remain separate durable writes
    /// and are not claimed to be crash-atomic.
    /// </summary>
    internal async Task<FolderDeletionResult> DeleteAsync(
        string folderPath,
        Func<Task> flushExpandStateAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(flushExpandStateAsync);

        await flushExpandStateAsync().ConfigureAwait(false);

        await _configManager.MutateServersAsync(inventory =>
        {
            foreach (ServerProfileDto server in inventory)
            {
                if (server.Group is not null &&
                    FolderPath.IsSelfOrDescendant(server.Group, folderPath))
                {
                    server.Group = null;
                }
            }

            return true;
        }).ConfigureAwait(false);

        await _configManager.MergeSettingAsync(
            settings => PurgeFolderMetadata(settings, folderPath)).ConfigureAwait(false);

        AppSettings persistedSettings =
            await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        List<ServerProfileDto> persistedServers =
            await _configManager.LoadServersAsync().ConfigureAwait(false);
        return new FolderDeletionResult(persistedServers, persistedSettings);
    }

    private static void PurgeFolderMetadata(AppSettings settings, string folderPath)
    {
        settings.EmptyGroups.RemoveAll(
            path => IsFolderMetadataPath(path, folderPath));
        settings.TreeExpandedNodes.RemoveAll(
            path => IsFolderMetadataPath(path, folderPath));

        string[] deletedDefaultPaths = settings.GroupDefaults.Keys
            .Where(path => IsFolderMetadataPath(path, folderPath))
            .ToArray();
        foreach (string path in deletedDefaultPaths)
        {
            settings.GroupDefaults.Remove(path);
        }
    }

    private static bool IsFolderMetadataPath(string? path, string folderPath)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               FolderPath.IsSelfOrDescendant(path, folderPath);
    }
}
