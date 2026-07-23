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

using System.Collections.Frozen;
using Heimdall.Core.Models;

namespace Heimdall.Core.StateMachine;

/// <summary>
/// Shared semantic groups for connection states consumed outside the state machine.
/// </summary>
public static class ConnectionStateSets
{
    /// <summary>
    /// States in which a session has successfully reached or handed off its destination.
    /// </summary>
    public static FrozenSet<ConnectionState> Connected { get; } =
        new[]
        {
            ConnectionState.Connected,
            ConnectionState.LaunchedExternalClient,
            ConnectionState.RemoteSessionHandedOff
        }.ToFrozenSet();

    public static bool IsConnected(ConnectionState state) => Connected.Contains(state);

    public static bool IsConnected(string? state) =>
        Enum.TryParse(state, ignoreCase: true, out ConnectionState parsed)
        && Connected.Contains(parsed);
}
