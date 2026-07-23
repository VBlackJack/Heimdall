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
/// Outcome of an atomic server display-name change.
/// </summary>
public enum ServerRenameStatus
{
    Renamed,
    NoChange,
    InvalidName,
    NameTooLong,
    NotFound
}

/// <summary>
/// Result returned by <see cref="ServerRenameService"/>.
/// </summary>
public sealed record ServerRenameResult(
    ServerRenameStatus Status,
    ServerProfileDto? Server = null);

/// <summary>
/// Validates and atomically persists server display-name changes.
/// </summary>
public sealed class ServerRenameService
{
    public const int MaxDisplayNameLength = 200;

    private readonly IConfigManager _configManager;

    public ServerRenameService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// Renames one inventory entry without replacing the inventory snapshot.
    /// </summary>
    public Task<ServerRenameResult> RenameAsync(string serverId, string? requestedName)
    {
        string normalizedName = requestedName?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0 || normalizedName.Any(char.IsControl))
        {
            return Task.FromResult(new ServerRenameResult(ServerRenameStatus.InvalidName));
        }

        if (normalizedName.Length > MaxDisplayNameLength)
        {
            return Task.FromResult(new ServerRenameResult(ServerRenameStatus.NameTooLong));
        }

        return _configManager.MutateServersAsync(inventory =>
        {
            ServerProfileDto? server = inventory.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
            if (server is null)
            {
                return new ServerRenameResult(ServerRenameStatus.NotFound);
            }

            if (string.Equals(server.DisplayName, normalizedName, StringComparison.Ordinal))
            {
                return new ServerRenameResult(ServerRenameStatus.NoChange, server);
            }

            if (string.IsNullOrWhiteSpace(server.VaultEntryName))
            {
                server.VaultEntryName = server.DisplayName;
            }

            server.DisplayName = normalizedName;
            return new ServerRenameResult(ServerRenameStatus.Renamed, server);
        });
    }
}
