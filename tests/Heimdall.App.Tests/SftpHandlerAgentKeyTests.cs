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

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Session;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Pageant;
using Heimdall.TestSupport;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// The SFTP side of a session that names one agent key: the same single key is offered, the refusal
/// comes before any prompt, the side panel inherits the choice and an export never carries it.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SftpHandlerAgentKeyTests : IDisposable
{
    private static readonly byte[] BlobA = [1, 1, 1, 1];
    private static readonly byte[] BlobB = [2, 2, 2, 2];

    private readonly CredentialProtectorStateScope _protectorState = new();

    /// <inheritdoc />
    public void Dispose() => _protectorState.Dispose();

    [Fact]
    public async Task BuildSshParams_CarriesAgentKey()
    {
        string fingerprint = SshAgentKeyFingerprints.Compute(BlobA);
        List<SshConnectionParams> dialled = [];
        SftpHandler handler = CreateHandler(
            [new SshHandlerAgentKeyTests.FakeKey("a", BlobA)],
            (browser, sshParams, store, verifier, token) =>
            {
                dialled.Add(sshParams);
                return Task.CompletedTask;
            });

        ConnectionResult result = await handler.ConnectAsync(
            CreateServer(fingerprint),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(fingerprint, Assert.Single(dialled).AgentKeyFingerprint);

        // The bundle the pane keeps for the editor, the exec runner and the reconnect is this object.
        SftpSessionBundle bundle = Assert.IsType<SftpSessionBundle>(result.Session);
        Assert.Equal(fingerprint, bundle.SshParams.AgentKeyFingerprint);
    }

    [Fact]
    public async Task ChosenKeyNotLoaded_RefusesWithoutPasswordPrompt()
    {
        string missing = SshAgentKeyFingerprints.Compute(BlobB);
        int dials = 0;
        int tunnelSetups = 0;
        SftpHandler handler = CreateHandler(
            [new SshHandlerAgentKeyTests.FakeKey("a", BlobA)],
            (browser, sshParams, store, verifier, token) =>
            {
                dials++;
                return Task.CompletedTask;
            },
            onTunnelSetup: () => tunnelSetups++);
        ServerProfileDto server = CreateServer(missing);
        server.AllowCredentialPrompt = true;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        // The dialog double throws on every member, so a password box raised here would have
        // failed the call instead of reaching this assertion.
        Assert.False(result.Success);
        Assert.Contains("ErrorSshAgentKeyNotLoaded", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, dials);
        Assert.Equal(0, tunnelSetups);
    }

    [Fact]
    public async Task Sftp_ChosenKeyRejected_PasswordRetryBehavesAsWithoutChosenKey()
    {
        string fingerprint = SshAgentKeyFingerprints.Compute(BlobA);
        IDialogService dialog = DispatchProxy.Create<IDialogService, SshHandlerAgentKeyTests.RecordingPasswordDialogProxy>();
        var recording = (SshHandlerAgentKeyTests.RecordingPasswordDialogProxy)dialog;
        recording.Answer = "typed-secret";
        List<(string? Password, string? Fingerprint)> attempts = [];
        SftpHandler handler = CreateHandler(
            [new SshHandlerAgentKeyTests.FakeKey("a", BlobA)],
            (browser, sshParams, store, verifier, token) =>
            {
                attempts.Add((sshParams.Password, sshParams.AgentKeyFingerprint));
                throw new SshAuthenticationException("No suitable authentication method found.");
            },
            dialog: dialog);
        ServerProfileDto server = CreateServer(fingerprint);
        server.AllowCredentialPrompt = true;

        await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        // Exactly what happens without a chosen key: one prompt, one retry with what was typed,
        // and the retry still offers the same single agent key.
        Assert.Single(recording.PasswordPrompts);
        Assert.Equal([(null, fingerprint), ("typed-secret", fingerprint)], attempts);
    }

    [Fact]
    public void CompanionSftpProfile_CarriesAgentKey()
    {
        string fingerprint = SshAgentKeyFingerprints.Compute(BlobA);
        ServerProfileDto ssh = CreateServer(fingerprint);
        ssh.ConnectionType = "SSH";

        ServerProfileDto companion = SessionCoordinator.CreateCompanionSftpProfile(ssh, "sftp-1");

        Assert.Equal(fingerprint, companion.SshAgentKeyFingerprint);
    }

    [Fact]
    public void Export_StripsAgentKeyFingerprint()
    {
        ServerProfileDto server = CreateServer(SshAgentKeyFingerprints.Compute(BlobA));
        FieldInfo? field = typeof(SettingsViewModel).GetField(
            "ExportJsonOptions",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var options = Assert.IsType<JsonSerializerOptions>(field.GetValue(null));

        ProfileConfigDocument document = SettingsViewModel.BuildExportConfigDocument([server], new AppSettings());
        JsonObject exported = JsonNode.Parse(JsonSerializer.Serialize(document, options))!.AsObject();

        JsonObject exportedServer = exported["servers"]![0]!.AsObject();
        Assert.False(exportedServer.ContainsKey("sshAgentKeyFingerprint"));
        Assert.Equal("operator", exportedServer["sshUsername"]?.GetValue<string>());
    }

    private static SftpHandler CreateHandler(
        IReadOnlyList<ISshAgentKey> agentKeys,
        SftpHandler.ConnectBrowserDelegate connectBrowser,
        Action? onTunnelSetup = null,
        IDialogService? dialog = null)
    {
        return new SftpHandler(
            new CountingTunnelService(onTunnelSetup),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            new HostKeyStore(),
            AutoAcceptHostKeyVerifier.Instance,
            dialog ?? DispatchProxy.Create<IDialogService, ThrowingDialogProxy>(),
            connectBrowser,
            _ => new SshAgentRegistry([new SshHandlerAgentKeyTests.FakeAgent(PageantAgent.AgentName, agentKeys)]));
    }

    private static ServerProfileDto CreateServer(string? fingerprint) => new()
    {
        Id = "sftp-agent-key",
        DisplayName = "SFTP agent key",
        ConnectionType = "SFTP",
        RemoteServer = "127.0.0.1",
        SshPort = DefaultPorts.Ssh,
        SshUsername = "operator",
        SshAgentKeyFingerprint = fingerprint,
        UseDirectConnection = true,
    };

    private class ThrowingDialogProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException($"unexpected dialog call: {targetMethod?.Name}");
    }

    private sealed class CountingTunnelService(Action? onSetup) : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            onSetup?.Invoke();
            return Task.FromResult(
                new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }
}
