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

using Heimdall.Core.Logging;
using Renci.SshNet;

namespace Heimdall.Ssh;

/// <summary>
/// Owns the lifecycle of a single SSH tunnel: the <see cref="SshClient"/> connection
/// and the <see cref="ForwardedPortLocal"/> that performs the actual port forwarding.
/// Disposing this object tears down both the forwarding and the SSH connection.
/// </summary>
public sealed class TunnelSession : IDisposable
{
    private int _disposed;

    /// <summary>The SSH client connection to the gateway.</summary>
    public SshClient Client { get; }

    /// <summary>The local port forward bound to this session.</summary>
    public ForwardedPortLocal ForwardedPort { get; }

    /// <summary>Optional SOCKS5 dynamic proxy port bound to this session.</summary>
    public ForwardedPortDynamic? DynamicPort { get; set; }

    /// <summary>Optional remote (reverse) port forward bound to this session.</summary>
    public ForwardedPortRemote? RemotePort { get; set; }

    /// <summary>Descriptive metadata about this tunnel.</summary>
    public TunnelInfo Info { get; }

    /// <summary>
    /// Intermediate SSH clients used in a chained (multi-hop) tunnel.
    /// Disposed in reverse order when the tunnel is closed.
    /// Empty for single-hop tunnels.
    /// </summary>
    internal IReadOnlyList<SshClient> IntermediateClients { get; }

    /// <summary>
    /// Intermediate forwarded ports used in a chained tunnel.
    /// Stopped and disposed in reverse order on cleanup.
    /// Empty for single-hop tunnels.
    /// </summary>
    internal IReadOnlyList<ForwardedPortLocal> IntermediatePorts { get; }

    public TunnelSession(
        SshClient client,
        ForwardedPortLocal forwardedPort,
        TunnelInfo info,
        IReadOnlyList<SshClient>? intermediateClients = null,
        IReadOnlyList<ForwardedPortLocal>? intermediatePorts = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        ForwardedPort = forwardedPort ?? throw new ArgumentNullException(nameof(forwardedPort));
        Info = info ?? throw new ArgumentNullException(nameof(info));
        IntermediateClients = intermediateClients ?? Array.Empty<SshClient>();
        IntermediatePorts = intermediatePorts ?? Array.Empty<ForwardedPortLocal>();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop and dispose the final forwarded port
        StopPortSafe(ForwardedPort);

        // Stop and dispose the optional SOCKS5 dynamic proxy
        // Bare catch: Dispose() must never throw (IDisposable contract)
        try
        {
            if (DynamicPort is { IsStarted: true })
            {
                DynamicPort.Stop();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Debug("TunnelSession dynamic port stop suppressed", ex);
        }

        try
        {
            DynamicPort?.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.Debug("TunnelSession dynamic port dispose suppressed", ex);
        }

        // Stop and dispose the optional remote (reverse) port forward
        try
        {
            if (RemotePort is { IsStarted: true })
            {
                RemotePort.Stop();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Debug("TunnelSession remote port stop suppressed", ex);
        }

        try
        {
            RemotePort?.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.Debug("TunnelSession remote port dispose suppressed", ex);
        }

        // Disconnect and dispose the final client
        DisconnectClientSafe(Client);

        // Tear down intermediate hops in reverse order
        for (int i = IntermediatePorts.Count - 1; i >= 0; i--)
        {
            StopPortSafe(IntermediatePorts[i]);
        }

        for (int i = IntermediateClients.Count - 1; i >= 0; i--)
        {
            DisconnectClientSafe(IntermediateClients[i]);
        }
    }

    private static void StopPortSafe(ForwardedPortLocal port)
    {
        try
        {
            if (port.IsStarted)
            {
                port.Stop();
            }

            port.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        catch (Exception ex)
        {
            FileLogger.Debug("TunnelSession local port cleanup suppressed", ex);
        }
    }

    private static void DisconnectClientSafe(SshClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }

            client.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        catch (Exception ex)
        {
            FileLogger.Debug("TunnelSession SSH client cleanup suppressed", ex);
        }
    }
}
