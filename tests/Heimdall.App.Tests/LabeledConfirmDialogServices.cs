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

/// <summary>One confirmation as the labeled overloads received it.</summary>
internal sealed record LabeledConfirmCall(
    string Title,
    string Message,
    string Severity,
    string ConfirmLabel,
    string DeclineLabel,
    bool Topmost);

/// <summary>
/// Base of the dialog fakes that tell the plain confirmations from the labeled ones. The plain
/// 3-arg overload is counted and answers yes, so a call that reaches it is visible and is also
/// the answer that would consent.
/// </summary>
internal abstract class LabeledConfirmDialogServiceBase
{
    /// <summary>The answer the labeled overload gives.</summary>
    public bool Answer { get; set; }

    /// <summary>The title of every call that reached the plain 3-arg overload, in order.</summary>
    public List<string> PlainTitles { get; } = [];

    /// <summary>How many times the plain 3-arg overload was reached.</summary>
    public int PlainCallCount => PlainTitles.Count;

    /// <summary>The message of every call that reached the plain 3-arg overload, in order.</summary>
    public List<string> PlainMessages { get; } = [];

    /// <summary>The answer the plain 3-arg overload gives.</summary>
    public bool PlainAnswer { get; set; } = true;

    /// <summary>Every call that reached the labeled overload, in order.</summary>
    public List<LabeledConfirmCall> LabeledCalls { get; } = [];

    public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
    {
        PlainTitles.Add(title);
        PlainMessages.Add(message);
        return Task.FromResult(PlainAnswer);
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

    public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public void ShowError(string title, string message)
    {
    }

    public void ShowInfo(string title, string message)
    {
    }

    public void ShowWarning(string title, string message)
    {
    }

    /// <summary>Runs inside the labeled confirmation, before it answers.</summary>
    public Action? WhileOpen { get; set; }

    protected Task<bool> Record(LabeledConfirmCall call)
    {
        LabeledCalls.Add(call);
        WhileOpen?.Invoke();
        return Task.FromResult(Answer);
    }
}

/// <summary>A fake that overrides the labeled 5-arg overload only.</summary>
internal sealed class FiveArgConfirmDialogService : LabeledConfirmDialogServiceBase, IDialogService
{
    public Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string severity,
        string confirmLabel,
        string declineLabel)
        => Record(new LabeledConfirmCall(title, message, severity, confirmLabel, declineLabel, Topmost: false));
}

/// <summary>A fake that overrides the labeled 6-arg overload, which carries topmost.</summary>
internal sealed class SixArgConfirmDialogService : LabeledConfirmDialogServiceBase, IDialogService
{
    public Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string severity,
        string confirmLabel,
        string declineLabel,
        bool topmost)
        => Record(new LabeledConfirmCall(title, message, severity, confirmLabel, declineLabel, topmost));
}
