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

using System.Collections.Concurrent;
using System.Net.Sockets;
using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh;

/// <summary>
/// Manages the lifecycle of SSH port-forwarding tunnels. Thread-safe.
/// Replaces the legacy plink-based tunnel management with in-process SSH.NET tunnels.
/// </summary>
public sealed partial class TunnelManager : IDisposable
{
    private readonly ConcurrentDictionary<int, TunnelSession> _activeTunnels = new();
    private readonly ConcurrentDictionary<int, ExternalTunnelSession> _externalTunnels = new();
    private readonly ConcurrentDictionary<int, int> _refCounts = new();
    private readonly HashSet<string> _reservedLoopbackAliases = new(StringComparer.Ordinal);
    private readonly object _registryLock = new();
    private bool _disposed;

    /// <summary>Raised when a tunnel is successfully opened.</summary>
    public event Action<TunnelInfo>? TunnelOpened;

    /// <summary>Raised when a tunnel is closed (localPort, optional error message).</summary>
    public event Action<int, string?>? TunnelClosed;

    /// <summary>
    /// Raised when a local-forwarded port reports a failure - typically the
    /// gateway being unable to reach the forward's remote target.
    /// </summary>
    public event Action<TunnelForwardedPortFailure>? ForwardedPortFailed;

    /// <summary>
    /// Increments the reference count for a tunnel on the specified local port.
    /// Call this when a new session begins using an existing tunnel.
    /// </summary>
    /// <param name="localPort">Local port of the tunnel to reference.</param>
    public void AddReference(int localPort)
    {
        lock (_registryLock)
        {
            if (!IsPortTracked(localPort))
            {
                return;
            }

            AddReferenceUnderLock(localPort);
        }
    }

    /// <summary>
    /// Decrements the reference count for a tunnel on the specified local port.
    /// Returns true if the count has reached zero (or no refs were tracked),
    /// meaning the caller should close the tunnel. Returns false if other
    /// sessions still reference the tunnel.
    /// </summary>
    /// <param name="localPort">Local port of the tunnel to release.</param>
    /// <returns>True if the tunnel should be closed; false if still in use.</returns>
    public bool ReleaseReference(int localPort)
    {
        IDisposable? detached = null;
        bool shouldClose = false;

        lock (_registryLock)
        {
            if (!_refCounts.TryGetValue(localPort, out int current))
            {
                shouldClose = true;
                detached = DetachTunnelUnderLock(localPort);
            }
            else
            {
                int newCount = Math.Max(0, current - 1);
                if (newCount <= 0)
                {
                    shouldClose = true;
                    detached = DetachTunnelUnderLock(localPort);
                }
                else
                {
                    _refCounts[localPort] = newCount;
                }
            }
        }

        if (detached is not null)
        {
            DisposeAndNotifyClosed(localPort, detached);
        }

        return shouldClose;
    }

    /// <summary>
    /// Opens a single-hop SSH port-forwarding tunnel through the specified gateway.
    /// Binds <paramref name="localPort"/> on localhost and forwards traffic to
    /// <paramref name="remoteHost"/>:<paramref name="remotePort"/> via the gateway.
    /// </summary>
    /// <param name="gatewayParams">SSH connection parameters for the gateway.</param>
    /// <param name="remoteHost">Target host on the remote network.</param>
    /// <param name="remotePort">Target port on the remote network.</param>
    /// <param name="localPort">Local port to bind for forwarding.</param>
    /// <param name="hostKeyStore">TOFU host key store for server verification.</param>
    /// <param name="verifier">Verifier used when a host key is unknown or changed.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    /// <returns>Result indicating success or structured failure.</returns>
    public async Task<TunnelResult> OpenTunnelAsync(
        SshConnectionParams gatewayParams,
        string remoteHost,
        int remotePort,
        int localPort,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken cancellationToken = default,
        int keepAliveIntervalSeconds = AppSettings.DefaultSshKeepAliveIntervalSeconds,
        int socksProxyPort = 0,
        int remoteBindPort = 0,
        int remoteLocalPort = 0,
        string? label = null,
        string? gatewayChainKey = null,
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gatewayParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);

        if (localPort > 0 && IsPortTracked(localPort))
        {
            ReleaseLoopbackAliasReservationIfUnbound(localBindHost);
            return new TunnelResult(false, null, $"Local port {localPort} is already in use by an existing tunnel.", SshFailureCode.PortInUse);
        }

        var context = new TunnelBuildContext();

        try
        {
            var pinnedVerifier = await ResolvePinnedVerifierAsync(
                    gatewayParams,
                    gatewayParams.Host,
                    gatewayParams.Port,
                    hostKeyStore,
                    verifier,
                    cancellationToken)
                .ConfigureAwait(false);

            context.FinalClient = SshConnectionFactory.CreateSshClient(gatewayParams);
            context.FinalClient.KeepAliveInterval = TimeSpan.FromSeconds(keepAliveIntervalSeconds);

            int reportedLocalPort = localPort;
            context.FinalClient.ErrorOccurred += (_, args) =>
                Core.Logging.FileLogger.Error($"SSH tunnel error on port {reportedLocalPort}: {args.Exception.Message}");

            await ConnectSshClientWithCancellationAsync(
                    context.FinalClient,
                    gatewayParams.Host,
                    gatewayParams.Port,
                    pinnedVerifier,
                    cancellationToken,
                    "Client disconnect on cancel suppressed")
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            int boundLocalPort = WireFinalForwardedPorts(
                context,
                remoteHost,
                remotePort,
                localPort,
                socksProxyPort,
                remoteBindPort,
                remoteLocalPort,
                isChained: false,
                localBindHost);
            reportedLocalPort = boundLocalPort;

            var info = BuildTunnelInfo(
                gatewayParams.Host,
                boundLocalPort,
                remoteHost,
                remotePort,
                socksProxyPort,
                remoteBindPort,
                label,
                gatewayChainKey,
                localBindHost);

            var session = context.CreateSession(info);

            return RegisterTunnelSession(session, boundLocalPort, info);
        }
        catch (Exception ex)
        {
            ReleaseLoopbackAliasReservationIfUnbound(localBindHost);
            return ClassifyAndBuildFailureResult(ex, context.Cleanup, isChained: false);
        }
    }

    /// <summary>
    /// Opens a multi-hop (chained) tunnel through a sequence of gateways.
    /// Each gateway in the chain forwards to the next, with the final gateway
    /// forwarding to <paramref name="remoteHost"/>:<paramref name="remotePort"/>.
    /// </summary>
    /// <param name="gatewayChain">Ordered list of gateways from root to target.</param>
    /// <param name="remoteHost">Final target host on the remote network.</param>
    /// <param name="remotePort">Final target port on the remote network.</param>
    /// <param name="localPort">Local port to bind for the outermost forwarding.</param>
    /// <param name="hostKeyStore">TOFU host key store for server verification.</param>
    /// <param name="verifier">Verifier used when a host key is unknown or changed.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    /// <returns>Result indicating success or structured failure.</returns>
    public async Task<TunnelResult> OpenChainedTunnelAsync(
        IReadOnlyList<SshConnectionParams> gatewayChain,
        string remoteHost,
        int remotePort,
        int localPort,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken cancellationToken = default,
        int socksProxyPort = 0,
        int remoteBindPort = 0,
        int remoteLocalPort = 0,
        string? label = null,
        string? gatewayChainKey = null,
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gatewayChain);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);

        if (gatewayChain.Count == 0)
        {
            return new TunnelResult(false, null, "Gateway chain must contain at least one gateway.", SshFailureCode.Unknown);
        }

        // Single gateway: delegate to simple tunnel
        if (gatewayChain.Count == 1)
        {
            return await OpenTunnelAsync(gatewayChain[0], remoteHost, remotePort, localPort, hostKeyStore, verifier,
                    cancellationToken,
                    socksProxyPort: socksProxyPort, remoteBindPort: remoteBindPort, remoteLocalPort: remoteLocalPort,
                    label: label,
                    gatewayChainKey: gatewayChainKey,
                    localBindHost: localBindHost)
                .ConfigureAwait(false);
        }

        if (localPort > 0 && IsPortTracked(localPort))
        {
            ReleaseLoopbackAliasReservationIfUnbound(localBindHost);
            return new TunnelResult(false, null, $"Local port {localPort} is already in use by an existing tunnel.", SshFailureCode.PortInUse);
        }

        var context = new TunnelBuildContext();

        try
        {
            // Build the chain: each hop connects to the next via a local port forward
            // Hop 0: connect to gateway[0] directly
            // Hop 1: forward through gateway[0] to gateway[1], connect to gateway[1] via local forward
            // ...
            // Final: forward through last intermediate to remoteHost:remotePort

            var rootPinnedVerifier = await ResolvePinnedVerifierAsync(
                    gatewayChain[0],
                    gatewayChain[0].Host,
                    gatewayChain[0].Port,
                    hostKeyStore,
                    verifier,
                    cancellationToken)
                .ConfigureAwait(false);

            // Connect to the first (root) gateway directly
            var rootClient = SshConnectionFactory.CreateSshClient(gatewayChain[0]);
            context.IntermediateClients.Add(rootClient);

            await ConnectSshClientWithCancellationAsync(
                    rootClient,
                    gatewayChain[0].Host,
                    gatewayChain[0].Port,
                    rootPinnedVerifier,
                    cancellationToken,
                    "Root client disconnect on cancel suppressed")
                .ConfigureAwait(false);

            SshClient currentClient = rootClient;

            // Set up intermediate hops
            for (int i = 1; i < gatewayChain.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nextGateway = gatewayChain[i];
                // Forward through current client to the next gateway's SSH port.
                // Only the final forwarded port gets an Exception handler that
                // raises ForwardedPortFailed. A runtime failure in an
                // intermediate hop drops the downstream SSH client and surfaces
                // through the final port/client; per-hop mid-chain attribution
                // is intentionally out of scope.
                var intermediatePort = new ForwardedPortLocal(
                    LoopbackBinding.DefaultHost,
                    0,
                    nextGateway.Host,
                    (uint)nextGateway.Port);
                context.IntermediatePorts.Add(intermediatePort);
                currentClient.AddForwardedPort(intermediatePort);
                StartForwardedPortWithRetry(intermediatePort, "OS-assigned intermediate chain port");
                int intermediateLocalPort = ResolveStartedLocalPort(intermediatePort, 0);

                // Connect to the next gateway through the forwarded port
                var hopParams = CreateLoopbackHopParams(nextGateway, intermediateLocalPort);

                var hopPinnedVerifier = await ResolvePinnedVerifierAsync(
                        hopParams,
                        nextGateway.Host,
                        nextGateway.Port,
                        hostKeyStore,
                        verifier,
                        cancellationToken)
                    .ConfigureAwait(false);

                var hopClient = SshConnectionFactory.CreateSshClient(hopParams);

                if (i < gatewayChain.Count - 1)
                {
                    context.IntermediateClients.Add(hopClient);
                    currentClient = hopClient;
                }
                else
                {
                    // This is the final gateway
                    context.FinalClient = hopClient;
                }

                await ConnectSshClientWithCancellationAsync(
                        hopClient,
                        nextGateway.Host,
                        nextGateway.Port,
                        hopPinnedVerifier,
                        cancellationToken,
                        "Hop client disconnect on cancel suppressed")
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            int boundLocalPort = WireFinalForwardedPorts(
                context,
                remoteHost,
                remotePort,
                localPort,
                socksProxyPort,
                remoteBindPort,
                remoteLocalPort,
                isChained: true,
                localBindHost);

            var tunnelInfo = BuildTunnelInfo(
                gatewayChain[^1].Host,
                boundLocalPort,
                remoteHost,
                remotePort,
                socksProxyPort,
                remoteBindPort,
                label,
                gatewayChainKey,
                localBindHost);

            return RegisterTunnelSession(context.CreateSession(tunnelInfo), boundLocalPort, tunnelInfo);
        }
        catch (Exception ex)
        {
            ReleaseLoopbackAliasReservationIfUnbound(localBindHost);
            return ClassifyAndBuildFailureResult(ex, context.Cleanup, isChained: true);
        }
    }

    /// <summary>
    /// Closes and removes the tunnel bound to the specified local port.
    /// If the tunnel has a ref count greater than zero, the tunnel is kept alive.
    /// Use <see cref="ReleaseReference"/> for ref-counted teardown.
    /// </summary>
    /// <param name="localPort">Local port of the tunnel to close.</param>
    public void CloseTunnel(int localPort)
    {
        IDisposable? detached = null;

        lock (_registryLock)
        {
            if (_refCounts.TryGetValue(localPort, out var count) && count > 0)
            {
                return;
            }

            detached = DetachTunnelUnderLock(localPort);
        }

        if (detached is not null)
        {
            DisposeAndNotifyClosed(localPort, detached);
        }
    }

    /// <summary>
    /// Forcefully closes a tunnel regardless of reference count.
    /// Use when the user explicitly requests closure from the UI.
    /// </summary>
    public void ForceCloseTunnel(int localPort)
    {
        IDisposable? detached;

        lock (_registryLock)
        {
            detached = DetachTunnelUnderLock(localPort);
        }

        if (detached is not null)
        {
            DisposeAndNotifyClosed(localPort, detached);
        }
    }

    /// <summary>Closes all active tunnels (force, ignores ref counts).</summary>
    public void CloseAllTunnels()
    {
        foreach (var localPort in _activeTunnels.Keys.Concat(_externalTunnels.Keys).Distinct().ToList())
        {
            ForceCloseTunnel(localPort);
        }
    }

    /// <summary>Returns true if a tunnel is active on the specified local port.</summary>
    public bool HasTunnel(int localPort) => IsPortTracked(localPort);

    /// <summary>Returns tunnel info for the specified local port, or null if not found.</summary>
    public TunnelInfo? GetTunnel(int localPort)
    {
        if (_activeTunnels.TryGetValue(localPort, out var session))
        {
            // Return a fresh snapshot with current alive status
            return session.Info with { IsAlive = session.Client.IsConnected };
        }

        if (_externalTunnels.TryGetValue(localPort, out var externalSession))
        {
            return externalSession.Info with { IsAlive = externalSession.IsAlive };
        }

        return null;
    }

    /// <summary>Returns snapshots of all active tunnels.</summary>
    public IReadOnlyList<TunnelInfo> GetActiveTunnels()
    {
        return _activeTunnels.Values
            .Select(s => s.Info with { IsAlive = s.Client.IsConnected })
            .Concat(_externalTunnels.Values.Select(
                s => s.Info with { IsAlive = s.IsAlive }))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Registers an externally managed tunnel, such as a plink.exe process,
    /// so it participates in normal tunnel listing and cleanup.
    /// </summary>
    public bool TryRegisterExternalTunnel(
        TunnelInfo info,
        IDisposable tunnelHandle,
        Func<bool> isAlive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(tunnelHandle);
        ArgumentNullException.ThrowIfNull(isAlive);
        info = info with { LocalBindHost = LoopbackBinding.NormalizeHost(info.LocalBindHost) };

        var session = new ExternalTunnelSession(info, tunnelHandle, isAlive);
        bool registered = false;

        lock (_registryLock)
        {
            if (!IsPortTracked(info.LocalPort) && _externalTunnels.TryAdd(info.LocalPort, session))
            {
                AddReferenceUnderLock(info.LocalPort);
                registered = true;
            }
        }

        if (!registered)
        {
            ReleaseLoopbackAliasReservationIfUnbound(info.LocalBindHost);
            session.Dispose();
            return false;
        }

        RaiseTunnelOpened(info);
        return true;
    }

    /// <summary>
    /// Reserves a distinct 127.0.0.x loopback alias for a future local forward.
    /// The reservation is released when the registered tunnel is torn down, or
    /// by calling <see cref="ReleaseLoopbackAliasReservation"/> after a failed open.
    /// </summary>
    public string AllocateLoopbackAlias()
    {
        lock (_registryLock)
        {
            for (int octet = LoopbackBinding.FirstAliasOctet; octet <= LoopbackBinding.LastAliasOctet; octet++)
            {
                string candidate = LoopbackBinding.FormatAlias(octet);
                if (IsLoopbackAliasInUseUnderLock(candidate))
                {
                    continue;
                }

                _reservedLoopbackAliases.Add(candidate);
                return candidate;
            }
        }

        throw new InvalidOperationException("No loopback alias is available for tunnel binding.");
    }

    /// <summary>
    /// Releases a previously reserved loopback alias when no tunnel was registered.
    /// </summary>
    public void ReleaseLoopbackAliasReservation(string localBindHost)
    {
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);
        if (LoopbackBinding.IsDefaultHost(localBindHost))
        {
            return;
        }

        lock (_registryLock)
        {
            _reservedLoopbackAliases.Remove(localBindHost);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAllTunnels();
    }

    private bool IsPortTracked(int localPort)
    {
        return _activeTunnels.ContainsKey(localPort) || _externalTunnels.ContainsKey(localPort);
    }

    private void AddReferenceUnderLock(int localPort)
    {
        _refCounts.AddOrUpdate(localPort, 1, (_, current) => current + 1);
    }

    private IDisposable? DetachTunnelUnderLock(int localPort)
    {
        _refCounts.TryRemove(localPort, out _);

        if (_activeTunnels.TryRemove(localPort, out var session))
        {
            ReleaseLoopbackAliasReservationUnderLock(session.Info.LocalBindHost);
            return session;
        }

        if (_externalTunnels.TryRemove(localPort, out var externalSession))
        {
            ReleaseLoopbackAliasReservationUnderLock(externalSession.Info.LocalBindHost);
            return externalSession;
        }

        return null;
    }

    private bool IsLoopbackAliasInUseUnderLock(string localBindHost)
    {
        if (_reservedLoopbackAliases.Contains(localBindHost))
        {
            return true;
        }

        return IsLoopbackAliasBoundByTunnelUnderLock(localBindHost);
    }

    private bool IsLoopbackAliasBoundByTunnelUnderLock(string localBindHost)
        => _activeTunnels.Values.Any(session =>
               string.Equals(session.Info.LocalBindHost, localBindHost, StringComparison.Ordinal))
           || _externalTunnels.Values.Any(session =>
               string.Equals(session.Info.LocalBindHost, localBindHost, StringComparison.Ordinal));

    private void ReleaseLoopbackAliasReservationIfUnbound(string localBindHost)
    {
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);
        if (LoopbackBinding.IsDefaultHost(localBindHost))
        {
            return;
        }

        lock (_registryLock)
        {
            ReleaseLoopbackAliasReservationIfUnboundUnderLock(localBindHost);
        }
    }

    private void ReleaseLoopbackAliasReservationIfUnboundUnderLock(string localBindHost)
    {
        if (IsLoopbackAliasBoundByTunnelUnderLock(localBindHost))
        {
            return;
        }

        _reservedLoopbackAliases.Remove(localBindHost);
    }

    private void ReleaseLoopbackAliasReservationUnderLock(string localBindHost)
    {
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);
        if (!LoopbackBinding.IsDefaultHost(localBindHost))
        {
            _reservedLoopbackAliases.Remove(localBindHost);
        }
    }

    private void DisposeAndNotifyClosed(int localPort, IDisposable session)
    {
        string? error = null;
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        RaiseTunnelClosed(localPort, error);
    }

    /// <summary>
    /// Allocates an available local port for tunnel forwarding.
    /// If the requested port is available and not tracked, returns it.
    /// Otherwise, finds a free ephemeral port via the OS.
    /// </summary>
    /// <param name="preferredPort">Preferred port from the server profile.</param>
    /// <returns>An available local port number.</returns>
    public int AllocatePort(int preferredPort = 0)
    {
        if (preferredPort > 0)
        {
            if (IsPortTracked(preferredPort))
            {
                Heimdall.Core.Logging.FileLogger.Info(
                    $"TunnelManager: preferred local port {preferredPort} is already tracked by another tunnel; using ephemeral.");
            }
            else
            {
                try
                {
                    using var listener = new TcpListener(System.Net.IPAddress.Loopback, preferredPort);
                    listener.Start();
                    listener.Stop();
                    return preferredPort;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    Heimdall.Core.Logging.FileLogger.Info(
                        $"TunnelManager: preferred local port {preferredPort} is held by another process; using ephemeral.");
                }
                catch (SocketException ex)
                {
                    Heimdall.Core.Logging.FileLogger.Warn(
                        $"TunnelManager: preferred local port {preferredPort} bind failed ({ex.SocketErrorCode}); using ephemeral.");
                }
            }
        }

        return GetEphemeralPort();
    }

    /// <summary>Finds an available ephemeral port by briefly binding to port 0.</summary>
    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void RaiseTunnelOpened(TunnelInfo info)
    {
        try
        {
            TunnelOpened?.Invoke(info);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Debug("TunnelManager.TunnelOpened subscriber failed", ex);
        }
    }

    private void RaiseTunnelClosed(int localPort, string? error)
    {
        try
        {
            TunnelClosed?.Invoke(localPort, error);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Debug("TunnelManager.TunnelClosed subscriber failed", ex);
        }
    }

    internal void RaiseForwardedPortFailed(TunnelForwardedPortFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        try
        {
            ForwardedPortFailed?.Invoke(failure);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Debug("TunnelManager.ForwardedPortFailed subscriber failed", ex);
        }
    }

}
