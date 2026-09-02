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

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;
using Heimdall.Core.Security;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.Services;

public sealed class PortScanProgress
{
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public PortProbeResult? LatestResult { get; init; }
}

public interface IPortScanService
{
    Task<IReadOnlyList<PortProbeResult>> ScanAsync(
        string host,
        IReadOnlyList<int> ports,
        Action<PortScanProgress>? onProgress,
        CancellationToken ct);

    void SetGateway(SshGatewayDto? gateway);
}

/// <summary>
/// Service that performs direct and tunnel-based port scanning.
/// </summary>
public sealed class PortScanService : IPortScanService
{
    private SshGatewayDto? _gateway;

    public PortScanService(SshGatewayDto? gateway = null)
    {
        _gateway = gateway;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<IReadOnlyList<PortProbeResult>> ScanAsync(
        string host,
        IReadOnlyList<int> ports,
        Action<PortScanProgress>? onProgress,
        CancellationToken ct)
    {
        var results = new List<PortProbeResult>();
        var completed = 0;
        var lockObject = new object();

        SshClient? tunnelClient = null;
        if (_gateway is not null)
        {
            tunnelClient = ToolGatewayConnector.Connect(_gateway);
        }

        var concurrency = tunnelClient is not null ? 10 : PortScanEngine.MaxConcurrent;
        using var semaphore = new SemaphoreSlim(concurrency);

        try
        {
            var tasks = ports.Select(async port =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var probeResult = tunnelClient is not null
                        ? await ProbePortViaTunnelAsync(tunnelClient, host, port, ct).ConfigureAwait(false)
                        : await ProbePortDirectAsync(host, port, ct).ConfigureAwait(false);

                    int current;
                    lock (lockObject)
                    {
                        results.Add(probeResult);
                        current = ++completed;
                    }

                    onProgress?.Invoke(new PortScanProgress
                    {
                        Completed = current,
                        Total = ports.Count,
                        LatestResult = probeResult,
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Scan was cancelled by the caller.
            }
        }
        finally
        {
            if (tunnelClient is not null)
            {
                try
                {
                    tunnelClient.Disconnect();
                }
                catch
                {
                    // Best effort cleanup.
                }

                tunnelClient.Dispose();
            }
        }

        return results;
    }

    private static async Task<PortProbeResult> ProbePortDirectAsync(string host, int port, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(PortScanEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var service = NetworkToolPresets.GetPortServiceLabel(port);
            var rawBanner = await GrabBannerAsync(client, ct).ConfigureAwait(false);
            var banner = BannerGrabEngine.ParseBanner(rawBanner);
            return new PortProbeResult(port, true, service, $"{stopwatch.ElapsedMilliseconds} ms", banner);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            stopwatch.Stop();
            var service = NetworkToolPresets.GetPortServiceLabel(port);
            return new PortProbeResult(port, false, service, "-", null);
        }
    }

    private static async Task<string?> GrabBannerAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(PortScanEngine.BannerGrabTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var stream = client.GetStream();
            var buffer = new byte[PortScanEngine.BannerMaxBytes];
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            return read > 0 ? Encoding.ASCII.GetString(buffer, 0, read).Trim() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PortProbeResult> ProbePortViaTunnelAsync(
        SshClient sshClient,
        string host,
        int port,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await Task.Run(() =>
            {
                var safeHost = InputValidator.EscapeShellArg(host);
                using var command = sshClient.CreateCommand(
                    $"timeout 2 bash -c \"echo >/dev/tcp/{safeHost}/{port}\" 2>/dev/null && echo OPEN || echo CLOSED");
                command.CommandTimeout = TimeSpan.FromSeconds(5);
                command.Execute();
                return command.Result?.Trim();
            }, ct).ConfigureAwait(false);

            stopwatch.Stop();
            var isOpen = string.Equals(result, "OPEN", StringComparison.OrdinalIgnoreCase);
            var service = NetworkToolPresets.GetPortServiceLabel(port);
            return new PortProbeResult(port, isOpen, service, isOpen ? $"{stopwatch.ElapsedMilliseconds} ms" : "-", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            stopwatch.Stop();
            var service = NetworkToolPresets.GetPortServiceLabel(port);
            return new PortProbeResult(port, false, service, "-", null);
        }
    }
}
