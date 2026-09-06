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
using System.Text.Json;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Pageant;
using Heimdall.Ssh.Plink;
using Microsoft.Extensions.Time.Testing;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// What the Plink fallback does with a profile it cannot serve. Plink is
/// launched with a single local forward; a profile that also needs a SOCKS
/// proxy or a reverse forward used to be handed a tunnel that silently lacked
/// them, reported as a success.
/// </summary>
/// <remarks>
/// The catalogue is a fixture written to a temporary directory, so the test
/// reads the sentence it placed, not the shipped wording;
/// <c>CSharpLocaleKeyCoverageTests</c> guards that the shipped key exists.
/// </remarks>
public sealed class TunnelServicePlinkFallbackTests : IDisposable
{
    private const string RelayedServerRefusal = "Permission denied (password).";
    private const string GatewayId = "6e1d3d4c-2a7b-4f3e-9c1a-6f0d2b8e5a11";
    private const string ForwardingModeSentence = "FIXTURE the Plink fallback cannot open this forward.";
    private const int SocksProxyPort = 1080;
    private const int RemoteBindPort = 2222;

    private readonly string _keyFilePath;
    private readonly string _localesPath;
    private readonly string _absentPlinkPath;

    public TunnelServicePlinkFallbackTests()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"heimdall-plink-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        _keyFilePath = Path.Combine(scratch, "gateway.pem");
        File.WriteAllText(_keyFilePath, "not parsed: the SSH client factory is replaced in these tests");

        _localesPath = Path.Combine(scratch, "locales");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [SshLocalizationKeys.ErrorPlinkForwardingModeUnsupported] = ForwardingModeSentence
            }));

        _absentPlinkPath = Path.Combine(scratch, "plink.exe");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_keyFilePath)!, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing a test over.
        }
    }

    [Fact]
    public async Task AProfileThatNeedsASocksProxy_IsRefusedByTheFallbackBeforePlinkIsLookedUp()
    {
        Harness harness = await CreateHarnessAsync();

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            Gateway(),
            "server-rdp-1",
            socksProxyPort: SocksProxyPort,
            remoteBindPort: 0);

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.ForwardingFailed, outcome.FailureCode);
        Assert.StartsWith(RelayedServerRefusal, outcome.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(ForwardingModeSentence, outcome.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(harness.TunnelManager.GetActiveTunnels());
    }

    [Fact]
    public async Task AProfileThatNeedsARemoteForward_IsRefusedByTheFallbackBeforePlinkIsLookedUp()
    {
        Harness harness = await CreateHarnessAsync();

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            Gateway(),
            "server-rdp-1",
            socksProxyPort: 0,
            remoteBindPort: RemoteBindPort);

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.ForwardingFailed, outcome.FailureCode);
        Assert.StartsWith(RelayedServerRefusal, outcome.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(ForwardingModeSentence, outcome.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(harness.TunnelManager.GetActiveTunnels());
    }

    [Fact]
    public async Task AProfileWithALocalForwardOnly_ReachesTheFallback()
    {
        Harness harness = await CreateHarnessAsync();

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            Gateway(),
            "server-rdp-1",
            socksProxyPort: 0,
            remoteBindPort: 0);

        Assert.False(outcome.Success);
        Assert.NotEqual(SshFailureCode.ForwardingFailed, outcome.FailureCode);
        Assert.DoesNotContain(ForwardingModeSentence, outcome.ErrorMessage!, StringComparison.Ordinal);
    }

    private async Task<Harness> CreateHarnessAsync()
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(_localesPath, "en");
        return new Harness(localizer, _absentPlinkPath);
    }

    private SshGatewayDto Gateway() =>
        new()
        {
            Id = GatewayId,
            Name = "bastion",
            Host = "gw.example.test",
            Port = 22,
            User = "ssh-user",
            KeyPath = _keyFilePath
        };

    /// <summary>
    /// A TunnelService whose SSH.NET dial is refused by the gateway and whose
    /// agent registry holds a reachable Pageant, which is what makes the
    /// service attempt the Plink fallback. Plink itself is pointed at a path
    /// that does not exist, so a fallback that gets as far as looking for it
    /// fails on that, never on a machine's real installation.
    /// </summary>
    private sealed class Harness
    {
        private readonly string _plinkPath;

        public Harness(LocalizationManager localizer, string plinkPath)
        {
            _plinkPath = plinkPath;
            Clock = new FakeTimeProvider();
            TunnelManager = new TunnelManager(ResolveVerifierAsync, CreateClient, RefuseDialAsync);
            Service = new TunnelService(
                TunnelManager,
                new HostKeyStore(),
                new HostKeyTrustService(new HostKeyStore()),
                new ConnectionStateMachine(),
                localizer,
                RejectingHostKeyVerifier.Instance,
                new NoPresentationPlinkHostKeyProbe(),
                Clock,
                _ => new SshAgentRegistry([new PageantLikeAgent()]));
        }

        public FakeTimeProvider Clock { get; }

        public TunnelManager TunnelManager { get; }

        public TunnelService Service { get; }

        public async Task<TunnelSetupOutcome> ConnectAsync(
            SshGatewayDto gateway,
            string serverId,
            int socksProxyPort,
            int remoteBindPort)
        {
            ServerProfileDto server = new ServerProfileDto
            {
                Id = serverId,
                RemoteServer = "target.example.test",
                RemotePort = 3389,
                ConnectionType = "RDP",
                SshGatewayId = gateway.Id,
                UseDirectConnection = false,
                SocksProxyPort = socksProxyPort,
                RemoteBindPort = remoteBindPort
            };
            AppSettings settings = new AppSettings
            {
                SshGateways = [gateway],
                PlinkPath = _plinkPath
            };

            return await Service.SetupTunnelIfNeededAsync(
                server,
                3389,
                settings,
                CancellationToken.None);
        }

        private static Task<PinnedFingerprintVerifier> ResolveVerifierAsync(
            SshConnectionParams connectionParams,
            string verificationHost,
            int verificationPort,
            HostKeyStore hostKeyStore,
            IHostKeyVerifier verifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PinnedFingerprintVerifier(verificationHost, verificationPort, "SHA256:pinned"));

        private static SshClient CreateClient(SshConnectionParams connectionParams) =>
            new SshClient(new ConnectionInfo(
                connectionParams.Host,
                connectionParams.Port,
                connectionParams.Username,
                new NoneAuthenticationMethod(connectionParams.Username)));

        private static Task RefuseDialAsync(
            SshClient client,
            string verificationHost,
            int verificationPort,
            PinnedFingerprintVerifier pinnedVerifier,
            CancellationToken cancellationToken,
            string cancelLogMessage) =>
            throw new SshAuthenticationException(RelayedServerRefusal);
    }

    /// <summary>
    /// Reads no host key, so a fallback that reaches the host key decision
    /// fails closed on every machine.
    /// </summary>
    private sealed class NoPresentationPlinkHostKeyProbe : IPlinkHostKeyProbe
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

    /// <summary>
    /// An agent the registry recognises as Pageant, which is the condition for
    /// the Plink fallback to be attempted at all.
    /// </summary>
    private sealed class PageantLikeAgent : ISshAgent
    {
        public string Name => PageantAgent.AgentName;

        public bool IsAvailable() => true;

        public IReadOnlyList<ISshAgentKey> GetIdentities() => [];
    }
}
