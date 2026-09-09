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
    private const string KeyboardInteractiveRefusal = "Permission denied (keyboard-interactive).";
    private const string VerificationCodePrompt = "Verification code:";
    private const string PlinkReached = "FIXTURE the Plink launch was reached.";
    private const string RetryingViaPlinkStatus = "FIXTURE retrying through the interactive client.";
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
                [SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported] = PlinkAgentUnusableSentence,
                [SshLocalizationKeys.StatusSshRetryingViaPlink] = RetryingViaPlinkStatus
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

    /// <summary>
    /// A server that asks for a second factor is retried through the interactive client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// P-01. The embedded client has one secret and no way to ask for another, so a server whose
    /// remaining question is a verification code is unreachable through it. Plink runs in the
    /// terminal pane with a real console, so the question reaches somebody who can answer.
    /// </para>
    /// <para>
    /// Built on this harness rather than on a launch fixture, because the composition under test
    /// is the classification feeding the retry decision. The launch itself is replaced and
    /// counted: what is pinned is that the decision reached it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AServerAskingForASecondFactor_IsRetriedThroughPlink()
    {
        using Harness harness = await CreateHarnessAsync(
            agents: [], leaveAPromptUnanswered: true);

        await harness.ConnectAsync();

        Assert.Contains(RetryingViaPlinkStatus, harness.Statuses);
    }

    /// <summary>
    /// With an agent the Plink fallback cannot use, the same server gets no retry.
    /// </summary>
    /// <remarks>
    /// The real boundary of the change, and the one an earlier proposal had not seen. The retry
    /// set decides WHETHER a refusal is worth a second attempt; the agent guard decides whether
    /// this machine can make one at all. A test that only proved the first would report the
    /// feature working on a machine where it never runs.
    /// </remarks>
    [Fact]
    public async Task AServerAskingForASecondFactor_IsNotRetriedWhenTheAgentRulesPlinkOut()
    {
        using Harness harness = await CreateHarnessAsync(
            agents: [new FakeAgent(OpenSshPipeAgent.AgentName)], leaveAPromptUnanswered: true);

        ConnectionResult result = await harness.ConnectAsync();

        Assert.DoesNotContain(RetryingViaPlinkStatus, harness.Statuses);
        Assert.Contains(PlinkAgentUnusableSentence, result.ErrorMessage, StringComparison.Ordinal);
    }

    private async Task<Harness> CreateHarnessAsync(
        IReadOnlyList<ISshAgent> agents,
        bool leaveAPromptUnanswered = false,
        bool interactiveAnswer = false)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(_localesPath, "en");
        return new Harness(localizer, agents, leaveAPromptUnanswered, interactiveAnswer);
    }

    [Fact]
    public async Task RejectedInteractiveAnswer_DoesNotRetryAnotherAuthenticationClient()
    {
        using Harness harness = await CreateHarnessAsync([], interactiveAnswer: true);

        ConnectionResult result = await harness.ConnectAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain(RetryingViaPlinkStatus, harness.Statuses);
        Assert.Equal(AuthRejectedSentence, result.ErrorMessage);
    }

    private sealed class Harness : IDisposable
    {
        public const string ServerId = "b2a8b6e0-1c6a-4d3f-9f2f-6a3c1d5e7f90";

        private readonly SshHandler _handler;

        public Harness(
            LocalizationManager localizer,
            IReadOnlyList<ISshAgent> agents,
            bool leaveAPromptUnanswered = false,
            bool interactiveAnswer = false)
        {
            ConnectionStates = new ConnectionStateMachine();
            _handler = new SshHandler(
                new NoTunnelService(),
                ConnectionStates,
                localizer,
                new HostKeyStore(),

                // Null for every case that must never reach the Plink path, so touching it
                // fails loudly. The second-factor cases DO reach it by design, and a real one
                // over an empty store answers "no stored key" without any I/O.
                hostKeyTrustService: leaveAPromptUnanswered
                    ? new HostKeyTrustService(new HostKeyStore())
                    : null!,
                RejectingHostKeyVerifier.Instance,
                x11ServerManager: null!,
                dialogService: null!,
                plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
                agentRegistryFactory: _ => new SshAgentRegistry(agents),
                connectShellSession: (_, connectionParams, _, _, _, _, _) =>
                {
                    if (interactiveAnswer)
                    {
                        Assert.NotNull(connectionParams.KeyboardInteractiveResponder);
                        connectionParams.KeyboardInteractive.RecordInteractiveAnswer();
                    }

                    if (!leaveAPromptUnanswered)
                    {
                        throw new SshAuthenticationException(RefusalFromServer);
                    }

                    // What a server authenticating in stages leaves behind: the round that
                    // asked for the second factor was refused and recorded, so the classifier
                    // reads the refusal as an unanswered question rather than a bad password.
                    connectionParams.KeyboardInteractive.RecordUnanswered(VerificationCodePrompt);
                    throw new SshAuthenticationException(KeyboardInteractiveRefusal);
                },
                startPipeModeSession: (_, _, _, _, _, _) =>
                    throw new InvalidOperationException(PlinkReached));

            _handler.SetStatusText = text => Statuses.Add(text);
        }

        /// <summary>Every status the handler announced, in order.</summary>
        /// <remarks>
        /// The retry decision is observed here rather than at the launch, and the difference
        /// matters. The Plink path carries host-key gates of its own that stop it before any
        /// process on this harness, so a launch counter reads zero whether the decision fired or
        /// not. The announcement is emitted on the line after the decision and nowhere else.
        /// </remarks>
        public List<string> Statuses { get; } = [];

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
