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

namespace Heimdall.Ssh;

/// <summary>
/// Immutable snapshot describing an active SSH port-forwarding tunnel.
/// </summary>
/// <param name="ServerName">Host of the (final) gateway server.</param>
/// <param name="LocalPort">Local port bound for forwarding.</param>
/// <param name="RemoteHost">Target host on the remote network.</param>
/// <param name="RemotePort">Target port on the remote network.</param>
/// <param name="StartedAt">UTC timestamp when the tunnel was established.</param>
/// <param name="IsAlive">Whether the underlying SSH connection is still active.</param>
public sealed record TunnelInfo(
    string ServerName,
    int LocalPort,
    string RemoteHost,
    int RemotePort,
    DateTime StartedAt,
    bool IsAlive)
{
    /// <summary>
    /// Local port for the SOCKS5 dynamic proxy, or 0 if disabled.
    /// </summary>
    public int SocksProxyPort { get; init; }

    /// <summary>
    /// Port opened on the remote server for reverse forwarding, or 0 if disabled.
    /// </summary>
    public int RemoteBindPort { get; init; }

    /// <summary>
    /// Effective local destination port used by the reverse forward. This is
    /// <c>0</c> when reverse forwarding is disabled; otherwise it is the
    /// explicitly configured local port, or <see cref="RemoteBindPort"/> when
    /// the local port was left at its default value.
    /// </summary>
    public int EffectiveRemoteLocalPort { get; init; }

    /// <summary>
    /// Local IPv4 loopback address bound by the final local forward.
    /// </summary>
    public string LocalBindHost { get; init; } = LoopbackBinding.DefaultHost;

    /// <summary>
    /// Optional user-facing label for manually created tunnels.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Stable identifier of the gateway chain that opened this tunnel. Used by
    /// callers to decide whether an alive tunnel can be reused for a new
    /// request, instead of matching only on the remote endpoint. Empty for
    /// tunnels opened without an associated gateway chain.
    /// </summary>
    public string GatewayChainKey { get; init; } = string.Empty;

    /// <summary>
    /// How the gateway chain that opened this tunnel read at the moment it was dialled, for
    /// showing a person which machine they are being asked about. Null for a tunnel opened
    /// without a chain, and for one whose opener recorded nothing.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it lives on the tunnel and not on the connection that asks.</b>
    /// <see cref="GatewayChainKey"/> above is a hash over the chain's gateway IDENTIFIERS, and
    /// editing a gateway leaves its identifier alone - which is deliberate, since a tunnel
    /// already dialled to a host is still a working tunnel to that host. The consequence is that
    /// a later connection reusing this tunnel can resolve a chain naming an entirely different
    /// machine, and it may belong to a different profile altogether. The route that is true is
    /// the one recorded here by whoever opened it.</para>
    /// <para><b>Settable, unlike its siblings, and written once.</b> The chain is resolved a
    /// layer above this assembly, so the value is stamped onto the instance the manager
    /// registered rather than passed through the open call - which would put a presentation
    /// string into the tunnel-opening signature. A <c>with</c> expression cannot serve here: it
    /// would produce a copy while the registry, and therefore every future reuse, kept the
    /// original.</para>
    /// </remarks>
    public string? GatewayRoute { get; set; }
}
