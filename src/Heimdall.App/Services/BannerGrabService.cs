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

public sealed class BannerGrabProgress
{
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public BannerProbeResult? LatestResult { get; init; }
}

public interface IBannerGrabService
{
    Task<IReadOnlyList<BannerProbeResult>> GrabAsync(
        string host,
        IReadOnlyList<int> ports,
        Action<BannerGrabProgress>? onProgress,
        CancellationToken ct);

    void SetGateway(SshGatewayDto? gateway);
}

/// <summary>
/// Service that performs direct and tunnel-based banner grabbing.
/// </summary>
public sealed class BannerGrabService : IBannerGrabService
{
    private SshGatewayDto? _gateway;

    public BannerGrabService(SshGatewayDto? gateway = null)
    {
        _gateway = gateway;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<IReadOnlyList<BannerProbeResult>> GrabAsync(
        string host,
        IReadOnlyList<int> ports,
        Action<BannerGrabProgress>? onProgress,
        CancellationToken ct)
    {
        var results = new List<BannerProbeResult>();
        var completed = 0;
        var lockObject = new object();

        SshClient? tunnelClient = null;
        SemaphoreSlim? commandLock = null;
        if (_gateway is not null)
        {
            tunnelClient = ToolGatewayConnector.Connect(_gateway);
            commandLock = new SemaphoreSlim(1, 1);
        }

        var concurrency = tunnelClient is not null ? 10 : BannerGrabEngine.MaxConcurrent;
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
                        ? await ProbePortViaTunnelAsync(tunnelClient, commandLock!, host, port, ct).ConfigureAwait(false)
                        : await ProbePortDirectAsync(host, port, ct).ConfigureAwait(false);

                    int current;
                    lock (lockObject)
                    {
                        results.Add(probeResult);
                        current = ++completed;
                    }

                    onProgress?.Invoke(new BannerGrabProgress
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
            commandLock?.Dispose();

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

    private static async Task<BannerProbeResult> ProbePortDirectAsync(string host, int port, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(BannerGrabEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var rawBanner = await GrabBannerAsync(client, ct).ConfigureAwait(false);
            var banner = BannerGrabEngine.ParseBanner(rawBanner);
            var service = BannerGrabEngine.IdentifyService(port, banner, NetworkToolPresets.GetPortServiceLabel);
            return new BannerProbeResult(port, service, $"{stopwatch.ElapsedMilliseconds} ms", banner);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            stopwatch.Stop();
            var service = BannerGrabEngine.IdentifyService(port, null, NetworkToolPresets.GetPortServiceLabel);
            return new BannerProbeResult(port, service, "-", null);
        }
    }

    private static async Task<string?> GrabBannerAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BannerGrabEngine.BannerReadTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var stream = client.GetStream();
            var buffer = new byte[BannerGrabEngine.BannerMaxBytes];
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            return read > 0 ? Encoding.ASCII.GetString(buffer, 0, read).Trim() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BannerProbeResult> ProbePortViaTunnelAsync(
        SshClient sshClient,
        SemaphoreSlim commandLock,
        string host,
        int port,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var safeHost = InputValidator.EscapeShellArg(host);

            await commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var connectResult = await Task.Run(() =>
                {
                    using var command = sshClient.CreateCommand(
                        $"timeout 2 bash -c \"echo >/dev/tcp/{safeHost}/{port}\" 2>/dev/null && echo OPEN || echo CLOSED");
                    command.CommandTimeout = TimeSpan.FromSeconds(5);
                    command.Execute();
                    return command.Result?.Trim();
                }, ct).ConfigureAwait(false);

                if (!string.Equals(connectResult, "OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    stopwatch.Stop();
                    var closedService = BannerGrabEngine.IdentifyService(port, null, NetworkToolPresets.GetPortServiceLabel);
                    return new BannerProbeResult(port, closedService, "-", null);
                }

                var bannerRaw = await Task.Run(() =>
                {
                    using var command = sshClient.CreateCommand(
                        $"timeout 2 bash -c \"cat < /dev/tcp/{safeHost}/{port}\" 2>/dev/null | head -c {BannerGrabEngine.BannerMaxBytes}");
                    command.CommandTimeout = TimeSpan.FromSeconds(5);
                    command.Execute();
                    return command.Result?.Trim();
                }, ct).ConfigureAwait(false);

                stopwatch.Stop();
                var banner = BannerGrabEngine.ParseBanner(bannerRaw);
                var enrichedService = BannerGrabEngine.IdentifyService(port, banner, NetworkToolPresets.GetPortServiceLabel);
                return new BannerProbeResult(port, enrichedService, $"{stopwatch.ElapsedMilliseconds} ms", banner);
            }
            finally
            {
                commandLock.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            stopwatch.Stop();
            var service = BannerGrabEngine.IdentifyService(port, null, NetworkToolPresets.GetPortServiceLabel);
            return new BannerProbeResult(port, service, "-", null);
        }
    }
}
