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
using Heimdall.Core.Discovery;
using Heimdall.Core.Security;
using Renci.SshNet;

namespace Heimdall.App.Services;

/// <summary>
/// Progress payload emitted by cartography scans.
/// </summary>
public sealed class CartographyScanProgress
{
    public required string Phase { get; init; }

    public required string StatusKey { get; init; }

    public string[]? StatusArgs { get; init; }

    public bool IsIndeterminate { get; init; }

    public int Completed { get; init; }

    public int Total { get; init; }

    public HostScanResult? CompletedHost { get; init; }
}

/// <summary>
/// Abstraction for direct and tunnel-backed cartography scans.
/// </summary>
public interface ICartographyScanner
{
    /// <summary>
    /// Runs a full cartography scan, either direct or via SSH tunnel.
    /// </summary>
    Task<NetworkScanSnapshot> ScanAsync(
        ScanProfile profile,
        NetworkKnowledgeBase? knowledgeBase,
        Action<CartographyScanProgress>? onProgress,
        CancellationToken ct);

    /// <summary>
    /// Detects subnets available on a remote SSH gateway.
    /// </summary>
    Task<List<string>> DetectRemoteSubnetsAsync(CancellationToken ct);

    /// <summary>
    /// Sets the SSH gateway for tunnel mode. Null = direct scan.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Cleans up resources.
    /// </summary>
    void Cleanup();
}

/// <summary>
/// Scanner implementation that delegates to <see cref="CartographyEngine"/>
/// for direct scans and executes equivalent SSH-based probes for tunnel scans.
/// </summary>
public sealed class CartographyScanner : ICartographyScanner
{
    private const int MinCommandTimeoutSeconds = 10;

    private SshGatewayDto? _gateway;

    public Task<List<string>> DetectRemoteSubnetsAsync(CancellationToken ct)
    {
        if (_gateway is null)
        {
            throw new InvalidOperationException("Remote subnet detection requires an SSH gateway.");
        }

        return SubnetDetector.DetectRemoteSubnetsAsync(_gateway, ct);
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public void Cleanup()
    {
        // No shared resources to dispose. Tunnel scans use short-lived SSH sessions.
    }

    public async Task<NetworkScanSnapshot> ScanAsync(
        ScanProfile profile,
        NetworkKnowledgeBase? knowledgeBase,
        Action<CartographyScanProgress>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_gateway is null)
        {
            return await ScanDirectAsync(profile, knowledgeBase, onProgress, ct).ConfigureAwait(false);
        }

        return await ScanViaTunnelAsync(profile, onProgress, ct).ConfigureAwait(false);
    }

    private static async Task<NetworkScanSnapshot> ScanDirectAsync(
        ScanProfile profile,
        NetworkKnowledgeBase? knowledgeBase,
        Action<CartographyScanProgress>? onProgress,
        CancellationToken ct)
    {
        var engine = new CartographyEngine();
        engine.CacheHitProgress += (host, phase) =>
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "Cache",
                StatusKey = "ToolNetMapKbCacheHit",
                StatusArgs = [host, phase],
                IsIndeterminate = true,
            });
        engine.HostDiscoveryProgress += (completed, total) =>
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "Discovery",
                StatusKey = "ToolNetMapStatusDiscovery",
                StatusArgs =
                [
                    completed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    total.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                IsIndeterminate = false,
                Completed = completed,
                Total = total,
            });
        engine.PortScanProgress += (host, completed, totalPorts) =>
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "PortScan",
                StatusKey = "ToolNetMapStatusScanning",
                StatusArgs =
                [
                    host,
                    completed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    totalPorts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                IsIndeterminate = false,
                Completed = completed,
                Total = totalPorts,
            });
        engine.EnrichmentProgress += (host, phase) =>
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "Enrichment",
                StatusKey = "ToolNetMapStatusEnriching",
                StatusArgs = [host, phase],
                IsIndeterminate = true,
            });
        engine.HostCompleted += host =>
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "HostCompleted",
                StatusKey = string.Empty,
                IsIndeterminate = true,
                CompletedHost = host,
            });

        return await engine.ScanAsync(profile, knowledgeBase, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans a subnet via SSH gateway using remote commands.
    /// Phase 1: ping sweep + ARP table for host discovery.
    /// Phase 2: parallel /dev/tcp port probes with per-probe timeout.
    /// Phase 3: batch reverse DNS.
    /// </summary>
    private async Task<NetworkScanSnapshot> ScanViaTunnelAsync(
        ScanProfile profile,
        Action<CartographyScanProgress>? onProgress,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var gateway = _gateway!;

        Report(onProgress, new CartographyScanProgress
        {
            Phase = "TunnelConnect",
            StatusKey = "ToolTunnelConnecting",
            StatusArgs = [gateway.Name],
            IsIndeterminate = true,
        });

        using var sshClient = await ToolGatewayConnector.ConnectAsync(gateway, ct).ConfigureAwait(false);

        var ipList = CartographyEngine.ParseCidr(profile.Subnet);
        var hosts = new List<HostScanResult>();
        var ports = profile.CustomPorts ?? (profile.Depth == ScanDepth.Quick
            ? CartographyEngine.QuickPorts
            : CartographyEngine.StandardPorts);
        var portList = string.Join(" ", ports.Where(port => port is >= 1 and <= 65535));

        HashSet<string> aliveHosts;

        if (profile.SkipPing)
        {
            aliveHosts = new HashSet<string>(ipList, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "TunnelDiscovery",
                StatusKey = "ToolNetMapTunnelPingSweep",
                IsIndeterminate = true,
            });

            aliveHosts = await TunnelPingSweepAsync(sshClient, ipList, ct).ConfigureAwait(false);

            var arpHosts = await TunnelArpDiscoveryAsync(sshClient, ipList, ct).ConfigureAwait(false);
            foreach (var ip in arpHosts)
            {
                aliveHosts.Add(ip);
            }

            if (aliveHosts.Count == 0)
            {
                aliveHosts = new HashSet<string>(ipList, StringComparer.OrdinalIgnoreCase);
            }

            Report(onProgress, new CartographyScanProgress
            {
                Phase = "TunnelDiscovery",
                StatusKey = "ToolNetMapTunnelDiscovered",
                StatusArgs =
                [
                    aliveHosts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ipList.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                IsIndeterminate = true,
            });
        }

        var targetsInOrder = ipList.Where(aliveHosts.Contains).ToList();
        var dnsMap = profile.ReverseDns
            ? await TunnelBatchReverseDnsAsync(sshClient, targetsInOrder, ct).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var completed = 0;
        foreach (var ip in targetsInOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!System.Net.IPAddress.TryParse(ip, out _))
            {
                continue;
            }

            var openServices = new List<ServiceResult>();
            try
            {
                var safeIp = InputValidator.EscapeShellArg(ip);
                using var command = sshClient.CreateCommand(
                    $"bash -c 'for p in {portList}; do " +
                    $"(echo >/dev/tcp/{safeIp}/$p && echo $p) 2>/dev/null & " +
                    "done; sleep 5; kill $(jobs -p) 2>/dev/null; wait'");
                command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(MinCommandTimeoutSeconds, ports.Length / 4));
                var result = await Task.Run(command.Execute, ct).ConfigureAwait(false);

                if (result is not null)
                {
                    foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out var port))
                        {
                            var serviceName = RoleClassifier.GetPortServiceName(port);
                            openServices.Add(new ServiceResult(
                                port,
                                true,
                                serviceName != $"Port-{port}" ? serviceName : null,
                                null,
                                null,
                                0));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep scanning remaining hosts.
            }

            dnsMap.TryGetValue(ip, out var hostname);
            var openPorts = openServices.Select(service => service.Port).ToList();
            var roles = RoleClassifier.Classify(openPorts);

            var hostResult = new HostScanResult(
                ip,
                hostname,
                true,
                0,
                openServices,
                roles.FirstOrDefault(),
                roles);
            hosts.Add(hostResult);

            completed++;
            Report(onProgress, new CartographyScanProgress
            {
                Phase = "TunnelScan",
                StatusKey = "ToolNetMapTunnelScanningHost",
                StatusArgs =
                [
                    ip,
                    completed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    targetsInOrder.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                IsIndeterminate = false,
                Completed = completed,
                Total = targetsInOrder.Count,
                CompletedHost = hostResult,
            });
        }

        var orderedHosts = hosts
            .OrderBy(host => CartographyEngine.IpToLong(host.IpAddress))
            .ToList();
        var vlans = VlanDetector.InferFromHosts(orderedHosts, profile.Subnet);

        return new NetworkScanSnapshot(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            profile,
            gateway.Name,
            DateTime.UtcNow - startTime,
            orderedHosts,
            vlans);
    }

    /// <summary>
    /// Runs a batch ping sweep on the remote gateway.
    /// </summary>
    private static async Task<HashSet<string>> TunnelPingSweepAsync(
        SshClient client,
        List<string> ips,
        CancellationToken ct)
    {
        var aliveHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ipArgs = string.Join(" ", ips
            .Where(ip => System.Net.IPAddress.TryParse(ip, out _))
            .Select(InputValidator.EscapeShellArg));

        try
        {
            using var command = client.CreateCommand(
                $"for ip in {ipArgs}; do " +
                "(ping -c1 -W1 $ip >/dev/null 2>&1 && echo $ip) & " +
                "done; wait");
            command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(15, ips.Count / 4));
            var result = await Task.Run(command.Execute, ct).ConfigureAwait(false);

            if (result is not null)
            {
                foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ip = line.Trim();
                    if (System.Net.IPAddress.TryParse(ip, out _))
                    {
                        aliveHosts.Add(ip);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore tunnel discovery errors and continue with fallback behavior.
        }

        return aliveHosts;
    }

    /// <summary>
    /// Reads the gateway ARP table to discover hosts that block ICMP
    /// but have recently communicated on the remote network.
    /// </summary>
    private static async Task<HashSet<string>> TunnelArpDiscoveryAsync(
        SshClient client,
        List<string> subnetIps,
        CancellationToken ct)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subnetSet = new HashSet<string>(subnetIps, StringComparer.OrdinalIgnoreCase);

        try
        {
            using var command = client.CreateCommand(
                "cat /proc/net/arp 2>/dev/null || arp -n 2>/dev/null");
            command.CommandTimeout = TimeSpan.FromSeconds(5);
            var result = await Task.Run(command.Execute, ct).ConfigureAwait(false);

            if (result is not null)
            {
                foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1
                        && System.Net.IPAddress.TryParse(parts[0], out _)
                        && subnetSet.Contains(parts[0]))
                    {
                        discovered.Add(parts[0]);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore tunnel ARP discovery failures.
        }

        return discovered;
    }

    /// <summary>
    /// Resolves reverse DNS for all IPs in a single SSH command.
    /// </summary>
    private static async Task<Dictionary<string, string>> TunnelBatchReverseDnsAsync(
        SshClient client,
        List<string> ips,
        CancellationToken ct)
    {
        var dnsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ips.Count == 0)
        {
            return dnsMap;
        }

        var ipArgs = string.Join(" ", ips
            .Where(ip => System.Net.IPAddress.TryParse(ip, out _))
            .Select(InputValidator.EscapeShellArg));

        try
        {
            using var command = client.CreateCommand(
                $"for ip in {ipArgs}; do " +
                "r=$(host $ip 2>/dev/null | head -1); " +
                "echo \"$ip|$r\"; " +
                "done");
            command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(MinCommandTimeoutSeconds, ips.Count));
            var result = await Task.Run(command.Execute, ct).ConfigureAwait(false);

            if (result is not null)
            {
                foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = line.IndexOf('|');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var ip = line[..separator].Trim();
                    var dnsLine = line[(separator + 1)..].Trim();
                    if (dnsLine.Contains("domain name pointer", StringComparison.Ordinal)
                        && System.Net.IPAddress.TryParse(ip, out _))
                    {
                        var hostname = dnsLine
                            .Split("domain name pointer", StringSplitOptions.None)[1]
                            .Trim()
                            .TrimEnd('.');
                        if (hostname.Length > 0)
                        {
                            dnsMap[ip] = hostname;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore reverse DNS failures.
        }

        return dnsMap;
    }

    private static void Report(Action<CartographyScanProgress>? onProgress, CartographyScanProgress progress)
    {
        onProgress?.Invoke(progress);
    }
}
