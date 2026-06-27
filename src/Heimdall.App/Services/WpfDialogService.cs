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

using System.Windows;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/>.
/// Creates and shows modal dialog windows, transferring results back to callers.
/// </summary>
public sealed class WpfDialogService(
    LocalizationManager localizer,
    IConfigManager configManager,
    IServiceScopeFactory scopeFactory) : IDialogService
{
    private readonly LocalizationManager _localizer = localizer;
    private readonly IConfigManager _configManager = configManager;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <inheritdoc/>
    public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        => ShowConfirmAsync(title, message, severity, topmost: false);

    /// <inheritdoc/>
    public Task<bool> ShowConfirmAsync(string title, string message, string severity, bool topmost)
    {
        if (IsApplicationShuttingDown())
        {
            return Task.FromResult(true);
        }

        var yes = _localizer["BtnYes"];
        var no = _localizer["BtnNo"];
        var result = Views.Dialogs.MessageDialog.ShowConfirm(
            GetOwnerWindow(), title, message, severity, yes, no, topmost);

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
    {
        if (IsApplicationShuttingDown())
        {
            return Task.FromResult<bool?>(false);
        }

        var save = _localizer["BtnSave"];
        var discard = _localizer["BtnDiscard"];
        var cancel = _localizer["BtnCancel"];
        var result = Views.Dialogs.MessageDialog.ShowThreeWay(
            GetOwnerWindow(), title, message, "warning", save, discard, cancel);

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
    {
        var dialog = new InputDialog(_localizer)
        {
            Title = title,
            Prompt = prompt,
            InputText = defaultValue ?? "",
            Owner = GetOwnerWindow()
        };

        string? result = dialog.ShowDialog() == true ? dialog.InputText : null;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<string?> ShowPasswordInputAsync(
        string title,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        return InvokeOnUiThreadAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dialog = new PasswordInputDialog
                {
                    Title = title,
                    Prompt = prompt,
                    Owner = GetOwnerWindow()
                };

                return dialog.ShowDialog() == true ? dialog.ResultPassword : null;
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var vm = new ServerBulkEditViewModel(_localizer, count, initialPort);
        var dialog = new ServerBulkEditDialog
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        int? result = dialog.ShowDialog() == true ? vm.ResolvedPort : null;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var vm = new ServerBulkEditUsernameViewModel(_localizer, count, initialUsername);
        var dialog = new ServerBulkEditUsernameDialog
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        string? result = dialog.ShowDialog() == true ? vm.ResolvedUsername : null;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var vm = new ServerBulkEditPasswordViewModel(_localizer, count);
        var dialog = new ServerBulkEditPasswordDialog
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        string? result = dialog.ShowDialog() == true ? vm.ResolvedPassword : null;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
    {
        var vm = editVm ?? new ServerDialogViewModel();
        vm.Localizer ??= _localizer;
        vm.DialogService ??= this;
        vm.ServiceScopeFactory ??= _scopeFactory;
        await vm.InitializePostConnectLinksAsync().ConfigureAwait(true);
        var dialog = new ServerDialog(_localizer, _configManager)
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        ServerDialogResult? result = dialog.ShowDialog() == true
            ? new ServerDialogResult(vm.ToDto(), true)
            : null;

        return result;
    }

    /// <inheritdoc/>
    public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
    {
        var vm = editVm ?? new GatewayDialogViewModel();
        vm.Localizer ??= _localizer;
        var dialog = new GatewayDialog
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        GatewayDialogResult? result = dialog.ShowDialog() == true
            ? new GatewayDialogResult(vm.ToDto(), true)
            : null;

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task ShowGatewayOverviewAsync(GatewayOverviewDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var dialog = new GatewayOverviewDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
    {
        var vm = editVm ?? new ProjectDialogViewModel();
        vm.Localizer ??= _localizer;
        var dialog = new ProjectDialog
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        ProjectDialogResult? result = dialog.ShowDialog() == true
            ? new ProjectDialogResult(vm.ToDto(), true)
            : null;

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
    {
        var vm = editVm ?? new ScheduledTaskDialogViewModel();
        vm.Localizer ??= _localizer;
        var dialog = new Views.Dialogs.ScheduledTaskDialog
        {
            DataContext = vm,
            Owner = GetOwnerWindow()
        };

        ScheduledTaskDialogResult? result = dialog.ShowDialog() == true
            ? new ScheduledTaskDialogResult(vm.ToDto(), true)
            : null;

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        PinDialog dialog = new PinDialog
        {
            DataContext = viewModel
        };

        Window? owner = GetOwnerWindow();
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ShowVaultUnlockDialogAsync(VaultUnlockDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        VaultUnlockDialog dialog = new VaultUnlockDialog
        {
            DataContext = viewModel
        };

        Window? owner = GetOwnerWindow();
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        PinSetupDialog dialog = new PinSetupDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        PinSetupResult? result = dialog.ShowDialog() == true
            ? viewModel.Result
            : null;

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var dialog = new SnapshotRestoreDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        SnapshotRestoreDialogResult? result = dialog.ShowDialog() == true
            ? viewModel.Result
            : null;

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var dialog = new RdpImportDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        RdpImportSelection? result = dialog.ShowDialog() == true
            ? viewModel.Result
            : null;

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        using var scope = _scopeFactory.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<ImportOpenSshConfigDialogViewModel>();
        await viewModel.InitializeAsync(parseResult).ConfigureAwait(true);

        var dialog = new ImportSessionsPreviewDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        var confirmed = dialog.ShowDialog() == true;
        return confirmed ? viewModel.Result : null;
    }

    /// <inheritdoc/>
    public async Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        using var scope = _scopeFactory.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<ImportPuttySessionsDialogViewModel>();
        await viewModel.InitializeAsync(parseResult).ConfigureAwait(true);

        var dialog = new ImportSessionsPreviewDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        var confirmed = dialog.ShowDialog() == true;
        return confirmed ? viewModel.Result : null;
    }

    /// <inheritdoc/>
    public async Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        using var scope = _scopeFactory.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<ImportKnownHostsDialogViewModel>();
        await viewModel.InitializeAsync(preview).ConfigureAwait(true);

        var dialog = new ImportKnownHostsDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        var confirmed = dialog.ShowDialog() == true;
        return confirmed ? viewModel.Result : null;
    }

    /// <inheritdoc/>
    public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var dialog = new TrustedHostKeyDetailsDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
        ImportKnownHostsConflictDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var dialog = new ImportKnownHostsConflictDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        var confirmed = dialog.ShowDialog() == true;
        return Task.FromResult(confirmed ? viewModel.Result : null);
    }

    /// <inheritdoc/>
    public async Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
        CommandLibraryPickerDialogViewModel viewModel,
        AutoPrefillContext? prefillContext = null,
        string? existingActionId = null,
        IReadOnlyDictionary<string, string>? existingValues = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (!string.IsNullOrWhiteSpace(existingActionId))
        {
            await viewModel.InitializeForChangeAsync(
                prefillContext ?? new AutoPrefillContext(null, null, null, null),
                existingActionId,
                existingValues ?? new Dictionary<string, string>(StringComparer.Ordinal)).ConfigureAwait(true);
        }
        else
        {
            await viewModel.InitializeAsync(prefillContext).ConfigureAwait(true);
        }

        var dialog = new CommandLibraryPickerDialog
        {
            DataContext = viewModel,
            Owner = GetOwnerWindow()
        };

        CommandLibraryPickerResult? result = dialog.ShowDialog() == true && viewModel.ResultActionId is not null
            ? new CommandLibraryPickerResult(
                viewModel.ResultActionId,
                viewModel.ResultActionTitle ?? viewModel.ResultActionId,
                viewModel.ResultParams ?? [])
            : null;

        return result;
    }

    /// <inheritdoc/>
    public void ShowError(string title, string message)
    {
        if (IsApplicationShuttingDown())
        {
            return;
        }

        Views.Dialogs.MessageDialog.ShowMessage(GetOwnerWindow(), title, message, "error", _localizer["BtnOk"]);
    }

    /// <inheritdoc/>
    public void ShowInfo(string title, string message)
    {
        if (IsApplicationShuttingDown())
        {
            return;
        }

        Views.Dialogs.MessageDialog.ShowMessage(GetOwnerWindow(), title, message, "info", _localizer["BtnOk"]);
    }

    /// <inheritdoc/>
    public void ShowWarning(string title, string message)
    {
        if (IsApplicationShuttingDown())
        {
            return;
        }

        Views.Dialogs.MessageDialog.ShowMessage(GetOwnerWindow(), title, message, "warning", _localizer["BtnOk"]);
    }

    /// <summary>
    /// Gets the current main window as dialog owner for proper centering and modal behavior.
    /// Returns null if no main window is available (e.g., during startup).
    /// </summary>
    private static Window? GetOwnerWindow()
    {
        return Application.Current?.MainWindow;
    }

    private static bool IsApplicationShuttingDown()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;
    }

    private static Task<T> InvokeOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(
                action,
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken)
            .Task;
    }
}
