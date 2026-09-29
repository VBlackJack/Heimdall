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
using FluentAssertions;
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

    /// <summary>
    /// Plink ignores the size it is started with, so the size travels in a temporary saved session
    /// that the arguments load. It must exist, with the reported size, at the moment of the launch.
    /// </summary>
    [Fact]
    public async Task ThePlinkLaunchLoadsATemporarySessionCarryingTheReportedSize()
    {
        InMemoryPuttySessionRegistry registry = new();
        string? arguments = null;
        IReadOnlyList<PuttyRegistryValue>? atLaunch = null;

        ConnectionResult result = await ConnectViaPlinkAsync(
            registry,
            (_, _, args, _, _, _) =>
            {
                arguments = args;
                string name = registry.Written.Single();
                atLaunch = registry.ReadSession(name);
                return Task.CompletedTask;
            });

        result.Success.Should().BeTrue(result.ErrorMessage);
        string sessionName = registry.Written.Should().ContainSingle().Subject;
        arguments.Should().StartWith($"-load {sessionName} ");
        atLaunch.Should().ContainEquivalentOf(
            new PuttyRegistryValue(PlinkSizeSession.TermWidthValueName, ReportedColumns, Microsoft.Win32.RegistryValueKind.DWord));
        atLaunch.Should().ContainEquivalentOf(
            new PuttyRegistryValue(PlinkSizeSession.TermHeightValueName, ReportedRows, Microsoft.Win32.RegistryValueKind.DWord));

        // Not released by the launch itself: the process has not read it yet. The release on first
        // output or exit is pinned against the handle in PlinkSizeSessionTests.
        registry.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task ALaunchFailureDeletesTheSizeSessionExactlyOnce()
    {
        InMemoryPuttySessionRegistry registry = new();

        ConnectionResult result = await ConnectViaPlinkAsync(
            registry,
            static (_, _, _, _, _, _) => throw new InvalidOperationException("launch refused"));

        result.Success.Should().BeFalse();
        string sessionName = registry.Written.Should().ContainSingle().Subject;
        registry.Deleted.Should().Equal(sessionName);
        registry.Contains(sessionName).Should().BeFalse();
    }

    [Fact]
    public async Task CancellationAtTheLaunchDeletesTheSizeSessionExactlyOnce()
    {
        InMemoryPuttySessionRegistry registry = new();
        using CancellationTokenSource cancellation = new();

        Func<Task> connect = () => ConnectViaPlinkAsync(
            registry,
            (_, _, _, _, _, ct) =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cancellation.Token);

        await connect.Should().ThrowAsync<OperationCanceledException>();
        string sessionName = registry.Written.Should().ContainSingle().Subject;
        registry.Deleted.Should().Equal(sessionName);
    }

    /// <summary>
    /// Fail-open for the size only: a registry that refuses costs the terminal width, never the
    /// connection, and the arguments are exactly the ones a launch without a size session builds.
    /// </summary>
    [Fact]
    public async Task ARegistryRefusalLaunchesWithoutLoadAndStillConnects()
    {
        string keyPath = Path.GetTempFileName();
        try
        {
            InMemoryPuttySessionRegistry working = new();
            string? withSession = null;
            await ConnectViaPlinkAsync(
                working,
                (_, _, args, _, _, _) =>
                {
                    withSession = args;
                    return Task.CompletedTask;
                },
                sharedKeyPath: keyPath);

            InMemoryPuttySessionRegistry refusing = new() { WriteFailure = new UnauthorizedAccessException("denied") };
            string? withoutSession = null;
            ConnectionResult result = await ConnectViaPlinkAsync(
                refusing,
                (_, _, args, _, _, _) =>
                {
                    withoutSession = args;
                    return Task.CompletedTask;
                },
                sharedKeyPath: keyPath);

            result.Success.Should().BeTrue(result.ErrorMessage);
            withoutSession.Should().NotContain("-load");
            string loadPrefix = $"-load {working.Written.Single()} ";
            withSession.Should().StartWith(loadPrefix);
            withoutSession.Should().Be(withSession![loadPrefix.Length..]);
            refusing.Contains(refusing.Deleted.Should().ContainSingle().Subject).Should().BeFalse();
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void ConstructingTheHandlerSweepsLeftoverSizeSessionsOnly()
    {
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed("prod-bastion");
        registry.Seed($"{PlinkSizeSessionNaming.Prefix}leftover");

        using SshHandler handler = CreateHandler(puttySessionRegistry: registry);

        registry.GetSessionNames().Should().Equal("prod-bastion");
    }

    [Fact]
    public void PipeModeArgumentsCarryLoadFirstWhenASessionWasCreated()
    {
        string withSession = SshHandler.BuildPipeModeArguments(
            keyPath: null,
            compression: true,
            agentForwarding: false,
            x11Forwarding: false,
            port: 2222,
            target: "operator@host.example.test",
            hostKeyFingerprint: "SHA256:abc123",
            passwordFilePath: "heimdall-pw",
            sizeSessionName: "HeimdallPtySize-0123abcd");

        withSession.Should().Be(
            "-load HeimdallPtySize-0123abcd -ssh -t -no-antispoof -C -P 2222 -hostkey \"SHA256:abc123\" "
            + "-pwfile \"heimdall-pw\" operator@host.example.test");
    }

    /// <summary>
    /// Byte-identical to the arguments built before the size session existed, spelled out rather
    /// than derived so a change to the no-session shape cannot pass unnoticed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void PipeModeArgumentsAreUnchangedWithoutASession(string? sizeSessionName)
    {
        string arguments = SshHandler.BuildPipeModeArguments(
            keyPath: null,
            compression: true,
            agentForwarding: false,
            x11Forwarding: false,
            port: 2222,
            target: "operator@host.example.test",
            hostKeyFingerprint: "SHA256:abc123",
            passwordFilePath: "heimdall-pw",
            sizeSessionName: sizeSessionName);

        arguments.Should().Be(
            "-ssh -t -no-antispoof -C -P 2222 -hostkey \"SHA256:abc123\" "
            + "-pwfile \"heimdall-pw\" operator@host.example.test");
    }

    private async Task<ConnectionResult> ConnectViaPlinkAsync(
        InMemoryPuttySessionRegistry registry,
        SshHandler.StartPipeModeSession start,
        CancellationToken cancellationToken = default,
        string? sharedKeyPath = null)
    {
        string plinkPath = Path.GetTempFileName();
        string keyPath = sharedKeyPath ?? Path.GetTempFileName();
        ConnectionResult? result = null;
        try
        {
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            using SshHandler handler = CreateHandler(
                hostKeyTrustService: new HostKeyTrustService(hostKeyStore),
                startPipeModeSession: start,
                puttySessionRegistry: registry);
            handler.ResolveInitialTerminalSize = static _ => new TerminalSize(ReportedColumns, ReportedRows);

            result = await handler.ConnectSshViaPlinkAsync(
                CreateGatewayServer(keyPath),
                new AppSettings { PlinkPath = plinkPath },
                "127.0.0.1",
                TunnelLocalPort,
                usesTunnel: true,
                originalFailure: null,
                cancellationToken);
            return result;
        }
        finally
        {
            DisposeSession(result);
            File.Delete(plinkPath);
            if (sharedKeyPath is null)
            {
                File.Delete(keyPath);
            }
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
        IHostKeyTrustService? hostKeyTrustService = null,
        IPuttySessionRegistry? puttySessionRegistry = null)
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
            startPipeModeSession: startPipeModeSession,
            puttySessionRegistry: puttySessionRegistry ?? new InMemoryPuttySessionRegistry());
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
