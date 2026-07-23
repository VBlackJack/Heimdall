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
/// Finds every persisted reference to a gateway before it is deleted.
/// </summary>
public static class GatewayReferenceAnalyzer
{
    public static GatewayReferenceImpact AnalyzeDeletion(
        string gatewayId,
        IEnumerable<SshGatewayDto>? gateways,
        IEnumerable<ServerProfileDto>? servers,
        IReadOnlyDictionary<string, GroupDefaultsDto>? groupDefaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);

        List<SshGatewayDto> gatewayList = (gateways ?? []).ToList();
        string canonicalGatewayId = gatewayList
            .LastOrDefault(gateway => string.Equals(
                gateway.Id,
                gatewayId,
                StringComparison.OrdinalIgnoreCase))
            ?.Id ?? gatewayId;

        string[] serverIds = (servers ?? [])
            .Where(server => string.Equals(
                server.SshGatewayId,
                canonicalGatewayId,
                StringComparison.OrdinalIgnoreCase))
            .Select(server => server.Id)
            .ToArray();

        string[] groupPaths = (groupDefaults ?? new Dictionary<string, GroupDefaultsDto>())
            .Where(pair => string.Equals(
                pair.Value.SshGatewayId,
                canonicalGatewayId,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToArray();

        string[] childGatewayIds = gatewayList
            .Where(gateway => string.Equals(
                gateway.ParentGatewayId,
                canonicalGatewayId,
                StringComparison.OrdinalIgnoreCase))
            .Select(gateway => gateway.Id)
            .ToArray();

        return new GatewayReferenceImpact(
            canonicalGatewayId,
            serverIds,
            groupPaths,
            childGatewayIds);
    }
}

public sealed record GatewayReferenceImpact(
    string GatewayId,
    IReadOnlyList<string> ServerIds,
    IReadOnlyList<string> GroupPaths,
    IReadOnlyList<string> ChildGatewayIds)
{
    public int ServerCount => ServerIds.Count;

    public int GroupDefaultCount => GroupPaths.Count;

    public int ChildGatewayCount => ChildGatewayIds.Count;
}
