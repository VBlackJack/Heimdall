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

/// <summary>A provider that holds no shared session, for a lookup built without shared sources.</summary>
public sealed class EmptySharedSessionProvider : ISharedSessionProvider
{
    public static EmptySharedSessionProvider Instance { get; } = new();

    private EmptySharedSessionProvider()
    {
    }

    /// <inheritdoc />
    public event Action? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public IReadOnlyList<SharedSessionSourceState> Sources => [];

    /// <inheritdoc />
    public IReadOnlyList<ServerProfileDto> Profiles => [];

    /// <inheritdoc />
    public IReadOnlyList<SshGatewayDto> Gateways => [];

    /// <inheritdoc />
    public ServerProfileDto? FindProfile(string profileId) => null;

    /// <inheritdoc />
    public Task LoadAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
