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

using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using TwinShell.Core.Interfaces;
using AppDialogService = Heimdall.App.Services.IDialogService;

namespace Heimdall.App.Tests;

public sealed class CommandLibraryViewModelSyncTests
{
    [Fact]
    public async Task CancelSyncCommand_WhenSyncing_CallsGitSyncCancel()
    {
        var fixture = await CreateFixtureAsync();

        Assert.False(fixture.ViewModel.CancelSyncCommand.CanExecute(null));

        fixture.ViewModel.IsSyncing = true;

        Assert.True(fixture.ViewModel.CancelSyncCommand.CanExecute(null));

        fixture.ViewModel.CancelSyncCommand.Execute(null);

        Assert.True(fixture.GitSync.CancelOperationCalled);
        Assert.Equal("Cancelling sync...", fixture.ViewModel.SyncStatusMessage);
    }

    [Fact]
    public async Task SyncAsync_WhenGitReturnsCancelled_ShowsCancelledInfoWithoutError()
    {
        var fixture = await CreateFixtureAsync(
            GitOperationResult.Fail(
                "Operation was cancelled.",
                GitSyncErrorCode.Cancelled,
                "Cancelled by test."));

        await fixture.ViewModel.SyncAsync();

        Assert.False(fixture.ViewModel.IsSyncing);
        Assert.Equal("Sync Cancelled", fixture.DialogService.LastInfoTitle);
        Assert.Equal("Operation was cancelled.", fixture.DialogService.LastInfoMessage);
        Assert.Null(fixture.DialogService.LastErrorTitle);
    }

    private static async Task<TestFixture> CreateFixtureAsync(GitOperationResult? syncResult = null)
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var serviceProvider = CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Tail log", "tail -f /tmp/app.log"));
        var configManager = new FakeConfigManager(new AppSettings
        {
            CmdLibGitSyncEnabled = true,
            CmdLibGitSyncUrl = "https://example.invalid/repo.git"
        });
        var dialogService = new RecordingDialogService();
        var gitSync = new FakeGitSyncService
        {
            FullSyncHandler = () => Task.FromResult(syncResult ?? GitOperationResult.Ok("Sync complete."))
        };
        var viewModel = new CommandLibraryViewModel(
            serviceProvider,
            configManager,
            localizer,
            dialogService,
            gitSync,
            new CommandLibraryTransferService());

        return new TestFixture(viewModel, dialogService, gitSync);
    }

    private sealed record TestFixture(
        CommandLibraryViewModel ViewModel,
        RecordingDialogService DialogService,
        FakeGitSyncService GitSync);

    private sealed class FakeGitSyncService : IGitSyncService
    {
        public bool IsConfigured => true;

        public bool IsOperationInProgress { get; private set; }

        public string StatusMessage { get; private set; } = string.Empty;

        public bool CancelOperationCalled { get; private set; }

        public Func<Task<GitOperationResult>> FullSyncHandler { get; init; } =
            () => Task.FromResult(GitOperationResult.Ok("Sync complete."));

        public event EventHandler<GitSyncStatusEventArgs>? StatusChanged;

        public Task<GitOperationResult> InitializeRepositoryAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> PullAndImportAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> ExportAndPushAsync(string? commitMessage = null)
            => Task.FromResult(GitOperationResult.Ok());

        public async Task<GitOperationResult> FullSyncAsync()
        {
            IsOperationInProgress = true;
            try
            {
                StatusChanged?.Invoke(this, new GitSyncStatusEventArgs
                {
                    Status = "Synchronizing...",
                    IsOperationInProgress = true,
                    Phase = SyncPhase.Fetching
                });
                return await FullSyncHandler();
            }
            finally
            {
                IsOperationInProgress = false;
            }
        }

        public Task<GitOperationResult> TestConnectionAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitRepositoryStatus> GetRepositoryStatusAsync()
            => Task.FromResult(new GitRepositoryStatus { IsInitialized = true });

        public void CancelOperation()
        {
            CancelOperationCalled = true;
        }
    }

    private sealed class FakeConfigManager : IConfigManager
    {
        public FakeConfigManager(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; private set; }

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://config/settings.json";

        public string ServersPath => "mem://config/servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            Settings = settings;
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
            => Task.FromResult(true);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries)
            => Task.FromResult(entries.Count());

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            mutate(Settings);
            SettingsChanged?.Invoke(Settings);
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync()
            => Task.FromResult(new List<ServerProfileDto>());

        public Task SaveServersAsync(List<ServerProfileDto> servers)
            => Task.CompletedTask;
    }

    private sealed class RecordingDialogService : AppDialogService
    {
        public string? LastErrorTitle { get; private set; }

        public string? LastErrorMessage { get; private set; }

        public string? LastInfoTitle { get; private set; }

        public string? LastInfoMessage { get; private set; }

        public string? LastWarningTitle { get; private set; }

        public string? LastWarningMessage { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(true);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult<bool?>(false);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult<string?>(defaultValue);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
            LastErrorTitle = title;
            LastErrorMessage = message;
        }

        public void ShowInfo(string title, string message)
        {
            LastInfoTitle = title;
            LastInfoMessage = message;
        }

        public void ShowWarning(string title, string message)
        {
            LastWarningTitle = title;
            LastWarningMessage = message;
        }
    }
}
