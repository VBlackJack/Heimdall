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
/// Verifies command-execution and export confirmation contracts: commands applied from
/// an example always require Send confirmation (example text bypasses parameter validation
/// and escaping), normally generated commands keep their level-driven behavior, and export
/// requires confirmation first because it writes free-text fields in cleartext.
/// </summary>
public sealed class CommandLibraryViewModelSendTests
{
    [Fact]
    public async Task SendAsync_ExampleCommand_InfoLevel_DeclinedConfirmation_DoesNotSend()
    {
        var dialog = new RecordingDialogService { ConfirmResult = false };
        var viewModel = await CreateSelectedViewModelAsync(dialog);

        var sent = new List<string>();
        viewModel.SendCommandHandler = sent.Add;

        viewModel.ApplyExample("rm -rf /");
        await viewModel.SendAsync();

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task SendAsync_ExampleCommand_InfoLevel_AcceptedConfirmation_Sends()
    {
        var dialog = new RecordingDialogService { ConfirmResult = true };
        var viewModel = await CreateSelectedViewModelAsync(dialog);

        var sent = new List<string>();
        viewModel.SendCommandHandler = sent.Add;

        viewModel.ApplyExample("rm -rf /");
        await viewModel.SendAsync();

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(new[] { "rm -rf /" }, sent);
    }

    [Fact]
    public async Task SendAsync_GeneratedCommand_InfoLevel_SendsWithoutConfirmation()
    {
        var dialog = new RecordingDialogService { ConfirmResult = false };
        var viewModel = await CreateSelectedViewModelAsync(dialog);

        var sent = new List<string>();
        viewModel.SendCommandHandler = sent.Add;

        await viewModel.SendAsync();

        Assert.Equal(0, dialog.ConfirmCount);
        Assert.Equal(new[] { "tail -f /tmp/app.log" }, sent);
    }

    [Fact]
    public async Task IsSendEnabled_FalseWhenProbeReturnsFalse_EvenIfCommandValid()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());
        viewModel.SendCommandHandler = _ => { };

        // Command is valid (no required params), but no injectable terminal exists.
        Assert.True(viewModel.IsCommandValid);
        viewModel.CanSendToTerminalProbe = () => false;
        Assert.False(viewModel.IsSendEnabled);

        // A terminal sink appears -> Send becomes enabled.
        viewModel.CanSendToTerminalProbe = () => true;
        Assert.True(viewModel.IsSendEnabled);
    }

    [Fact]
    public async Task SendTooltip_InvalidCommand_ReturnsInvalidReason()
    {
        var viewModel = await CreateInvalidSelectedViewModelAsync();

        // Even with an available terminal, an invalid command wins the reason.
        viewModel.CanSendToTerminalProbe = () => true;

        Assert.False(viewModel.IsCommandValid);
        Assert.Equal(
            viewModel.LocalizeKey("ToolCmdLibSendTooltipInvalid"),
            viewModel.SendTooltip);
    }

    [Fact]
    public async Task SendTooltip_NoTerminalSink_ReturnsNoTerminalReason()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());
        viewModel.CanSendToTerminalProbe = () => false;

        Assert.True(viewModel.IsCommandValid);
        Assert.Equal(
            viewModel.LocalizeKey("ToolCmdLibSendTooltipNoTerminal"),
            viewModel.SendTooltip);
    }

    [Fact]
    public async Task SendTooltip_ReadyState_ReturnsReadyReason()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());
        viewModel.CanSendToTerminalProbe = () => true;

        Assert.True(viewModel.IsSendEnabled);
        Assert.Equal(
            viewModel.LocalizeKey("ToolCmdLibSendTooltipReady"),
            viewModel.SendTooltip);
    }

    [Fact]
    public async Task ShowCopyHint_True_WhenAttachedAndValidButNoTerminalSink()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());
        viewModel.SendCommandHandler = _ => { };
        viewModel.CanSendToTerminalProbe = () => false;

        Assert.True(viewModel.IsSendVisible);
        Assert.True(viewModel.IsCommandValid);
        Assert.True(viewModel.ShowCopyHint);
    }

    [Fact]
    public async Task ShowCopyHint_False_WhenTerminalSinkPresent()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());
        viewModel.SendCommandHandler = _ => { };
        viewModel.CanSendToTerminalProbe = () => true;

        Assert.False(viewModel.ShowCopyHint);
    }

    [Fact]
    public async Task ShowCopyHint_False_WhenCommandInvalid()
    {
        var viewModel = await CreateInvalidSelectedViewModelAsync();
        viewModel.SendCommandHandler = _ => { };
        viewModel.CanSendToTerminalProbe = () => false;

        Assert.False(viewModel.IsCommandValid);
        Assert.False(viewModel.ShowCopyHint);
    }

    [Fact]
    public async Task ShowCopyHint_False_WhenStandaloneTab()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());
        // No SendCommandHandler wired -> standalone tab, IsSendVisible stays false.
        viewModel.CanSendToTerminalProbe = () => false;

        Assert.False(viewModel.IsSendVisible);
        Assert.True(viewModel.IsCommandValid);
        Assert.False(viewModel.ShowCopyHint);
    }

    [Fact]
    public async Task RefreshSendState_RaisesPropertyChangedForShowCopyHint()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.RefreshSendState();

        Assert.Contains(nameof(viewModel.ShowCopyHint), raised);
    }

    [Fact]
    public async Task InitializeAsync_WithInitialActionId_SelectsMatchingAction()
    {
        var viewModel = await CreateLibraryViewModelAsync();

        await viewModel.InitializeAsync(targetHost: null, initialActionId: "action-2");

        Assert.NotNull(viewModel.SelectedAction);
        Assert.Equal("action-2", viewModel.SelectedAction!.Id);
        Assert.True(viewModel.IsGeneratorVisible);
    }

    [Fact]
    public async Task InitializeAsync_WithUnknownInitialActionId_DoesNotSelectAndDoesNotThrow()
    {
        var viewModel = await CreateLibraryViewModelAsync();

        await viewModel.InitializeAsync(targetHost: null, initialActionId: "does-not-exist");

        Assert.Null(viewModel.SelectedAction);
        Assert.False(viewModel.IsGeneratorVisible);
    }

    [Fact]
    public async Task SelectActionById_UnknownId_LeavesSelectionUnchanged()
    {
        var viewModel = await CreateLibraryViewModelAsync();
        await viewModel.InitializeAsync(targetHost: null);

        viewModel.SelectActionById("nope");

        Assert.Null(viewModel.SelectedAction);
    }

    private static async Task<CommandLibraryViewModel> CreateLibraryViewModelAsync()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var serviceProvider = CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Tail log", "tail -f /tmp/app.log"),
            CommandLibraryTestHelpers.CreateLinuxAction("action-2", "Echo hi", "echo hi"));

        return new CommandLibraryViewModel(
            serviceProvider,
            new StubConfigManager(new AppSettings()),
            localizer,
            new RecordingDialogService(),
            new InertGitSyncService(),
            new CommandLibraryTransferService());
    }

    [Fact]
    public async Task ApplyExample_DistinctNonFirstValues_UpdateGeneratedCommandAndMarkValid()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());

        // Mirrors clicking the 2nd / 3rd example rows: each carries its own command.
        viewModel.ApplyExample("netdom query fsmo -WhatIf");
        Assert.Equal("netdom query fsmo -WhatIf", viewModel.GeneratedCommand);
        Assert.True(viewModel.IsCommandValid);

        viewModel.ApplyExample("netdom query fsmo -Confirm:$false");
        Assert.Equal("netdom query fsmo -Confirm:$false", viewModel.GeneratedCommand);
        Assert.True(viewModel.IsCommandValid);
    }

    [Fact]
    public async Task ApplyExample_NullOrEmpty_IsNoOp()
    {
        var viewModel = await CreateSelectedViewModelAsync(new RecordingDialogService());

        viewModel.ApplyExample("netdom query fsmo");
        var before = viewModel.GeneratedCommand;

        viewModel.ApplyExample(null);
        Assert.Equal(before, viewModel.GeneratedCommand);

        viewModel.ApplyExample(string.Empty);
        Assert.Equal(before, viewModel.GeneratedCommand);
    }

    [Fact]
    public async Task ExportAsync_WhenConfirmationDeclined_DoesNotExport()
    {
        var dialog = new RecordingDialogService { ConfirmResult = false };
        var transfer = new CountingTransferService();
        var viewModel = await CreateExportViewModelAsync(dialog, transfer);
        viewModel.ShowSaveFileDialog = (_, _) => "C:\\temp\\export.json";

        await viewModel.ExportAsync();

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(0, transfer.ExportCount);
    }

    [Fact]
    public async Task ExportAsync_WhenConfirmationAccepted_Exports()
    {
        var dialog = new RecordingDialogService { ConfirmResult = true };
        var transfer = new CountingTransferService();
        var viewModel = await CreateExportViewModelAsync(dialog, transfer);
        viewModel.ShowSaveFileDialog = (_, _) => "C:\\temp\\export.json";

        await viewModel.ExportAsync();

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(1, transfer.ExportCount);
    }

    [Fact]
    public async Task ExportAsync_WhenNoPathChosen_DoesNotConfirmOrExport()
    {
        var dialog = new RecordingDialogService { ConfirmResult = true };
        var transfer = new CountingTransferService();
        var viewModel = await CreateExportViewModelAsync(dialog, transfer);
        viewModel.ShowSaveFileDialog = (_, _) => null;

        await viewModel.ExportAsync();

        Assert.Equal(0, dialog.ConfirmCount);
        Assert.Equal(0, transfer.ExportCount);
    }

    private static async Task<CommandLibraryViewModel> CreateExportViewModelAsync(
        RecordingDialogService dialog,
        ICommandLibraryTransferService transfer)
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var serviceProvider = CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Tail log", "tail -f /tmp/app.log"));

        return new CommandLibraryViewModel(
            serviceProvider,
            new StubConfigManager(new AppSettings()),
            localizer,
            dialog,
            new InertGitSyncService(),
            transfer);
    }

    private sealed class CountingTransferService : ICommandLibraryTransferService
    {
        public int ExportCount { get; private set; }

        public Task<int> ExportAsync(IActionService actionService, string path)
        {
            ExportCount++;
            return Task.FromResult(0);
        }

        public Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path)
            => Task.FromResult(CommandLibraryImportResult.InvalidFormat());
    }

    private static async Task<CommandLibraryViewModel> CreateSelectedViewModelAsync(RecordingDialogService dialog)
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var serviceProvider = CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Tail log", "tail -f /tmp/app.log"));

        var viewModel = new CommandLibraryViewModel(
            serviceProvider,
            new StubConfigManager(new AppSettings()),
            localizer,
            dialog,
            new InertGitSyncService(),
            new CommandLibraryTransferService());

        await viewModel.InitializeAsync(targetHost: null);
        viewModel.SelectAction(viewModel.AllEntries[0]);
        return viewModel;
    }

    private static async Task<CommandLibraryViewModel> CreateInvalidSelectedViewModelAsync()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var action = CommandLibraryTestHelpers.CreateLinuxAction(
            "action-1",
            "Grep log",
            "grep {pattern} /var/log/syslog",
            CommandLibraryTestHelpers.RequiredParameter("pattern", "Pattern"));
        var serviceProvider = CommandLibraryTestHelpers.CreateResolverServiceProvider(action);

        var viewModel = new CommandLibraryViewModel(
            serviceProvider,
            new StubConfigManager(new AppSettings()),
            localizer,
            new RecordingDialogService(),
            new InertGitSyncService(),
            new CommandLibraryTransferService());

        await viewModel.InitializeAsync(targetHost: null);
        // The required "pattern" parameter has no value, so the generated command
        // is invalid right after selection.
        viewModel.SelectAction(viewModel.AllEntries[0]);
        return viewModel;
    }

    private sealed class RecordingDialogService : AppDialogService
    {
        public bool ConfirmResult { get; init; }

        public int ConfirmCount { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCount++;
            return Task.FromResult(ConfirmResult);
        }

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
}
