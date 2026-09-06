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
using Heimdall.App.Localization;
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
/// A session that asked for X11 forwarding and has no X server gets told, and is launched
/// without <c>-X</c>.
/// </summary>
/// <remarks>
/// <para><c>EnsureRunningAsync</c> returns <see langword="false"/> when no X server is running and
/// none could be started. All three launch paths ignored that answer: Plink and PuTTY were still
/// given <c>-X</c>, the remote side printed "cannot open display" for every X client, and the user
/// saw nothing on this side explaining why. The localized notice existed and went to the log
/// only.</para>
/// <para>The notice now travels the status-text path that the direct-transport capability notice
/// already uses, and forwarding is dropped from the launch for that session. Observed through
/// the pipe-mode launch seam, so no process starts and no X server is looked for on the test
/// box.</para>
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerX11ForwardingTests
{
    private const string TrustedHost = "server01.contoso.local";
    private const string TrustedFingerprint = "SHA256:stored-test-fingerprint";
    private const int TunnelLocalPort = 49162;
    private const string ForwardingFlag = "-X";

    [Fact]
    public async Task WithoutAnXServerThePlinkLaunchDropsForwardingAndTellsTheUser()
    {
        using LaunchFixture fixture = new LaunchFixture();
        FakeX11ServerManager x11 = new FakeX11ServerManager(available: false);
        List<string> statusTexts = [];
        LocalizationManager localizer = await LoadEnglishAsync();
        using SshHandler handler = fixture.CreateHandler(localizer, x11);
        handler.SetStatusText = statusTexts.Add;
        ServerProfileDto server = fixture.CreateServer(x11Forwarding: true);

        ConnectionResult result = await fixture.ConnectViaPlinkAsync(handler, server);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, x11.CallCount);
        Assert.DoesNotContain(ForwardingFlag, fixture.LaunchArguments);
        string expected = localizer[SshLocalizationKeys.X11ServerNotFound];
        Assert.NotEqual(SshLocalizationKeys.X11ServerNotFound, expected);
        Assert.Equal([expected], statusTexts);
    }

    [Fact]
    public async Task WithAnXServerThePlinkLaunchForwardsAndSaysNothing()
    {
        using LaunchFixture fixture = new LaunchFixture();
        FakeX11ServerManager x11 = new FakeX11ServerManager(available: true);
        List<string> statusTexts = [];
        using SshHandler handler = fixture.CreateHandler(new LocalizationManager(), x11);
        handler.SetStatusText = statusTexts.Add;
        ServerProfileDto server = fixture.CreateServer(x11Forwarding: true);

        ConnectionResult result = await fixture.ConnectViaPlinkAsync(handler, server);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, x11.CallCount);
        Assert.Contains(ForwardingFlag, fixture.LaunchArguments);
        Assert.Empty(statusTexts);
    }

    [Fact]
    public async Task ASessionThatDidNotAskForForwardingDoesNotLookForAServer()
    {
        using LaunchFixture fixture = new LaunchFixture();
        FakeX11ServerManager x11 = new FakeX11ServerManager(available: false);
        List<string> statusTexts = [];
        using SshHandler handler = fixture.CreateHandler(new LocalizationManager(), x11);
        handler.SetStatusText = statusTexts.Add;
        ServerProfileDto server = fixture.CreateServer(x11Forwarding: false);

        ConnectionResult result = await fixture.ConnectViaPlinkAsync(handler, server);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, x11.CallCount);
        Assert.DoesNotContain(ForwardingFlag, fixture.LaunchArguments);
        Assert.Empty(statusTexts);
    }

    /// <summary>
    /// The external PuTTY path launches a real process, so its wiring is read from source: the
    /// flag it passes has to be the resolved answer, not the profile's wish.
    /// </summary>
    [Fact]
    public void TheExternalPuttyLaunchPassesTheResolvedAnswer()
    {
        string body = ExtractMethodBody(
            ReadHandlerSource(),
            "private async Task<ConnectionResult> ConnectSshExternal(");
        string text = ExecutableText(body);

        Assert.Contains("bool x11Forwarding = await ResolveX11ForwardingAsync(server)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("server.SshX11Forwarding", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one place that resolves the answer is the one that surfaces the notice.
    /// </summary>
    [Fact]
    public void TheResolverSurfacesTheNoticeThroughTheStatusText()
    {
        string body = ExtractMethodBody(
            ReadHandlerSource(),
            "private async Task<bool> ResolveX11ForwardingAsync(ServerProfileDto server)");
        string text = ExecutableText(body);

        Assert.Contains("SetStatusText?.Invoke(_localizer[SshLocalizationKeys.X11ServerNotFound]);", text, StringComparison.Ordinal);
    }

    private static async Task<LocalizationManager> LoadEnglishAsync()
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(FindRepoRoot(), "locales"), "en");
        return localizer;
    }

    private static string ReadHandlerSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(),
        "src",
        "Heimdall.App",
        "Services",
        "Handlers",
        "SshHandler.cs"));

    private static string ExecutableText(string body) => string.Join(
        ' ',
        body.Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)));

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Signature not found: {signature}");

        int open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0, $"No body for: {signature}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced body for: {signature}");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Everything a Plink launch needs to reach the launch seam without a process: a trusted
    /// host key so the decider proceeds, a key file so no password is asked, and a file at the
    /// configured launcher path so the handler finds one.
    /// </summary>
    private sealed class LaunchFixture : IDisposable
    {
        private readonly string _plinkPath = Path.GetTempFileName();
        private readonly string _keyPath = Path.GetTempFileName();
        private ConnectionResult? _result;

        public string LaunchArguments { get; private set; } = string.Empty;

        public SshHandler CreateHandler(LocalizationManager localizer, IX11ServerManager x11ServerManager)
        {
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            return new SshHandler(
                new NoTunnelService(),
                new ConnectionStateMachine(),
                localizer,
                new HostKeyStore(),
                new HostKeyTrustService(hostKeyStore),
                AutoAcceptHostKeyVerifier.Instance,
                x11ServerManager,
                dialogService: null!,
                plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
                plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
                plinkAttestation: static _ => PlinkAttestationLease.NotAttested,
                agentRegistryFactory: static _ => new SshAgentRegistry([]),
                startPipeModeSession: (_, _, arguments, _, _, _) =>
                {
                    LaunchArguments = arguments;
                    return Task.CompletedTask;
                });
        }

        public ServerProfileDto CreateServer(bool x11Forwarding) => new ServerProfileDto
        {
            Id = "ssh-x11-plink",
            DisplayName = "x11 plink",
            ConnectionType = "SSH",
            RemoteServer = TrustedHost,
            SshPort = DefaultPorts.Ssh,
            SshMode = "Embedded",
            SshUsername = "operator",
            SshKeyPath = _keyPath,
            SshGatewayId = "gateway-01",
            SshX11Forwarding = x11Forwarding
        };

        public async Task<ConnectionResult> ConnectViaPlinkAsync(SshHandler handler, ServerProfileDto server)
        {
            _result = await handler.ConnectSshViaPlinkAsync(
                server,
                new AppSettings { PlinkPath = _plinkPath },
                "127.0.0.1",
                TunnelLocalPort,
                usesTunnel: true,
                originalFailure: null,
                CancellationToken.None);
            return _result;
        }

        public void Dispose()
        {
            if (_result?.Session is TerminalSessionResult terminal)
            {
                terminal.Session.Dispose();
            }

            File.Delete(_plinkPath);
            File.Delete(_keyPath);
        }
    }

    private sealed class FakeX11ServerManager(bool available) : IX11ServerManager
    {
        public int CallCount { get; private set; }

        public Task<bool> EnsureRunningAsync()
        {
            CallCount++;
            return Task.FromResult(available);
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
