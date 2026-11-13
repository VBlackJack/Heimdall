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
using System.Text.RegularExpressions;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Pageant;
using Heimdall.Ssh.Plink;
using Xunit;

namespace Heimdall.App.Tests;

/// <summary>
/// The plink password prompt: Cancel fails, and an empty answer launches plink without a password
/// file and in batch mode (Kerberos or an agent key decides), whatever Pageant holds. The prompt
/// always carries the hint that says so.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerPlinkAgentOnlyTests : IDisposable
{
    // The stored-password test protects a password through the process-global protector: pinned
    // to the no-vault baseline so a writer scheduled before this class cannot leave it locked.
    private readonly CredentialProtectorStateScope _scope = new();

    private static readonly byte[] BlobA = SshHandlerAgentKeyTests.Blob("ssh-ed25519", 1);

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task PlinkPrompt_EmptyAnswer_PageantHoldsAKey_LaunchesWithoutPasswordFile()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: string.Empty);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, harness.Launches);
            Assert.DoesNotContain("-pwfile", harness.LastArguments, StringComparison.Ordinal);
            Assert.Contains(" -batch", harness.LastArguments, StringComparison.Ordinal);
            Assert.Empty(harness.PasswordFilePaths);
            Assert.True(((TerminalSessionResult)result.Session!).CredentialFreeLaunch);
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
        }
    }

    [Fact]
    public async Task PlinkPrompt_StoredPassword_LaunchesWithoutPromptAndWithoutBatch()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: null);

        ConnectionResult result = await harness.ConnectViaPlinkAsync(
            server => server.SshPasswordEncrypted = CredentialProtector.Protect("stored-secret"));
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Empty(harness.Prompts);
            Assert.Contains("-pwfile", harness.LastArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-batch", harness.LastArguments, StringComparison.Ordinal);
            Assert.False(((TerminalSessionResult)result.Session!).CredentialFreeLaunch);
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
        }
    }

    [Fact]
    public async Task PlinkPrompt_KeyFileWithoutPassword_LaunchesWithoutPromptAndWithoutBatch()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: null);

        string keyFile = Path.GetTempFileName();
        ConnectionResult result = await harness.ConnectViaPlinkAsync(server => server.SshKeyPath = keyFile);
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Empty(harness.Prompts);
            Assert.Contains("-i ", harness.LastArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-pwfile", harness.LastArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-batch", harness.LastArguments, StringComparison.Ordinal);
            Assert.False(((TerminalSessionResult)result.Session!).CredentialFreeLaunch);
        }
        finally
        {
            File.Delete(keyFile);
            (result.Session as TerminalSessionResult)?.Session.Dispose();
        }
    }

    [Fact]
    public async Task PlinkPrompt_SpacesOnlyAnswer_IsATypedPassword()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: "   ");

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains("-pwfile", harness.LastArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-batch", harness.LastArguments, StringComparison.Ordinal);
            Assert.False(((TerminalSessionResult)result.Session!).CredentialFreeLaunch);
            Assert.Equal("   ", Assert.Single(harness.PasswordFileContents));
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
        }
    }

    [Fact]
    public async Task PlinkPrompt_Cancel_StillFailsCancelled()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: null);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();

        Assert.False(result.Success);
        Assert.Equal(SshFailureMessageBuilder.Cancelled(harness.Localizer), result.ErrorMessage);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task PlinkPrompt_TypedPassword_WritesThePasswordFileAsBefore()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: "typed-secret");

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains("-pwfile", harness.LastArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-batch", harness.LastArguments, StringComparison.Ordinal);
            Assert.False(((TerminalSessionResult)result.Session!).CredentialFreeLaunch);
            Assert.Equal("typed-secret", Assert.Single(harness.PasswordFileContents));
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
        }
    }

    [Fact]
    public async Task PlinkPrompt_PageantHoldsAKey_ShowsThePasswordlessHint()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(PageantWithAKey(), answer: string.Empty);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        (result.Session as TerminalSessionResult)?.Session.Dispose();

        string message = Assert.Single(harness.Prompts).Message;
        Assert.Contains(
            harness.Localizer[SshLocalizationKeys.PlinkPasswordPromptPasswordlessHint],
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlinkPrompt_NoAgentIdentity_StillShowsThePasswordlessHint()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(
            [new SshHandlerAgentKeyTests.FakeAgent(PageantAgent.AgentName, [])],
            answer: string.Empty);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        (result.Session as TerminalSessionResult)?.Session.Dispose();

        string message = Assert.Single(harness.Prompts).Message;
        Assert.Contains(
            harness.Localizer[SshLocalizationKeys.PlinkPasswordPromptPasswordlessHint],
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlinkPrompt_EmptyAnswer_NoAgentIdentity_LaunchesWithoutPasswordFileInBatchMode()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(
            [new SshHandlerAgentKeyTests.FakeAgent(PageantAgent.AgentName, [])],
            answer: string.Empty);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, harness.Launches);
            Assert.DoesNotContain("-pwfile", harness.LastArguments, StringComparison.Ordinal);
            Assert.Contains(" -batch ", harness.LastArguments, StringComparison.Ordinal);
            Assert.Empty(harness.PasswordFilePaths);
            Assert.True(((TerminalSessionResult)result.Session!).CredentialFreeLaunch);
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
        }
    }

    [Fact]
    public async Task PlinkPrompt_PreferenceExcludesPageant_PageantHoldsAKey_StillShowsThePasswordlessHint()
    {
        using PlinkPromptHarness harness = await PlinkPromptHarness.CreateAsync(
            PageantWithAKey(),
            answer: string.Empty,
            preference: SshAgentPreference.OpenSshOnly);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        (result.Session as TerminalSessionResult)?.Session.Dispose();

        string message = Assert.Single(harness.Prompts).Message;
        Assert.Contains(
            harness.Localizer[SshLocalizationKeys.PlinkPasswordPromptPasswordlessHint],
            message,
            StringComparison.Ordinal);
        Assert.Equal(1, harness.Launches);
    }
    private static List<ISshAgent> PageantWithAKey() =>
        [new SshHandlerAgentKeyTests.FakeAgent(PageantAgent.AgentName, [new SshHandlerAgentKeyTests.FakeKey("a", BlobA)])];

    /// <summary>
    /// A handler whose launch and dialog are scripted and everything else ships: the profile has
    /// a username, no stored password and no key file, so the plink path asks for a password.
    /// </summary>
    internal sealed class PlinkPromptHarness : IDisposable
    {
        private const string TrustedHost = "server01.contoso.local";
        private const string TrustedFingerprint = "SHA256:stored-test-fingerprint";

        private readonly string _plinkPath = Path.GetTempFileName();
        private readonly SshHandler _handler;

        private PlinkPromptHarness(
            List<ISshAgent> agents,
            LocalizationManager localizer,
            string? answer,
            Action<string?>? onDelete,
            SshAgentPreference preference)
        {
            Localizer = localizer;
            IDialogService dialog = System.Reflection.DispatchProxy.Create<
                IDialogService, SshHandlerAgentKeyTests.RecordingPasswordDialogProxy>();
            var recording = (SshHandlerAgentKeyTests.RecordingPasswordDialogProxy)dialog;
            recording.Answer = answer;
            Prompts = recording.PasswordPrompts;

            HostKeyStore hostKeyStore = new();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            _handler = new SshHandler(
                new NoTunnelService(),
                new ConnectionStateMachine(),
                localizer,
                new HostKeyStore(),
                new HostKeyTrustService(hostKeyStore),
                AutoAcceptHostKeyVerifier.Instance,
                new X11ServerManager(new InMemoryConfigManager(), localizer),
                dialogService: dialog,
                plinkHostKeyProbe: new NoProbe(),
                plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
                plinkAttestation: _ => PlinkAttestationLease.NotAttested,
                deletePlinkPasswordFile: path =>
                {
                    onDelete?.Invoke(path);
                    if (path is not null)
                    {
                        File.Delete(path);
                    }
                },
                agentRegistryFactory: _ => new SshAgentRegistry(agents, () => preference),
                startPipeModeSession: (_, _, args, _, _, _) =>
                {
                    Launches++;
                    LastArguments = args;
                    Match named = Regex.Match(args, "-pwfile \"([^\"]+)\"");
                    if (named.Success)
                    {
                        PasswordFilePaths.Add(named.Groups[1].Value);
                        PasswordFileContents.Add(File.ReadAllText(named.Groups[1].Value));
                    }

                    return Task.CompletedTask;
                },
                puttySessionRegistry: SizeSessions);
        }

        public LocalizationManager Localizer { get; }

        public List<(string Title, string Message)> Prompts { get; }

        public InMemoryPuttySessionRegistry SizeSessions { get; } = new();

        public int Launches { get; private set; }

        public string LastArguments { get; private set; } = string.Empty;

        public List<string> PasswordFilePaths { get; } = [];

        /// <summary>The content of each password file at the moment of the launch.</summary>
        public List<string> PasswordFileContents { get; } = [];

        public static async Task<PlinkPromptHarness> CreateAsync(
            List<ISshAgent> agents,
            string? answer,
            Action<string?>? onDelete = null,
            SshAgentPreference preference = SshAgentPreference.AutoOpenSshFirst)
        {
            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            return new PlinkPromptHarness(agents, localizer, answer, onDelete, preference);
        }

        public Task<ConnectionResult> ConnectViaPlinkAsync(Action<ServerProfileDto>? configure = null)
        {
            var profile = new ServerProfileDto
            {
                Id = "plink-prompt",
                DisplayName = "plink prompt",
                ConnectionType = "SSH",
                RemoteServer = TrustedHost,
                SshPort = DefaultPorts.Ssh,
                SshMode = "Embedded",
                SshUsername = "operator",
                UseDirectConnection = true,
            };
            configure?.Invoke(profile);

            return _handler.ConnectSshViaPlinkAsync(
                profile,
                new AppSettings { PlinkPath = _plinkPath },
                TrustedHost,
                DefaultPorts.Ssh,
                usesTunnel: false,
                originalFailure: null,
                CancellationToken.None);
        }

        public void Dispose()
        {
            _handler.Dispose();
            File.Delete(_plinkPath);
            foreach (string path in PasswordFilePaths)
            {
                File.Delete(path);
            }
        }

        private sealed class NoProbe : IPlinkHostKeyProbe
        {
            public Task<PlinkHostKeyPresentation?> ProbeAsync(
                string plinkPath,
                string host,
                int port,
                string? username,
                int timeoutMs,
                CancellationToken ct) => Task.FromResult<PlinkHostKeyPresentation?>(null);
        }

        private sealed class NoTunnelService : ITunnelService
        {
            public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
                ServerProfileDto server,
                int remotePort,
                AppSettings settings,
                CancellationToken ct,
                bool preferDistinctLoopback = false) =>
                Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, null, null));

            public void UpdateSettings(AppSettings settings)
            {
            }

            public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

            public void ReleaseTunnelReference(int localPort)
            {
            }
        }
    }
}
