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
/// Names the SSH gateways a tunnelled session's traffic passes through, in travel order.
/// </summary>
/// <remarks>
/// <para><b>This is the half of the machine's identity the question was missing.</b> A tunnelled
/// profile is dialled at 127.0.0.1, so the question fell back to the profile's own
/// <c>RemoteServer</c> and port. That is still not an identity: two profiles reaching the same
/// short name through two different gateways are reaching two different machines, and their
/// questions read identically apart from an ephemeral local port the user has never seen. The
/// gateway is what tells them apart, and it is the one part of the route the endpoint text
/// looked at - to choose its format string - and then discarded.</para>
/// <para><b>Resolved from the profile, not from the live tunnel.</b> The question is asked during
/// Preparing, before the session is ready, and the tab's route string is filled in when the
/// session completes. Reading the profile's own gateway chain gives the same names without
/// depending on how far the connection has got.</para>
/// <para>Pure and free of WPF, so two routes can be compared in a test without a window.</para>
/// </remarks>
public static class RdpTrustPromptRoute
{
    /// <summary>Separates the gateways of a chain, nearest the user first.</summary>
    /// <remarks>
    /// The same arrow the tunnels panel already draws a chain with, so a user who has seen one
    /// recognises the other. Written as an escape so the source file stays ASCII.
    /// </remarks>
    public const string ChainSeparator = " \u2192 ";

    /// <summary>The gateways this profile reaches its target through, or null when it is direct.</summary>
    /// <param name="useDirectConnection">Whether the profile bypasses every gateway.</param>
    /// <param name="sshGatewayId">The gateway the profile enters the chain at.</param>
    /// <param name="gateways">Every configured gateway, for walking the chain.</param>
    /// <remarks>
    /// <para><b>A gateway that no longer exists still identifies the route.</b> When the id
    /// resolves to no configured gateway the id itself is returned rather than nothing: it is not
    /// a name a user chose, but it differs between two profiles, and a question that says nothing
    /// is the failure this whole field exists to end.</para>
    /// <para>A chain that loops back on itself stops at the repeat rather than walking forever.
    /// Configuration is user-supplied and nothing else on this path enforces a tree.</para>
    /// </remarks>
    public static string? Describe(
        bool useDirectConnection,
        string? sshGatewayId,
        IReadOnlyList<SshGatewayDto>? gateways)
    {
        if (useDirectConnection || string.IsNullOrWhiteSpace(sshGatewayId))
        {
            return null;
        }

        List<string> names = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string? currentId = sshGatewayId;

        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            SshGatewayDto? gateway = Find(gateways, currentId);
            if (gateway is null)
            {
                break;
            }

            names.Add(string.IsNullOrWhiteSpace(gateway.Name) ? gateway.Host : gateway.Name);
            currentId = gateway.ParentGatewayId;
        }

        if (names.Count == 0)
        {
            return sshGatewayId.Trim();
        }

        names.Reverse();
        return string.Join(ChainSeparator, names);
    }

    private static SshGatewayDto? Find(IReadOnlyList<SshGatewayDto>? gateways, string id)
    {
        if (gateways is null)
        {
            return null;
        }

        foreach (SshGatewayDto gateway in gateways)
        {
            if (string.Equals(gateway.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return gateway;
            }
        }

        return null;
    }
}
