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
/// The sessions read from shared MobaXterm files, held in memory and never written anywhere.
/// </summary>
/// <remarks>
/// Every profile and gateway handed out is a copy: a caller that edits what it was given changes
/// nothing here, and nothing it does can reach the file the sessions came from.
/// </remarks>
public interface ISharedSessionProvider
{
    /// <summary>The state of every configured source, as of the last load.</summary>
    IReadOnlyList<SharedSessionSourceState> Sources { get; }

    /// <summary>Copies of every shared profile, sanitized, from every loaded source.</summary>
    IReadOnlyList<ServerProfileDto> Profiles { get; }

    /// <summary>Copies of every shared gateway, sanitized, from every loaded source.</summary>
    IReadOnlyList<SshGatewayDto> Gateways { get; }

    /// <summary>A copy of the shared profile <paramref name="profileId"/>, or null.</summary>
    ServerProfileDto? FindProfile(string profileId);

    /// <summary>
    /// Reads every enabled source of <paramref name="settings"/> and replaces what is held. Never
    /// throws over a source: a source that cannot be read is reported in its state.
    /// </summary>
    Task LoadAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the reload debounce of <paramref name="settings"/> for the file watch, reading no file.
    /// A later <see cref="LoadAsync"/> takes it too; whichever of the two is called last wins.
    /// </summary>
    void ApplyReloadDebounce(AppSettings settings);

    /// <summary>Raised after a load replaced what is held.</summary>
    event Action? Changed;

    /// <summary>
    /// Raised after one source was read again on its own, with the state it is in now: its file
    /// changed, or it was retried. <see cref="Changed"/> is raised first.
    /// </summary>
    event Action<SharedSessionSourceReload>? SourceReloaded;
}
