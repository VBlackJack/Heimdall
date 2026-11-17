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
/// Answering "do not run" to the trust question is a choice, not a failure: no error state, no
/// modal, one plain status line, and a skip rather than a failure in a batch.
/// </summary>
public sealed class ExecutionTrustDeclineTests
{
    private const string LocalProfileId = "local-1";

    [Fact]
    public async Task ConnectionService_Declined_ReportsDeclined()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false);
        fixture.ConnectionService.ConfirmExecution = _ => Task.FromResult(false);

        ConnectionResult result = await fixture.ConnectionService.ConnectLocalShellAsync(
            CreateLocalProfile(LocalProfileId), new AppSettings());

        Assert.False(result.Success);
        Assert.True(result.Declined);
        Assert.Equal(0, fixture.Handler.CallCount);
        Assert.Equal(fixture.Localizer["StatusExecutionBlockedUnconfirmed"], result.ErrorMessage);
    }

    [Fact]
    public async Task ConnectionService_NoDelegate_FailsClosedWithoutDeclined()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false, wireConfirm: false);

        ConnectionResult result = await fixture.ConnectionService.ConnectLocalShellAsync(
            CreateLocalProfile(LocalProfileId), new AppSettings());

        Assert.False(result.Success);
        Assert.False(result.Declined);
        Assert.Equal(0, fixture.Handler.CallCount);
    }

    [Fact]
    public async Task Connect_Declined_ShowsNoErrorAndOneStatusLine_AndKeepsTheRowState()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false);
        await fixture.LoadAsync(CreateLocalProfile(LocalProfileId));
        ServerItemViewModel row = Assert.Single(fixture.ViewModel.Servers);
        string stateBefore = row.ConnectionState;

        await fixture.ViewModel.ConnectCommand.ExecuteAsync(row);

        Assert.Equal(0, fixture.DialogService.ErrorCallCount);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal(0, fixture.Handler.CallCount);
        string status = Assert.Single(fixture.StatusMessages);
        Assert.Equal(fixture.Localizer.Format("StatusExecutionDeclined", "Imported Local"), status);
        Assert.NotEqual("Error", row.ConnectionState);
        Assert.Equal(stateBefore, row.ConnectionState);
    }

    [Fact]
    public async Task BulkConnect_Declined_CountsAsSkippedNotFailed()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false);
        await fixture.LoadAsync(CreateLocalProfile(LocalProfileId));

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(),
            CancellationToken.None);

        Assert.Equal(0, fixture.DialogService.ErrorCallCount);
        Assert.Equal(
            "Connected 0, failed 0, skipped 1. Skipped: 1 (cancelled).",
            fixture.StatusMessages[^1]);
        Assert.NotEqual("Error", Assert.Single(fixture.ViewModel.Servers).ConnectionState);
    }

    [Fact]
    public async Task Connect_WebCommandTooLong_ShowsExactlyOneModalAndNoDeclineStatus()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: true, handlerProtocol: "WEB");
        ServerProfileDto web = new()
        {
            Id = "web-1",
            DisplayName = "Imported Web",
            ConnectionType = "WEB",
            RemoteServer = "intranet",
            WebOpenExternally = true,
            WebExternalProgram = "browser.exe",
            WebExternalArguments = new string('a', WebExternalLaunch.MaxDescribedArgumentsLength + 1),
        };
        await fixture.LoadAsync(web);
        ServerItemViewModel row = Assert.Single(fixture.ViewModel.Servers);

        await fixture.ViewModel.ConnectCommand.ExecuteAsync(row);

        Assert.Equal(1, fixture.DialogService.ErrorCallCount);
        Assert.Equal(0, fixture.DialogService.ConfirmCallCount);
        Assert.DoesNotContain(
            fixture.StatusMessages,
            message => message.Contains("you chose", StringComparison.Ordinal));
        Assert.NotEqual("Error", row.ConnectionState);
    }

    [Fact]
    public async Task BulkConnect_WebCommandTooLong_CountsAsFailedNotSkipped()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: true, handlerProtocol: "WEB");
        await fixture.LoadAsync(CreateTooLongWebProfile());

        await fixture.ViewModel.ConnectServersBulkCoreAsync(
            fixture.ViewModel.Servers.ToList(),
            CancellationToken.None);

        Assert.Equal(1, fixture.DialogService.ErrorCallCount);
        Assert.Equal(
            "Connected 0, failed 1, skipped 0.",
            fixture.StatusMessages[^1]);
    }

    [Fact]
    public async Task Connect_DeclinedProfileNameWithLineBreakAndOverride_IsShownEscaped()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false);
        ServerProfileDto profile = CreateLocalProfile(LocalProfileId);
        profile.DisplayName = "Evil\nName‮txt";
        await fixture.LoadAsync(profile);
        ServerItemViewModel row = Assert.Single(fixture.ViewModel.Servers);

        await fixture.ViewModel.ConnectCommand.ExecuteAsync(row);

        string status = Assert.Single(fixture.StatusMessages);
        Assert.DoesNotContain('\n', status);
        Assert.DoesNotContain('‮', status);
        Assert.Equal(
            fixture.Localizer.Format("StatusExecutionDeclined", ExecutionTrustDescriber.Visible(profile.DisplayName)),
            status);
    }

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("CITRIX")]
    [InlineData("TELNET")]
    public async Task UnconfirmedLookup_ProtocolWithNoLookup_RaisesNoNoteAndNeedsNoConfirmation(string connectionType)
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false);
        ServerProfileDto profile = CreateImportedProfile(connectionType);

        bool resolved = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            profile,
            new AppSettings { UseExternalCredentialProvider = true },
            CancellationToken.None,
            skipOnFailure: false);

        Assert.False(resolved);
        Assert.Empty(fixture.StatusMessages);
        Assert.False(ServerItemViewModel.FromDto(profile).NeedsCredentialLookupConfirmation);
    }

    [Fact]
    public async Task UnconfirmedLookup_ImportedSsh_StillRaisesTheNoteAndNeedsConfirmation()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: false);
        ServerProfileDto profile = CreateImportedProfile("SSH");

        bool resolved = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            profile,
            new AppSettings { UseExternalCredentialProvider = true },
            CancellationToken.None,
            skipOnFailure: false);

        Assert.False(resolved);
        Assert.Equal(
            fixture.Localizer.Format("StatusCredentialLookupUnconfirmed", profile.DisplayName),
            Assert.Single(fixture.StatusMessages));
        Assert.True(ServerItemViewModel.FromDto(profile).NeedsCredentialLookupConfirmation);
    }

    [Fact]
    public async Task Import_ProviderOn_UnconfirmedLookupCountLeavesOutProtocolsWithNoLookup()
    {
        await using DeclineFixture fixture = await DeclineFixture.CreateAsync(confirmResult: true);
        await fixture.ConfigManager.SaveSettingsAsync(new AppSettings { UseExternalCredentialProvider = true });
        ProfileImportService service = new(
            fixture.ConfigManager,
            fixture.Localizer,
            fixture.DialogService,
            new RdpImportService(fixture.ConfigManager, fixture.Localizer));
        string path = Path.Combine(fixture.RootPath, "mixed.json");
        await File.WriteAllTextAsync(
            path,
            """
            [
              { "id": "a", "displayName": "prod-db", "connectionType": "SSH", "remoteServer": "elsewhere.example.com", "sshPort": 22 },
              { "id": "b", "displayName": "shell", "connectionType": "LOCAL", "remoteServer": "localhost" }
            ]
            """);

        ProfileImportResult result = await service.ImportFromPathAsync(path, CancellationToken.None);

        Assert.True(result.ImportedCount == 2, result.UserMessage);
        Assert.Equal(1, result.UnconfirmedLookupCount);
    }

    private static ServerProfileDto CreateImportedProfile(string connectionType) => new()
    {
        Id = "imported-1",
        DisplayName = "Imported One",
        ConnectionType = connectionType,
        RemoteServer = "elsewhere.example.com",
        VaultEntryName = "vault-entry",
        CredentialLookupConfirmed = false,
    };

    private static ServerProfileDto CreateTooLongWebProfile() => new()
    {
        Id = "web-1",
        DisplayName = "Imported Web",
        ConnectionType = "WEB",
        RemoteServer = "intranet",
        WebOpenExternally = true,
        WebExternalProgram = "browser.exe",
        WebExternalArguments = new string('a', WebExternalLaunch.MaxDescribedArgumentsLength + 1),
    };

    private static ServerProfileDto CreateLocalProfile(string id) => new()
    {
        Id = id,
        DisplayName = "Imported Local",
        ConnectionType = "LOCAL",
        RemoteServer = "localhost",
        LocalShellExecutable = "evil.exe",
    };

    private sealed class DeclineFixture : IAsyncDisposable
    {
        private readonly string _rootPath;

        private DeclineFixture(
            string rootPath,
            ConfigManager configManager,
            LocalizationManager localizer,
            ConnectionService connectionService,
            CountingProtocolHandler handler,
            ServerListViewModel viewModel,
            TrackingDialogService dialogService)
        {
            _rootPath = rootPath;
            ConfigManager = configManager;
            Localizer = localizer;
            ConnectionService = connectionService;
            Handler = handler;
            ViewModel = viewModel;
            DialogService = dialogService;
            viewModel.StatusMessageRequested += StatusMessages.Add;
        }

        public string RootPath => _rootPath;

        public ConfigManager ConfigManager { get; }

        public LocalizationManager Localizer { get; }

        public ConnectionService ConnectionService { get; }

        public CountingProtocolHandler Handler { get; }

        public ServerListViewModel ViewModel { get; }

        public TrackingDialogService DialogService { get; }

        public List<string> StatusMessages { get; } = [];

        public static async Task<DeclineFixture> CreateAsync(
            bool confirmResult,
            bool wireConfirm = true,
            string handlerProtocol = "LOCAL")
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall-execution-decline",
                Guid.NewGuid().ToString("N"));
            ConfigManager configManager = new(rootPath);
            await configManager.InitializeAsync();

            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            CountingProtocolHandler handler = new(handlerProtocol);
            ConnectionService connectionService = new(
                configManager,
                localizer,
                new NullTunnelService(),
                new IProtocolHandler[] { handler });
            TrackingDialogService dialogService = new(confirmResult);
            ServerListViewModel viewModel = new(
                configManager,
                localizer,
                new FakeUiDispatcher(),
                new ConnectionStateMachine(),
                connectionService,
                dialogService,
                new NullRdpImportService(),
                new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager),
                new KnownHostsImporter(configManager, new HostKeyStore()));

            if (wireConfirm)
            {
                // The same wiring the session coordinator performs for the application.
                connectionService.ConfirmExecution = profile => viewModel.ConfirmAndTrustExecutionAsync(profile);
            }

            return new DeclineFixture(
                rootPath, configManager, localizer, connectionService, handler, viewModel, dialogService);
        }

        public async Task LoadAsync(ServerProfileDto profile)
        {
            AppSettings settings = new();
            await ConfigManager.SaveSettingsAsync(settings);
            await ConfigManager.SaveServersAsync(new List<ServerProfileDto> { profile });
            ViewModel.LoadServers(new List<ServerProfileDto> { profile }, settings);
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            ConnectionService.Dispose();

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

    private sealed class CountingProtocolHandler(string protocol) : IProtocolHandler
    {
        public string Protocol { get; } = protocol;

        public int CallCount { get; private set; }

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            CallCount++;
            return Task.FromResult(new ConnectionResult(true, null, null));
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

        public int LabeledConfirmCallCount { get; private set; }

        public string LastConfirmLabel { get; private set; } = string.Empty;

        public string LastDeclineLabel { get; private set; } = string.Empty;

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            LastSeverity = severity;
            ConfirmCallCount++;
            return Task.FromResult(confirmResult);
        }

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string severity,
            string confirmLabel,
            string declineLabel)
        {
            LabeledConfirmCallCount++;
            LastConfirmLabel = confirmLabel;
            LastDeclineLabel = declineLabel;
            return ShowConfirmAsync(title, message, severity);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => Task.FromResult<RdpImportSelection?>(new RdpImportSelection
            {
                Entries =
                [
                    .. viewModel.Rows.Select(row => new RdpImportSelectionEntry
                    {
                        SourceFilePath = row.SourceFilePath,
                        IsSelected = !row.HasParseError,
                        ConflictResolution = RdpConflictResolution.Skip
                    })
                ]
            });

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

        public int ErrorCallCount { get; private set; }

        public void ShowError(string title, string message)
        {
            ErrorCallCount++;
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
