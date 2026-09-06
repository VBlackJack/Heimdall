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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Tests;

/// <summary>
/// The PTY is created at the size the terminal page already reported, on both transports.
/// </summary>
/// <remarks>
/// <para>The page posts its size in <c>ready:</c> as soon as xterm has measured the surface, which
/// is usually while the connection is still being negotiated. That size used to be dropped
/// because nothing was attached to receive it, and the PTY was created at 80x24: the shell drew
/// its first prompt for the wrong width until the first window resize. On the Plink pipe path the
/// transport cannot resize after start, so 80x24 was permanent.</para>
/// <para>The handler now asks the shell for the size just before it creates the PTY. The shell
/// answers with what the page said or with nothing, and nothing means the default.</para>
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerInitialTerminalSizeTests
{
    private const int ReportedColumns = 132;
    private const int ReportedRows = 43;
    private const string TrustedHost = "server01.contoso.local";
    private const string TrustedFingerprint = "SHA256:stored-test-fingerprint";
    private const int TunnelLocalPort = 49161;

    [Fact]
    public async Task TheDirectShellIsOpenedAtTheSizeTheTerminalReported()
    {
        (int Columns, int Rows)? opened = null;
        string? askedFor = null;
        using SshHandler handler = CreateHandler(
            connectShellSession: (_, _, _, _, columns, rows, _) =>
            {
                opened = (columns, rows);
                return Task.CompletedTask;
            });
        handler.ResolveInitialTerminalSize = sessionId =>
        {
            askedFor = sessionId;
            return new TerminalSize(ReportedColumns, ReportedRows);
        };
        ServerProfileDto server = CreateDirectServer();

        ConnectionResult result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);
        DisposeSession(result);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(server.Id, askedFor);
        Assert.Equal((ReportedColumns, ReportedRows), opened);
    }

    [Fact]
    public async Task ADirectShellWithNoReportedSizeGetsTheDefault()
    {
        (int Columns, int Rows)? opened = null;
        using SshHandler handler = CreateHandler(
            connectShellSession: (_, _, _, _, columns, rows, _) =>
            {
                opened = (columns, rows);
                return Task.CompletedTask;
            });
        handler.ResolveInitialTerminalSize = static _ => null;

        ConnectionResult result = await handler.ConnectAsync(CreateDirectServer(), new AppSettings(), CancellationToken.None);
        DisposeSession(result);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal((TerminalSize.DefaultColumns, TerminalSize.DefaultRows), opened);
    }

    /// <summary>
    /// A resolver that fails is a defect in the shell wiring, not a reason to refuse the session.
    /// </summary>
    [Fact]
    public async Task AFailingResolverFallsBackToTheDefaultAndStillConnects()
    {
        (int Columns, int Rows)? opened = null;
        using SshHandler handler = CreateHandler(
            connectShellSession: (_, _, _, _, columns, rows, _) =>
            {
                opened = (columns, rows);
                return Task.CompletedTask;
            });
        handler.ResolveInitialTerminalSize = static _ => throw new InvalidOperationException("no tab");

        ConnectionResult result = await handler.ConnectAsync(CreateDirectServer(), new AppSettings(), CancellationToken.None);
        DisposeSession(result);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal((TerminalSize.DefaultColumns, TerminalSize.DefaultRows), opened);
    }

    /// <summary>
    /// The pipe transport cannot resize after start, so the size has to travel with the launch.
    /// </summary>
    [Fact]
    public async Task ThePlinkLaunchCarriesTheSizeTheTerminalReported()
    {
        string plinkPath = Path.GetTempFileName();
        string keyPath = Path.GetTempFileName();
        ConnectionResult? result = null;
        try
        {
            (int Columns, int Rows)? started = null;
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            using SshHandler handler = CreateHandler(
                hostKeyTrustService: new HostKeyTrustService(hostKeyStore),
                startPipeModeSession: (_, _, _, columns, rows, _) =>
                {
                    started = (columns, rows);
                    return Task.CompletedTask;
                });
            handler.ResolveInitialTerminalSize = static _ => new TerminalSize(ReportedColumns, ReportedRows);
            ServerProfileDto server = CreateGatewayServer(keyPath);

            result = await handler.ConnectSshViaPlinkAsync(
                server,
                new AppSettings { PlinkPath = plinkPath },
                "127.0.0.1",
                TunnelLocalPort,
                usesTunnel: true,
                originalFailure: null,
                CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal((ReportedColumns, ReportedRows), started);
        }
        finally
        {
            DisposeSession(result);
            File.Delete(plinkPath);
            File.Delete(keyPath);
        }
    }

    private static void DisposeSession(ConnectionResult? result)
    {
        switch (result?.Session)
        {
            case SshSessionResult ssh:
                ssh.Session.Dispose();
                break;
            case TerminalSessionResult terminal:
                terminal.Session.Dispose();
                break;
        }
    }

    private static SshHandler CreateHandler(
        SshHandler.ConnectShellSession? connectShellSession = null,
        SshHandler.StartPipeModeSession? startPipeModeSession = null,
        IHostKeyTrustService? hostKeyTrustService = null)
    {
        LocalizationManager localizer = new LocalizationManager();
        return new SshHandler(
            new NoTunnelService(),
            new ConnectionStateMachine(),
            localizer,
            new HostKeyStore(),
            hostKeyTrustService ?? new HostKeyTrustService(new HostKeyStore()),
            AutoAcceptHostKeyVerifier.Instance,
            new X11ServerManager(new InMemoryConfigManager(), localizer),
            dialogService: null!,
            plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
            plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
            plinkAttestation: static _ => PlinkAttestationLease.NotAttested,
            agentRegistryFactory: static _ => new SshAgentRegistry([]),
            connectShellSession: connectShellSession,
            startPipeModeSession: startPipeModeSession);
    }

    private static ServerProfileDto CreateDirectServer() => new ServerProfileDto
    {
        Id = "ssh-size-direct",
        DisplayName = "size direct",
        RemoteServer = "host.example.test",
        SshPort = DefaultPorts.Ssh,
        ConnectionType = "SSH",
        SshMode = "Embedded",
        SshUsername = "operator",
        UseDirectConnection = true
    };

    private static ServerProfileDto CreateGatewayServer(string keyPath) => new ServerProfileDto
    {
        Id = "ssh-size-plink",
        DisplayName = "size plink",
        ConnectionType = "SSH",
        RemoteServer = TrustedHost,
        SshPort = DefaultPorts.Ssh,
        SshMode = "Embedded",
        SshUsername = "operator",
        SshKeyPath = keyPath,
        SshGatewayId = "gateway-01"
    };

    /// <summary>A direct connection: no tunnel is set up and none is released.</summary>
    private sealed class NoTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false) =>
            Task.FromResult(
                new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NeverProbedPlinkHostKeyProbe : IPlinkHostKeyProbe
    {
        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct) =>
            Task.FromResult<PlinkHostKeyPresentation?>(null);
    }
}
