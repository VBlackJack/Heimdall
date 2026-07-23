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
/// Outcome of a folder rename request.
/// </summary>
public enum FolderRenameStatus
{
    Renamed,
    NoChange,
    InvalidSegment,
    SiblingCollision
}

/// <summary>
/// Result returned by the folder rename domain operation.
/// </summary>
public sealed record FolderRenameResult(
    FolderRenameStatus Status,
    string? NewPath = null,
    List<ServerProfileDto>? Servers = null,
    AppSettings? Settings = null);

/// <summary>
/// Validates and persists path-complete folder renames.
/// </summary>
public sealed class FolderRenameService
{
    private readonly IConfigManager _configManager;

    public FolderRenameService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// Renames a folder and all path-keyed descendants without exposing a state
    /// in which an inventory path has lost its inherited settings.
    /// </summary>
    public async Task<FolderRenameResult> RenameAsync(string oldPath, string? newLeafName)
    {
        AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        List<ServerProfileDto> servers = await _configManager.LoadServersAsync().ConfigureAwait(false);
        List<string?> existingPaths = BuildExistingPaths(servers, settings);

        if (!FolderPath.TryCreateRename(
                oldPath,
                newLeafName,
                existingPaths,
                out FolderRenamePlan? plan,
                out FolderRenameValidationError validationError))
        {
            return new FolderRenameResult(MapValidationError(validationError));
        }

        if (string.Equals(plan!.OldPath, plan.NewPath, StringComparison.Ordinal))
        {
            return new FolderRenameResult(FolderRenameStatus.NoChange, plan.NewPath);
        }

        // Stage both path variants first. If execution stops before the inventory
        // write, servers still resolve through the old paths. If it stops after the
        // inventory write but before cleanup, the new paths already resolve.
        await _configManager.MergeSettingAsync(current => StageSettings(current, plan))
            .ConfigureAwait(false);

        List<ServerProfileDto> migratedServers =
            await _configManager.MutateServersAsync(inventory =>
            {
                foreach (ServerProfileDto server in inventory)
                {
                    if (server.Group is not null)
                    {
                        server.Group = plan.Rewrite(server.Group);
                    }
                }

                return inventory;
            }).ConfigureAwait(false);

        await _configManager.MergeSettingAsync(current => FinalizeSettings(current, plan))
            .ConfigureAwait(false);

        AppSettings migratedSettings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        return new FolderRenameResult(
            FolderRenameStatus.Renamed,
            plan.NewPath,
            migratedServers,
            migratedSettings);
    }

    private static List<string?> BuildExistingPaths(
        IEnumerable<ServerProfileDto> servers,
        AppSettings settings)
    {
        var paths = servers.Select(server => server.Group).ToList();
        paths.AddRange(settings.EmptyGroups);
        paths.AddRange(settings.GroupDefaults.Keys);
        paths.AddRange(settings.TreeExpandedNodes);
        return paths;
    }

    private static FolderRenameStatus MapValidationError(FolderRenameValidationError error)
    {
        return error switch
        {
            FolderRenameValidationError.InvalidSegment => FolderRenameStatus.InvalidSegment,
            FolderRenameValidationError.SiblingCollision => FolderRenameStatus.SiblingCollision,
            _ => throw new InvalidOperationException($"Unexpected folder rename error: {error}.")
        };
    }

    private static void StageSettings(AppSettings settings, FolderRenamePlan plan)
    {
        StagePathList(settings.EmptyGroups, plan);
        StagePathList(settings.TreeExpandedNodes, plan);

        Dictionary<string, GroupDefaultsDto> stagedDefaults =
            new(settings.GroupDefaults, StringComparer.Ordinal);
        foreach ((string path, GroupDefaultsDto defaults) in settings.GroupDefaults.ToArray())
        {
            if (FolderPath.IsSelfOrDescendant(path, plan.OldPath))
            {
                stagedDefaults.TryAdd(plan.Rewrite(path), defaults);
            }
        }

        settings.GroupDefaults = stagedDefaults;
    }

    private static void StagePathList(List<string> paths, FolderRenamePlan plan)
    {
        string[] aliases = paths
            .Where(path => FolderPath.IsSelfOrDescendant(path, plan.OldPath))
            .Select(plan.Rewrite)
            .ToArray();

        foreach (string alias in aliases)
        {
            if (!paths.Contains(alias, StringComparer.Ordinal))
            {
                paths.Add(alias);
            }
        }
    }

    private static void FinalizeSettings(AppSettings settings, FolderRenamePlan plan)
    {
        settings.EmptyGroups = RewritePathList(settings.EmptyGroups, plan);
        settings.TreeExpandedNodes = RewritePathList(settings.TreeExpandedNodes, plan);

        Dictionary<string, GroupDefaultsDto> finalizedDefaults = new(StringComparer.Ordinal);
        KeyValuePair<string, GroupDefaultsDto>[] entries = settings.GroupDefaults.ToArray();

        // Canonical staged keys win over their old aliases if another settings
        // writer updated them while the inventory mutation was in flight.
        foreach ((string path, GroupDefaultsDto defaults) in entries)
        {
            string rewrittenPath = plan.Rewrite(path);
            if (string.Equals(path, rewrittenPath, StringComparison.Ordinal))
            {
                finalizedDefaults[path] = defaults;
            }
        }

        foreach ((string path, GroupDefaultsDto defaults) in entries)
        {
            finalizedDefaults.TryAdd(plan.Rewrite(path), defaults);
        }

        settings.GroupDefaults = finalizedDefaults;
    }

    private static List<string> RewritePathList(
        IEnumerable<string> paths,
        FolderRenamePlan plan)
    {
        return paths
            .Select(plan.Rewrite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
