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

namespace Heimdall.App.Services.SharedSessions;

/// <inheritdoc cref="IProfileLookup"/>
public sealed class ProfileLookup : IProfileLookup
{
    private readonly IConfigManager _configManager;

    public ProfileLookup(IConfigManager configManager, ISharedSessionProvider sharedSessions)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        SharedSessions = sharedSessions ?? throw new ArgumentNullException(nameof(sharedSessions));
    }

    /// <inheritdoc />
    public ISharedSessionProvider SharedSessions { get; }

    /// <summary>A lookup over the local inventory alone, for callers built without shared sources.</summary>
    public static IProfileLookup LocalOnly(IConfigManager configManager) =>
        new ProfileLookup(configManager, EmptySharedSessionProvider.Instance);

    /// <inheritdoc />
    public async Task<ServerProfileDto?> FindAsync(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return null;
        }

        List<ServerProfileDto> local = await _configManager.LoadServersAsync().ConfigureAwait(false);
        ServerProfileDto? found = local.FirstOrDefault(
            profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));
        return found ?? SharedSessions.FindProfile(profileId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServerProfileDto>> GetAllAsync()
    {
        List<ServerProfileDto> local = await _configManager.LoadServersAsync().ConfigureAwait(false);
        HashSet<string> localIds = local
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);

        return [.. local, .. SharedSessions.Profiles.Where(profile => !localIds.Contains(profile.Id))];
    }

    /// <inheritdoc />
    public AppSettings EffectiveSettingsFor(ServerProfileDto profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);

        return SharedSessionIds.IsSharedProfile(profile)
            ? SharedSessionSettings.WithSharedGateways(settings, SharedSessions.Gateways)
            : settings;
    }
}
