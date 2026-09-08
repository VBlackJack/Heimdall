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
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Traceroute service contract for direct and gateway-routed traces.
/// </summary>
public interface ITracerouteService
{
    /// <summary>
    /// Sets the SSH gateway used for the next trace. Null means direct mode.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Executes a traceroute and reports hops, progress, and reverse DNS updates.
    /// </summary>
    Task<bool> TraceAsync(
        TraceInputs inputs,
        IProgress<TraceHopResult>? onHop,
        IProgress<(int Current, int Total)>? onProgress,
        IProgress<HopHostnameUpdate>? onHostname,
        CancellationToken ct);
}

/// <summary>
/// Direct and SSH-tunneled traceroute implementation.
/// </summary>
public sealed class TracerouteService : ITracerouteService
{
    private SshGatewayDto? _gateway;

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<bool> TraceAsync(
        TraceInputs inputs,
        IProgress<TraceHopResult>? onHop,
        IProgress<(int Current, int Total)>? onProgress,
        IProgress<HopHostnameUpdate>? onHostname,
        CancellationToken ct)
    {
        if (_gateway is not null)
        {
            return await TraceViaTunnelAsync(inputs, onHop, onProgress, onHostname, ct).ConfigureAwait(false);
        }

        return await TraceDirectAsync(inputs, onHop, onProgress, onHostname, ct).ConfigureAwait(false);
    }

    private static async Task<bool> TraceDirectAsync(
        TraceInputs inputs,
        IProgress<TraceHopResult>? onHop,
        IProgress<(int Current, int Total)>? onProgress,
        IProgress<HopHostnameUpdate>? onHostname,
        CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(inputs.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException(inputs.Host);
        }

        var targetIp = addresses[0];
        var buffer = new byte[TracerouteEngine.PingBufferSize];

        for (var ttl = 1; ttl <= inputs.MaxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Report((ttl, inputs.MaxHops));

            var probes = new List<HopProbeResult>(TracerouteEngine.ProbesPerHop);
            for (var probe = 0; probe < TracerouteEngine.ProbesPerHop; probe++)
            {
                ct.ThrowIfCancellationRequested();
                probes.Add(await SendProbeAsync(targetIp, ttl, buffer).ConfigureAwait(false));
            }

            var hop = TracerouteEngine.AggregateHopProbes(ttl, probes, targetIp);
            onHop?.Report(hop);

            if (hop.Address != "*")
            {
                var hopIndex = ttl - 1;
                _ = ResolveHostnameAsync(hopIndex, hop.Address, onHostname, ct);
            }

            if (hop.Status == HopStatus.Destination)
            {
                break;
            }
        }

        return true;
    }

    private async Task<bool> TraceViaTunnelAsync(
        TraceInputs inputs,
        IProgress<TraceHopResult>? onHop,
        IProgress<(int Current, int Total)>? onProgress,
        IProgress<HopHostnameUpdate>? onHostname,
        CancellationToken ct)
    {
        Renci.SshNet.SshClient? client = null;
        try
        {
            client = await ToolGatewayConnector.ConnectAsync(_gateway!, ct).ConfigureAwait(false);
            var escapedHost = InputValidator.EscapeShellArg(inputs.Host);
            var command =
                $"traceroute -n -m {inputs.MaxHops} {escapedHost} 2>/dev/null || " +
                $"tracert -d -h {inputs.MaxHops} {escapedHost} 2>/dev/null";

            onProgress?.Report((0, inputs.MaxHops));

            var result = await Task.Run(() =>
            {
                using var sshCommand = client.CreateCommand(command);
                sshCommand.CommandTimeout = TimeSpan.FromSeconds(inputs.MaxHops * 5);
                sshCommand.Execute();
                return sshCommand.Result?.Trim();
            }, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(result))
            {
                return false;
            }

            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var hopNumber = 0;

            foreach (var rawLine in lines)
            {
                ct.ThrowIfCancellationRequested();

                var parsed = TracerouteEngine.ParseTracerouteLine(rawLine.Trim(), ref hopNumber);
                if (parsed is null)
                {
                    continue;
                }

                onHop?.Report(parsed);
                onProgress?.Report((hopNumber, inputs.MaxHops));

                if (parsed.Address != "*")
                {
                    var hopIndex = hopNumber - 1;
                    _ = ResolveHostnameAsync(hopIndex, parsed.Address, onHostname, ct);
                }
            }

            return true;
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }

                client.Dispose();
            }
        }
    }

    private static async Task<HopProbeResult> SendProbeAsync(IPAddress targetIp, int ttl, byte[] buffer)
    {
        try
        {
            using var ping = new Ping();
            var options = new PingOptions(ttl, true);
            var reply = await ping.SendPingAsync(
                targetIp,
                TracerouteEngine.PingTimeoutMs,
                buffer,
                options).ConfigureAwait(false);

            if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
            {
                return new HopProbeResult(reply.RoundtripTime, reply.Address);
            }

            return new HopProbeResult(-1, null);
        }
        catch (PingException)
        {
            return new HopProbeResult(-1, null);
        }
    }

    private static Task ResolveHostnameAsync(
        int hopIndex,
        string address,
        IProgress<HopHostnameUpdate>? onHostname,
        CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            try
            {
                var ip = IPAddress.Parse(address);
                var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(entry.HostName) &&
                    !string.Equals(entry.HostName, address, StringComparison.Ordinal))
                {
                    onHostname?.Report(new HopHostnameUpdate(hopIndex, address, entry.HostName));
                }
            }
            catch
            {
                // Reverse DNS is best-effort only.
            }
        }, ct);
    }
}
