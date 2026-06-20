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

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.Sync;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Commands partial of <see cref="CommandLibraryViewModel"/>: copy/send,
/// CRUD, favorites, sync, import/export, and panel toggles.
/// </summary>
public sealed partial class CommandLibraryViewModel
{
    // ── Copy / Send / Clipboard helpers ───────────────────────────

    /// <summary>
    /// Copies the current generated command to the clipboard, records a
    /// history entry, and triggers the visual feedback animation.
    /// </summary>
    [RelayCommand]
    public void Copy()
    {
        if (string.IsNullOrEmpty(GeneratedCommand)) return;
        var copied = SetClipboardText?.Invoke(GeneratedCommand) ?? false;
        if (!copied) return;

        ShowCopyFeedback?.Invoke("copy");
        RecordHistory();
    }

    /// <summary>
    /// Invokes the registered Send-to-Terminal handler with the current
    /// generated command and records the action in history. Because Send
    /// executes the command immediately on the session, actions flagged
    /// <see cref="CriticalityLevel.Dangerous"/> require user confirmation first.
    /// Commands originating from an applied example always require confirmation,
    /// since example text bypasses parameter validation and escaping.
    /// </summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        if (string.IsNullOrEmpty(GeneratedCommand) || SendCommandHandler is null) return;

        // Example text bypasses GenerateCommand validation and escaping, and both the
        // example string and the action level come from (possibly imported) data. Force a
        // confirmation for example-originated commands regardless of the declared level.
        var level = _generatedFromExample
            ? CriticalityLevel.Dangerous
            : _selectedAction?.Level ?? CriticalityLevel.Info;
        if (!await DangerousCommandGuard.ConfirmIfDangerousAsync(level, _dialogService, LocalizeKey))
        {
            return;
        }

        SendCommandHandler(GeneratedCommand);
        ShowCopyFeedback?.Invoke("send");
        RecordHistory();
    }

    /// <summary>
    /// Replaces the generator output with an example command and copies it
    /// to the clipboard in one gesture (used by the example copy button).
    /// </summary>
    [RelayCommand]
    public void CopyExample(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;
        var copied = SetClipboardText?.Invoke(command) ?? false;
        if (copied) ShowCopyFeedback?.Invoke("example");
    }

    /// <summary>
    /// Replaces the current generator output with an example command,
    /// marking it as valid so Copy/Send can be used immediately.
    /// </summary>
    [RelayCommand]
    public void ApplyExample(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;
        ApplyExampleText(command);
    }

    /// <summary>
    /// Copies a previously executed command from the history panel to the
    /// clipboard and shows the transient "copied" feedback banner.
    /// </summary>
    [RelayCommand]
    public void CopyHistoryEntry(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;
        var copied = SetClipboardText?.Invoke(command) ?? false;
        if (copied) TriggerHistoryCopyFeedback();
    }

    /// <summary>Clears the search box.</summary>
    [RelayCommand]
    public void ClearSearch() => SearchText = string.Empty;

    /// <summary>Edits the currently selected action (bridges to the dialog callback).</summary>
    [RelayCommand]
    public Task EditSelectedAsync() => EditActionAsync(SelectedEntry);

    /// <summary>Deletes the currently selected action (bridges to the dialog callback).</summary>
    [RelayCommand]
    public Task DeleteSelectedAsync() => DeleteActionAsync(SelectedEntry);

    /// <summary>Toggles the favorite status of the action with the given ID.</summary>
    [RelayCommand]
    public async Task ToggleFavoriteByIdAsync(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        await ToggleFavoriteAsync(actionId);
        _actionsView?.Refresh();
    }

    private void TriggerHistoryCopyFeedback()
    {
        IsHistoryCopyFeedbackVisible = true;

        _historyCopyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _historyCopyTimer.Stop();
        _historyCopyTimer.Tick -= OnHistoryCopyTimerTick;
        _historyCopyTimer.Tick += OnHistoryCopyTimerTick;
        _historyCopyTimer.Start();
    }

    private void OnHistoryCopyTimerTick(object? sender, EventArgs e)
    {
        IsHistoryCopyFeedbackVisible = false;
        _historyCopyTimer?.Stop();
    }

    // ── Favorites ─────────────────────────────────────────────────

    /// <summary>
    /// Toggles favorite status for the given action and updates the local
    /// cache so display entries reflect the change on the next refresh.
    /// </summary>
    public async Task ToggleFavoriteAsync(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var favoritesService = scope.ServiceProvider.GetRequiredService<IFavoritesService>();
            var nowFavorited = await favoritesService.ToggleFavoriteAsync(actionId);

            if (nowFavorited) _favoriteIds.Add(actionId);
            else _favoriteIds.Remove(actionId);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Favorite toggle failed: {ex.Message}");
            _dialogService.ShowError(LocalizeKey("ToolCmdLibErrorTitle"), ex.Message);
        }
    }

    /// <summary>Inverts the favorites-only filter toggle.</summary>
    [RelayCommand]
    public void ToggleFavoritesFilter() => FavoritesFilterActive = !FavoritesFilterActive;

    // ── Help & history panel toggles ──────────────────────────────

    /// <summary>Shows or hides the help panel.</summary>
    [RelayCommand]
    public void ToggleHelp() => IsHelpVisible = !IsHelpVisible;

    /// <summary>
    /// Shows or hides the history panel. Hides the generator panel as a side
    /// effect when opening (the two panels are mutually exclusive) and
    /// reloads the bound <see cref="HistoryEntries"/> collection.
    /// </summary>
    [RelayCommand]
    public async Task ToggleHistoryAsync()
    {
        if (IsHistoryVisible)
        {
            IsHistoryVisible = false;
            return;
        }
        IsGeneratorVisible = false;
        IsHistoryVisible = true;
        await LoadHistoryAsync();
    }

    // ── Action-service envelope ───────────────────────────────────

    /// <summary>
    /// Runs an action-service operation inside a fresh DI scope with the shared
    /// busy-state, logging, and error-dialog envelope used by the CRUD and
    /// import/export commands. The operation receives a scoped IActionService.
    /// Returns true when it completed without throwing, false otherwise.
    /// </summary>
    private async Task<bool> RunActionServiceOperationAsync(
        Func<IActionService, Task> operation,
        string logLabel,
        string errorTitleKey)
    {
        IsBusy = true;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var actionService = scope.ServiceProvider.GetRequiredService<IActionService>();
            await operation(actionService);
            return true;
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] {logLabel} failed: {ex.Message}");
            _dialogService.ShowError(LocalizeKey(errorTitleKey), ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── CRUD ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens the Add Action dialog via the view-installed callback and creates
    /// the action when the user saves.
    /// </summary>
    [RelayCommand]
    public async Task AddActionAsync()
    {
        if (ShowActionDialogAsync is null) return;

        var vm = new CommandActionDialogViewModel
        {
            DialogTitle = LocalizeKey("ToolCmdLibDialogTitleAdd"),
            Localizer = _localizer,
            AvailableCategories = _categoryList.ToList()
        };

        var saved = await ShowActionDialogAsync(vm);
        if (!saved) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var action = vm.ToAction();
                await actionService.CreateActionAsync(action);
                await ReloadAsync();
            },
            "Create action",
            "ToolCmdLibErrorTitle");
    }

    /// <summary>
    /// Opens the Edit Action dialog for <paramref name="entry"/> and persists
    /// the changes when the user saves.
    /// </summary>
    public async Task EditActionAsync(CommandLibraryActionEntry? entry)
    {
        if (entry is null || ShowActionDialogAsync is null) return;

        var vm = CommandActionDialogViewModel.FromAction(entry.Source);
        vm.DialogTitle = LocalizeKey("ToolCmdLibDialogTitleEdit");
        vm.Localizer = _localizer;
        vm.AvailableCategories = _categoryList.ToList();

        var saved = await ShowActionDialogAsync(vm);
        if (!saved) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var updated = vm.ToAction();
                await actionService.UpdateActionAsync(updated);
                await ReloadAsync();
            },
            "Update action",
            "ToolCmdLibErrorTitle");
    }

    /// <summary>
    /// Confirms with the user, then deletes <paramref name="entry"/> from
    /// the database and reloads the library.
    /// </summary>
    public async Task DeleteActionAsync(CommandLibraryActionEntry? entry)
    {
        if (entry is null) return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            LocalizeKey("ToolCmdLibDeleteConfirmTitle"),
            string.Format(LocalizeKey("ToolCmdLibDeleteConfirmMessage"), entry.Title),
            "warning");
        if (!confirmed) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                await actionService.DeleteActionAsync(entry.Source.Id);
                await ReloadAsync();
            },
            "Delete action",
            "ToolCmdLibErrorTitle");
    }

    // ── Import / Export ──────────────────────────────────────────

    /// <summary>
    /// Prompts for a destination path and writes a JSON envelope containing
    /// every action currently in the database.
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        if (ShowSaveFileDialog is null) return;

        var defaultName = $"commands-export-{DateTime.Now:yyyyMMdd}.json";
        var path = ShowSaveFileDialog(defaultName, "JSON files (*.json)|*.json");
        if (string.IsNullOrEmpty(path)) return;

        // The export writes parameter defaults, examples, and notes verbatim, so any
        // secret a user embedded in those free-text fields would land in cleartext.
        // Warn before writing so the user can cancel.
        var proceed = await _dialogService.ShowConfirmAsync(
            LocalizeKey("ToolCmdLibExportSecretWarningTitle"),
            LocalizeKey("ToolCmdLibExportSecretWarningMessage"),
            "warning");
        if (!proceed) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var count = await _transferService.ExportAsync(actionService, path);
                _dialogService.ShowInfo(
                    LocalizeKey("ToolCmdLibExportSuccess"),
                    string.Format(LocalizeKey("ToolCmdLibExportSuccessMessage"), count, path));
            },
            "Export",
            "ToolCmdLibExportError");
    }

    /// <summary>
    /// Prompts for a source file, parses the JSON envelope, and merges the
    /// contained actions into the database. System (seed) actions are never
    /// overwritten.
    /// </summary>
    [RelayCommand]
    public async Task ImportAsync()
    {
        if (ShowOpenFileDialog is null) return;

        var path = ShowOpenFileDialog("JSON files (*.json)|*.json");
        if (string.IsNullOrEmpty(path)) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var result = await _transferService.ImportAsync(actionService, path);
                switch (result.Outcome)
                {
                    case CommandLibraryImportOutcome.FileTooLarge:
                        _dialogService.ShowError(
                            LocalizeKey("ToolCmdLibImportError"),
                            LocalizeKey("ToolCmdLibImportFileTooLarge"));
                        return;
                    case CommandLibraryImportOutcome.InvalidFormat:
                        _dialogService.ShowError(
                            LocalizeKey("ToolCmdLibImportError"),
                            LocalizeKey("ToolCmdLibImportInvalidFormat"));
                        return;
                    default:
                        await ReloadAsync();
                        _dialogService.ShowInfo(
                            LocalizeKey("ToolCmdLibImportResultTitle"),
                            string.Format(
                                LocalizeKey("ToolCmdLibImportResultMessage"),
                                result.Imported, result.Updated, result.Skipped));
                        return;
                }
            },
            "Import",
            "ToolCmdLibImportError");
    }

    // ── Git Sync ──────────────────────────────────────────────────

    /// <summary>
    /// Performs a full Git sync (pull + merge + push) using the configured
    /// repository, then reloads the library. Surfaces the result through the
    /// injected dialog service.
    /// </summary>
    [RelayCommand]
    public async Task SyncAsync()
    {
        var settings = await _configManager.LoadSettingsAsync();
        if (!settings.CmdLibGitSyncEnabled || string.IsNullOrWhiteSpace(settings.CmdLibGitSyncUrl))
        {
            _dialogService.ShowWarning(
                LocalizeKey("ToolCmdLibSyncNotConfigured"),
                LocalizeKey("ToolCmdLibSyncNotConfiguredDesc"));
            return;
        }

        IsSyncing = true;
        SyncStatusMessage = LocalizeKey("ToolCmdLibSyncInProgress");
        try
        {
            var result = await Task.Run(() => _gitSyncService.FullSyncAsync());
            await ReloadAsync();

            var presentation = CommandLibrarySyncResultMapper.Map(result, LocalizeKey);
            switch (presentation.Kind)
            {
                case SyncDialogKind.Warning:
                    _dialogService.ShowWarning(presentation.Title, presentation.Body);
                    break;
                case SyncDialogKind.Error:
                    _dialogService.ShowError(presentation.Title, presentation.Body);
                    break;
                default:
                    _dialogService.ShowInfo(presentation.Title, presentation.Body);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _dialogService.ShowInfo(
                LocalizeKey("ToolCmdLibSyncCancelled"),
                LocalizeKey("ToolCmdLibSyncCancelledMessage"));
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Sync failed: {ex.Message}");
            _dialogService.ShowError(LocalizeKey("ToolCmdLibSyncError"), ex.Message);
        }
        finally
        {
            IsSyncing = false;
            SyncStatusMessage = string.Empty;
        }
    }

    /// <summary>
    /// Requests cancellation of the active Git sync operation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelSync))]
    public void CancelSync()
    {
        if (!IsSyncing) return;

        SyncStatusMessage = LocalizeKey("ToolCmdLibSyncCancelling");
        _gitSyncService.CancelOperation();
    }

    private bool CanCancelSync() => IsSyncing;
}
