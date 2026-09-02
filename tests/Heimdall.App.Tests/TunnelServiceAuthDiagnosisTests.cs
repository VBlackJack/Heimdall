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
using System.Text.Json;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;
using Heimdall.Ssh.Pageant;
using Microsoft.Extensions.Time.Testing;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// What the owner sees when a gateway refuses authentication, and how many
/// times he sees it when ten profiles share that gateway.
/// </summary>
/// <remarks>
/// The catalogue used here is a fixture written to a temporary directory, not
/// locales/en.json. What the shipped sentences say is the integrator's business
/// and <c>CSharpLocaleKeyCoverageTests</c> guards that they exist at all; what
/// these tests fix is where those sentences are placed relative to the server's
/// own wording, which is the thing the lot got wrong.
/// </remarks>
public sealed class TunnelServiceAuthDiagnosisTests : IDisposable
{
    private const string RelayedServerRefusal = "Permission denied (password).";
    private const string GatewayId = "179b5496-c586-4144-9104-6172ca725be1";

    private const string NoAgentKeySentence = "FIXTURE no agent key was loaded.";
    private const string OneAgentKeySentence = "FIXTURE one agent key was offered and refused.";
    private const string ManyAgentKeysSentence = "FIXTURE {0} agent keys were offered and refused.";
    private const string FallbackAgentUnusableSentence =
        "FIXTURE the Plink fallback cannot use this agent.";
    private const string FallbackHostKeyUnavailableSentence =
        "FIXTURE the Plink fallback could not read the gateway host key.";

    private readonly string _keyFilePath;
    private readonly string _localesPath;

    public TunnelServiceAuthDiagnosisTests()
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
                [SshAuthFailureLocaleKeys.OneAgentKeyRefused] = OneAgentKeySentence,
                [SshAuthFailureLocaleKeys.ManyAgentKeysRefused] = ManyAgentKeysSentence,
                [SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported] = FallbackAgentUnusableSentence,
                [SshLocalizationKeys.ErrorSshHostKeyUnavailable] = FallbackHostKeyUnavailableSentence
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
            // Leftover temp files are not worth failing a test over.
        }
    }

    // The rule the whole repair turns on. A wrong stored password and an
    // unloaded agent key produce the same refusal, and only the server knows
    // which it was, so the server's sentence is never dropped.
    [Fact]
    public async Task ARefusedSignIn_KeepsTheServersOwnSentenceAtTheHeadOfTheMessage()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.AuthRejected, outcome.FailureCode);
        Assert.StartsWith(RelayedServerRefusal, outcome.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSignInWithNoAgentKeyLoaded_AddsTheObservationAfterTheServersSentence()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.Equal($"{RelayedServerRefusal} {NoAgentKeySentence}", outcome.ErrorMessage);
    }

    // An agent loaded, holding a key this gateway does not accept. The old build
    // said nothing about the agent here, so the owner saw exactly the message
    // that cost him half an hour. The agent is the Windows OpenSSH one, which
    // the Plink fallback cannot use, so Heimdall says that too - after the
    // gateway's sentence and after the observation, never over either.
    [Fact]
    public async Task ARefusedSignInWhileOneAgentKeyWasLoaded_SaysThatKeyWasOfferedAndRefused()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [AgentHolding(1)]);

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.Equal(
            $"{RelayedServerRefusal} {OneAgentKeySentence} {FallbackAgentUnusableSentence}",
            outcome.ErrorMessage);
    }

    // That branch used to return before anything was reported, so the one full
    // ERROR line the log reader looks for was never written on the path where
    // an agent was actually running.
    [Fact]
    public async Task AnAgentThePlinkFallbackCannotUse_IsStillReportedInFull()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [AgentHolding(1)]);

        await harness.ConnectAsync(GatewayForKeyFile(_keyFilePath), "server-rdp-1");

        string reported = Assert.Single(harness.FullReports);
        Assert.Contains(RelayedServerRefusal, reported, StringComparison.Ordinal);
        Assert.Contains(OneAgentKeySentence, reported, StringComparison.Ordinal);
        Assert.Contains(FallbackAgentUnusableSentence, reported, StringComparison.Ordinal);
    }

    // The owner's nearest neighbour, exactly: Pageant running with a key this
    // gateway does not accept. SSH.NET is refused, Heimdall retries over Plink,
    // and the retry fails in turn. Whatever the fallback goes on to say, the
    // gateway's own sentence still heads the message and the agent observation
    // is still in it.
    [Fact]
    public async Task APlinkFallbackThatAlsoFails_KeepsTheServersSentenceAndTheAgentObservation()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [AgentHolding(1, PageantAgent.AgentName)],
            plinkHostKeyProbe: new NoPresentationPlinkHostKeyProbe());

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.False(outcome.Success);
        Assert.Equal(
            $"{RelayedServerRefusal} {OneAgentKeySentence} {FallbackHostKeyUnavailableSentence}",
            outcome.ErrorMessage);
        Assert.Contains(
            RelayedServerRefusal,
            Assert.Single(harness.FullReports),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSignInWhileSeveralAgentKeysWereLoaded_SaysHowMany()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [AgentHolding(3)]);

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.Equal(
            $"{RelayedServerRefusal} FIXTURE 3 agent keys were offered and refused."
            + $" {FallbackAgentUnusableSentence}",
            outcome.ErrorMessage);
    }

    // The sentence quotes a number of keys as keys this dial offered, so it has
    // to be the number the dial had. Pageant is empty when the dial starts and
    // three keys are loaded while the wrong-password refusal is in flight, which
    // is a minute of a real working day: the owner reads the first line, loads
    // his keys, and the refusal he provoked before that arrives afterwards.
    //
    // Reading the agents after the failure gives 3, and it also flips the branch
    // taken - with an agent now reachable that the Plink retry cannot use, the
    // message picks up a sentence about that retry as well. Both halves of the
    // message are pinned here, because the claim being fixed is that ONE agent
    // state decides the whole message, not just its number.
    //
    // Scope: the harness dials a single gateway, so "this dial" in the name is the
    // refusing gateway's own. On a chain the reading is taken before the first hop
    // and the sentence can be early by however long the earlier hops take, which
    // TunnelService.AppendAuthFailureContext states and nothing here measures.
    [Fact]
    public async Task KeysLoadedWhileTheRefusalWasInFlight_AreNotCountedAsKeysThisDialOffered()
    {
        // A registry holds its agents, not their contents: an agent answers
        // afresh every time it is probed, which is what makes a registry read
        // after the failure a different reading. A frozen list of fakes would
        // not model that, and a test built on one cannot fail.
        LiveAgent pageant = new LiveAgent(PageantAgent.AgentName) { Available = false };
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agentsProvider: () => [pageant],
            onDial: () =>
            {
                pageant.Available = true;
                pageant.IdentityCount = 3;
            });

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.Equal(1, harness.DialAttempts);
        Assert.Equal(3, pageant.IdentityCount);
        Assert.Equal($"{RelayedServerRefusal} {NoAgentKeySentence}", outcome.ErrorMessage);
        Assert.Equal(
            $"{RelayedServerRefusal} {NoAgentKeySentence}",
            harness.ConnectionStates.GetStateData("server-rdp-1")?.ErrorMessage);
    }

    // Same instant, read the other way round: three keys are loaded when the
    // dial is made and every one of them is removed before the refusal returns.
    // A diagnosis that re-reads the agents would say no key was offered, which
    // is the same defect with the sign flipped, so a fix that merely moved the
    // read earlier for the empty case does not pass this one.
    [Fact]
    public async Task KeysUnloadedWhileTheRefusalWasInFlight_AreStillCountedAsKeysThisDialOffered()
    {
        LiveAgent pageant = new LiveAgent(PageantAgent.AgentName) { IdentityCount = 3 };
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agentsProvider: () => [pageant],
            onDial: () =>
            {
                pageant.Available = false;
                pageant.IdentityCount = 0;
            },
            plinkHostKeyProbe: new NoPresentationPlinkHostKeyProbe());

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.False(pageant.Available);
        Assert.Equal(
            $"{RelayedServerRefusal} FIXTURE 3 agent keys were offered and refused."
            + $" {FallbackHostKeyUnavailableSentence}",
            outcome.ErrorMessage);
    }

    // Same surface, the other branch: what the pane reads must carry the
    // gateway's sentence there too.
    [Fact]
    public async Task AnAgentThePlinkFallbackCannotUse_LeavesTheComposedMessageOnTheConnectionState()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [AgentHolding(1)]);

        await harness.ConnectAsync(GatewayForKeyFile(_keyFilePath), "server-rdp-1");

        Assert.Equal(
            $"{RelayedServerRefusal} {OneAgentKeySentence} {FallbackAgentUnusableSentence}",
            harness.ConnectionStates.GetStateData("server-rdp-1")?.ErrorMessage);
    }

    // The returned result is not what the pane shows: it reads the message the
    // connection state machine holds. Error -> Error is not a valid transition
    // there, so a second SetError after the fallback has already set its own is
    // silently dropped and the pane keeps the fallback's wording while the
    // caller believes it composed over it.
    [Fact]
    public async Task APlinkFallbackThatAlsoFails_LeavesTheComposedMessageOnTheConnectionState()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [AgentHolding(1, PageantAgent.AgentName)],
            plinkHostKeyProbe: new NoPresentationPlinkHostKeyProbe());

        await harness.ConnectAsync(GatewayForKeyFile(_keyFilePath), "server-rdp-1");

        Assert.Equal(
            $"{RelayedServerRefusal} {OneAgentKeySentence} {FallbackHostKeyUnavailableSentence}",
            harness.ConnectionStates.GetStateData("server-rdp-1")?.ErrorMessage);
    }

    // If the catalogue merge is skipped, the localizer returns the key itself.
    // The guard is what makes that state visible before a release; this bounds
    // the damage if it ever ships, because an identifier is worse than the
    // server's own sentence.
    [Fact]
    public async Task AContextSentenceMissingFromTheCatalogue_IsDroppedRatherThanShownAsItsKey()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: [],
            localesPath: null);

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.Equal(RelayedServerRefusal, outcome.ErrorMessage);
        Assert.DoesNotContain(
            "ErrorSshAuthContext",
            harness.FullReports.Single(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonAuthFailure_IsNotGivenAnAgentObservation()
    {
        Harness harness = await HarnessRefusingAsync(
            new SocketException((int)SocketError.ConnectionRefused),
            agents: []);

        TunnelSetupOutcome outcome = await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath),
            "server-rdp-1");

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.NetworkRefused, outcome.FailureCode);
        Assert.DoesNotContain("FIXTURE", outcome.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFullReport_QuotesTheServerWordingExactlyOnce()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);

        await harness.ConnectAsync(GatewayForKeyFile(_keyFilePath), "server-rdp-1");

        string reported = Assert.Single(harness.FullReports);
        Assert.Contains(NoAgentKeySentence, reported, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(reported, RelayedServerRefusal));
    }

    [Fact]
    public async Task TenProfilesFailingTheSameWayOnOneGateway_ProduceOneFullReport()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);
        SshGatewayDto gateway = GatewayForKeyFile(_keyFilePath);

        for (int profile = 0; profile < 10; profile++)
        {
            await harness.ConnectAsync(gateway, $"server-{profile}");
            harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        Assert.Single(harness.FullReports);
        Assert.Equal(9, harness.HeldBackRepeats.Count);
    }

    // A held-back repeat that only asserts sameness cannot answer "why did
    // attempt N fail" for a reader who has nothing but the log file.
    [Fact]
    public async Task AHeldBackRepeat_CarriesTheFailureTextItIsHoldingBack()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);
        SshGatewayDto gateway = GatewayForKeyFile(_keyFilePath);

        await harness.ConnectAsync(gateway, "server-a");
        await harness.ConnectAsync(gateway, "server-b");

        string heldBack = Assert.Single(harness.HeldBackRepeats);
        Assert.Contains(RelayedServerRefusal, heldBack, StringComparison.Ordinal);
        Assert.Contains(NoAgentKeySentence, heldBack, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondGatewayFailingTheSameWay_IsStillReportedInFull()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);

        await harness.ConnectAsync(GatewayForKeyFile(_keyFilePath), "server-a");
        await harness.ConnectAsync(
            GatewayForKeyFile(_keyFilePath, id: "second-gateway", host: "gw2.example.test"),
            "server-b");

        Assert.Equal(2, harness.FullReports.Count);
        Assert.Empty(harness.HeldBackRepeats);
    }

    [Fact]
    public async Task TheSameGatewayFailingADifferentWay_IsStillReportedInFull()
    {
        Harness harness = await HarnessSwitchingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            new SocketException((int)SocketError.ConnectionRefused),
            agents: []);
        SshGatewayDto gateway = GatewayForKeyFile(_keyFilePath);

        await harness.ConnectAsync(gateway, "server-a");
        await harness.ConnectAsync(gateway, "server-b");

        Assert.Equal(2, harness.FullReports.Count);
        Assert.Empty(harness.HeldBackRepeats);
    }

    // The owner reads the first line, loads Pageant, and retries inside the
    // window. The gateway refuses again under the same code, but for a
    // different reason and with a different message. If the report were keyed on
    // the code alone, the only ERROR line in the file would still say no agent
    // key was loaded, which is false by the time he reads it.
    [Fact]
    public async Task LoadingAnAgentKeyBetweenTwoAttempts_ReopensTheReportRatherThanFoldingIntoIt()
    {
        List<ISshAgent> agents = new();
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agentsProvider: () => agents);
        SshGatewayDto gateway = GatewayForKeyFile(_keyFilePath);

        await harness.ConnectAsync(gateway, "server-0");
        agents.Add(AgentHolding(1));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.ConnectAsync(gateway, "server-0");

        Assert.Equal(2, harness.FullReports.Count);
        Assert.Contains(NoAgentKeySentence, harness.FullReports[0], StringComparison.Ordinal);
        Assert.Contains(OneAgentKeySentence, harness.FullReports[1], StringComparison.Ordinal);
        Assert.Empty(harness.HeldBackRepeats);
    }

    [Fact]
    public async Task AHopWhoseOnlySignInSourceIsAnAbsentAgent_IsRefusedBeforeDialling()
    {
        Harness harness = await HarnessRefusingAsync(
            new SshAuthenticationException(RelayedServerRefusal),
            agents: []);

        SshGatewayDto root = GatewayForKeyFile(_keyFilePath, id: "root-gateway", host: "root.example.test");
        SshGatewayDto leaf = new SshGatewayDto
        {
            Id = GatewayId,
            Name = "bastion-leaf",
            Host = "leaf.example.test",
            Port = 22,
            User = "ssh-user",
            ParentGatewayId = root.Id
        };

        TunnelSetupOutcome outcome = await harness.ConnectAsync([root, leaf], leaf.Id, "server-rdp-1");

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.PageantKeyUnavailable, outcome.FailureCode);
        Assert.Contains(leaf.Name, outcome.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(0, harness.DialAttempts);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static ISshAgent AgentHolding(
        int identities,
        string name = OpenSshPipeAgent.AgentName) =>
        new FakeAgent(
            name,
            available: true,
            [.. Enumerable.Range(0, identities).Select(_ => new FakeAgentKey())]);

    private Task<Harness> HarnessRefusingAsync(
        Exception failure,
        IReadOnlyList<ISshAgent> agents,
        string? localesPath = "",
        IPlinkHostKeyProbe? plinkHostKeyProbe = null) =>
        HarnessRefusingAsync(failure, () => agents, localesPath, plinkHostKeyProbe);

    /// <param name="onDial">
    /// Runs inside the transport, after the client has been built and before the
    /// refusal is raised. That is the only place a test can stand between the
    /// dial and the failure, which is where the agent state has to change for
    /// the drift to show.
    /// </param>
    private async Task<Harness> HarnessRefusingAsync(
        Exception failure,
        Func<IReadOnlyList<ISshAgent>> agentsProvider,
        string? localesPath = "",
        IPlinkHostKeyProbe? plinkHostKeyProbe = null,
        Action? onDial = null)
    {
        return new Harness(
            _ => failure,
            agentsProvider,
            await CreateLocalizerAsync(localesPath),
            plinkHostKeyProbe ?? new NeverProbedPlinkHostKeyProbe(),
            onDial);
    }

    private async Task<Harness> HarnessSwitchingAsync(
        Exception firstFailure,
        Exception laterFailure,
        IReadOnlyList<ISshAgent> agents)
    {
        return new Harness(
            attempt => attempt == 1 ? firstFailure : laterFailure,
            () => agents,
            await CreateLocalizerAsync(string.Empty),
            new NeverProbedPlinkHostKeyProbe());
    }

    /// <summary>
    /// An empty string means the fixture catalogue; null means an empty
    /// catalogue, which is what the shipped one looks like before the
    /// integrator merges the new keys.
    /// </summary>
    private async Task<LocalizationManager> CreateLocalizerAsync(string? localesPath)
    {
        LocalizationManager localizer = new LocalizationManager();
        if (localesPath is not null)
        {
            await localizer.LoadAsync(
                localesPath.Length == 0 ? _localesPath : localesPath,
                "en");
        }

        return localizer;
    }

    private static SshGatewayDto GatewayForKeyFile(
        string keyPath,
        string id = GatewayId,
        string host = "gw.example.test") =>
        new()
        {
            Id = id,
            Name = $"bastion-{id}",
            Host = host,
            Port = 22,
            User = "ssh-user",
            KeyPath = keyPath
        };

    /// <summary>
    /// A TunnelService wired to a TunnelManager whose dial always fails the way
    /// the test asks, with the agent registry and the failure log under the
    /// test's control.
    /// </summary>
    private sealed class Harness
    {
        private readonly Func<int, Exception> _failureForAttempt;
        private readonly Action? _onDial;
        private int _dialAttempts;

        public Harness(
            Func<int, Exception> failureForAttempt,
            Func<IReadOnlyList<ISshAgent>> agentsProvider,
            LocalizationManager localizer,
            IPlinkHostKeyProbe plinkHostKeyProbe,
            Action? onDial = null)
        {
            _failureForAttempt = failureForAttempt;
            _onDial = onDial;
            Clock = new FakeTimeProvider();

            TunnelManager = new TunnelManager(
                ResolveVerifierAsync,
                CreateClient,
                ConnectAndFailAsync);

            ConnectionStates = new ConnectionStateMachine();

            Service = new TunnelService(
                TunnelManager,
                new HostKeyStore(),
                new HostKeyTrustService(new HostKeyStore()),
                ConnectionStates,
                localizer,
                RejectingHostKeyVerifier.Instance,
                plinkHostKeyProbe,
                Clock,
                _ => new SshAgentRegistry(agentsProvider()),
                new TunnelFailureLogCoalescer(Clock, TunnelFailureLogCoalescer.DefaultWindow),
                RecordLogLine);
        }

        public FakeTimeProvider Clock { get; }

        public ConnectionStateMachine ConnectionStates { get; }

        public TunnelManager TunnelManager { get; }

        public TunnelService Service { get; }

        public List<string> FullReports { get; } = new();

        public List<string> HeldBackRepeats { get; } = new();

        public int DialAttempts => _dialAttempts;

        public Task<TunnelSetupOutcome> ConnectAsync(SshGatewayDto gateway, string serverId) =>
            ConnectAsync([gateway], gateway.Id, serverId);

        public async Task<TunnelSetupOutcome> ConnectAsync(
            IReadOnlyList<SshGatewayDto> gateways,
            string gatewayId,
            string serverId)
        {
            ServerProfileDto server = new ServerProfileDto
            {
                Id = serverId,
                RemoteServer = "target.example.test",
                RemotePort = 3389,
                ConnectionType = "RDP",
                SshGatewayId = gatewayId,
                UseDirectConnection = false
            };
            AppSettings settings = new AppSettings { SshGateways = [.. gateways] };

            return await Service.SetupTunnelIfNeededAsync(
                server,
                3389,
                settings,
                CancellationToken.None);
        }

        private void RecordLogLine(bool fullReport, string message)
        {
            if (fullReport)
            {
                FullReports.Add(message);
            }
            else
            {
                HeldBackRepeats.Add(message);
            }
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
            int attempt = Interlocked.Increment(ref _dialAttempts);
            _onDial?.Invoke();
            throw _failureForAttempt(attempt);
        }
    }

    /// <summary>
    /// Reads no host key. With nothing stored for the gateway that is a
    /// fail-closed refusal, so the Plink fallback fails the same way on every
    /// machine whether or not a plink.exe is installed on it.
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

    private sealed class NeverProbedPlinkHostKeyProbe : IPlinkHostKeyProbe
    {
        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct) =>
            throw new InvalidOperationException("These tests never reach the Plink fallback.");
    }

    /// <summary>
    /// An agent whose reachability and identity count are read at every probe,
    /// the way a real Pageant or Windows OpenSSH Agent is. A fake that captures
    /// its identities once cannot move between the dial and the refusal, and a
    /// test built on one proves nothing about when the reading happened.
    /// </summary>
    private sealed class LiveAgent(string name) : ISshAgent
    {
        public string Name { get; } = name;

        public bool Available { get; set; } = true;

        public int IdentityCount { get; set; }

        public bool IsAvailable() => Available;

        public IReadOnlyList<ISshAgentKey> GetIdentities() =>
            [.. Enumerable.Range(0, IdentityCount).Select(_ => new FakeAgentKey())];
    }

    private sealed class FakeAgent(
        string name,
        bool available,
        IReadOnlyList<ISshAgentKey> identities) : ISshAgent
    {
        public string Name { get; } = name;
        public bool IsAvailable() => available;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => identities;
    }

    private sealed class FakeAgentKey : ISshAgentKey
    {
        public string Comment => "fake";
        public string KeyType => "ssh-ed25519";
        public byte[] PublicKeyBlob => [0, 0, 0, 11, 115, 115, 104, 45, 101, 100, 50, 53, 53, 49, 57];
        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1];
    }
}
