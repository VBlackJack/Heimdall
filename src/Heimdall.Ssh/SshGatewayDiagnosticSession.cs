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

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh;

/// <summary>Uses pinned SSH.NET clients and owned loopback listeners for a diagnostic route.</summary>
internal sealed class SshGatewayDiagnosticSession : IGatewayDiagnosticSession
{
    private readonly List<SshClient> _clients = [];
    private readonly List<ForwardedPort> _ports = [];

    public async Task ConnectHopAsync(SshConnectionParams hop, string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SshConnectionParams dial = hop;
        if (_clients.Count > 0)
        {
            ForwardedPortLocal forward = new(LoopbackBinding.DefaultHost, 0, hop.Host, (uint)hop.Port);
            _ports.Add(forward);
            _clients[^1].AddForwardedPort(forward);
            forward.Start();
            dial = new SshConnectionParams
            {
                Host = LoopbackBinding.DefaultHost,
                Port = (int)forward.BoundPort,
                LogicalHost = hop.Host,
                LogicalPort = hop.Port,
                Username = hop.Username,
                Password = hop.Password,
                KeyPath = hop.KeyPath,
                KeyPassphrase = hop.KeyPassphrase,
                SshAgentPreference = hop.SshAgentPreference,
                ConnectTimeout = hop.ConnectTimeout,
                KeepAliveIntervalSeconds = hop.KeepAliveIntervalSeconds,
                UseLegacyPasswordAsKeyPassphrase = hop.UseLegacyPasswordAsKeyPassphrase,
                LegacyCredentialName = hop.LegacyCredentialName,
                KeyboardInteractive = hop.KeyboardInteractive
            };
        }
        SshClient client = SshConnectionFactory.CreateSshClient(dial);
        _clients.Add(client);
        client.KeepAliveInterval = TimeSpan.FromSeconds(hop.KeepAliveIntervalSeconds ?? 0);
        SshConnectionFactory.AttachPinnedHostKeyVerification(client, hop.Host, hop.Port,
            new PinnedFingerprintVerifier(hop.Host, hop.Port, fingerprint));
        await client.ConnectAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public async Task ProbeTargetAsync(string host, int port, CancellationToken ct)
    {
        if (_clients.Count == 0) throw new InvalidOperationException("No authenticated gateway.");
        ArgumentOutOfRangeException.ThrowIfLessThan(port, IPEndPoint.MinPort + 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        ForwardedPortDynamic proxy = new(LoopbackBinding.DefaultHost, 0);
        _ports.Add(proxy);
        _clients[^1].AddForwardedPort(proxy);
        proxy.Start();
        using TcpClient socket = new();
        await socket.ConnectAsync(LoopbackBinding.DefaultHost, (int)proxy.BoundPort, ct).ConfigureAwait(false);
        await ConfirmSocksTargetAsync(socket.GetStream(), host, port, ct).ConfigureAwait(false);
    }

    // This parser is private to our owned SSH.NET 2025.1.0 loopback proxy, not a general SOCKS client.
    // That proxy emits a fixed 10-byte success/failure reply after channel.Open. Channel data can
    // arrive before the reply (for example an SSH banner); such data itself proves TCP access.
    internal static async Task ConfirmSocksTargetAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct).ConfigureAwait(false);
        byte[] greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, ct).ConfigureAwait(false);
        if (greeting[0] != 5 || greeting[1] != 0) throw new ProxyException("Diagnostic proxy negotiation failed.");

        byte addressType;
        byte[] address;
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            addressType = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
            address = ip.GetAddressBytes();
        }
        else
        {
            byte[] domain = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
            if (domain.Length is 0 or > byte.MaxValue) throw new ArgumentException("Invalid target host.", nameof(host));
            addressType = 3;
            address = [(byte)domain.Length, .. domain];
        }
        byte[] request = [5, 1, 0, addressType, .. address, (byte)(port >> 8), (byte)port];
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        byte[] reply = new byte[4];
        await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
        // The owned proxy only emits 05 00 00 01 or 05 01 00 01 here. Any other
        // prefix is destination data forwarded after a successful direct-tcpip channel open.
        if (reply[0] != 5 || reply[1] > 1 || reply[2] != 0 || reply[3] != 1) return;
        if (reply[1] == 1) throw new ProxyException("The gateway did not confirm destination access.");
        byte[] remainder = new byte[6];
        await stream.ReadExactlyAsync(remainder, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Close clients first to interrupt pending forwarded channels before disposing listeners.
        for (int index = _clients.Count - 1; index >= 0; index--) _clients[index].Dispose();
        for (int index = _ports.Count - 1; index >= 0; index--) _ports[index].Dispose();
        _clients.Clear();
        _ports.Clear();
    }
}
