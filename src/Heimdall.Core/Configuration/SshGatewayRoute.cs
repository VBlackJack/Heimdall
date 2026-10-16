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

using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Whether a profile is reached through an SSH gateway, and which one.
/// </summary>
/// <remarks>
/// <para>
/// The one copy of that decision. The tunnel service, the handlers that open or refuse a route,
/// the embedded RDP view that picks the endpoint, the health monitor that decides whether to
/// probe, the session row's gateway badge and the gateway overview all ask here. Each used to keep its own copy, and the copies disagreed: the monitor
/// ignored <see cref="ServerProfileDto.UseDirectConnection"/> and the protocol, so a profile that
/// opened direct showed as unprobed behind a gateway, while a split pane never received the
/// gateway its group supplied and opened direct where its tab tunnelled.
/// </para>
/// <para>
/// A profile goes through a gateway when all three hold: its protocol is one whose handler routes
/// through the tunnel service (<see cref="ProtocolCapabilities.SupportsSshGateway"/>), it is not
/// marked for a direct connection, and it names a gateway, on the profile itself or else
/// inherited from its group defaults (<see cref="GroupDefaultsDto.Resolve"/>).
/// </para>
/// </remarks>
public static class SshGatewayRoute
{
    /// <summary>
    /// Whether a gateway identifier names a gateway at all. Blank counts as none, as the server
    /// dialog writes it.
    /// </summary>
    public static bool IsAssigned(string? gatewayId) => !string.IsNullOrWhiteSpace(gatewayId);

    /// <summary>
    /// The gateway the connection is tunnelled through, or null when it goes direct.
    /// </summary>
    /// <param name="server">The profile.</param>
    /// <param name="groupDefaults">
    /// The configured group defaults, so an inherited gateway counts even on a profile that has
    /// not been through <see cref="GroupDefaultsDto.ApplyInherited"/>. Applying them first gives
    /// the same answer.
    /// </param>
    public static string? GatewayIdFor(
        ServerProfileDto server,
        IReadOnlyDictionary<string, GroupDefaultsDto>? groupDefaults)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.UseDirectConnection || !ProtocolCapabilities.SupportsSshGateway(server.ConnectionType))
        {
            return null;
        }

        if (IsAssigned(server.SshGatewayId))
        {
            return server.SshGatewayId;
        }

        // A shared profile never inherits a local folder's gateway (see GroupDefaultsDto.ApplyInherited).
        if (groupDefaults is null || groupDefaults.Count == 0 || SharedSessionIds.IsSharedProfile(server))
        {
            return null;
        }

        string? inherited = GroupDefaultsDto.Resolve(server.Group, groupDefaults).SshGatewayId;
        return IsAssigned(inherited) ? inherited : null;
    }

    /// <summary>
    /// Whether the connection is tunnelled through an SSH gateway.
    /// </summary>
    public static bool IsThroughGateway(
        ServerProfileDto server,
        IReadOnlyDictionary<string, GroupDefaultsDto>? groupDefaults)
        => GatewayIdFor(server, groupDefaults) is not null;
}
