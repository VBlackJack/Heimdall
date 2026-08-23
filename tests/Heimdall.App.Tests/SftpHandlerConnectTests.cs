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
using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Renci.SshNet.Common;

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

    [Fact]
    public async Task ConnectAsync_RejectsMissingUsername_BeforeTunnelSetup()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.SshUsername = null;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorSshUsernameRequired", result.ErrorMessage);
        Assert.Null(result.Session);

        // SSH.NET rejects an empty user name from every authentication method, so the
        // session could never authenticate. Nothing should be raised and no host key
        // trusted on its behalf.
        Assert.Equal(0, tunnelService.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_RejectsWhitespaceUsername_BeforeTunnelSetup()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        SftpHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.SshUsername = "   ";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorSshUsernameRequired", result.ErrorMessage);
        Assert.Equal(0, tunnelService.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_NoCredentialOffered_OffersAPasswordAndRetriesWithIt()
    {
        FakeTunnelService tunnelService = new() { UsesTunnel = false };
        var dialog = DispatchProxy.Create<IDialogService, RecordingPasswordDialogProxy>();
        var recording = (RecordingPasswordDialogProxy)dialog;
        recording.Answer = "typed-secret";

        List<string?> passwordsSeen = [];
        SftpHandler handler = CreateHandler(
            tunnelService,
            dialog,
            (browser, sshParams, store, verifier, token) =>
            {
                passwordsSeen.Add(sshParams.Password);
                throw new SshAuthenticationException("No suitable authentication method found.");
            });

        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.AllowCredentialPrompt = true;

        await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        // Exactly one prompt, and the second attempt carried what was typed.
        Assert.Single(recording.PasswordPrompts);
        Assert.Equal([null, "typed-secret"], passwordsSeen);
    }

    [Fact]
    public async Task ConnectAsync_WithoutTheCallerFlag_NeverAsks()
    {
        FakeTunnelService tunnelService = new() { UsesTunnel = false };
        int attempts = 0;

        // The default dialog double throws on every member, so a prompt here fails loudly.
        SftpHandler handler = CreateHandler(
            tunnelService,
            connectBrowser: (browser, sshParams, store, verifier, token) =>
            {
                attempts++;
                throw new SshAuthenticationException("No suitable authentication method found.");
            });

        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        // A pane opened as a side effect must fail quietly, not raise a modal.
        Assert.False(result.Success);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ConnectAsync_UserDismissesThePrompt_StopsWithoutASecondAttempt()
    {
        FakeTunnelService tunnelService = new() { UsesTunnel = false };
        var dialog = DispatchProxy.Create<IDialogService, RecordingPasswordDialogProxy>();
        ((RecordingPasswordDialogProxy)dialog).Answer = null;

        int attempts = 0;
        SftpHandler handler = CreateHandler(
            tunnelService,
            dialog,
            (browser, sshParams, store, verifier, token) =>
            {
                attempts++;
                throw new SshAuthenticationException("No suitable authentication method found.");
            });

        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.AllowCredentialPrompt = true;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ConnectAsync_FailureAPasswordCannotFix_NeverAsks()
    {
        FakeTunnelService tunnelService = new() { UsesTunnel = false };
        int attempts = 0;

        SftpHandler handler = CreateHandler(
            tunnelService,
            connectBrowser: (browser, sshParams, store, verifier, token) =>
            {
                attempts++;
                throw new System.Net.Sockets.SocketException(10061);
            });

        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.AllowCredentialPrompt = true;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        // A refused connection is not a credential problem. Offering a password box
        // there would be a lie about what went wrong.
        Assert.False(result.Success);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ConnectAsync_RetryAfterAPrompt_KeepsTheSameTunnel()
    {
        FakeTunnelService tunnelService = new()
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 54321
        };
        var dialog = DispatchProxy.Create<IDialogService, RecordingPasswordDialogProxy>();
        ((RecordingPasswordDialogProxy)dialog).Answer = "typed-secret";

        SftpHandler handler = CreateHandler(
            tunnelService,
            dialog,
            (browser, sshParams, store, verifier, token) =>
                throw new SshAuthenticationException("No suitable authentication method found."));

        ServerProfileDto server = CreateGatewayServer();
        server.AllowCredentialPrompt = true;

        await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        // Set up once, released once, across two attempts. Releasing between them would
        // dispose the forward, and a re-setup would bind a different, OS-assigned port.
        Assert.Equal(1, tunnelService.SetupCallCount);
        Assert.Equal(1, tunnelService.ReleaseCount);
    }

    [Fact]
    public async Task ConnectAsync_TypedPassword_NeverReachesTheProfile()
    {
        FakeTunnelService tunnelService = new() { UsesTunnel = false };
        var dialog = DispatchProxy.Create<IDialogService, RecordingPasswordDialogProxy>();
        const string Secret = "sentinel-never-persisted";
        ((RecordingPasswordDialogProxy)dialog).Answer = Secret;

        SftpHandler handler = CreateHandler(
            tunnelService,
            dialog,
            (browser, sshParams, store, verifier, token) =>
                throw new SshAuthenticationException("No suitable authentication method found."));

        ServerProfileDto server = CreateDirectServer(DefaultPorts.Ssh);
        server.AllowCredentialPrompt = true;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        // The typed value lives for the attempt and nowhere else. Not on the profile,
        // not in the message the user is shown, not in the diagnostic.
        Assert.Null(server.SshPasswordEncrypted);
        Assert.DoesNotContain(Secret, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    private static SftpHandler CreateHandler(
        FakeTunnelService tunnelService,
        IDialogService? dialogService = null,
        SftpHandler.ConnectBrowserDelegate? connectBrowser = null,
        ConnectionStateMachine? connectionSm = null,
        LocalizationManager? localizer = null)
    {
        return new SftpHandler(
            tunnelService,
            connectionSm ?? new ConnectionStateMachine(),
            localizer ?? new LocalizationManager(),
            new HostKeyStore(),
            AutoAcceptHostKeyVerifier.Instance,

            // Throws on every member by default. A dialog raised where none was
            // expected has to fail loudly: a double that quietly returns null would
            // let an unwanted prompt ship as a green test.
            dialogService ?? DispatchProxy.Create<IDialogService, ThrowingDialogProxy>(),
            connectBrowser);
    }

    /// <summary>Refuses every dialog. The default for tests that expect no question.</summary>
    private class ThrowingDialogProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException(
                $"unexpected dialog call: {targetMethod?.Name}");
    }

    /// <summary>Answers the password prompt with a scripted value and records the ask.</summary>
    private class RecordingPasswordDialogProxy : DispatchProxy
    {
        public List<(string Title, string Message)> PasswordPrompts { get; } = [];

        public string? Answer { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowPasswordInputAsync))
            {
                PasswordPrompts.Add(((string)args![0]!, (string)args![1]!));
                return Task.FromResult(Answer);
            }

            throw new NotSupportedException(
                $"unexpected dialog call: {targetMethod?.Name}");
        }
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
