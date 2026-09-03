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
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// What an embedded SSH session shows when the server refuses the sign-in and
/// Heimdall declines to retry over Plink.
/// </summary>
/// <remarks>
/// One machine state reaches this branch: the Windows OpenSSH Agent running and
/// Pageant absent. On it, the handler used to replace the server's own refusal
/// with a sentence about the Plink retry, so a wrong password produced a screen
/// that said nothing about a password. The catalogue is a fixture; what is
/// pinned here is which sentences survive and in what order.
/// <para>
/// The dial is replaced and nothing else is: the classification, the
/// localization, the agent decision and the composition under test all run as
/// they ship. The collaborators this path must never touch are passed as null,
/// so touching one fails loudly instead of passing quietly.
/// </para>
/// </remarks>
public sealed class SshHandlerAuthFailureCompositionTests : IDisposable
{
    private const string RefusalFromServer = "Permission denied.";
    private const string AuthRejectedSentence = "FIXTURE the server refused this sign-in.";
    private const string PlinkAgentUnusableSentence =
        "FIXTURE the Plink fallback cannot use this agent.";

    private readonly string _localesPath;

    public SshHandlerAuthFailureCompositionTests()
    {
        _localesPath = Path.Combine(Path.GetTempPath(), $"heimdall-locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["ErrorSshAuthRejected"] = AuthRejectedSentence,
                [SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported] = PlinkAgentUnusableSentence
            }));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_localesPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task AnAgentThePlinkFallbackCannotUse_DoesNotReplaceTheServersOwnRefusal()
    {
        using Harness harness = await CreateHarnessAsync(
            agents: [new FakeAgent(OpenSshPipeAgent.AgentName)]);

        ConnectionResult result = await harness.ConnectAsync();

        Assert.False(result.Success);
        Assert.Equal(
            $"{AuthRejectedSentence} {PlinkAgentUnusableSentence}",
            result.ErrorMessage);
    }

    // The pane reads the state machine, not the returned result, so the same
    // composition has to reach it.
    [Fact]
    public async Task AnAgentThePlinkFallbackCannotUse_LeavesTheComposedMessageOnTheConnectionState()
    {
        using Harness harness = await CreateHarnessAsync(
            agents: [new FakeAgent(OpenSshPipeAgent.AgentName)]);

        await harness.ConnectAsync();

        Assert.Equal(
            $"{AuthRejectedSentence} {PlinkAgentUnusableSentence}",
            harness.ConnectionStates.GetStateData(Harness.ServerId)?.ErrorMessage);
    }

    // The diagnostic recorded for the session is the same branch's second
    // output, and a support reader sees that one rather than the pane. It names
    // the stage, which is what says the branch under test is the branch that
    // ran, and it carries the same composed detail.
    [Fact]
    public async Task AnAgentThePlinkFallbackCannotUse_RecordsTheComposedMessageAsTheSessionDiagnostic()
    {
        using Harness harness = await CreateHarnessAsync(
            agents: [new FakeAgent(OpenSshPipeAgent.AgentName)]);

        ConnectionResult result = await harness.ConnectAsync();

        Assert.NotNull(result.Failure);
        Assert.Equal(SessionFailureStage.SshPlinkFallback, result.Failure.Stage);
        Assert.Equal(
            $"{AuthRejectedSentence} {PlinkAgentUnusableSentence}",
            result.Failure.Detail);
    }

    private async Task<Harness> CreateHarnessAsync(IReadOnlyList<ISshAgent> agents)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(_localesPath, "en");
        return new Harness(localizer, agents);
    }

    private sealed class Harness : IDisposable
    {
        public const string ServerId = "b2a8b6e0-1c6a-4d3f-9f2f-6a3c1d5e7f90";

        private readonly SshHandler _handler;

        public Harness(LocalizationManager localizer, IReadOnlyList<ISshAgent> agents)
        {
            ConnectionStates = new ConnectionStateMachine();
            _handler = new SshHandler(
                new NoTunnelService(),
                ConnectionStates,
                localizer,
                new HostKeyStore(),
                hostKeyTrustService: null!,
                RejectingHostKeyVerifier.Instance,
                x11ServerManager: null!,
                dialogService: null!,
                plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
                agentRegistryFactory: _ => new SshAgentRegistry(agents),
                connectShellSession: (_, _, _, _, _) =>
                    throw new SshAuthenticationException(RefusalFromServer));
        }

        public ConnectionStateMachine ConnectionStates { get; }

        public Task<ConnectionResult> ConnectAsync()
        {
            ServerProfileDto server = new ServerProfileDto
            {
                Id = ServerId,
                DisplayName = "shell",
                RemoteServer = "host.example.test",
                SshPort = 22,
                ConnectionType = "SSH",
                SshMode = "Embedded",
                SshUsername = "ssh-user",
                UseDirectConnection = true
            };

            return _handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);
        }

        public void Dispose()
        {
            _handler.Dispose();
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

    private sealed class FakeAgent(string name) : ISshAgent
    {
        public string Name { get; } = name;
        public bool IsAvailable() => true;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => [];
    }
}
