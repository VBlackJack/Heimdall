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

using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using TwinShell.Core.Enums;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies the shared dangerous-command confirmation rule: only
/// <see cref="CriticalityLevel.Dangerous"/> (and any unexpected higher value)
/// prompts; <see cref="CriticalityLevel.Info"/> and
/// <see cref="CriticalityLevel.Run"/> proceed without a dialog.
/// </summary>
public sealed class DangerousCommandGuardTests
{
    private static readonly Func<string, string> IdentityLocalizer = key => key;

    [Fact]
    public async Task ConfirmIfDangerousAsync_Info_ReturnsTrueWithoutPrompting()
    {
        var dialog = new RecordingDialogService(confirmResult: true);

        var result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            CriticalityLevel.Info, dialog, IdentityLocalizer);

        result.Should().BeTrue();
        dialog.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmIfDangerousAsync_Run_ReturnsTrueWithoutPrompting()
    {
        var dialog = new RecordingDialogService(confirmResult: true);

        var result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            CriticalityLevel.Run, dialog, IdentityLocalizer);

        result.Should().BeTrue();
        dialog.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmIfDangerousAsync_DangerousConfirmed_ReturnsTrueAndPromptsOnce()
    {
        var dialog = new RecordingDialogService(confirmResult: true);

        var result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            CriticalityLevel.Dangerous, dialog, IdentityLocalizer);

        result.Should().BeTrue();
        dialog.ConfirmCallCount.Should().Be(1);
        dialog.LastConfirmTitle.Should().Be("ConfirmDangerousSendTitle");
        dialog.LastConfirmMessage.Should().Be("ConfirmDangerousSendMessage");
        dialog.LastConfirmSeverity.Should().Be("warning");
    }

    [Fact]
    public async Task ConfirmIfDangerousAsync_DangerousDeclined_ReturnsFalse()
    {
        var dialog = new RecordingDialogService(confirmResult: false);

        var result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            CriticalityLevel.Dangerous, dialog, IdentityLocalizer);

        result.Should().BeFalse();
        dialog.ConfirmCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmIfDangerousAsync_UnexpectedHigherLevel_PromptsFailSafe()
    {
        var dialog = new RecordingDialogService(confirmResult: true);

        var result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            (CriticalityLevel)99, dialog, IdentityLocalizer);

        result.Should().BeTrue();
        dialog.ConfirmCallCount.Should().Be(1);
    }

    /// <summary>
    /// Minimal <see cref="IDialogService"/> fake that records
    /// <see cref="ShowConfirmAsync"/> invocations and returns a fixed result.
    /// Every other member is unused by the guard and throws if called.
    /// </summary>
    private sealed class RecordingDialogService : IDialogService
    {
        private readonly bool _confirmResult;

        public RecordingDialogService(bool confirmResult) => _confirmResult = confirmResult;

        public int ConfirmCallCount { get; private set; }

        public string? LastConfirmTitle { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public string? LastConfirmSeverity { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCallCount++;
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            LastConfirmSeverity = severity;
            return Task.FromResult(_confirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => throw new NotSupportedException();

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => throw new NotSupportedException();

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => throw new NotSupportedException();

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => throw new NotSupportedException();

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => throw new NotSupportedException();

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void ShowError(string title, string message) => throw new NotSupportedException();

        public void ShowInfo(string title, string message) => throw new NotSupportedException();

        public void ShowWarning(string title, string message) => throw new NotSupportedException();
    }
}
