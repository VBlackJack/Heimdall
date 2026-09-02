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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// What a manual tunnel says when the gateway refuses the sign-in.
/// </summary>
/// <remarks>
/// The tunnel service composes the gateway's own sentence and the agent
/// observation together for a server profile. The Tunnels tab opens its forward
/// through <c>TunnelManager</c> directly, so it never went through that
/// composition: the same refusal told the owner less from this surface than from
/// the other one. The catalogue here is a fixture, as in
/// <see cref="TunnelServiceAuthDiagnosisTests"/>: what these tests fix is which
/// sentences appear and in what order, not their shipped wording.
/// </remarks>
public sealed class TunnelsViewModelAuthFailureTests : IDisposable
{
    private const string RelayedServerRefusal = "Permission denied (password).";
    private const string NoAgentKeySentence = "FIXTURE no agent key was loaded.";
    private const string ManyAgentKeysSentence = "FIXTURE {0} agent keys were offered and refused.";

    private readonly string _localesPath;
    private readonly string _keyFilePath;

    public TunnelsViewModelAuthFailureTests()
    {
        _keyFilePath = Path.Combine(Path.GetTempPath(), $"heimdall-gateway-{Guid.NewGuid():N}.pem");
        File.WriteAllText(_keyFilePath, "not parsed: the SSH client factory is replaced in these tests");

        _localesPath = Path.Combine(Path.GetTempPath(), $"heimdall-locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [SshAuthFailureLocaleKeys.NoAgentKeyLoaded] = NoAgentKeySentence,
                [SshAuthFailureLocaleKeys.ManyAgentKeysRefused] = ManyAgentKeysSentence
            }));
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_keyFilePath);
            Directory.Delete(_localesPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // The gateway's own sentence still heads the message here, and the
    // observation the other surface adds is added here too. Before this, the
    // status bar showed the bare relayed refusal and nothing else.
    [Fact]
    public async Task AManualTunnelRefusedWithNoAgentKeyLoaded_CarriesTheObservationAfterTheGatewaysSentence()
    {
        await using Fixture fixture = await Fixture.CreateAsync(_localesPath, _keyFilePath, agents: []);

        TunnelResult result = await fixture.OpenManualTunnelAsync();

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.AuthRejected, result.FailureCode);
        Assert.Equal($"{RelayedServerRefusal} {NoAgentKeySentence}", result.ErrorMessage);
    }

    [Fact]
    public async Task AManualTunnelRefusedWithSeveralAgentKeysLoaded_SaysHowManyWereOffered()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            _localesPath,
            _keyFilePath,
            agents: [AgentHolding(3)]);

        TunnelResult result = await fixture.OpenManualTunnelAsync();

        Assert.Equal(
            $"{RelayedServerRefusal} FIXTURE 3 agent keys were offered and refused.",
            result.ErrorMessage);
    }

    // The count belongs to the dial, on this surface as on the other one. Keys
    // loaded while the refusal was in flight were never offered to anything.
    [Fact]
    public async Task KeysLoadedWhileTheRefusalWasInFlight_AreNotCountedAsKeysThisDialOffered()
    {
        // A live agent, not a frozen list: a registry keeps its agents and asks
        // each one afresh, so only an agent that answers differently on a second
        // probe can tell a reading before the dial from a reading after it.
        LiveAgent agent = new LiveAgent { Available = false };
        await using Fixture fixture = await Fixture.CreateAsync(
            _localesPath,
            _keyFilePath,
            agentsProvider: () => [agent],
            onDial: () =>
            {
                agent.Available = true;
                agent.IdentityCount = 3;
            });

        TunnelResult result = await fixture.OpenManualTunnelAsync();

        Assert.Equal(1, fixture.DialAttempts);
        Assert.Equal(3, agent.IdentityCount);
        Assert.Equal($"{RelayedServerRefusal} {NoAgentKeySentence}", result.ErrorMessage);
    }

    // A failure that is not a refused sign-in gets no agent sentence: the agents
    // had nothing to do with a gateway that could not be reached.
    [Fact]
    public async Task AManualTunnelThatFailedForAnotherReason_IsNotGivenAnAgentObservation()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            _localesPath,
            _keyFilePath,
            agents: [AgentHolding(3)],
            failure: new SshOperationTimeoutException("Connection timed out."));

        TunnelResult result = await fixture.OpenManualTunnelAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain("FIXTURE", result.ErrorMessage!, StringComparison.Ordinal);
    }

    private static ISshAgent AgentHolding(int identities) =>
        new LiveAgent { IdentityCount = identities };

    /// <summary>
    /// An agent whose reachability and identity count are read at every probe,
    /// the way a real one is.
    /// </summary>
    private sealed class LiveAgent : ISshAgent
    {
        public string Name => OpenSshPipeAgent.AgentName;

        public bool Available { get; set; } = true;

        public int IdentityCount { get; set; }

        public bool IsAvailable() => Available;

        public IReadOnlyList<ISshAgentKey> GetIdentities() =>
            [.. Enumerable.Range(0, IdentityCount).Select(_ => new FakeAgentKey())];
    }

    /// <summary>
    /// A real <see cref="TunnelsViewModel"/> whose tunnel manager always refuses
    /// the dial the way the test asks, with the agent registry under the test's
    /// control so the composed message does not depend on what is running on the
    /// machine.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Exception _failure;
        private readonly Action? _onDial;
        private readonly AppSettings _settings;
        private readonly TunnelsViewModel _viewModel;
        private readonly TunnelManager _tunnelManager;
        private int _dialAttempts;

        private Fixture(
            LocalizationManager localizer,
            string keyFilePath,
            Func<IReadOnlyList<ISshAgent>> agentsProvider,
            Exception failure,
            Action? onDial)
        {
            _failure = failure;
            _onDial = onDial;

            SshGatewayDto gateway = new SshGatewayDto
            {
                Id = "8f0f1f38-4a1b-4b58-9b0e-8b2f6f1a0d21",
                Name = "bastion",
                Host = "gw.example.test",
                Port = 22,
                User = "ssh-user",
                KeyPath = keyFilePath
            };
            _settings = new AppSettings { SshGateways = [gateway] };
            Gateway = gateway;

            _tunnelManager = new TunnelManager(
                ResolveVerifierAsync,
                CreateClient,
                ConnectAndFailAsync);

            TestTunnelsHost host = new TestTunnelsHost(_settings);
            _viewModel = new TunnelsViewModel(
                host,
                localizer,
                _tunnelManager,
                new ConnectionStateMachine(),
                new HostKeyStore(),
                RejectingHostKeyVerifier.Instance,
                new InMemoryConfigManager(),
                _ => new SshAgentRegistry(agentsProvider()));
        }

        public SshGatewayDto Gateway { get; }

        public int DialAttempts => _dialAttempts;

        public static async Task<Fixture> CreateAsync(
            string localesPath,
            string keyFilePath,
            IReadOnlyList<ISshAgent> agents,
            Exception? failure = null) =>
            await CreateAsync(localesPath, keyFilePath, () => agents, failure: failure);

        public static async Task<Fixture> CreateAsync(
            string localesPath,
            string keyFilePath,
            Func<IReadOnlyList<ISshAgent>> agentsProvider,
            Action? onDial = null,
            Exception? failure = null)
        {
            LocalizationManager localizer = new LocalizationManager();
            await localizer.LoadAsync(localesPath, "en");
            return new Fixture(
                localizer,
                keyFilePath,
                agentsProvider,
                failure ?? new SshAuthenticationException(RelayedServerRefusal),
                onDial);
        }

        public Task<TunnelResult> OpenManualTunnelAsync()
        {
            NewTunnelDialogViewModel dialog = new NewTunnelDialogViewModel(
                _settings.SshGateways,
                new LocalizationManager(),
                new HashSet<int>())
            {
                SelectedGateway = Gateway,
                RemoteHost = "target.example.test",
                RemotePort = 3389,
                LocalPort = 0
            };

            return _viewModel.OpenManualTunnelAsync(dialog, _settings, CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            _viewModel.Dispose();
            _tunnelManager.Dispose();
            return ValueTask.CompletedTask;
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

        private Task ConnectAndFailAsync(
            SshClient client,
            string verificationHost,
            int verificationPort,
            PinnedFingerprintVerifier pinnedVerifier,
            CancellationToken cancellationToken,
            string cancelLogMessage)
        {
            Interlocked.Increment(ref _dialAttempts);
            _onDial?.Invoke();
            throw _failure;
        }
    }

    private sealed class TestTunnelsHost(AppSettings settings) : ITunnelsHost
    {
        public ConnectionViewModel Connection { get; } = new(
            new LocalizationManager(),
            null!,
            null!,
            new PaneCloseArbiter(),
            new SessionWindowService(static (_, _) => { }));

        public AppSettings? CurrentSettings { get; } = settings;

        public string StatusText { get; set; } = string.Empty;
    }

    private sealed class FakeAgentKey : ISshAgentKey
    {
        public string Comment => "fake";
        public string KeyType => "ssh-ed25519";
        public byte[] PublicKeyBlob => [0, 0, 0, 11, 115, 115, 104, 45, 101, 100, 50, 53, 53, 49, 57];
        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1];
    }
}
