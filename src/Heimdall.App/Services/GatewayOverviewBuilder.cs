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
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Builds a read-only gateway-to-session overview from configuration DTOs.
/// The output is UI-ready data, but the builder has no WPF or localization dependency.
/// </summary>
public static class GatewayOverviewBuilder
{
    public static GatewayOverview Build(
        IEnumerable<SshGatewayDto>? gateways,
        IEnumerable<ServerProfileDto>? servers)
    {
        List<SshGatewayDto> gatewayList = (gateways ?? [])
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id))
            .ToList();

        List<SshGatewayDto> canonicalGateways = gatewayList
            .Select((Gateway, Index) => new IndexedGateway(Gateway, Index))
            .GroupBy(item => item.Gateway.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Index)
            .Select(item => item.Gateway)
            .ToList();

        Dictionary<string, SshGatewayDto> gatewayMap = canonicalGateways
            .ToDictionary(gateway => gateway.Id, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<GatewayOverviewSession>> sessionsByGateway = canonicalGateways
            .ToDictionary(
                gateway => gateway.Id,
                _ => new List<GatewayOverviewSession>(),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<GatewayOverviewSession>> missingReferences = new(StringComparer.OrdinalIgnoreCase);

        foreach (ServerProfileDto server in servers ?? [])
        {
            string? gatewayId = server.SshGatewayId;
            if (!ProtocolCapabilities.SupportsSshGateway(server.ConnectionType)
                || string.IsNullOrWhiteSpace(gatewayId))
            {
                continue;
            }

            GatewayOverviewSession session = CreateSession(server);
            if (gatewayMap.ContainsKey(gatewayId))
            {
                sessionsByGateway[gatewayId].Add(session);
                continue;
            }

            if (!missingReferences.TryGetValue(gatewayId, out List<GatewayOverviewSession>? missingSessions))
            {
                missingSessions = [];
                missingReferences[gatewayId] = missingSessions;
            }

            missingSessions.Add(session);
        }

        List<GatewayOverviewGatewayGroup> gatewayGroups = canonicalGateways
            .Select(gateway => new GatewayOverviewGatewayGroup(
                gateway.Id,
                FormatGatewayName(gateway),
                FormatGatewayEndpoint(gateway),
                ResolveParentGatewayName(gateway, gatewayMap),
                SortSessions(sessionsByGateway[gateway.Id])))
            .ToList();

        List<GatewayOverviewMissingReferenceGroup> missingGroups = missingReferences
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new GatewayOverviewMissingReferenceGroup(
                pair.Key,
                SortSessions(pair.Value)))
            .ToList();

        return new GatewayOverview(gatewayGroups, missingGroups);
    }

    private static IReadOnlyList<GatewayOverviewSession> SortSessions(IEnumerable<GatewayOverviewSession> sessions)
    {
        return sessions
            .OrderBy(session => session.SortOrder)
            .ThenBy(session => session.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static GatewayOverviewSession CreateSession(ServerProfileDto server)
    {
        return new GatewayOverviewSession(
            server.Id,
            string.IsNullOrWhiteSpace(server.DisplayName) ? server.RemoteServer : server.DisplayName,
            string.IsNullOrWhiteSpace(server.ConnectionType) ? "" : server.ConnectionType.ToUpperInvariant(),
            server.Group ?? "",
            FormatServerEndpoint(server),
            server.SortOrder);
    }

    private static string FormatGatewayName(SshGatewayDto gateway)
    {
        return string.IsNullOrWhiteSpace(gateway.Name)
            ? gateway.Id
            : gateway.Name;
    }

    private static string FormatGatewayEndpoint(SshGatewayDto gateway)
    {
        string hostPort = gateway.Port > 0 ? $"{gateway.Host}:{gateway.Port}" : gateway.Host;
        return string.IsNullOrWhiteSpace(gateway.User) ? hostPort : $"{gateway.User}@{hostPort}";
    }

    private static string ResolveParentGatewayName(
        SshGatewayDto gateway,
        IReadOnlyDictionary<string, SshGatewayDto> gatewayMap)
    {
        if (string.IsNullOrWhiteSpace(gateway.ParentGatewayId))
        {
            return "";
        }

        return gatewayMap.TryGetValue(gateway.ParentGatewayId, out SshGatewayDto? parent)
            ? FormatGatewayName(parent)
            : gateway.ParentGatewayId;
    }

    private static string FormatServerEndpoint(ServerProfileDto server)
    {
        if (string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            return "";
        }

        int port = server.ConnectionType?.ToUpperInvariant() switch
        {
            "SSH" or "SFTP" => server.SshPort,
            "WINRM" => server.WinRmPort,
            "FTP" => server.FtpPort,
            "VNC" => server.VncPort,
            "TELNET" => server.TelnetPort,
            _ => server.RemotePort
        };

        return port > 0 ? $"{server.RemoteServer}:{port}" : server.RemoteServer;
    }

    private sealed record IndexedGateway(SshGatewayDto Gateway, int Index);
}

public sealed record GatewayOverview(
    IReadOnlyList<GatewayOverviewGatewayGroup> Gateways,
    IReadOnlyList<GatewayOverviewMissingReferenceGroup> MissingReferences)
{
    public int GatewayCount => Gateways.Count;

    public int RoutedSessionCount => Gateways.Sum(gateway => gateway.Sessions.Count);

    public int MissingReferenceCount => MissingReferences.Sum(reference => reference.Sessions.Count);
}

public sealed record GatewayOverviewGatewayGroup(
    string GatewayId,
    string GatewayName,
    string Endpoint,
    string ParentGatewayName,
    IReadOnlyList<GatewayOverviewSession> Sessions);

public sealed record GatewayOverviewMissingReferenceGroup(
    string GatewayId,
    IReadOnlyList<GatewayOverviewSession> Sessions);

public sealed record GatewayOverviewSession(
    string Id,
    string DisplayName,
    string ConnectionType,
    string GroupPath,
    string Endpoint,
    int SortOrder);
