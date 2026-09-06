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
using System.Text;

namespace Heimdall.Ssh.Tests;

public sealed class SshConnectionProbeTests
{
    private const int ClosedPortProbeTimeoutMs = 1000;
    private const int LocalServerProbeTimeoutMs = 5000;

    [Fact]
    public async Task ProbeAsync_ClosedPort_ReturnsNetworkFailure()
    {
        // The port is held closed for the whole probe rather than released before it. Nothing can
        // accept on an endpoint this test owns and never listens on, so a success is impossible
        // by construction instead of merely unlikely.
        using Socket reservation = ReserveClosedLoopbackPort(out int port);

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, ClosedPortProbeTimeoutMs);

        Assert.False(result.Success);
        Assert.True(
            result.FailureCode is SshFailureCode.NetworkRefused or SshFailureCode.NetworkTimedOut,
            $"Expected a network failure, got {result.FailureCode}.");
        Assert.True(
            result.MessageKey == SshConnectionProbe.MessageKeyConnectionRefused
                || result.MessageKey == SshConnectionProbe.MessageKeyConnectionTimedOut,
            $"Expected a network failure message key, got {result.MessageKey}.");

        // Checked AFTER the probe returned, deliberately. The contract above is permissive by
        // design - refused OR timed out - so it cannot tell a closed port from an open silent
        // one and cannot police the reservation. This can: if the port had been released before
        // the probe, as the old helper did, the competing bind below would succeed.
        AssertEndpointIsStillHeld(port);
        GC.KeepAlive(reservation);
    }

    /// <summary>
    /// Asserts that nothing else can take the endpoint, which is what makes the probe above a
    /// measurement of a genuinely closed port rather than of whatever claimed the port next.
    /// </summary>
    private static void AssertEndpointIsStillHeld(int port)
    {
        using Socket challenger = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        SocketException refusal = Assert.Throws<SocketException>(
            () => challenger.Bind(new IPEndPoint(IPAddress.Loopback, port)));

        Assert.Contains(
            refusal.SocketErrorCode,
            new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
    }

    [Fact]
    public async Task ProbeAsync_MissingBanner_ReturnsProtocolFailureMessageKey()
    {
        var (port, serverTask) = StartSingleResponseServer("");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, LocalServerProbeTimeoutMs);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.ProtocolError, result.FailureCode);
        Assert.Null(result.Banner);
        Assert.Equal(SshConnectionProbe.MessageKeyMissingBanner, result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    [Fact]
    public async Task ProbeAsync_NonSshBanner_ReturnsProtocolFailure()
    {
        var (port, serverTask) = StartSingleResponseServer("HTTP/1.1 200 OK\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, LocalServerProbeTimeoutMs);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.ProtocolError, result.FailureCode);
        Assert.Equal("HTTP/1.1 200 OK", result.Banner);
        Assert.Equal(SshConnectionProbe.MessageKeyNonSshBanner, result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    [Fact]
    public async Task ProbeAsync_NonSshUtf8Banner_DecodesDiagnosticText()
    {
        const string banner = "Acc\u00e8s refus\u00e9";
        var (port, serverTask) = StartSingleResponseServer(banner + "\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, LocalServerProbeTimeoutMs);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.ProtocolError, result.FailureCode);
        Assert.Equal(banner, result.Banner);
        Assert.Equal(SshConnectionProbe.MessageKeyNonSshBanner, result.MessageKey);
    }

    [Fact]
    public async Task ProbeAsync_PreLoginLineBeforeSshBanner_ReturnsSuccess()
    {
        var (port, serverTask) = StartSingleResponseServer("Authorized access only\r\nSSH-2.0-OpenSSH_9\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, LocalServerProbeTimeoutMs);
        await serverTask;

        Assert.True(
            result.Success,
            $"Expected probe success, got {result.FailureCode} with banner '{result.Banner}' and message key '{result.MessageKey}'.");
        Assert.Equal("SSH-2.0-OpenSSH_9", result.Banner);
        Assert.Null(result.FailureCode);
        Assert.Null(result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    // A-07: a reset during the banner read surfaces as an IOException wrapping the
    // SocketException. Only the bare SocketException was caught, so the caller
    // displayed the raw .NET message instead of the classified network failure.
    [Fact]
    public async Task ProbeAsync_ConnectionResetDuringBannerRead_ReturnsNetworkResetFailure()
    {
        (int port, Task serverTask) = StartResettingServer();

        SshConnectionProbe.ProbeResult result =
            await SshConnectionProbe.ProbeAsync("127.0.0.1", port, LocalServerProbeTimeoutMs);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.NetworkReset, result.FailureCode);
        Assert.Equal(SshConnectionProbe.MessageKeyConnectionReset, result.MessageKey);
        Assert.Null(result.Banner);
    }

    [Fact]
    public async Task ProbeAsync_SshBanner_ReturnsSuccess()
    {
        var (port, serverTask) = StartSingleResponseServer("SSH-2.0-MockServer\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, LocalServerProbeTimeoutMs);
        await serverTask;

        Assert.True(result.Success);
        Assert.Equal("SSH-2.0-MockServer", result.Banner);
        Assert.Null(result.FailureCode);
        Assert.Null(result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    /// <summary>
    /// Reserves a loopback port and keeps it closed to connections for as long as the returned
    /// socket is alive.
    /// </summary>
    /// <remarks>
    /// This used to return a bare <see cref="int"/> after starting and stopping a listener, which
    /// is a time-of-check/time-of-use hole: from the moment the listener stops, the ephemeral port
    /// belongs to whichever socket on the machine asks for one next, and the probe then measures
    /// whatever took it. The caller holds this socket across the probe instead, so a reachable
    /// result is impossible by construction rather than merely improbable.
    /// <para>
    /// The socket is bound and never listened on: that is what keeps the endpoint owned by the
    /// test and closed to connections at the same time. <see cref="Socket.ExclusiveAddressUse"/>
    /// is set as an explicit declaration that the endpoint is not to be shared; on Windows a
    /// competing bind is refused with or without it.
    /// </para>
    /// </remarks>
    private static Socket ReserveClosedLoopbackPort(out int port)
    {
        Socket reservation = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            reservation.ExclusiveAddressUse = true;
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static (int Port, Task ServerTask) StartSingleResponseServer(string response)
    {
        return StartSingleResponseServer(Encoding.UTF8.GetBytes(response));
    }

    /// <summary>
    /// Accepts one connection and closes it with a zero linger, which sends a TCP reset
    /// instead of a FIN, so the probe's pending banner read fails with a connection reset.
    /// </summary>
    private static (int Port, Task ServerTask) StartResettingServer()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serverTask = Task.Run(async () =>
        {
            using (listener)
            {
                using Socket accepted = await listener.AcceptSocketAsync();
                accepted.LingerState = new LingerOption(enable: true, seconds: 0);
                accepted.Close();
            }
        });

        return (port, serverTask);
    }

    private static (int Port, Task ServerTask) StartSingleResponseServer(byte[] response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using (listener)
            using (var client = await listener.AcceptTcpClientAsync())
            await using (var stream = client.GetStream())
            {
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            }
        });

        return (port, serverTask);
    }
}
