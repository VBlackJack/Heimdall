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

/// <summary>
/// The one way to find a profile by identifier: the local inventory first, then the shared
/// sources.
/// </summary>
/// <remarks>
/// Read paths ask here; write paths never do. Every write path re-reads servers.json, where a
/// shared profile never is, and the configuration manager refuses a shared identifier arriving in
/// it, so a shared profile found here cannot be saved by accident.
/// </remarks>
public interface IProfileLookup
{
    /// <summary>The shared sources this lookup falls back to.</summary>
    ISharedSessionProvider SharedSessions { get; }

    /// <summary>
    /// The profile <paramref name="profileId"/> from servers.json, else a copy of the shared
    /// profile with that identifier, else null.
    /// </summary>
    Task<ServerProfileDto?> FindAsync(string profileId);

    /// <summary>Every local profile, then every shared profile no local one shadows.</summary>
    Task<IReadOnlyList<ServerProfileDto>> GetAllAsync();

    /// <summary>
    /// The settings a connection to <paramref name="profile"/> runs with: <paramref name="settings"/>
    /// itself for a local profile, a copy holding the shared gateways for a shared one.
    /// </summary>
    AppSettings EffectiveSettingsFor(ServerProfileDto profile, AppSettings settings);
}
