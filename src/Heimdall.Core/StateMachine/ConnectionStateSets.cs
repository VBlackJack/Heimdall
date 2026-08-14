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

    /// <summary>
    /// States in which the connection state is the meaningful status, so a live reachability
    /// verdict must not be surfaced in its place.
    /// </summary>
    /// <remarks>
    /// This is the text-side statement of the priority the sidebar dot already applies: the dot
    /// falls back to the health palette only on the default branch, so an <c>Error</c> or a
    /// transitional state colours the dot from the state while the accessible name used to read
    /// out the health verdict - the two disagreed exactly here, and a screen reader got the
    /// opposite of what a sighted user saw.
    /// </remarks>
    public static FrozenSet<ConnectionState> OverridesHealth { get; } =
        new[]
        {
            ConnectionState.Connected,
            ConnectionState.LaunchedExternalClient,
            ConnectionState.RemoteSessionHandedOff,
            ConnectionState.Error,
            ConnectionState.Initializing,
            ConnectionState.ValidatingConfig,
            ConnectionState.EstablishingTunnel,
            ConnectionState.TunnelEstablished,
            ConnectionState.LaunchingRdp,
            ConnectionState.LaunchingSsh,
            ConnectionState.LaunchingSftp,
            ConnectionState.LaunchingLocal,
            ConnectionState.LaunchingVnc,
            ConnectionState.LaunchingFtp,
            ConnectionState.LaunchingTelnet,
            ConnectionState.LaunchingCitrix,
            ConnectionState.LaunchingWinRm,
            ConnectionState.Disconnecting
        }.ToFrozenSet();

    public static bool IsConnected(ConnectionState state) => Connected.Contains(state);

    public static bool IsConnected(string? state) =>
        Enum.TryParse(state, ignoreCase: true, out ConnectionState parsed)
        && Connected.Contains(parsed);

    /// <summary>Whether the connection state, rather than the health verdict, is the status to report.</summary>
    public static bool StateOverridesHealth(string? state) =>
        Enum.TryParse(state, ignoreCase: true, out ConnectionState parsed)
        && OverridesHealth.Contains(parsed);
}
