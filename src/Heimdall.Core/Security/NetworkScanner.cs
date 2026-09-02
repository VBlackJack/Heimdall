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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Heimdall.Core.Models;

namespace Heimdall.Core.Security;

/// <summary>
/// Lightweight network scanner that performs ICMP ping sweeps and common port probes
/// to discover hosts on a subnet. Designed for sysadmin network discovery workflows.
/// </summary>
public static class NetworkScanner
{
    private static readonly int PingTimeoutMs = 1000;
    private static readonly int PortProbeTimeoutMs = 500;
    private static readonly int[] CommonPorts =
        [DefaultPorts.Ssh, DefaultPorts.Rdp, DefaultPorts.HttpStd, DefaultPorts.HttpsStd, DefaultPorts.Vnc];

    /// <summary>
    /// Result of scanning a single host.
    /// </summary>
    public sealed record ScanResult(
        string IpAddress,
        bool IsAlive,
        long RoundtripMs,
        string? Hostname,
        List<int> OpenPorts);

    /// <summary>
    /// Scans a CIDR subnet (e.g., "192.168.1.0/24") and returns discovered hosts.
    /// Uses parallel ICMP pings followed by port probes on responsive hosts.
    /// </summary>
    /// <param name="cidr">CIDR notation subnet (e.g., "192.168.1.0/24").</param>
    /// <param name="progress">Optional progress callback: (completed, total).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of scan results for responsive hosts.</returns>
    public static async Task<List<ScanResult>> ScanSubnetAsync(
        string cidr,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        var (network, prefixLength) = ParseCidr(cidr);
        var addresses = GenerateAddresses(network, prefixLength);
        var results = new List<ScanResult>();
        var completed = 0;
        var total = addresses.Count;

        // Parallel ping sweep (max 64 concurrent)
        var semaphore = new SemaphoreSlim(64);
        try
        {
            var tasks = addresses.Select(async ip =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await PingHostAsync(ip, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref completed);
                    progress?.Invoke(completed, total);
                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var pingResults = await Task.WhenAll(tasks).ConfigureAwait(false);

            // Port probe on alive hosts
            foreach (var result in pingResults.Where(r => r is { IsAlive: true }))
            {
                ct.ThrowIfCancellationRequested();
                var openPorts = await ProbePortsAsync(result!.IpAddress, ct)
                    .ConfigureAwait(false);
                results.Add(result with { OpenPorts = openPorts });
            }
        }
        finally
        {
            semaphore.Dispose();
        }

        return results.OrderBy(r => IPAddress.Parse(r.IpAddress).GetAddressBytes(),
            new IpComparer()).ToList();
    }

    private static async Task<ScanResult?> PingHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, PingTimeoutMs).ConfigureAwait(false);

            if (reply.Status != IPStatus.Success)
                return null;

            string? hostname = null;
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip, ct).ConfigureAwait(false);
                hostname = entry.HostName;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[NetworkScanner] DNS resolve: {ex.Message}"); }

            return new ScanResult(ip, true, reply.RoundtripTime, hostname, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[NetworkScanner] ping: {ex.Message}");
            return null;
        }
    }

    private static async Task<List<int>> ProbePortsAsync(string ip, CancellationToken ct)
    {
        var open = new List<int>();

        foreach (var port in CommonPorts)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = new CancellationTokenSource(PortProbeTimeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                await client.ConnectAsync(ip, port, linked.Token).ConfigureAwait(false);
                open.Add(port);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-port timeout - not a scan cancellation. Treat as closed port.
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[NetworkScanner] port probe {port}: {ex.Message}");
            }
        }

        return open;
    }

    internal static (string Network, int PrefixLength) ParseCidr(string cidr)
    {
        string trimmed = cidr.Trim();

        // Support single IP without CIDR prefix (e.g. "192.168.1.1")
        if (!trimmed.Contains('/'))
        {
            if (!TryParseIPv4(trimmed))
                throw new ArgumentException($"Invalid IP address: {trimmed}. NetworkScanner supports IPv4 only.");
            return (trimmed, 32);
        }

        string[] parts = trimmed.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int prefix) || prefix < 16 || prefix > 32)
            throw new ArgumentException($"Invalid CIDR notation: {cidr}. Use format like 192.168.1.0/24 (prefix 16-32).");

        if (!TryParseIPv4(parts[0]))
            throw new ArgumentException($"Invalid IP address: {parts[0]}. NetworkScanner supports IPv4 only.");

        return (parts[0], prefix);
    }

    private static bool TryParseIPv4(string value) =>
        IPAddress.TryParse(value, out IPAddress? parsed)
        && parsed.AddressFamily == AddressFamily.InterNetwork;

    internal static List<string> GenerateAddresses(string network, int prefixLength)
    {
        var ip = IPAddress.Parse(network);
        var bytes = ip.GetAddressBytes();
        var networkInt = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        int hostBits = 32 - prefixLength;
        uint mask = hostBits >= 32 ? 0u : ~((1u << hostBits) - 1);
        networkInt &= mask;

        // /32 = single host, /31 = point-to-point (2 hosts, no broadcast)
        if (prefixLength == 32)
        {
            return [$"{(networkInt >> 24) & 0xFF}.{(networkInt >> 16) & 0xFF}.{(networkInt >> 8) & 0xFF}.{networkInt & 0xFF}"];
        }

        if (prefixLength == 31)
        {
            return
            [
                $"{(networkInt >> 24) & 0xFF}.{(networkInt >> 16) & 0xFF}.{(networkInt >> 8) & 0xFF}.{networkInt & 0xFF}",
                $"{((networkInt + 1) >> 24) & 0xFF}.{((networkInt + 1) >> 16) & 0xFF}.{((networkInt + 1) >> 8) & 0xFF}.{(networkInt + 1) & 0xFF}"
            ];
        }

        var hostCount = (1u << hostBits) - 2; // exclude network and broadcast
        var addresses = new List<string>((int)hostCount);

        for (uint i = 1; i <= hostCount; i++)
        {
            var hostIp = networkInt + i;
            addresses.Add($"{(hostIp >> 24) & 0xFF}.{(hostIp >> 16) & 0xFF}.{(hostIp >> 8) & 0xFF}.{hostIp & 0xFF}");
        }

        return addresses;
    }

    private sealed class IpComparer : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null || y is null) return 0;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var cmp = x[i].CompareTo(y[i]);
                if (cmp != 0) return cmp;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
