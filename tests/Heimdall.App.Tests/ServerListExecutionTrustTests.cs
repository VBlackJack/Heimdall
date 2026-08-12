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

public sealed class ServerListExecutionTrustTests
{
    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_Confirmed_PersistsExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto storedProfile = CreateLocalShellProfile("local-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.True(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal("warning", fixture.DialogService.LastSeverity);
        Assert.Contains("evil.exe", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_Declined_DoesNotPersistExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto storedProfile = CreateLocalShellProfile("local-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.False(result);
        Assert.False(promptProfile.ExecutionConfirmed);
        Assert.False(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_GeneratedSessionId_PersistsInventoryProfile()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto storedProfile = CreateLocalShellProfile("local-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1_abcdef12");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.True(stored.ExecutionConfirmed);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_Confirmed_PersistsExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto storedProfile = CreatePostConnectProfile("ssh-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 2);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.True(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal("warning", fixture.DialogService.LastSeverity);
        Assert.Contains("2", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_Declined_DoesNotPersistExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto storedProfile = CreatePostConnectProfile("ssh-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 2);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.False(result);
        Assert.False(promptProfile.ExecutionConfirmed);
        Assert.False(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
    }

    private static ServerProfileDto CreateLocalShellProfile(string id)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = "Imported Local",
            RemoteServer = "localhost",
            ConnectionType = "LOCAL",
            LocalShellExecutable = "evil.exe"
        };
    }

    private static ServerProfileDto CreatePostConnectProfile(string id)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = "Imported SSH",
            RemoteServer = "ssh.example.com",
            ConnectionType = "SSH",
            PostConnectSteps =
            [
                new PostConnectStep { Enabled = true, Input = "whoami" },
                new PostConnectStep { Enabled = true, CommandLibraryId = "tail-log" }
            ]
        };
    }

    private sealed class ServerListExecutionTrustFixture : IAsyncDisposable
    {
        private ServerListExecutionTrustFixture(
            string rootPath,
            ConfigManager configManager,
            ServerListViewModel viewModel,
            TrackingDialogService dialogService,
            ConnectionService connectionService)
        {
            RootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            DialogService = dialogService;
            ConnectionService = connectionService;
        }

        public string RootPath { get; }

        public ConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public TrackingDialogService DialogService { get; }

        public ConnectionService ConnectionService { get; }

        public static async Task<ServerListExecutionTrustFixture> CreateAsync(bool confirmResult)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall-execution-trust",
                Guid.NewGuid().ToString("N"));
            ConfigManager configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            LocalizationManager localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            ConnectionStateMachine stateMachine = new ConnectionStateMachine();
            ConnectionService connectionService = new ConnectionService(
                configManager,
                localizer,
                new NullTunnelService(),
                Array.Empty<IProtocolHandler>());
            TrackingDialogService dialogService = new TrackingDialogService(confirmResult);
            PuttySessionImporter puttyImporter = new PuttySessionImporter(
                new FakePuttySessionRegistrySource([]),
                configManager);
            KnownHostsImporter knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            FakeUiDispatcher uiDispatcher = new FakeUiDispatcher();
            ServerListViewModel viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialogService,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter);

            return new ServerListExecutionTrustFixture(rootPath, configManager, viewModel, dialogService, connectionService);
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            ConnectionService.Dispose();

            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
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
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

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

    private sealed class TrackingDialogService(bool confirmResult) : IDialogService
    {
        public string LastConfirmTitle { get; private set; } = string.Empty;

        public string LastConfirmMessage { get; private set; } = string.Empty;

        public string LastSeverity { get; private set; } = string.Empty;

        public int ConfirmCallCount { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            LastSeverity = severity;
            ConfirmCallCount++;
            return Task.FromResult(confirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

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

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

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
