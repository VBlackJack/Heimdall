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
using System.Net.NetworkInformation;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;
using Heimdall.Core.Security;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.Services;

public interface IPingService : IDisposable
{
    /// <summary>
    /// Sets the SSH gateway for tunnel-routed pings. Null = direct mode.
    /// Takes effect on the next session.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Opens the SSH tunnel if a gateway was set, otherwise returns immediately.
    /// </summary>
    Task StartSessionAsync(CancellationToken ct);

    /// <summary>
    /// Fires a single ping and returns the probe result.
    /// </summary>
    Task<PingProbeResult> PingAsync(string host, int seq, int timeoutMs, CancellationToken ct);

    /// <summary>
    /// Closes the SSH tunnel if one is open.
    /// </summary>
    void EndSession();
}

/// <summary>
/// Stateful ping service for direct and tunnel-based probes.
/// </summary>
public sealed class PingService : IPingService
{
    private SshGatewayDto? _gateway;
    private SshClient? _sshClient;
    private bool _disposed;

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task StartSessionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EndSession();

        if (_gateway is not null)
        {
            _sshClient = await ToolGatewayConnector.ConnectAsync(_gateway, ct).ConfigureAwait(false);
        }

    }

    public async Task<PingProbeResult> PingAsync(string host, int seq, int timeoutMs, CancellationToken ct)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        if (_sshClient is not null && _sshClient.IsConnected)
        {
            return await PingViaTunnelAsync(host, seq, timestamp, timeoutMs, ct).ConfigureAwait(false);
        }

        return await PingDirectAsync(host, seq, timestamp, timeoutMs, ct).ConfigureAwait(false);
    }

    public void EndSession()
    {
        if (_sshClient is null)
        {
            return;
        }

        try
        {
            if (_sshClient.IsConnected)
            {
                _sshClient.Disconnect();
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            _sshClient.Dispose();
            _sshClient = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndSession();
        GC.SuppressFinalize(this);
    }

    private static async Task<PingProbeResult> PingDirectAsync(
        string host,
        int seq,
        string timestamp,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (reply.Status == IPStatus.Success)
            {
                return new PingProbeResult(
                    seq,
                    timestamp,
                    reply.RoundtripTime,
                    PingStatus.Success,
                    reply.Options?.Ttl ?? 0,
                    "OK",
                    reply.Address?.ToString() ?? host);
            }

            return new PingProbeResult(
                seq,
                timestamp,
                -1,
                PingStatus.Timeout,
                0,
                reply.Status.ToString(),
                host);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PingException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return new PingProbeResult(seq, timestamp, -1, PingStatus.Error, 0, message, host);
        }
    }

    private async Task<PingProbeResult> PingViaTunnelAsync(
        string host,
        int seq,
        string timestamp,
        int timeoutMs,
        CancellationToken ct)
    {
        var timeoutSec = Math.Max(1, timeoutMs / 1000);
        var escapedHost = InputValidator.EscapeShellArg(host);
        var command = $"ping -c 1 -W {timeoutSec} {escapedHost}";

        try
        {
            var output = await Task.Run(() =>
            {
                using var cmd = _sshClient!.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMs + 5000);
                cmd.Execute();
                return cmd.Result;
            }, ct).ConfigureAwait(false);

            var latency = PingStatsEngine.ParsePingLatency(output);
            if (latency is long milliseconds)
            {
                return new PingProbeResult(seq, timestamp, milliseconds, PingStatus.Success, 0, "OK", host);
            }

            return new PingProbeResult(seq, timestamp, -1, PingStatus.Timeout, 0, "Timeout", host);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return new PingProbeResult(seq, timestamp, -1, PingStatus.Error, 0, message, host);
        }
    }
}
