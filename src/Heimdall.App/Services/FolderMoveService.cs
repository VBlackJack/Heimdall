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
/// Outcome of a folder move request.
/// </summary>
public enum FolderMoveStatus
{
    Moved,
    NoChange,
    IntoItself,
    SiblingCollision
}

/// <summary>
/// Result returned by the folder move domain operation.
/// </summary>
public sealed record FolderMoveResult(
    FolderMoveStatus Status,
    string? NewPath = null,
    List<ServerProfileDto>? Servers = null,
    AppSettings? Settings = null);

/// <summary>
/// Re-parents a folder, carrying its sub-folders, its sessions and its path-keyed settings.
/// </summary>
/// <remarks>
/// A move is a rename whose new path sits under another parent: "A/B" under "C" becomes "C/B".
/// The migration is the one the rename service runs - staged aliases, inventory rewrite, then
/// finalisation - so a move can no more strand a path than a rename can.
/// </remarks>
public sealed class FolderMoveService
{
    private readonly IConfigManager _configManager;

    public FolderMoveService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// Moves <paramref name="folderPath"/> under <paramref name="targetParentPath"/>, or to the
    /// top level when the target is null or empty.
    /// </summary>
    public async Task<FolderMoveResult> MoveAsync(string folderPath, string? targetParentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        List<ServerProfileDto> servers = await _configManager.LoadServersAsync().ConfigureAwait(false);
        List<string?> existingPaths = FolderRenameService.BuildExistingPaths(servers, settings);

        if (!FolderPath.TryCreateMove(
                folderPath,
                targetParentPath,
                existingPaths,
                out FolderRenamePlan? plan,
                out FolderMoveValidationError validationError))
        {
            return new FolderMoveResult(MapValidationError(validationError));
        }

        if (string.Equals(plan!.OldPath, plan.NewPath, StringComparison.Ordinal))
        {
            return new FolderMoveResult(FolderMoveStatus.NoChange, plan.NewPath);
        }

        (List<ServerProfileDto> migratedServers, AppSettings migratedSettings) =
            await FolderRenameService.MigrateAsync(_configManager, plan).ConfigureAwait(false);

        return new FolderMoveResult(
            FolderMoveStatus.Moved,
            plan.NewPath,
            migratedServers,
            migratedSettings);
    }

    private static FolderMoveStatus MapValidationError(FolderMoveValidationError error)
    {
        return error switch
        {
            FolderMoveValidationError.IntoItself => FolderMoveStatus.IntoItself,
            FolderMoveValidationError.SiblingCollision => FolderMoveStatus.SiblingCollision,
            _ => throw new InvalidOperationException($"Unexpected folder move error: {error}.")
        };
    }
}
