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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class SftpHandlerConnectTests
{
    [Fact]
    public async Task ConnectAsync_TunneledConnectFailureReleasesTunnelReference()
    {
        int freePort = ReserveAndReleaseLoopbackPort();
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = freePort
        };
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateGatewayServer();

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(freePort, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public async Task ConnectAsync_DirectConnectFailureDoesNotReleaseTunnelReference()
    {
        int freePort = ReserveAndReleaseLoopbackPort();
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = false
        };
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(freePort);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, tunnelService.ReleaseCount);
    }

    [Fact]
    public async Task ConnectAsync_RejectsWhitespaceHost_BeforeTunnelSetup()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.RemoteServer = "   ";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorInvalidTargetHost", result.ErrorMessage);
        Assert.Null(result.Session);
        Assert.Equal(0, tunnelService.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_WhenSftpBrowserDisabled_ReturnsErrorBeforeTunnelSetup()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings { SftpBrowserEnabled = false },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorSftpBrowserDisabled", result.ErrorMessage);
        Assert.Null(result.Session);
        Assert.Equal(0, tunnelService.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_RejectsInvalidHost_BeforeTunnelSetup()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.RemoteServer = "this is not a host";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorInvalidTargetHost", result.ErrorMessage);
        Assert.Null(result.Session);
        Assert.Equal(0, tunnelService.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_RejectsOutOfRangePort_BeforeTunnelSetup()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(70_000);
        server.RemoteServer = "sftp.example.com";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorInvalidPort", result.ErrorMessage);
        Assert.Null(result.Session);
        Assert.Equal(0, tunnelService.SetupCallCount);
    }

    private static SftpHandler CreateHandler(FakeTunnelService tunnelService)
    {
        return new SftpHandler(
            tunnelService,
            new ConnectionStateMachine(),
            new LocalizationManager(),
            new HostKeyStore(),
            AutoAcceptHostKeyVerifier.Instance);
    }

    private static ServerProfileDto CreateGatewayServer()
    {
        return new ServerProfileDto
        {
            Id = "sftp-gateway-test",
            DisplayName = "SFTP Gateway Test",
            ConnectionType = "SFTP",
            RemoteServer = "server01.contoso.local",
            SshPort = DefaultPorts.Ssh,
            SshUsername = "operator",
            SshGatewayId = "gateway-01",
            UseDirectConnection = false
        };
    }

    private static ServerProfileDto CreateDirectServer(int port)
    {
        return new ServerProfileDto
        {
            Id = "sftp-direct-test",
            DisplayName = "SFTP Direct Test",
            ConnectionType = "SFTP",
            RemoteServer = "127.0.0.1",
            SshPort = port,
            SshUsername = "operator",
            UseDirectConnection = true
        };
    }

    private static int ReserveAndReleaseLoopbackPort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            IPEndPoint endpoint = (IPEndPoint)listener.LocalEndpoint;
            return endpoint.Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public bool UsesTunnel { get; init; }
        public string TargetHost { get; init; } = "";
        public int TargetPort { get; init; }
        public int SetupCallCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int ReleasedLocalPort { get; private set; }

        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            SetupCallCount++;
            string host = UsesTunnel ? TargetHost : server.RemoteServer;
            int port = UsesTunnel ? TargetPort : remotePort;
            return Task.FromResult(new TunnelSetupOutcome(true, UsesTunnel, host, port, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
            ReleaseCount++;
            ReleasedLocalPort = localPort;
        }
    }
}
