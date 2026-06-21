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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies the Windows Hello gate also guards the bulk-connect path
/// (<see cref="ServerListViewModel.ConnectServersBulkCoreAsync(System.Collections.Generic.IReadOnlyList{ServerItemViewModel}, System.Threading.CancellationToken)"/>):
/// fail-closed before any credential is resolved, and a single prompt per batch.
/// </summary>
public sealed class ServerListBulkWindowsHelloGateTests
{
    [Fact]
    public async Task BulkConnect_HelloVerificationFails_NoConnectionAttempted()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = false };
        var handler = new CountingProtocolHandler("SSH");
        await using var fixture = await Fixture.CreateAsync(hello, handler, requireHello: true);

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(), CancellationToken.None);

        Assert.Empty(handler.ConnectedServerIds);
        Assert.Equal(1, hello.VerifyCalls);
    }

    [Fact]
    public async Task BulkConnect_HelloUnavailable_NoConnectionAttempted()
    {
        var hello = new FakeWindowsHelloService { Available = false };
        var handler = new CountingProtocolHandler("SSH");
        await using var fixture = await Fixture.CreateAsync(hello, handler, requireHello: true);

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(), CancellationToken.None);

        Assert.Empty(handler.ConnectedServerIds);
        Assert.Equal(1, hello.IsAvailableCalls);
        Assert.Equal(0, hello.VerifyCalls);
    }

    [Fact]
    public async Task BulkConnect_HelloVerified_ProceedsAndVerifiesOncePerBatch()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = true };
        var handler = new CountingProtocolHandler("SSH");
        await using var fixture = await Fixture.CreateAsync(hello, handler, requireHello: true);

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(), CancellationToken.None);

        // All three servers connected, but Hello was prompted exactly once for the batch.
        Assert.Equal(3, handler.ConnectedServerIds.Count);
        Assert.Equal(1, hello.VerifyCalls);
    }

    [Fact]
    public async Task BulkConnect_SettingOff_ServiceNeverCalled()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = true };
        var handler = new CountingProtocolHandler("SSH");
        await using var fixture = await Fixture.CreateAsync(hello, handler, requireHello: false);

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(), CancellationToken.None);

        Assert.Equal(3, handler.ConnectedServerIds.Count);
        Assert.Equal(0, hello.IsAvailableCalls);
        Assert.Equal(0, hello.VerifyCalls);
    }

    private sealed class FakeWindowsHelloService : IWindowsHelloService
    {
        public bool Available { get; init; } = true;

        public bool VerifyResult { get; init; } = true;

        public int IsAvailableCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public Task<bool> IsAvailableAsync()
        {
            IsAvailableCalls++;
            return Task.FromResult(Available);
        }

        public Task<bool> VerifyAsync(string message, CancellationToken ct)
        {
            VerifyCalls++;
            return Task.FromResult(VerifyResult);
        }
    }

    private sealed class CountingProtocolHandler(string protocol) : IProtocolHandler
    {
        public string Protocol { get; } = protocol;

        public List<string> ConnectedServerIds { get; } = [];

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            ConnectedServerIds.Add(server.Id);
            return Task.FromResult(new ConnectionResult(true, null, null));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _rootPath;

        private Fixture(string rootPath, ServerListViewModel viewModel)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
        }

        public ServerListViewModel ViewModel { get; }

        public static async Task<Fixture> CreateAsync(
            IWindowsHelloService helloService,
            IProtocolHandler protocolHandler,
            bool requireHello)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(), "heimdall-bulk-hello-gate", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                configManager, localizer, new NullTunnelService(), [protocolHandler]);
            var dialog = new NullDialogService();
            var puttyImporter = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager);
            var knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            var uiDispatcher = new FakeUiDispatcher();

            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialog,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter,
                windowsHelloService: helloService);

            var settings = new AppSettings
            {
                RequireWindowsHelloOnConnect = requireHello,
                WindowsHelloGraceMinutes = 0
            };
            var servers = new List<ServerProfileDto>
            {
                CreateSshServer("alpha"),
                CreateSshServer("beta"),
                CreateSshServer("gamma")
            };
            await configManager.SaveSettingsAsync(settings);
            await configManager.SaveServersAsync(servers);
            viewModel.LoadServers(servers, settings);

            return new Fixture(rootPath, viewModel);
        }

        private static ServerProfileDto CreateSshServer(string id) => new()
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.com",
            ConnectionType = "SSH",
            SshPort = 22,
            SshUsername = "user",
            Origin = ProfileOrigin.Manual
        };

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();

            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult((true, false, server.RemoteServer, remotePort, (string?)null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NullRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            Task.FromResult(new RdpImportPreview
            {
                Entries = [],
                FilesNotFound = [],
                FilesUnreadable = []
            });

        public Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct) =>
            Task.FromResult(new RdpImportResult());
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") => Task.FromResult(true);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null) => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
