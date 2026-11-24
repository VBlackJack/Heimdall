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
using Heimdall.Core.Import;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;


/// <summary>
/// The large confirmation is for the recipe import preview only: the import asks for it, and an
/// implementer that does not know it still shows the labeled confirmation.
/// </summary>
public sealed class LargeConfirmTests
{
    [Fact]
    public async Task TheRecipeImportPreviewAsksForTheLargeConfirmation()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Offer(RecipeImportFiles.Bytes());
        harness.Dialogs.ConfirmResult = true;

        await harness.ViewModel.ImportCommand.ExecuteAsync(null);

        RecipeRecordingDialogs.ConfirmCall call = Assert.Single(harness.Dialogs.Confirms);
        Assert.True(call.Large);
        Assert.Equal("Import", call.ConfirmLabel);
    }

    [Fact]
    public async Task TheRemovalConfirmationStaysAnOrdinaryOne()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Offer(RecipeImportFiles.Bytes());
        harness.Dialogs.ConfirmResult = true;
        await harness.ViewModel.ImportCommand.ExecuteAsync(null);
        harness.Dialogs.Confirms.Clear();

        await harness.ViewModel.Items[0].RemoveCommand.ExecuteAsync(null);

        Assert.All(harness.Dialogs.Confirms, call => Assert.False(call.Large));
    }

    [Fact]
    public async Task AnImplementerThatDoesNotKnowTheLargeConfirmationGetsTheLabeledOne()
    {
        PlainConfirmDialogs dialogs = new() { ConfirmResult = true };
        IDialogService service = dialogs;

        bool answer = await service.ShowLargeConfirmAsync("Title", "Message", "warning", "Go", "Stop");

        Assert.True(answer);
        PlainConfirmDialogs.ConfirmCall call = Assert.Single(dialogs.Confirms);
        Assert.Equal(("Title", "Message", "warning", "Go", "Stop"), (call.Title, call.Message, call.Severity, call.ConfirmLabel, call.DeclineLabel));
    }
}

/// <summary>A dialog service that does not know the large confirmation: the default interface member answers for it.</summary>
internal sealed class PlainConfirmDialogs : IDialogService
{
    public bool ConfirmResult { get; set; }

    public List<ConfirmCall> Confirms { get; } = [];

    public List<(string Title, string Message)> Errors { get; } = [];

    public List<(string Title, string Message)> Infos { get; } = [];

    public Func<ConfirmCall, Task>? OnConfirm { get; set; }

    public sealed record ConfirmCall(string Title, string Message, string Severity, string? ConfirmLabel, string? DeclineLabel, bool Large = false);


    public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        => Record(new ConfirmCall(title, message, severity, null, null));

    public Task<bool> ShowConfirmAsync(string title, string message, string severity, string confirmLabel, string declineLabel)
        => Record(new ConfirmCall(title, message, severity, confirmLabel, declineLabel));

    private async Task<bool> Record(ConfirmCall call)
    {
        Confirms.Add(call);
        if (OnConfirm is not null)
        {
            await OnConfirm(call);
        }

        return ConfirmResult;
    }

    public void ShowError(string title, string message) => Errors.Add((title, message));

    public void ShowInfo(string title, string message) => Infos.Add((title, message));

    public void ShowWarning(string title, string message)
    {
    }

    public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(false);

    public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

    public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

    public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

    public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
        => Task.FromResult<ScheduledTaskDialogResult?>(null);

    public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

    public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

    public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
        => Task.FromResult<SnapshotRestoreDialogResult?>(null);

    public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

    public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

    public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

    public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
        => Task.FromResult<KnownHostsImportOutcome?>(null);

    public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

    public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
        => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

    public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
        CommandLibraryPickerDialogViewModel viewModel,
        AutoPrefillContext? prefillContext = null,
        string? existingActionId = null,
        IReadOnlyDictionary<string, string>? existingValues = null)
        => Task.FromResult<CommandLibraryPickerResult?>(null);

    public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult(initialPort);

    public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
        => Task.FromResult(initialUsername);

    public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
