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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.Tests;

/// <summary>
/// C-08 / A-10: sentences composed in the SSH layer (tunnel manager, plink runner,
/// preflight checker) reached the status bar and the connection state as English
/// whatever the locale. They now travel as failure codes plus locale keys and are
/// formatted by the application against the catalogue.
/// </summary>
public sealed class TunnelFailureLocalizationTests : IDisposable
{
    private const string FixtureViaTemplate = "FIXTURE par {0}";
    private const string GatewayId = "2c1e5f0a-6c0b-4d7f-9c3e-4b2f8d1a7e55";
    private const string GatewayHost = "gw.example.test";
    private const string GatewayName = "Bastion";
    private const string ServerId = "server-1";
    private const int RegisteredTunnelPort = 50140;

    private readonly string _keyFilePath;
    private readonly string _fixtureLocalesPath;

    public TunnelFailureLocalizationTests()
    {
        _keyFilePath = Path.Combine(Path.GetTempPath(), $"heimdall-gateway-{Guid.NewGuid():N}.pem");
        File.WriteAllText(_keyFilePath, "not parsed: the SSH client factory is replaced in these tests");

        _fixtureLocalesPath = Path.Combine(Path.GetTempPath(), $"heimdall-locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixtureLocalesPath);
        File.WriteAllText(
            Path.Combine(_fixtureLocalesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["SessionGatewayBadgeVia"] = FixtureViaTemplate
            }));
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_keyFilePath);
            Directory.Delete(_fixtureLocalesPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ManualTunnel_CancelledByTheManager_ReportsTheCatalogueSentence()
    {
        LocalizationManager localizer = await LoadShippedLocalizerAsync("fr");
        AppSettings settings = CreateSettings(_keyFilePath);
        using TunnelManager tunnelManager = CreateManagerThatThrows(new OperationCanceledException());
        using TunnelsViewModel viewModel = CreateViewModel(localizer, settings, tunnelManager, new ConnectionStateMachine());

        TunnelResult result = await viewModel.OpenManualTunnelAsync(
            CreateManualTunnelDialog(settings),
            settings,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.Cancelled, result.FailureCode);
        Assert.Equal(localizer[TunnelMessageKeys.MessageKeyEstablishmentCancelled], result.ErrorMessage);
    }

    [Fact]
    public async Task ProfileTunnel_CancelledByTheManager_ReportsTheCatalogueSentence()
    {
        LocalizationManager localizer = await LoadShippedLocalizerAsync("fr");
        AppSettings settings = CreateSettings(_keyFilePath);
        using TunnelManager tunnelManager = CreateManagerThatThrows(new OperationCanceledException());
        ConnectionStateMachine stateMachine = new ConnectionStateMachine();
        TunnelService service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            stateMachine,
            localizer,
            RejectingHostKeyVerifier.Instance);
        ServerProfileDto server = new ServerProfileDto
        {
            Id = ServerId,
            RemoteServer = "target.example.test",
            RemotePort = 22,
            SshGatewayId = GatewayId,
            UseDirectConnection = false
        };

        TunnelSetupOutcome outcome = await service.SetupTunnelIfNeededAsync(
            server,
            22,
            settings,
            CancellationToken.None);

        string expected = localizer[TunnelMessageKeys.MessageKeyEstablishmentCancelled];
        Assert.False(outcome.Success);
        Assert.Equal(expected, outcome.ErrorMessage);
        Assert.Equal(expected, stateMachine.GetStateData(ServerId)?.ErrorMessage);
    }

    [Fact]
    public async Task ManualTunnel_KeyFileMissing_ReportsTheKeyFileSentenceWithThePath()
    {
        LocalizationManager localizer = await LoadShippedLocalizerAsync("fr");
        string missingKeyPath = Path.Combine(Path.GetTempPath(), $"heimdall-missing-{Guid.NewGuid():N}.pem");
        AppSettings settings = CreateSettings(missingKeyPath);
        using TunnelManager tunnelManager = CreateManagerThatThrows(new InvalidOperationException("must not dial"));
        using TunnelsViewModel viewModel = CreateViewModel(localizer, settings, tunnelManager, new ConnectionStateMachine());

        TunnelResult result = await viewModel.OpenManualTunnelAsync(
            CreateManualTunnelDialog(settings),
            settings,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.KeyFileNotFound, result.FailureCode);
        Assert.Equal(
            localizer.Format(SshLocalizationKeys.ErrorSshKeyFileNotFound, missingKeyPath),
            result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveRoute_UsesTheCatalogueViaTemplate()
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(_fixtureLocalesPath, "en");
        AppSettings settings = CreateSettings(_keyFilePath);
        ConnectionStateMachine stateMachine = new ConnectionStateMachine();
        using TunnelManager tunnelManager = new TunnelManager();
        using TunnelsViewModel viewModel = CreateViewModel(localizer, settings, tunnelManager, stateMachine);
        stateMachine.SetTunnelInfo(ServerId, RegisteredTunnelPort, processId: 0);
        TunnelInfo info = new TunnelInfo(GatewayHost, RegisteredTunnelPort, "target.example.test", 3389, DateTime.UtcNow, true);
        Assert.True(tunnelManager.TryRegisterExternalTunnel(info, new NoOpDisposable(), () => true));

        string route = viewModel.ResolveRoute(ServerId);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, FixtureViaTemplate, GatewayName), route);
    }

    private static async Task<LocalizationManager> LoadShippedLocalizerAsync(string language)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);
        return localizer;
    }

    private static AppSettings CreateSettings(string keyFilePath)
    {
        SshGatewayDto gateway = new SshGatewayDto
        {
            Id = GatewayId,
            Name = GatewayName,
            Host = GatewayHost,
            Port = 22,
            User = "ssh-user",
            KeyPath = keyFilePath
        };
        return new AppSettings { SshGateways = [gateway] };
    }

    private static TunnelsViewModel CreateViewModel(
        LocalizationManager localizer,
        AppSettings settings,
        TunnelManager tunnelManager,
        ConnectionStateMachine stateMachine)
    {
        return new TunnelsViewModel(
            new TestTunnelsHost(settings),
            localizer,
            tunnelManager,
            stateMachine,
            new HostKeyStore(),
            RejectingHostKeyVerifier.Instance,
            new InMemoryConfigManager());
    }

    private static NewTunnelDialogViewModel CreateManualTunnelDialog(AppSettings settings)
    {
        return new NewTunnelDialogViewModel(
            settings.SshGateways,
            new LocalizationManager(),
            new HashSet<int>())
        {
            SelectedGateway = settings.SshGateways[0],
            RemoteHost = "target.example.test",
            RemotePort = 3389,
            LocalPort = 0
        };
    }

    /// <summary>
    /// A real manager whose dial throws the given exception, so the failure is classified
    /// by the manager itself, the way it is in production.
    /// </summary>
    private static TunnelManager CreateManagerThatThrows(Exception failure)
    {
        return new TunnelManager(
            static (connectionParams, verificationHost, verificationPort, hostKeyStore, verifier, cancellationToken) =>
                Task.FromResult(new PinnedFingerprintVerifier(verificationHost, verificationPort, "SHA256:pinned")),
            static connectionParams => new SshClient(new ConnectionInfo(
                connectionParams.Host,
                connectionParams.Port,
                connectionParams.Username,
                new NoneAuthenticationMethod(connectionParams.Username))),
            (client, verificationHost, verificationPort, pinnedVerifier, cancellationToken, cancelLogMessage) =>
                throw failure);
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

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
