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
    public async Task ServerDialogViewModel_ExplicitDefaultSshPort_PersistsPresenceAndOverridesGroupDefault()
    {
        ServerDialogViewModel viewModel = new()
        {
            DisplayName = "Explicit SSH 22",
            RemoteServer = "ssh.example.com",
            ConnectionType = "SSH"
        };
        viewModel.SshPort = 2222;
        viewModel.SshPort = 22;
        ServerProfileDto dialogProfile = viewModel.ToDto();
        await using ServerListFixture fixture = await ServerListFixture.CreateAsync(dialogProfile);

        await fixture.ConfigManager.SaveServersAsync([dialogProfile]);

        string persistedJson = await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath);
        using JsonDocument document = JsonDocument.Parse(persistedJson);
        JsonElement persistedProfileJson = document.RootElement.GetProperty("servers")[0];
        Assert.True(persistedProfileJson.TryGetProperty("sshPort", out JsonElement persistedPort));
        Assert.Equal(22, persistedPort.GetInt32());

        ServerProfileDto persistedProfile = Assert.Single(
            await fixture.ConfigManager.LoadServersAsync());
        Assert.True(persistedProfile.HasSshPortField);

        GroupDefaultsDto groupDefaults = new() { SshPort = 2222 };
        groupDefaults.ApplyTo(persistedProfile);

        Assert.Equal(22, persistedProfile.SshPort);
    }

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

    [Theory]
    [InlineData("External")]
    [InlineData("Embedded")]
    public async Task ServerListViewModel_AddServer_UsesConfiguredSshDefaultMode(string configuredMode)
    {
        await using ServerListFixture fixture = await ServerListFixture.CreateAsync(new ServerProfileDto
        {
            DisplayName = "New SSH server",
            RemoteServer = "new.example.com",
            ConnectionType = "SSH"
        });
        await fixture.ConfigManager.MergeSettingAsync(settings => settings.SshDefaultMode = configuredMode);
        fixture.DialogService.ReturnSubmittedViewModel = true;

        await fixture.ViewModel.AddServerCommand.ExecuteAsync(null);

        Assert.Equal(configuredMode, Assert.IsType<ServerDialogViewModel>(fixture.DialogService.LastServerDialogViewModel).SshMode);
        ServerProfileDto persisted = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal(configuredMode, persisted.SshMode);
    }

    // Lot 2 of BL-0094, and it crosses the wiring rather than assuming it: the creation path
    // is attached where the dialog options are built, so a dialog opened by the shell must
    // come out able to create a gateway. Before this, the tab could only choose from a list
    // it had no way to fill.
    [Fact]
    public async Task ServerListViewModel_AddServer_NetworkTabCreatesAndSelectsAGateway()
    {
        await using ServerListFixture fixture = await ServerListFixture.CreateAsync(new ServerProfileDto
        {
            DisplayName = "New SSH server",
            RemoteServer = "new.example.com",
            ConnectionType = "SSH"
        });
        fixture.DialogService.GatewayDialogResultToReturn = new GatewayDialogResult(
            new SshGatewayDto
            {
                Name = "Bastion",
                Host = "bastion.example.test",
                Port = 22,
                User = "ops"
            },
            true);

        await fixture.ViewModel.AddServerCommand.ExecuteAsync(null);

        ServerDialogViewModel dialogVm = Assert.IsType<ServerDialogViewModel>(
            fixture.DialogService.LastServerDialogViewModel);
        dialogVm.ConnectionType = "SSH";
        Assert.Empty(dialogVm.AvailableGateways);
        Assert.True(dialogVm.HasNoGateway);
        Assert.True(dialogVm.CanCreateGateway);

        await dialogVm.CreateGatewayCommand.ExecuteAsync(null);

        GatewayOption offered = Assert.Single(dialogVm.AvailableGateways);
        Assert.Equal(offered.Id, dialogVm.SelectedGatewayId);
        Assert.False(dialogVm.HasNoGateway);

        // Persisted by the same path the Add menu uses, so it survives without anyone
        // opening the settings panel.
        SshGatewayDto persisted = Assert.Single(
            (await fixture.ConfigManager.LoadSettingsAsync()).SshGateways);
        Assert.Equal(offered.Id, persisted.Id);
        Assert.Equal("Bastion", persisted.Name);
    }

    // A gateway created from the Network tab is written to disk immediately, so the settings
    // snapshot taken to POPULATE the dialog is already out of date by the time the dialog
    // returns. Everything the list rebuilds afterwards - the gateway map, the project map, the
    // lookup collections - resolves against that snapshot, so the freshly chosen gateway
    // resolves to nothing and the row reports "gateway missing (<guid>)" over data that is
    // perfectly intact on disk. Reported from a live session on 2026-08-25.
    //
    // The sibling test above cannot catch this: it creates the gateway AFTER the add command
    // has finished, so no rebuild ever runs against a stale snapshot. Persisting correctly and
    // displaying correctly are two sides of a junction, and both were green while the junction
    // itself was broken.
    [Fact]
    public async Task ServerListViewModel_GatewayCreatedWhileTheDialogIsOpen_ResolvesOnTheSavedRow()
    {
        await using ServerListFixture fixture = await ServerListFixture.CreateAsync(new ServerProfileDto
        {
            DisplayName = "Server behind a bastion",
            RemoteServer = "target.example.com",
            ConnectionType = "SSH"
        });
        fixture.DialogService.ReturnSubmittedViewModel = true;
        fixture.DialogService.GatewayDialogResultToReturn = new GatewayDialogResult(
            new SshGatewayDto
            {
                Name = "Bastion",
                Host = "bastion.example.test",
                Port = 22,
                User = "ops"
            },
            true);

        // What the user does: opens the dialog, goes to the Network tab, creates a gateway
        // there, then saves the session - all without the dialog ever closing.
        fixture.DialogService.DuringServerDialogAsync = async dialogVm =>
        {
            dialogVm.DisplayName = "Server behind a bastion";
            dialogVm.RemoteServer = "target.example.com";
            dialogVm.ConnectionType = "SSH";
            await dialogVm.CreateGatewayCommand.ExecuteAsync(null);
        };

        await fixture.ViewModel.AddServerCommand.ExecuteAsync(null);

        SshGatewayDto persisted = Assert.Single(
            (await fixture.ConfigManager.LoadSettingsAsync()).SshGateways);
        Assert.Equal("Bastion", persisted.Name);

        ServerItemViewModel saved = Assert.Single(
            fixture.ViewModel.Servers,
            candidate => string.Equals(
                candidate.DisplayName, "Server behind a bastion", StringComparison.Ordinal));

        // The whole point: the row must resolve the gateway it was just given, not report it
        // as missing. Asserting the id alone would pass while the badge stayed red.
        Assert.False(saved.IsGatewayMissing);
        Assert.Contains("Bastion", saved.GatewayDetailText, StringComparison.Ordinal);

        // Renaming inline re-renders the row from _currentSettings, a field this view model
        // keeps for the session and never refreshes from SettingsChanged. Re-reading only into
        // a local would leave that field holding the pre-dialog snapshot, so the badge would
        // come back on the next F2 - correct on the path the fix was written for, broken one
        // keystroke away. The folder rename path and the bulk path already refresh the field;
        // this one read what nobody updated.
        ServerProfileDto renamed = Assert.Single(
            await fixture.ConfigManager.LoadServersAsync(),
            candidate => string.Equals(candidate.Id, saved.Id, StringComparison.Ordinal));
        renamed.DisplayName = "Renamed after the gateway existed";
        fixture.ViewModel.ApplyInlineServerRename(saved, renamed);

        Assert.False(saved.IsGatewayMissing);
        Assert.Contains("Bastion", saved.GatewayDetailText, StringComparison.Ordinal);
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
            FakeUiDispatcher dispatcher,
            DialogServiceStub dialogService)
        {
            RootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            StateMachine = stateMachine;
            Dispatcher = dispatcher;
            DialogService = dialogService;
        }

        public string RootPath { get; }

        public ConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public ConnectionStateMachine StateMachine { get; }

        public FakeUiDispatcher Dispatcher { get; }

        public DialogServiceStub DialogService { get; }

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

            return new ServerListFixture(rootPath, configManager, viewModel, stateMachine, uiDispatcher, dialogService);
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
        public ServerDialogViewModel? LastServerDialogViewModel { get; private set; }

        public bool ReturnSubmittedViewModel { get; set; }

        /// <summary>
        /// Runs while the server dialog is still open, before it reports its result.
        /// </summary>
        /// <remarks>
        /// The dialog stopped being read-only with respect to settings once its Network tab
        /// gained the ability to create a gateway. Without a hook here a test can only act
        /// before the dialog or after it, and the window that matters is the one in between -
        /// configuration changing underneath a snapshot the caller has already taken.
        /// </remarks>
        public Func<ServerDialogViewModel, Task>? DuringServerDialogAsync { get; set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") => Task.FromResult(false);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public async Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
        {
            LastServerDialogViewModel = editVm;

            if (DuringServerDialogAsync is not null && editVm is not null)
            {
                await DuringServerDialogAsync(editVm);
            }

            ServerProfileDto submittedServer = ReturnSubmittedViewModel && editVm is not null
                ? editVm.ToDto()
                : dialogServer;
            return new ServerDialogResult(submittedServer, true);
        }

        public GatewayDialogResult? GatewayDialogResultToReturn { get; set; }

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult(GatewayDialogResultToReturn);

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
