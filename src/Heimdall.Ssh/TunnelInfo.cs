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

/// <summary>Lifecycle state reported on an active or supervised SSH tunnel snapshot.</summary>
public enum TunnelLifecycleState
{
    Active,
    Reconnecting,
    Degraded,
    Stopped
}

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
    /// The reference this snapshot was handed out with by an open or a reuse, which the holder
    /// gives back through <see cref="TunnelManager.ReleaseReference(int, long)"/>; zero on a
    /// snapshot that carries no reference, such as a listing.
    /// </summary>
    public long LeaseId { get; init; }

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
    /// <see cref="GatewayChainKey"/> includes the endpoint and authentication configuration.
    /// A display-name edit preserves that identity, so the route label can still differ from
    /// the current settings. The opening description remains on the shared transport.</para>
    /// <para><b>Set at construction, which stamping it afterwards could not achieve.</b> The
    /// first attempt assigned it to the returned instance once the tunnel was up. That left two
    /// holes and both were reachable. The assignment happened after the configured establishment
    /// delay while the tunnel had been registered before it, so a connection reusing it during
    /// that window - thirty seconds, on a profile configured for one - copied a route that was
    /// still null. And the successful Plink fallback returns from its own branch, above the
    /// assignment, so every tunnel opened that way and every reuse of one kept no route at all.
    /// </para>
    /// <para><b>An <c>init</c> property is what makes the copies safe.</b> This record is copied
    /// with <c>with</c> in at least two places on the way to a caller - the reuse hand-back sets
    /// <c>IsAlive</c>, external registration normalises <c>LocalBindHost</c> - and a <c>with</c>
    /// carries every property it does not name. A settable property assigned after those copies
    /// were taken updates whichever instance happens to be in hand, which is not necessarily the
    /// one the registry holds.</para>
    /// </remarks>
    public string? GatewayRoute { get; init; }

    /// <summary>
    /// Internal tracking key inside <see cref="TunnelManager"/>. Zero means <see cref="LocalPort"/>
    /// is the tracking key; remote-only (<c>-R</c>) tunnels receive a unique negative tracking key
    /// so they never collide with local-bound ports or other gateways.
    /// </summary>
    public int TrackingKey { get; init; }

    /// <summary>Effective key used for closing or releasing references in <see cref="TunnelManager"/>.</summary>
    public int EffectiveTrackingKey => TrackingKey != 0 ? TrackingKey : LocalPort;

    /// <summary>True when this tunnel only forwards a remote port (<c>-R</c>) and binds no local listener.</summary>
    public bool IsRemoteOnly => RemoteBindPort > 0 && string.IsNullOrEmpty(RemoteHost) && SocksProxyPort <= 0;

    /// <summary>True when this entry represents a direct forward owned by an active SSH shell session.</summary>
    public bool IsSessionBound { get; init; }

    /// <summary>
    /// Unique identifier for a direct forward owned by an active <see cref="SshShellSession"/>.
    /// Used to remove individual session-bound forwards without disconnecting the session.
    /// </summary>
    public string? SessionForwardId { get; init; }

    /// <summary>Current lifecycle state of the tunnel.</summary>
    public TunnelLifecycleState State { get; init; } = TunnelLifecycleState.Active;

    /// <summary>Cumulative forwarded request count observed on this tunnel since launch.</summary>
    public long TotalConnections { get; init; }

    /// <summary>UTC timestamp of the most recent forwarded request, or null if idle.</summary>
    public DateTime? LastActivityUtc { get; init; }

    /// <summary>Number of reconnection attempts already performed by opt-in supervision.</summary>
    public int ReconnectAttempts { get; init; }

    /// <summary>Whether opt-in bounded automatic reconnection is enabled for this manual tunnel.</summary>
    public bool AutoReconnect { get; init; }

    /// <summary>Last sanitized lifecycle error message, when degraded or reconnecting.</summary>
    public string? LastError { get; init; }
}
