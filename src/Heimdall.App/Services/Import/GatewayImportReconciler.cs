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

namespace Heimdall.App.Services.Import;

internal static class GatewayImportReconciler
{
    public static GatewayImportReconciliationResult Reconcile(
        IReadOnlyList<SshGatewayDto> existingGateways,
        IReadOnlyList<SshGatewayDto> importedGateways,
        IReadOnlyList<ServerProfileDto> importedServers,
        Func<string>? newIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(existingGateways);
        ArgumentNullException.ThrowIfNull(importedGateways);
        ArgumentNullException.ThrowIfNull(importedServers);

        newIdFactory ??= static () => Guid.NewGuid().ToString();

        Dictionary<GatewayIdentity, SshGatewayDto> existingByIdentity = existingGateways
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id) && HasUsableIdentity(gateway))
            .GroupBy(BuildIdentity)
            .ToDictionary(group => group.Key, group => group.First());
        HashSet<string> usedIds = existingGateways
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id))
            .Select(gateway => gateway.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<GatewayIdentity, string> batchFinalIds = new();
        Dictionary<string, string> gatewayIdMap = new(StringComparer.OrdinalIgnoreCase);
        List<GatewayAssignment> assignments = [];
        var mergedCount = 0;

        foreach (SshGatewayDto imported in importedGateways)
        {
            if (!HasUsableIdentity(imported))
            {
                continue;
            }

            GatewayIdentity identity = BuildIdentity(imported);
            string? originalId = string.IsNullOrWhiteSpace(imported.Id) ? null : imported.Id;

            if (existingByIdentity.TryGetValue(identity, out SshGatewayDto? existing))
            {
                if (!string.IsNullOrWhiteSpace(originalId))
                {
                    gatewayIdMap[originalId] = existing.Id;
                }

                mergedCount++;
                continue;
            }

            if (batchFinalIds.TryGetValue(identity, out string? batchFinalId))
            {
                if (!string.IsNullOrWhiteSpace(originalId))
                {
                    gatewayIdMap[originalId] = batchFinalId;
                }

                mergedCount++;
                continue;
            }

            string finalId = !string.IsNullOrWhiteSpace(originalId) && !usedIds.Contains(originalId)
                ? originalId
                : CreateUniqueId(newIdFactory, usedIds);

            usedIds.Add(finalId);
            batchFinalIds[identity] = finalId;
            if (!string.IsNullOrWhiteSpace(originalId))
            {
                gatewayIdMap[originalId] = finalId;
            }

            assignments.Add(new GatewayAssignment(imported, finalId));
        }

        HashSet<string> resolvableIds = usedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<GatewayImportOrphanReference> orphanReferences = [];
        List<SshGatewayDto> gatewaysToAdd = new(assignments.Count);

        foreach (GatewayAssignment assignment in assignments)
        {
            SshGatewayDto gateway = CloneGatewayWithoutSecrets(assignment.Gateway);
            gateway.Id = assignment.FinalId;

            // The fingerprint seeds the host key store at startup, so a shared file that carried
            // one decided which key this machine would accept for that host. Trust is earned on
            // the first connection here, never inherited from someone else's file.
            gateway.HostKeyFingerprint = null;
            gateway.ParentGatewayId = ResolveGatewayId(gateway.ParentGatewayId, gatewayIdMap, resolvableIds);
            if (!string.IsNullOrWhiteSpace(assignment.Gateway.ParentGatewayId) &&
                string.IsNullOrWhiteSpace(gateway.ParentGatewayId))
            {
                orphanReferences.Add(new GatewayImportOrphanReference(
                    GatewayImportReferenceKind.GatewayParent,
                    assignment.FinalId,
                    assignment.Gateway.Name,
                    assignment.Gateway.ParentGatewayId));
            }

            gatewaysToAdd.Add(gateway);
        }

        BreakParentLoops(gatewaysToAdd, assignments, orphanReferences);

        foreach (ServerProfileDto server in importedServers)
        {
            if (string.IsNullOrWhiteSpace(server.SshGatewayId))
            {
                continue;
            }

            string? resolvedGatewayId = ResolveGatewayId(server.SshGatewayId, gatewayIdMap, resolvableIds);
            if (string.IsNullOrWhiteSpace(resolvedGatewayId))
            {
                orphanReferences.Add(new GatewayImportOrphanReference(
                    GatewayImportReferenceKind.Server,
                    server.Id,
                    server.DisplayName,
                    server.SshGatewayId));
            }
        }

        return new GatewayImportReconciliationResult(
            gatewaysToAdd,
            gatewayIdMap,
            orphanReferences,
            mergedCount);
    }

    // A file can carry a gateway that is its own parent, or two that point at each other. The
    // id remapping keeps such a loop intact, and the settings write that follows refuses a
    // self-parent after the servers referring to these gateways were already committed, which
    // left them pointing at gateways that were never saved. The first gateway found on each
    // loop loses its parent and is reported like any other parent the import could not keep.
    // Existing gateways never point at imported ones, so a loop can only run through the batch.
    private static void BreakParentLoops(
        List<SshGatewayDto> gatewaysToAdd,
        List<GatewayAssignment> assignments,
        List<GatewayImportOrphanReference> orphanReferences)
    {
        Dictionary<string, SshGatewayDto> addedById = gatewaysToAdd.ToDictionary(
            gateway => gateway.Id,
            StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < gatewaysToAdd.Count; index++)
        {
            SshGatewayDto gateway = gatewaysToAdd[index];
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { gateway.Id };
            string? parentId = gateway.ParentGatewayId;
            while (parentId is not null && addedById.TryGetValue(parentId, out SshGatewayDto? parent))
            {
                if (string.Equals(parent.Id, gateway.Id, StringComparison.OrdinalIgnoreCase))
                {
                    gateway.ParentGatewayId = null;
                    orphanReferences.Add(new GatewayImportOrphanReference(
                        GatewayImportReferenceKind.GatewayParent,
                        gateway.Id,
                        assignments[index].Gateway.Name,
                        assignments[index].Gateway.ParentGatewayId ?? string.Empty));
                    break;
                }

                if (!visited.Add(parent.Id))
                {
                    break;
                }

                parentId = parent.ParentGatewayId;
            }
        }
    }

    private static bool HasUsableIdentity(SshGatewayDto gateway) =>
        !string.IsNullOrWhiteSpace(gateway.Host) && gateway.Port > 0;

    private static GatewayIdentity BuildIdentity(SshGatewayDto gateway) =>
        new(
            Normalize(gateway.Host),
            gateway.Port,
            Normalize(gateway.User));

    private static string? ResolveGatewayId(
        string? gatewayId,
        IReadOnlyDictionary<string, string> gatewayIdMap,
        IReadOnlySet<string> resolvableIds)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
        {
            return null;
        }

        return gatewayIdMap.TryGetValue(gatewayId, out string? mapped)
            ? mapped
            : resolvableIds.Contains(gatewayId)
                ? gatewayId
                : null;
    }

    private static string CreateUniqueId(Func<string> newIdFactory, ISet<string> usedIds)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            string candidate = newIdFactory();
            if (!string.IsNullOrWhiteSpace(candidate) && !usedIds.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique gateway id.");
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    // Assigning null through the passphrase setter raised its presence flag just as surely as
    // assigning a value, so clearing a secret used to fabricate the declaration of a field.
    private static SshGatewayDto CloneGatewayWithoutSecrets(SshGatewayDto gateway) =>
        gateway.CloneWithoutSecrets();

    private sealed record GatewayAssignment(SshGatewayDto Gateway, string FinalId);

    private readonly record struct GatewayIdentity(string Host, int Port, string User);
}

internal sealed record GatewayImportReconciliationResult(
    IReadOnlyList<SshGatewayDto> GatewaysToAdd,
    IReadOnlyDictionary<string, string> GatewayIdMap,
    IReadOnlyList<GatewayImportOrphanReference> OrphanReferences,
    int MergedCount)
{
    public int CreatedCount => GatewaysToAdd.Count;
}

internal sealed record GatewayImportOrphanReference(
    GatewayImportReferenceKind Kind,
    string OwnerId,
    string OwnerName,
    string GatewayId);

internal enum GatewayImportReferenceKind
{
    Server,
    GatewayParent
}
