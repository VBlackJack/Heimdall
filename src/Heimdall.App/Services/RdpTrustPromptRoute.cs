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
/// Names the SSH gateways a tunnelled profile is configured to reach its target through, in
/// travel order.
/// </summary>
/// <remarks>
/// <para><b>This is the half of the machine's identity the question was missing.</b> A tunnelled
/// profile is dialled at 127.0.0.1, so the question fell back to the profile's own
/// <c>RemoteServer</c> and port. That is still not an identity: two profiles reaching the same
/// short name through two different gateways are reaching two different machines, and their
/// questions read identically apart from an ephemeral local port the user has never seen. The
/// gateway is what tells them apart, and it is the one part of the route the endpoint text
/// looked at - to choose its format string - and then discarded.</para>
/// <para><b>What makes this the route the connection took.</b> The question is asked during
/// Preparing, before the session is ready and before the tab's own route string is filled in, so
/// the live session cannot be read for it. What is read instead is the gateway chain as
/// configured in the settings instance THIS connection resolved its chain from - the same object
/// <c>TunnelService.EstablishTunnelAsync</c> walked to build the hops it dialled, carried to the
/// pane on <c>RdpSessionResult</c> and reached through <see cref="DescribeConnection"/>. For a
/// tunnel this connection opened, that instance IS the evidence: the chain was resolved from it
/// and immediately dialled, with no re-read in between. It is not evidence for a tunnel that was
/// already open, which is why the carrier is withheld in that case and no line is shown.</para>
/// <para><b>Reading the application's current settings instead was a defect, not a
/// shortcut.</b> Those are re-read when the pane is materialised, which is later than the
/// connect, and each read is a fresh deep clone: a gateway edited during a slow tunnel
/// establishment named the new host here for a certificate that arrived from the old one. Two
/// machines told apart by a line that could name either of them is worse than no line, because
/// the user acts on it.</para>
/// <para><b>The route the connection took is not recorded anywhere, so a reused tunnel gets no
/// line at all.</b> The resolved chain is a local of <c>TunnelService.EstablishTunnelAsync</c>
/// and does not survive establishment; what survives on <c>TunnelInfo</c> is the last hop's host
/// and a SHA-256 over the chain's gateway identifiers. That hash is invariant under editing a
/// gateway, because an edit leaves its identifier alone, so an already-open tunnel is still
/// reused for a chain whose hosts have since changed - and the connect-time settings of the
/// connection REUSING it are not the ones the tunnel was opened from. <c>RdpHandler</c>
/// therefore withholds the carrier when <c>TunnelSetupOutcome.ReusedExistingTunnel</c> is set,
/// and <see cref="DescribeConnection"/> answers null without one. The line is shown when it can
/// be proven and omitted when it cannot, which is what the wording above it needs in order not
/// to overclaim.</para>
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

    /// <summary>
    /// The same, for one connection, read from the settings that connection was made with.
    /// </summary>
    /// <param name="profile">The profile the pane is running, or null before it has one.</param>
    /// <param name="connectionSettings">
    /// The settings instance the connection resolved its gateway chain from, carried on
    /// <c>RdpSessionResult</c>. Null when nothing recorded it.
    /// </param>
    /// <returns>The route, or null when this cannot be said about THIS connection.</returns>
    /// <remarks>
    /// <para><b>No carrier means no line, and that is the whole point of the overload.</b> The
    /// obvious spelling - passing <c>connectionSettings?.SshGateways</c> straight to
    /// <see cref="Describe"/> - is not equivalent: with no gateway list to walk, that returns the
    /// raw gateway identifier, so an absent carrier would still put a line under "Reached
    /// through". A line naming the wrong machine is worse than no line, because the user acts on
    /// it, and so is a line whose provenance nobody can establish. Saying nothing is the only
    /// answer this can give without a carrier.</para>
    /// <para>Reaching for the application's current settings instead is exactly the defect this
    /// exists to close: those are re-read at pane materialisation, after the connection resolved
    /// its chain, and they carry every edit made in between.</para>
    /// </remarks>
    public static string? DescribeConnection(
        ServerProfileDto? profile,
        AppSettings? connectionSettings)
    {
        if (profile is null || connectionSettings is null)
        {
            return null;
        }

        return Describe(
            profile.UseDirectConnection,
            profile.SshGatewayId,
            connectionSettings.SshGateways);
    }

    /// <summary>
    /// The gateways this profile is configured to reach its target through, or null when it is
    /// direct.
    /// </summary>
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
