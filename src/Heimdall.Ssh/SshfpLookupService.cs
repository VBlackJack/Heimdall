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

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// Pure-managed RFC 4255/6891 DNS SSHFP resolver using UDP with TCP truncation fallback
/// against the operating system's configured DNS servers.
/// </summary>
public sealed class SshfpLookupService : ISshfpResolver
{
    private const int DnsPort = 53;
    private static readonly TimeSpan PerServerTimeout = TimeSpan.FromMilliseconds(1500);

    public async Task<SshfpVerificationResult> VerifyHostKeyAsync(
        string host,
        string algorithm,
        string presentedSha256Fingerprint,
        ReadOnlyMemory<byte> rawHostKeyBlob,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldQueryHost(host))
        {
            return new SshfpVerificationResult(SshfpVerificationStatus.Skipped);
        }

        var dnsServers = GetSystemDnsServers();
        if (dnsServers.Count == 0)
        {
            return new SshfpVerificationResult(SshfpVerificationStatus.Error, Detail: "No system DNS server found.");
        }

        ushort txId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        byte[] query;
        try
        {
            query = SshfpMatcher.BuildSshfpQuery(host, txId);
        }
        catch (ArgumentException)
        {
            return new SshfpVerificationResult(SshfpVerificationStatus.Skipped);
        }

        bool sawTimeout = false;
        foreach (var server in dnsServers.Take(2))
        {
            bool isLoopback = IPAddress.IsLoopback(server);
            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            serverCts.CancelAfter(PerServerTimeout);
            CancellationToken serverCt = serverCts.Token;

            try
            {
                byte[] response = await QueryUdpAsync(server, query, serverCt).ConfigureAwait(false);
                if (!SshfpMatcher.TryParseSshfpResponse(response, txId, host, out var records, out bool ad, out bool truncated))
                {
                    continue;
                }

                if (truncated)
                {
                    byte[] tcpResponse = await QueryTcpAsync(server, query, serverCt).ConfigureAwait(false);
                    if (!SshfpMatcher.TryParseSshfpResponse(tcpResponse, txId, host, out records, out ad, out _))
                    {
                        continue;
                    }
                }

                return SshfpMatcher.Verify(
                    algorithm,
                    presentedSha256Fingerprint,
                    rawHostKeyBlob.Span,
                    records,
                    ad,
                    isLoopback);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-server timeout: fall through and try the next configured DNS server.
                sawTimeout = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Try next configured DNS server.
            }
        }

        return new SshfpVerificationResult(
            SshfpVerificationStatus.Error,
            Detail: sawTimeout ? "DNS lookup timed out." : "DNS query failed.");
    }

    internal static bool ShouldQueryHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(trimmed, out _))
        {
            return false;
        }

        if (!trimmed.Contains('.', StringComparison.Ordinal)
            || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<IPAddress> GetSystemDnsServers()
    {
        var servers = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var addr in nic.GetIPProperties().DnsAddresses)
                {
                    if ((addr.AddressFamily == AddressFamily.InterNetwork || addr.AddressFamily == AddressFamily.InterNetworkV6)
                        && !addr.IsIPv6SiteLocal
                        && !servers.Contains(addr))
                    {
                        servers.Add(addr);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignore interface enumeration errors on restricted environments.
        }

        return servers;
    }

    private static async Task<byte[]> QueryUdpAsync(IPAddress server, byte[] query, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        var endpoint = new IPEndPoint(server, DnsPort);
        udp.Connect(endpoint);
        await udp.SendAsync(query, ct).ConfigureAwait(false);
        var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        return result.Buffer;
    }

    private static async Task<byte[]> QueryTcpAsync(IPAddress server, byte[] query, CancellationToken ct)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, DnsPort, ct).ConfigureAwait(false);
        using var stream = tcp.GetStream();

        byte[] lenPrefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenPrefix, (ushort)query.Length);
        await stream.WriteAsync(lenPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(query, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        await stream.ReadExactlyAsync(lenPrefix, ct).ConfigureAwait(false);
        int responseLen = BinaryPrimitives.ReadUInt16BigEndian(lenPrefix);
        if (responseLen is < 12 or > 65535)
        {
            throw new InvalidOperationException("Invalid DNS TCP response length.");
        }

        byte[] buffer = new byte[responseLen];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}
