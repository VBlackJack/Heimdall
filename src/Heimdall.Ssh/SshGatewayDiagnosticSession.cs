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

    /// <summary>Length of the proxy's SOCKS5 reply: version, status, reserved, IPv4 type, address, port.</summary>
    private const int SocksReplyLength = 10;

    /// <summary>Position of the status byte in the reply; zero is success.</summary>
    private const int SocksReplyStatusIndex = 1;

    /// <summary>
    /// Destination bytes tolerated ahead of the reply. A server-first protocol sends a banner of
    /// tens of bytes; a proxy that sends this much without its reply is not the one we parse.
    /// </summary>
    private const int MaxBytesBeforeSocksReply = 64 * 1024;

    private const int SocksReadChunkBytes = 4096;

    // This parser is private to our owned SSH.NET loopback proxy, not a general SOCKS client.
    // Verified against the SSH.NET 2026.0.0 source the projects reference
    // (ForwardedPortDynamic.HandleSocks5 and CreateSocks5Reply, ChannelDirectTcpip.OnData):
    // after channel.Open returns, the proxy sends exactly 05 SS 00 01 00 00 00 00 00 00, with
    // SS = 00 when the channel opened and 01 otherwise. Destination data can arrive BEFORE that
    // reply, because OnData writes to the socket from the message loop as soon as the channel
    // is open while the reply is sent afterwards by the accepting thread; the reply itself
    // always follows. So the stream is scanned for the reply, and only the reply decides: a
    // zero status is success, any other status is a failure, and a stream that ends or runs
    // past MaxBytesBeforeSocksReply without a reply is an unrecognised proxy, never a success.
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

        byte status = await ReadSocksReplyStatusAsync(stream, ct).ConfigureAwait(false);
        if (status != 0) throw new ProxyException("The gateway did not confirm destination access.");
    }

    /// <summary>
    /// Reads until the proxy's reply is found and returns its status byte.
    /// </summary>
    private static async Task<byte> ReadSocksReplyStatusAsync(Stream stream, CancellationToken ct)
    {
        byte[] received = new byte[MaxBytesBeforeSocksReply + SocksReplyLength];
        int length = 0;
        while (length < received.Length)
        {
            int read = await stream
                .ReadAsync(received.AsMemory(length, Math.Min(SocksReadChunkBytes, received.Length - length)), ct)
                .ConfigureAwait(false);
            if (read == 0) break;

            int searchFrom = Math.Max(0, length - (SocksReplyLength - 1));
            length += read;
            int found = FindSocksReply(received.AsSpan(0, length), searchFrom);
            if (found >= 0) return received[found + SocksReplyStatusIndex];
        }

        throw new ProxyException("The diagnostic proxy did not send a recognised reply.");
    }

    private static int FindSocksReply(ReadOnlySpan<byte> data, int searchFrom)
    {
        for (int start = searchFrom; start + SocksReplyLength <= data.Length; start++)
        {
            ReadOnlySpan<byte> candidate = data.Slice(start, SocksReplyLength);
            if (candidate[0] == 5 && candidate[2] == 0 && candidate[3] == 1
                && candidate[4..].IndexOfAnyExcept((byte)0) < 0)
            {
                return start;
            }
        }

        return -1;
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
