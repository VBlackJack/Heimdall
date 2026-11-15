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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The question a web tab asks before it starts the external program of an unconfirmed profile is
/// the labeled one: declining is the default and a shutdown answers no, as at connect time.
/// </summary>
public sealed class WebExternalLaunchAskTests
{
    private const string WarningSeverity = "warning";
    private const string ProfileName = "Imported console";
    private const string Command = "legacy.exe https://host/";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AskAsync_UsesTheLabeledOverloadOnceAndReturnsItsAnswer(bool answer)
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        RecordingDialogService dialogs = new(answer);

        bool result = await WebExternalLaunch.AskAsync(dialogs, ProfileName, Command, localizer);

        result.Should().Be(answer);
        dialogs.LabeledCalls.Should().ContainSingle();
        dialogs.OtherConfirmCalls.Should().Be(0);
        LabeledCall call = dialogs.LabeledCalls[0];
        call.Severity.Should().Be(WarningSeverity);
        call.Title.Should().Be(localizer["ConfirmWebExternalProgramTitle"]);
        call.Message.Should().Be(WebExternalLaunch.ConfirmationBody(ProfileName, Command, localizer));
        call.ConfirmLabel.Should().Be(localizer["ConfirmExecutionTrustAllow"]);
        call.DeclineLabel.Should().Be(localizer["ConfirmExecutionTrustDecline"]);
    }

    private static async Task<LocalizationManager> LoadLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private sealed record LabeledCall(string Title, string Message, string Severity, string ConfirmLabel, string DeclineLabel);

    private sealed class RecordingDialogService(bool answer) : IDialogService
    {
        public List<LabeledCall> LabeledCalls { get; } = [];

        public int OtherConfirmCalls { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            OtherConfirmCalls++;
            return Task.FromResult(answer);
        }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity, bool topmost)
        {
            OtherConfirmCalls++;
            return Task.FromResult(answer);
        }

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string severity,
            string confirmLabel,
            string declineLabel)
        {
            LabeledCalls.Add(new LabeledCall(title, message, severity, confirmLabel, declineLabel));
            return Task.FromResult(answer);
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
