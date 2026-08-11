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

using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services.WinRm;

internal sealed class WinRmPreflight
{
    internal const int DefaultProbeTimeoutMs = 5000;

    private readonly TimeSpan _probeTimeout;
    private readonly Func<string, int, TimeSpan, CancellationToken, Task> _tcpProbe;
    private readonly Func<string, int, TimeSpan, bool, CancellationToken, Task> _tlsProbe;

    public WinRmPreflight(
        Func<string, int, TimeSpan, CancellationToken, Task>? tcpProbe = null,
        Func<string, int, TimeSpan, bool, CancellationToken, Task>? tlsProbe = null,
        TimeSpan? probeTimeout = null)
    {
        _probeTimeout = probeTimeout ?? TimeSpan.FromMilliseconds(DefaultProbeTimeoutMs);
        if (_probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout), "Probe timeout must be positive.");
        }

        _tcpProbe = tcpProbe ?? WinRmTransportProbes.DefaultTcpProbeAsync;
        _tlsProbe = tlsProbe ?? WinRmTransportProbes.DefaultTlsProbeAsync;
    }

    public async Task EnsureReachableAsync(ServerProfileDto server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(server.RemoteServer);

        string host = server.RemoteServer;
        int port = WinRmPowerShellLaunchBuilder.ResolvePort(server);
        await EnsureReachableAsync(server, host, port, ct).ConfigureAwait(false);
    }

    public async Task EnsureReachableAsync(
        ServerProfileDto server,
        string host,
        int port,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        await RunTcpProbeAsync(host, port, ct).ConfigureAwait(false);
        if (server.WinRmUseSsl)
        {
            await RunTlsProbeAsync(host, port, server.WinRmSkipCertificateCheck, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task RunTcpProbeAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            await _tcpProbe(host, port, _probeTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException ex) when (IsDnsFailure(ex.SocketErrorCode))
        {
            throw BuildException("TCP", host, port, "ErrorWinRmPreflightDnsFailure", [host], ex);
        }
        catch (SocketException ex)
        {
            throw BuildException("TCP", host, port, "ErrorWinRmPreflightUnreachable", [host, port], ex);
        }
        catch (TimeoutException ex)
        {
            throw BuildException("TCP", host, port, "ErrorWinRmPreflightUnreachable", [host, port], ex);
        }
    }

    private async Task RunTlsProbeAsync(
        string host,
        int port,
        bool skipCertValidation,
        CancellationToken ct)
    {
        try
        {
            await _tlsProbe(host, port, _probeTimeout, skipCertValidation, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationException ex)
        {
            throw BuildException("TLS", host, port, "ErrorWinRmPreflightTlsFailed", [host, port], ex);
        }
        catch (IOException ex)
        {
            throw BuildException("TLS", host, port, "ErrorWinRmPreflightTlsFailed", [host, port], ex);
        }
        catch (SocketException ex)
        {
            throw BuildException("TLS", host, port, "ErrorWinRmPreflightUnreachable", [host, port], ex);
        }
        catch (TimeoutException ex)
        {
            throw BuildException("TLS", host, port, "ErrorWinRmPreflightUnreachable", [host, port], ex);
        }
    }

    private static bool IsDnsFailure(SocketError socketError) =>
        socketError is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain;

    private static WinRmPreflightException BuildException(
        string protocol,
        string host,
        int port,
        string localizationKey,
        object[] localizationArguments,
        Exception exception)
    {
        string detail = $"WinRM {protocol} preflight failed for {host}:{port}: {exception.Message}";
        Core.Logging.FileLogger.Warn(detail);
        return new WinRmPreflightException(localizationKey, localizationArguments, detail, exception);
    }
}
