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

using System.Collections.ObjectModel;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Decides which gateways may be offered as the parent of a gateway being added or edited.
/// </summary>
/// <remarks>
/// The pickers used to exclude only the gateway itself. With A the parent of B, editing A
/// offered B, and saving it closed a loop that nothing refused until a session tried to
/// connect through either of them and failed with a circular chain. A chain deeper than the
/// resolver follows was accepted the same way.
///
/// A candidate is offered only when choosing it leaves every chain that runs through the
/// edited gateway resolvable: it is not the gateway or one of its descendants, its own route
/// up to the root resolves, and that route plus the longest chain hanging below the edited
/// gateway stays within <see cref="GatewayChainResolver.DefaultMaxDepth"/>.
/// </remarks>
public static class GatewayParentEligibility
{
    /// <summary>
    /// Builds the parent options for a gateway, in inventory order.
    /// </summary>
    /// <param name="gateways">Every known gateway, the edited one included when it exists.</param>
    /// <param name="gatewayId">The edited gateway, or <see langword="null"/> for a new one.</param>
    /// <param name="maxDepth">The longest chain a connection accepts.</param>
    public static ObservableCollection<GatewayOption> BuildOptions(
        IReadOnlyList<SshGatewayDto> gateways,
        string? gatewayId,
        int maxDepth = GatewayChainResolver.DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(gateways);

        return new ObservableCollection<GatewayOption>(
            EligibleParents(gateways, gatewayId, maxDepth)
                .Select(gateway => new GatewayOption(gateway.Id, $"{gateway.Name} ({gateway.Host})")));
    }

    /// <summary>
    /// Returns the gateways that may become the parent of <paramref name="gatewayId"/>.
    /// </summary>
    internal static IReadOnlyList<SshGatewayDto> EligibleParents(
        IReadOnlyList<SshGatewayDto> gateways,
        string? gatewayId,
        int maxDepth = GatewayChainResolver.DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(gateways);

        // First entry wins on a duplicated id, the same way a lookup by id finds it elsewhere,
        // instead of throwing on an inventory a hand edit left inconsistent.
        Dictionary<string, SshGatewayDto> byId = new(StringComparer.OrdinalIgnoreCase);
        foreach (SshGatewayDto gateway in gateways)
        {
            if (!string.IsNullOrWhiteSpace(gateway.Id))
            {
                byId.TryAdd(gateway.Id, gateway);
            }
        }

        Dictionary<string, List<string>> childrenById = new(StringComparer.OrdinalIgnoreCase);
        foreach (SshGatewayDto gateway in byId.Values)
        {
            if (string.IsNullOrWhiteSpace(gateway.ParentGatewayId))
            {
                continue;
            }

            if (!childrenById.TryGetValue(gateway.ParentGatewayId, out List<string>? children))
            {
                children = [];
                childrenById[gateway.ParentGatewayId] = children;
            }

            children.Add(gateway.Id);
        }

        HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
        int subtreeHeight = 1;
        if (!string.IsNullOrWhiteSpace(gatewayId))
        {
            CollectSubtree(gatewayId, childrenById, excluded);
            subtreeHeight = SubtreeHeight(
                gatewayId,
                childrenById,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        List<SshGatewayDto> eligible = [];
        foreach (SshGatewayDto candidate in gateways)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id)
                || excluded.Contains(candidate.Id)
                || !ReferenceEquals(byId[candidate.Id], candidate))
            {
                continue;
            }

            int? routeLength = RouteLength(candidate, byId);
            if (routeLength is int length && length + subtreeHeight <= maxDepth)
            {
                eligible.Add(candidate);
            }
        }

        return eligible;
    }

    private static void CollectSubtree(
        string gatewayId,
        Dictionary<string, List<string>> childrenById,
        HashSet<string> collected)
    {
        Stack<string> pending = new([gatewayId]);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!collected.Add(current) || !childrenById.TryGetValue(current, out List<string>? children))
            {
                continue;
            }

            foreach (string child in children)
            {
                pending.Push(child);
            }
        }
    }

    // The number of gateways on the longest chain from this one down to a leaf, itself
    // included. A child already on the path is a loop on disk, which the new parent breaks
    // for this gateway, so it does not lengthen anything.
    private static int SubtreeHeight(
        string gatewayId,
        Dictionary<string, List<string>> childrenById,
        HashSet<string> path)
    {
        if (!path.Add(gatewayId))
        {
            return 0;
        }

        int tallestChild = 0;
        if (childrenById.TryGetValue(gatewayId, out List<string>? children))
        {
            foreach (string child in children)
            {
                tallestChild = Math.Max(tallestChild, SubtreeHeight(child, childrenById, path));
            }
        }

        path.Remove(gatewayId);
        return tallestChild + 1;
    }

    // The number of gateways from the candidate up to its root, itself included, or null when
    // that route does not resolve: a loop, or a parent that no longer exists.
    private static int? RouteLength(SshGatewayDto candidate, Dictionary<string, SshGatewayDto> byId)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        SshGatewayDto? current = candidate;
        while (current is not null)
        {
            if (!visited.Add(current.Id))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(current.ParentGatewayId))
            {
                return visited.Count;
            }

            if (!byId.TryGetValue(current.ParentGatewayId, out current))
            {
                return null;
            }
        }

        return null;
    }
}
