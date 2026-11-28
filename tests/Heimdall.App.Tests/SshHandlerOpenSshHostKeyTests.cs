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
using System.Text;
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
/// The native OpenSSH path of the SSH handler, up to the host-key decision: the handler's own
/// mapping of a refused resolution, and the guarantee that a refusal spends nothing.
/// </summary>
/// <remarks>
/// The client is a stub script on a configured path that only answers the attestation banner, so
/// the handler runs as it ships up to the decision. Nothing past the decision is reached, which is
/// the point: the tests prove a refusal starts no terminal process. The launch itself (known_hosts
/// file, ConPTY start) is not covered here: it needs a real Windows ssh.exe.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerOpenSshHostKeyTests
{
    private const string Host = "native.contoso.local";
    private const string AttestationBanner = "OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2";

    [Fact]
    public async Task ARevokedPresentedKey_IsRefusedAsRevoked_WithoutSpendingTheStartupApproval()
    {
        byte[] blob = Ed25519Blob();
        string fingerprint = HostKeyFormats.ComputeSha256Fingerprint(blob);
        Harness harness = await Harness.CreateAsync(
            new PlinkHostKeyPresentation("ssh-ed25519", fingerprint, Convert.ToBase64String(blob)));
        try
        {
            harness.Store.AddRevokedKey("*", fingerprint);
            SshStartupTrustGate.Set(harness.Server, approved: true);

            ConnectionResult result = await harness.ConnectAsync();

            Assert.False(result.Success);
            Assert.Null(result.Session);
            Assert.Equal(SshFailureMessageBuilder.HostKeyRevoked(harness.Localizer), result.ErrorMessage);
            Assert.Equal(1, harness.Probe.Calls);

            // The approval is still there for the connect that follows the user's fix.
            SshStartupTrustGate.Decision? stillAvailable = SshStartupTrustGate.Take(harness.Server);
            Assert.NotNull(stillAvailable);
            Assert.True(stillAvailable.Approved);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task AHostKeyThatCannotBeVerified_ShowsTheNativeText_NotThePlinkOne()
    {
        Harness harness = await Harness.CreateAsync(presentation: null);
        try
        {
            SshStartupTrustGate.Set(harness.Server, approved: true);

            ConnectionResult result = await harness.ConnectAsync();

            Assert.False(result.Success);
            Assert.Null(result.Session);
            string nativeText = harness.Localizer[SshLocalizationKeys.ErrorOpenSshHostKeyUnavailable];
            string plinkText = SshFailureMessageBuilder.HostKeyUnavailable(harness.Localizer);
            Assert.NotEqual(nativeText, plinkText);
            Assert.Equal(nativeText, result.ErrorMessage);
            Assert.NotNull(SshStartupTrustGate.Take(harness.Server));
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static byte[] Ed25519Blob()
    {
        const string KeyType = "ssh-ed25519";
        const int RawKeyBytes = 32;
        using MemoryStream stream = new();
        WriteString(stream, Encoding.ASCII.GetBytes(KeyType));
        WriteString(stream, System.Security.Cryptography.RandomNumberGenerator.GetBytes(RawKeyBytes));
        return stream.ToArray();
    }

    private static void WriteString(MemoryStream stream, byte[] value)
    {
        byte[] length = BitConverter.GetBytes(value.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(length);
        }

        stream.Write(length);
        stream.Write(value);
    }

    private sealed class FakeProbe(PlinkHostKeyPresentation? presentation) : IOpenSshHostKeyProbe
    {
        public int Calls { get; private set; }

        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string host,
            int port,
            SshCryptoPolicy cryptoPolicy,
            int timeoutMs,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(presentation);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _clientPath;
        private readonly SshHandler _handler;
        private readonly AppSettings _settings;

        private Harness(
            string clientPath,
            SshHandler handler,
            HostKeyStore store,
            FakeProbe probe,
            LocalizationManager localizer,
            ServerProfileDto server)
        {
            _clientPath = clientPath;
            _handler = handler;
            Store = store;
            Probe = probe;
            Localizer = localizer;
            Server = server;
            _settings = new AppSettings { OpenSshPath = clientPath };
        }

        public HostKeyStore Store { get; }

        public FakeProbe Probe { get; }

        public LocalizationManager Localizer { get; }

        public ServerProfileDto Server { get; }

        public static async Task<Harness> CreateAsync(PlinkHostKeyPresentation? presentation)
        {
            string clientPath = Path.Combine(Path.GetTempPath(), "heimdall-fake-ssh-" + Guid.NewGuid().ToString("N") + ".cmd");
            await File.WriteAllTextAsync(
                clientPath,
                "@echo off\r\nif \"%1\"==\"-V\" echo " + AttestationBanner + " 1>&2\r\nexit /b 0\r\n");

            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            HostKeyStore store = new();
            FakeProbe probe = new(presentation);
            SshHandler handler = new(
                new NoTunnelService(),
                new ConnectionStateMachine(),
                localizer,
                store,
                new HostKeyTrustService(store),
                AutoAcceptHostKeyVerifier.Instance,
                new X11ServerManager(new InMemoryConfigManager(), localizer),
                dialogService: null!,
                plinkHostKeyProbe: new NeverProbedPlinkHostKeyProbe(),
                plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
                plinkAttestation: static _ => PlinkAttestationLease.NotAttested,
                agentRegistryFactory: static _ => new SshAgentRegistry([]),
                puttySessionRegistry: new InMemoryPuttySessionRegistry(),
                openSshHostKeyProbe: probe);

            ServerProfileDto server = new()
            {
                Id = "ssh-openssh-host-key",
                DisplayName = "native host key",
                ConnectionType = "SSH",
                RemoteServer = Host,
                SshPort = DefaultPorts.Ssh,
                SshMode = "OpenSsh",
                SshUsername = "operator",
                UseDirectConnection = true,
            };
            return new Harness(clientPath, handler, store, probe, localizer, server);
        }

        public Task<ConnectionResult> ConnectAsync()
            => _handler.ConnectAsync(Server, _settings, CancellationToken.None);

        public void Dispose()
        {
            _handler.Dispose();
            File.Delete(_clientPath);
        }
    }

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
            throw new InvalidOperationException("The native path must never probe through Plink.");
    }
}
