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
/// The refusal the embedded client met travels with the result of the Plink retry that followed it.
/// </summary>
/// <remarks>
/// A Plink terminal reports success once its process started, before it signed in. Without the
/// refusal on that success, the connect path read a password the server had just refused as a
/// working connection, and a remembered one was sent again on every later connect.
/// <para>The dial and the launch are replaced and nothing else is: the classification, the retry
/// decision, the host-key gate and the launch preparation all run as they ship.</para>
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerPlinkFallbackRefusalTests
{
    private const string TrustedHost = "server01.contoso.local";
    private const string TrustedFingerprint = "SHA256:stored-test-fingerprint";

    [Fact]
    public async Task ARefusedSignIn_RetriedThroughPlink_CarriesTheRefusalOnItsSuccess()
    {
        int launches = 0;
        ConnectionResult result = await ConnectAsync(
            refusal: "Permission denied (password).",
            start: () => launches++);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, launches);
        Assert.IsType<TerminalSessionResult>(result.Session);
        Assert.Equal(SshFailureCode.AuthRejected, result.FallbackFrom);
    }

    [Fact]
    public async Task ARefusedSignIn_WhosePlinkRetryFails_StillCarriesTheRefusal()
    {
        ConnectionResult result = await ConnectAsync(
            refusal: "Permission denied (password).",
            start: () => throw new InvalidOperationException("launch refused"));

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.AuthRejected, result.FallbackFrom);
    }

    private static async Task<ConnectionResult> ConnectAsync(string refusal, Action start)
    {
        string plinkPath = Path.GetTempFileName();
        string keyPath = Path.GetTempFileName();
        ConnectionResult? result = null;
        try
        {
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            LocalizationManager localizer = new LocalizationManager();
            using SshHandler handler = new SshHandler(
                new NoTunnelService(),
                new ConnectionStateMachine(),
                localizer,
                new HostKeyStore(),
                new HostKeyTrustService(hostKeyStore),
                AutoAcceptHostKeyVerifier.Instance,
                new X11ServerManager(new InMemoryConfigManager(), localizer),

                // Key-only: nothing is asked, so touching the dialog fails loudly.
                dialogService: null!,
                plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
                plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
                plinkAttestation: static _ => PlinkAttestationLease.NotAttested,
                agentRegistryFactory: static _ => new SshAgentRegistry([]),
                connectShellSession: (_, _, _, _, _, _, _) => throw new SshAuthenticationException(refusal),
                startPipeModeSession: (_, _, _, _, _, _) =>
                {
                    start();
                    return Task.CompletedTask;
                },
                puttySessionRegistry: new InMemoryPuttySessionRegistry());

            ServerProfileDto server = new ServerProfileDto
            {
                Id = "ssh-plink-fallback-refusal",
                DisplayName = "fallback refusal",
                ConnectionType = "SSH",
                RemoteServer = TrustedHost,
                SshPort = DefaultPorts.Ssh,
                SshMode = "Embedded",
                SshUsername = "operator",
                SshKeyPath = keyPath,
                UseDirectConnection = true
            };

            result = await handler.ConnectAsync(server, new AppSettings { PlinkPath = plinkPath }, CancellationToken.None);
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
