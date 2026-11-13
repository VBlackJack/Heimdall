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
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// The handler says a Plink retry is starting, once, before it asks for the terminal size.
/// </summary>
/// <remarks>
/// The shell uses the signal to drop the pending file panel, which can never connect when the
/// embedded client was refused. The signal must come before the size is read, because Plink takes
/// its size once at launch, and only on the retry after an authentication failure.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerPlinkRetryStartingTests
{
    private const int ReportedColumns = 132;
    private const int ReportedRows = 43;
    private const string TrustedHost = "server01.contoso.local";
    private const string TrustedFingerprint = "SHA256:stored-test-fingerprint";
    private const string ServerId = "ssh-plink-retry-starting";
    private const string RefusalText = "Permission denied (password).";
    private const string TooManyFailuresText = "Too many authentication failures";
    private const string RelayEvent = "relay";
    private const string ResolveEvent = "resolve";
    private const string LaunchEvent = "launch";

    [Fact]
    public async Task ARetryAfterAnAuthenticationFailure_CallsTheRelayOnceBeforeTheSizeIsAsked()
    {
        List<string> events = [];
        List<string> relayedIds = [];
        (int Columns, int Rows)? launched = null;

        ConnectionResult result = await ConnectAsync(
            connect: new SshAuthenticationException(RefusalText),
            relay: id =>
            {
                relayedIds.Add(id);
                events.Add(RelayEvent);
            },
            events,
            onLaunch: (columns, rows) => launched = (columns, rows));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal([ServerId], relayedIds);
        // The embedded dial asked for its own size first; the Plink launch asks again after the relay.
        Assert.Equal([ResolveEvent, RelayEvent, ResolveEvent, LaunchEvent], events);
        Assert.Equal((ReportedColumns, ReportedRows), launched);
    }

    [Fact]
    public async Task AFailureOutsideTheRetrySet_NeverCallsTheRelay()
    {
        List<string> events = [];

        ConnectionResult result = await ConnectAsync(
            connect: new SshAuthenticationException(TooManyFailuresText),
            relay: _ => events.Add(RelayEvent),
            events,
            onLaunch: (_, _) => { });

        Assert.False(result.Success);
        Assert.DoesNotContain(RelayEvent, events);
        Assert.DoesNotContain(LaunchEvent, events);
    }

    [Fact]
    public async Task ADirectPlinkConnect_NeverCallsTheRelay()
    {
        List<string> events = [];

        ConnectionResult result = await ConnectAsync(
            connect: null,
            relay: _ => events.Add(RelayEvent),
            events,
            onLaunch: (_, _) => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal([ResolveEvent, LaunchEvent], events);
    }

    [Fact]
    public async Task ARelayThatThrows_DoesNotFailTheConnect()
    {
        List<string> events = [];

        ConnectionResult result = await ConnectAsync(
            connect: new SshAuthenticationException(RefusalText),
            relay: _ => throw new InvalidOperationException("layout failed"),
            events,
            onLaunch: (_, _) => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(LaunchEvent, events);
    }

    [Fact]
    public void TheConnectionService_ForwardsTheRelayToTheSshHandler()
    {
        using SshHandler handler = CreateHandler(connectShellSession: null, startPipeModeSession: null);
        using ConnectionService service = new ConnectionService(
            new InMemoryConfigManager(),
            new LocalizationManager(),
            new NoTunnelService(),
            [handler]);
        Action<string> relay = static _ => { };

        service.PlinkRetryStarting = relay;

        Assert.Same(relay, handler.PlinkRetryStarting);
    }

    /// <summary>
    /// Runs one connect. A non-null <paramref name="connect"/> is thrown by the embedded dial, so
    /// the handler retries through Plink; null calls the Plink path directly.
    /// </summary>
    private static async Task<ConnectionResult> ConnectAsync(
        Exception? connect,
        Action<string> relay,
        List<string> events,
        Action<int, int> onLaunch)
    {
        string plinkPath = Path.GetTempFileName();
        string keyPath = Path.GetTempFileName();
        ConnectionResult? result = null;
        try
        {
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            using SshHandler handler = CreateHandler(
                connectShellSession: connect is null
                    ? null
                    : (_, _, _, _, _, _, _) => throw connect,
                startPipeModeSession: (_, _, _, columns, rows, _) =>
                {
                    events.Add(LaunchEvent);
                    onLaunch(columns, rows);
                    return Task.CompletedTask;
                },
                hostKeyTrustService: new HostKeyTrustService(hostKeyStore));
            handler.PlinkRetryStarting = relay;
            handler.ResolveInitialTerminalSize = (_, _, _) =>
            {
                events.Add(ResolveEvent);
                return Task.FromResult(TerminalSizeLookup.Reported(new TerminalSize(ReportedColumns, ReportedRows)));
            };

            ServerProfileDto server = new ServerProfileDto
            {
                Id = ServerId,
                DisplayName = "plink retry starting",
                ConnectionType = "SSH",
                RemoteServer = TrustedHost,
                SshPort = DefaultPorts.Ssh,
                SshMode = "Embedded",
                SshUsername = "operator",
                SshKeyPath = keyPath,
                UseDirectConnection = true
            };
            AppSettings settings = new AppSettings { PlinkPath = plinkPath };

            result = connect is null
                ? await handler.ConnectSshViaPlinkAsync(
                    server,
                    settings,
                    TrustedHost,
                    DefaultPorts.Ssh,
                    usesTunnel: false,
                    originalFailure: null,
                    CancellationToken.None)
                : await handler.ConnectAsync(server, settings, CancellationToken.None);
            return result;
        }
        finally
        {
            if (result?.Session is TerminalSessionResult terminal)
            {
                terminal.Session.Dispose();
            }

            File.Delete(plinkPath);
            File.Delete(keyPath);
        }
    }

    private static SshHandler CreateHandler(
        SshHandler.ConnectShellSession? connectShellSession,
        SshHandler.StartPipeModeSession? startPipeModeSession,
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

            // Key-only: nothing is asked, so touching the dialog fails loudly.
            dialogService: null!,
            plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
            plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
            plinkAttestation: static _ => PlinkAttestationLease.NotAttested,
            agentRegistryFactory: static _ => new SshAgentRegistry([]),
            connectShellSession: connectShellSession,
            startPipeModeSession: startPipeModeSession,
            puttySessionRegistry: new InMemoryPuttySessionRegistry());
    }

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
