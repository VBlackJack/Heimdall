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

/// <summary>
/// Verifies that the "copied" visual feedback is gated on a successful
/// clipboard write: when <see cref="CommandLibraryViewModel.SetClipboardText"/>
/// reports failure (clipboard locked by another process), no feedback fires.
/// </summary>
public sealed class CommandLibraryViewModelClipboardTests
{
    [Fact]
    public async Task Copy_WhenClipboardWriteFails_DoesNotInvokeFeedback()
    {
        var viewModel = await CreateViewModelAsync();
        viewModel.GeneratedCommand = "tail -f /tmp/app.log";

        var feedback = new List<string>();
        viewModel.SetClipboardText = _ => false;
        viewModel.ShowCopyFeedback = feedback.Add;

        viewModel.Copy();

        Assert.Empty(feedback);
    }

    [Fact]
    public async Task Copy_WhenClipboardWriteSucceeds_InvokesFeedback()
    {
        var viewModel = await CreateViewModelAsync();
        viewModel.GeneratedCommand = "tail -f /tmp/app.log";

        var feedback = new List<string>();
        viewModel.SetClipboardText = _ => true;
        viewModel.ShowCopyFeedback = feedback.Add;

        viewModel.Copy();

        Assert.Equal(new[] { "copy" }, feedback);
    }

    [Fact]
    public async Task CopyHistoryEntry_WhenClipboardWriteFails_DoesNotShowFeedbackBanner()
    {
        var viewModel = await CreateViewModelAsync();

        viewModel.SetClipboardText = _ => false;

        viewModel.CopyHistoryEntry("tail -f /tmp/app.log");

        Assert.False(viewModel.IsHistoryCopyFeedbackVisible);
    }

    [Fact]
    public async Task CopyHistoryEntry_WhenClipboardWriteSucceeds_ShowsFeedbackBanner()
    {
        var viewModel = await CreateViewModelAsync();

        viewModel.SetClipboardText = _ => true;

        viewModel.CopyHistoryEntry("tail -f /tmp/app.log");

        Assert.True(viewModel.IsHistoryCopyFeedbackVisible);
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var serviceProvider = CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Tail log", "tail -f /tmp/app.log"));
        var configManager = new StubConfigManager(new AppSettings());

        return new CommandLibraryViewModel(
            serviceProvider,
            configManager,
            localizer,
            new NoOpDialogService(),
            new InertGitSyncService(),
            new CommandLibraryTransferService());
    }

    private sealed class StubConfigManager : IConfigManager
    {
        public StubConfigManager(AppSettings settings) => Settings = settings;

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

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
            => Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers)
            => Task.CompletedTask;
    }

    private sealed class InertGitSyncService : IGitSyncService
    {
        public bool IsConfigured => false;

        public bool IsOperationInProgress => false;

        public string StatusMessage => string.Empty;

        public event EventHandler<GitSyncStatusEventArgs>? StatusChanged { add { } remove { } }

        public Task<GitOperationResult> InitializeRepositoryAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> PullAndImportAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> ExportAndPushAsync(string? commitMessage = null)
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> FullSyncAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> TestConnectionAsync()
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitRepositoryStatus> GetRepositoryStatusAsync()
            => Task.FromResult(new GitRepositoryStatus { IsInitialized = false });

        public void CancelOperation() { }
    }

    private sealed class NoOpDialogService : AppDialogService
    {
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

        public void ShowError(string title, string message) { }

        public void ShowInfo(string title, string message) { }

        public void ShowWarning(string title, string message) { }
    }
}
