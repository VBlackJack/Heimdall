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

using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Abstraction for showing dialogs from ViewModels without referencing WPF types.
/// All methods are async to support both modal dialogs and potential future
/// non-blocking implementations.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a confirmation dialog with OK/Cancel buttons.
    /// </summary>
    /// <param name="title">Dialog title (locale key or pre-resolved text).</param>
    /// <param name="message">Dialog message body.</param>
    /// <param name="severity">Visual severity hint: "info", "warning", or "error".</param>
    /// <returns>True if the user confirmed, false if cancelled.</returns>
    Task<bool> ShowConfirmAsync(string title, string message, string severity = "info");

    /// <summary>
    /// Confirmation variant that can render above a topmost popup (e.g. the Ctrl+K
    /// command palette). Default implementation ignores <paramref name="topmost"/>
    /// and delegates to the 3-arg overload, so existing implementations need no change.
    /// </summary>
    Task<bool> ShowConfirmAsync(string title, string message, string severity, bool topmost)
        => ShowConfirmAsync(title, message, severity);

    /// <summary>
    /// Shows a three-choice dialog (Save / Discard / Cancel).
    /// </summary>
    /// <returns>True = Save, false = Discard, null = Cancel.</returns>
    Task<bool?> ShowSaveDiscardCancelAsync(string title, string message);

    /// <summary>
    /// Shows a text input dialog and returns the entered value.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="prompt">Descriptive prompt label.</param>
    /// <param name="defaultValue">Pre-filled text, or null for empty.</param>
    /// <returns>The entered string, or null if the user cancelled.</returns>
    Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null);

    /// <summary>
    /// Shows a password input dialog and returns the entered secret.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="prompt">Descriptive prompt label.</param>
    /// <param name="cancellationToken">Cancellation token checked before showing the dialog.</param>
    /// <returns>The entered password, or null if the user cancelled.</returns>
    Task<string?> ShowPasswordInputAsync(
        string title,
        string prompt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the server add/edit dialog.
    /// </summary>
    /// <param name="editVm">Pre-populated ViewModel for edit mode, or null for add mode.</param>
    /// <returns>The dialog result containing the DTO and save status, or null if cancelled.</returns>
    Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null);

    /// <summary>
    /// Shows the SSH gateway add/edit dialog.
    /// </summary>
    /// <param name="editVm">Pre-populated ViewModel for edit mode, or null for add mode.</param>
    /// <returns>The dialog result containing the DTO and save status, or null if cancelled.</returns>
    Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null);

    /// <summary>
    /// Shows the read-only gateway overview dialog.
    /// </summary>
    Task ShowGatewayOverviewAsync(GatewayOverviewDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the project add/edit dialog.
    /// </summary>
    /// <param name="editVm">Pre-populated ViewModel for edit mode, or null for add mode.</param>
    /// <returns>The dialog result containing the DTO and save status, or null if cancelled.</returns>
    Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null);

    /// <summary>
    /// Shows the scheduled task add/edit dialog.
    /// </summary>
    /// <param name="editVm">Pre-populated ViewModel for edit mode, or null for add mode.</param>
    /// <returns>The dialog result containing the DTO and save status, or null if cancelled.</returns>
    Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null);

    /// <summary>
    /// Shows the PIN entry dialog for authentication.
    /// </summary>
    /// <param name="viewModel">The PIN dialog ViewModel managing verification state.</param>
    Task ShowPinDialogAsync(PinDialogViewModel viewModel);

    /// <summary>
    /// Shows the blocking master-password unlock dialog for the vault at startup.
    /// Default no-op so headless test doubles need not implement it; the WPF
    /// implementation overrides it.
    /// </summary>
    /// <param name="viewModel">The unlock dialog ViewModel managing unlock + lockout state.</param>
    Task ShowVaultUnlockDialogAsync(VaultUnlockDialogViewModel viewModel) => Task.CompletedTask;

    /// <summary>Shows the "Enable master password" dialog. Default no-op for test doubles.</summary>
    /// <param name="viewModel">The enable dialog ViewModel.</param>
    Task ShowVaultEnableDialogAsync(VaultEnableDialogViewModel viewModel) => Task.CompletedTask;

    /// <summary>Shows the "Change master password" dialog. Default no-op for test doubles.</summary>
    /// <param name="viewModel">The change-password dialog ViewModel.</param>
    Task ShowVaultChangePasswordDialogAsync(VaultChangePasswordDialogViewModel viewModel) => Task.CompletedTask;

    /// <summary>Shows the "Disable master password" dialog. Default no-op for test doubles.</summary>
    /// <param name="viewModel">The disable dialog ViewModel.</param>
    Task ShowVaultDisableDialogAsync(VaultDisableDialogViewModel viewModel) => Task.CompletedTask;

    /// <summary>
    /// Shows the PIN setup dialog for setting, changing, or removing the application PIN.
    /// </summary>
    /// <param name="viewModel">The PIN setup dialog ViewModel managing setup state.</param>
    /// <returns>The completed setup result, or null if cancelled.</returns>
    Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel);

    /// <summary>
    /// Shows the startup restore dialog for a previously saved session snapshot.
    /// Returns <c>null</c> when the user dismisses the dialog via the window close button.
    /// </summary>
    Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel);

    /// <summary>
    /// Shows the .rdp import preview dialog and returns the user's selection,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel);

    /// <summary>
    /// Shows the OpenSSH config import preview dialog and returns the import outcome,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult);

    /// <summary>
    /// Shows the PuTTY sessions import preview dialog and returns the import outcome,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult);

    /// <summary>
    /// Shows the known_hosts import preview dialog and returns the import outcome,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview);

    /// <summary>
    /// Shows the trusted host key details dialog.
    /// </summary>
    Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel);

    /// <summary>
    /// Shows known_hosts import conflicts and returns the user's resolution,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
        ImportKnownHostsConflictDialogViewModel viewModel);

    /// <summary>
    /// Shows the Command Library picker dialog and returns the selected action,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
        CommandLibraryPickerDialogViewModel viewModel,
        AutoPrefillContext? prefillContext = null,
        string? existingActionId = null,
        IReadOnlyDictionary<string, string>? existingValues = null);

    /// <summary>
    /// Shows the bulk port edit dialog and returns the validated port value,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the bulk username edit dialog and returns the validated username value,
    /// or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the bulk password edit dialog (with confirmation field) and returns
    /// the plaintext password, or <c>null</c> when the dialog is cancelled.
    /// </summary>
    Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken);

    /// <summary>
    /// Shows a non-blocking error notification.
    /// </summary>
    /// <param name="title">Error title.</param>
    /// <param name="message">Error details.</param>
    void ShowError(string title, string message);

    /// <summary>
    /// Shows a non-blocking informational notification.
    /// </summary>
    /// <param name="title">Info title.</param>
    /// <param name="message">Info details.</param>
    void ShowInfo(string title, string message);

    /// <summary>
    /// Shows a non-blocking warning notification.
    /// </summary>
    /// <param name="title">Warning title.</param>
    /// <param name="message">Warning details.</param>
    void ShowWarning(string title, string message);
}
