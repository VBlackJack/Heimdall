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
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogOriginPreservationTests
{
    [Fact]
    public void ServerDialogViewModel_SaveExistingProfile_PreservesOrigin()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Imported",
            RemoteServer = "prod.example.com",
            ConnectionType = "SSH",
            Origin = ProfileOrigin.ImportPutty
        });
        vm.DisplayName = "Renamed";

        var dto = vm.ToDto();

        Assert.Equal(ProfileOrigin.ImportPutty, dto.Origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_WhenVaultEntryNameEmpty_FreezesOldDisplayNameAsCredentialTarget(
        string? vaultEntryName)
    {
        ServerProfileDto storedProfile = CreateProfile("Prod SSH", vaultEntryName);
        ServerProfileDto editedProfile = CreateProfile("Production SSH", vaultEntryName);
        await using var fixture = await ServerListFixture.CreateAsync(editedProfile, storedProfile);
        fixture.ViewModel.LoadServers(
            await fixture.ConfigManager.LoadServersAsync(),
            new AppSettings());

        bool edited = await fixture.ViewModel.EditServerByIdAsync(
            storedProfile.Id,
            CancellationToken.None);

        Assert.True(edited);
        ServerProfileDto persisted = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("Production SSH", persisted.DisplayName);
        Assert.Equal("Prod SSH", persisted.VaultEntryName);
    }

    [Fact]
    public async Task Rename_WhenVaultEntryNameSet_LeavesReferenceUntouched()
    {
        const string credentialReference = "Explicit Credential Reference";
        ServerProfileDto storedProfile = CreateProfile("Prod SSH", credentialReference);
        ServerProfileDto editedProfile = CreateProfile("Production SSH", credentialReference);
        await using var fixture = await ServerListFixture.CreateAsync(editedProfile, storedProfile);
        fixture.ViewModel.LoadServers(
            await fixture.ConfigManager.LoadServersAsync(),
            new AppSettings());

        bool edited = await fixture.ViewModel.EditServerByIdAsync(
            storedProfile.Id,
            CancellationToken.None);

        Assert.True(edited);
        ServerProfileDto persisted = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("Production SSH", persisted.DisplayName);
        Assert.Equal(credentialReference, persisted.VaultEntryName);
    }

    [Fact]
    public void ServerDialogViewModel_ToDto_MarksExecutionConfirmed()
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "Local Shell",
            RemoteServer = "localhost",
            ConnectionType = "LOCAL",
            LocalShellExecutable = "pwsh.exe"
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.True(dto.ExecutionConfirmed);
    }

    [Fact]
    public async Task ServerListViewModel_AddServer_SetsOriginToManual()
    {
        await using var fixture = await ServerListFixture.CreateAsync(new ServerProfileDto
        {
            DisplayName = "Imported via dialog",
            RemoteServer = "manual.example.com",
            ConnectionType = "SSH",
            Origin = ProfileOrigin.ImportPutty
        });

        await fixture.ViewModel.AddServerCommand.ExecuteAsync(null);

        var server = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal(ProfileOrigin.Manual, server.Origin);
    }

    [Fact]
    public async Task ServerListViewModel_OnConnectionStateChanged_PostsViaDispatcher()
    {
        await using var fixture = await ServerListFixture.CreateAsync(new ServerProfileDto
        {
            DisplayName = "Imported via dialog",
            RemoteServer = "manual.example.com",
            ConnectionType = "SSH",
            Origin = ProfileOrigin.ImportPutty
        });

        fixture.ViewModel.LoadServers(
            [
                new ServerProfileDto
                {
                    Id = "alpha",
                    DisplayName = "Alpha",
                    RemoteServer = "alpha.example.com",
                    ConnectionType = "SSH"
                }
            ],
            new AppSettings());

        var transitioned = fixture.StateMachine.TryTransition("alpha", ConnectionState.Initializing);

        Assert.True(transitioned);
        Assert.Equal(1, fixture.Dispatcher.InvokeAsyncCalls);
        Assert.Equal(ConnectionState.Initializing.ToString(), Assert.Single(fixture.ViewModel.Servers).ConnectionState);
    }

    [Fact]
    public async Task ServerListViewModel_StaleOrDuplicateRevisionCannotOverwriteNewerAggregateState()
    {
        await using var fixture = await ServerListFixture.CreateAsync(new ServerProfileDto
        {
            DisplayName = "Imported via dialog",
            RemoteServer = "manual.example.com",
            ConnectionType = "SSH",
            Origin = ProfileOrigin.ImportPutty
        });
        fixture.ViewModel.LoadServers(
            [
                new ServerProfileDto
                {
                    Id = "alpha",
                    DisplayName = "Alpha",
                    RemoteServer = "alpha.example.com",
                    ConnectionType = "SSH"
                }
            ],
            new AppSettings());
        List<Action> queuedUpdates = [];
        fixture.Dispatcher.InvokeAsyncActionHandler = queuedUpdates.Add;
        string firstSessionId = SessionIdCodec.Create("alpha");
        string secondSessionId = SessionIdCodec.Create("alpha");

        Assert.True(fixture.StateMachine.TryTransition(firstSessionId, ConnectionState.Initializing));
        Assert.True(fixture.StateMachine.TryTransition(firstSessionId, ConnectionState.ValidatingConfig));
        Assert.True(fixture.StateMachine.TryTransition(firstSessionId, ConnectionState.LaunchingSsh));
        Assert.True(fixture.StateMachine.TryTransition(firstSessionId, ConnectionState.Connected));
        Assert.True(fixture.StateMachine.TryTransition(secondSessionId, ConnectionState.Initializing));
        Assert.True(fixture.StateMachine.TryTransition(firstSessionId, ConnectionState.Disconnected));
        Assert.Equal(6, queuedUpdates.Count);

        queuedUpdates[5]();
        queuedUpdates[4]();
        queuedUpdates[5]();
        queuedUpdates[3]();

        Assert.Equal(
            ConnectionState.Initializing.ToString(),
            Assert.Single(fixture.ViewModel.Servers).ConnectionState);
        Assert.Equal(1, fixture.ViewModel.ActiveSessionAggregationEntryCount);
    }

    private sealed class ServerListFixture : IAsyncDisposable
    {
        private ServerListFixture(
            string rootPath,
            ConfigManager configManager,
            ServerListViewModel viewModel,
            ConnectionStateMachine stateMachine,
            FakeUiDispatcher dispatcher)
        {
            RootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            StateMachine = stateMachine;
            Dispatcher = dispatcher;
        }

        public string RootPath { get; }

        public ConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public ConnectionStateMachine StateMachine { get; }

        public FakeUiDispatcher Dispatcher { get; }

        public static async Task<ServerListFixture> CreateAsync(
            ServerProfileDto dialogServer,
            params ServerProfileDto[] storedProfiles)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "heimdall-b63-serverlist", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            if (storedProfiles.Length > 0)
            {
                await configManager.SaveServersAsync([.. storedProfiles]);
            }

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                configManager,
                localizer,
                new NullTunnelService(),
                Array.Empty<IProtocolHandler>());
            var dialogService = new DialogServiceStub(dialogServer);
            var puttyImporter = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager);
            var knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            var uiDispatcher = new FakeUiDispatcher();
            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialogService,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter);

            return new ServerListFixture(rootPath, configManager, viewModel, stateMachine, uiDispatcher);
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();

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

    private static ServerProfileDto CreateProfile(
        string displayName,
        string? vaultEntryName)
    {
        return new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = displayName,
            RemoteServer = "prod.example.com",
            ConnectionType = "SSH",
            VaultEntryName = vaultEntryName
        };
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult((true, false, server.RemoteServer, remotePort, (string?)null));
        }

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

    private sealed class DialogServiceStub(ServerProfileDto dialogServer) : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") => Task.FromResult(false);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(new ServerDialogResult(dialogServer, true));

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
