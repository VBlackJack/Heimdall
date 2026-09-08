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
using System.Net.Sockets;
using Heimdall.Core.Configuration;
using Heimdall.Core.Discovery;
using Heimdall.Core.Network;
using Heimdall.Core.Security;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

public sealed class SnmpWalkProgress
{
    public required int EntryCount { get; init; }
    public SnmpEntry? LatestEntry { get; init; }
}

public interface ISnmpWalkerService
{
    Task<IReadOnlyList<SnmpEntry>> WalkDirectAsync(
        string host,
        string community,
        string startOid,
        int timeoutMs,
        Action<SnmpWalkProgress>? onProgress,
        CancellationToken ct);

    Task<IReadOnlyList<SnmpEntry>> WalkViaTunnelAsync(
        string host,
        string community,
        string oid,
        Action<SnmpWalkProgress>? onProgress,
        CancellationToken ct);

    Task<IReadOnlyList<CommunityResult>> TestCommunitiesAsync(
        string host,
        Func<string, string>? localize,
        CancellationToken ct);

    void SetGateway(SshGatewayDto? gateway);
}

/// <summary>
/// SNMP walk service for direct UDP and tunnel-based execution.
/// </summary>
public sealed class SnmpWalkerService : ISnmpWalkerService
{
    private SshGatewayDto? _gateway;

    public SnmpWalkerService(SshGatewayDto? gateway = null)
    {
        _gateway = gateway;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<IReadOnlyList<SnmpEntry>> WalkDirectAsync(
        string host,
        string community,
        string startOid,
        int timeoutMs,
        Action<SnmpWalkProgress>? onProgress,
        CancellationToken ct)
    {
        var currentOid = startOid;
        var prefix = startOid + ".";
        var entries = new List<SnmpEntry>();

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
        var endpoint = new IPEndPoint(address, SnmpCodec.DefaultPort);

        using var udp = new UdpClient(address.AddressFamily);

        while (!ct.IsCancellationRequested && entries.Count < SnmpCodec.MaxWalkResults)
        {
            var oidComponents = SnmpCodec.ParseOidString(currentOid);
            if (oidComponents is null)
            {
                break;
            }

            var packet = SnmpCodec.BuildSnmpGetNextRequest(community, oidComponents);
            await udp.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            var response = SnmpCodec.ParseSnmpGetNextResponse(result.Buffer);
            if (response.Oid is null)
            {
                break;
            }

            if (!response.Oid.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.Equals(response.Oid, startOid, StringComparison.Ordinal))
            {
                break;
            }

            if (string.Equals(response.Type, "endOfMibView", StringComparison.Ordinal) ||
                string.Equals(response.Type, "noSuchObject", StringComparison.Ordinal) ||
                string.Equals(response.Type, "noSuchInstance", StringComparison.Ordinal))
            {
                break;
            }

            var entry = new SnmpEntry
            {
                Oid = response.Oid,
                Name = SnmpCodec.ResolveOidName(response.Oid),
                Type = response.Type,
                Value = response.Value,
            };

            entries.Add(entry);
            onProgress?.Invoke(new SnmpWalkProgress
            {
                EntryCount = entries.Count,
                LatestEntry = entry,
            });

            currentOid = response.Oid;
        }

        return entries;
    }

    public async Task<IReadOnlyList<SnmpEntry>> WalkViaTunnelAsync(
        string host,
        string community,
        string oid,
        Action<SnmpWalkProgress>? onProgress,
        CancellationToken ct)
    {
        if (_gateway is null)
        {
            throw new InvalidOperationException("Tunnel walk requested without a configured gateway.");
        }

        return await Task.Run(async () =>
        {
            var entries = new List<SnmpEntry>();
            using var client = await ToolGatewayConnector.ConnectAsync(_gateway, ct).ConfigureAwait(false);
            try
            {
                var commandText =
                    $"snmpwalk -v2c -c {InputValidator.EscapeShellArg(community)} {InputValidator.EscapeShellArg(host)} {InputValidator.EscapeShellArg(oid)} 2>&1";
                using var cmd = client.CreateCommand(commandText);
                cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                var output = cmd.Execute()?.Trim();

                if (string.IsNullOrWhiteSpace(output))
                {
                    return (IReadOnlyList<SnmpEntry>)entries;
                }

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    ct.ThrowIfCancellationRequested();
                    var parsed = SnmpCodec.ParseSnmpWalkLine(line.Trim());
                    if (parsed is null)
                    {
                        continue;
                    }

                    entries.Add(parsed);
                    onProgress?.Invoke(new SnmpWalkProgress
                    {
                        EntryCount = entries.Count,
                        LatestEntry = parsed,
                    });
                }

                return (IReadOnlyList<SnmpEntry>)entries;
            }
            finally
            {
                try
                {
                    client.Disconnect();
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommunityResult>> TestCommunitiesAsync(
        string host,
        Func<string, string>? localize,
        CancellationToken ct)
    {
        var results = new List<CommunityResult>();
        foreach (var community in NetworkToolPresets.SnmpCommonCommunities)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = await UdpProbeEngine.QuerySnmpAsync(host, community, 2000, ct).ConfigureAwait(false);
                if (info is null)
                {
                    continue;
                }

                results.Add(new CommunityResult
                {
                    Community = community,
                    Status = L(localize, "ToolSnmpCommunityAccepted"),
                    SysName = info.SysName ?? string.Empty,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Rejected community or transient failure.
            }
        }

        return results;
    }

    private static string L(Func<string, string>? localize, string key) => localize?.Invoke(key) ?? key;
}
