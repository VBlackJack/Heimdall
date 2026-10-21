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

        ShowCopyFeedback?.Invoke(CopyTarget);
        RecordHistory(isExecuted: false);
    }

    /// <summary>
    /// Invokes the registered Send-to-Terminal handler with the current
    /// generated command and records the action in history. Because Send
    /// executes the command immediately on the session, actions flagged
    /// <see cref="CriticalityLevel.Dangerous"/> require user confirmation first.
    /// Commands originating from an applied example always require confirmation,
    /// since example text bypasses parameter validation and escaping.
    /// </summary>
    /// <remarks>
    /// Refuses unless <see cref="IsSendEnabled"/>: a valid command and a terminal to receive it.
    /// The command's CanExecute carries the same condition for the button and Ctrl+Enter, and the
    /// method checks it again because it is reachable directly. When validation fails the
    /// generator leaves the raw pattern, braces and all, in <see cref="GeneratedCommand"/>, so a
    /// non-empty command is not a sendable one.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsSendEnabled))]
    public async Task SendAsync()
    {
        if (string.IsNullOrEmpty(GeneratedCommand) || SendCommandHandler is null || !IsSendEnabled) return;

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
        ShowCopyFeedback?.Invoke(SendTarget);
        RecordHistory(isExecuted: true);
    }

    /// <summary>
    /// Types the current generated command at the session's prompt without running it, so the
    /// operator can read it, adjust it and press Enter themselves.
    /// </summary>
    /// <remarks>
    /// Never asks the dangerous-command confirmation: nothing runs, and the operator's own Enter
    /// is the confirmation. Recorded in history as not executed, like a copy, because that is
    /// what it is until the operator decides otherwise, and the library never learns which.
    /// Enabled under the same condition as Send (<see cref="IsSendEnabled"/>); the keyboard
    /// gesture reaches this method directly, so it checks the same condition itself.
    /// </remarks>
    [RelayCommand]
    public void Insert()
    {
        if (string.IsNullOrEmpty(GeneratedCommand) || InsertTextHandler is null || !IsSendEnabled) return;

        InsertTextHandler(GeneratedCommand);
        ShowCopyFeedback?.Invoke(InsertTarget);
        RecordHistory(isExecuted: false);
    }

    /// <summary>
    /// Copies an example row's command to the clipboard, leaving the generator output
    /// untouched. Clicking the row itself is the gesture that replaces the output; that
    /// is <see cref="ApplyExample"/>.
    /// </summary>
    [RelayCommand]
    public void CopyExample(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;
        var copied = SetClipboardText?.Invoke(command) ?? false;
        if (copied) ShowCopyFeedback?.Invoke(CopyExampleTarget);
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
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
    public Task EditSelectedAsync() => EditActionAsync(SelectedEntry);

    /// <summary>Deletes the currently selected action (bridges to the dialog callback).</summary>
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
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
        IsRunbooksVisible = false;
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
    /// <remarks>
    /// Refuses to start while another operation holds <see cref="IsBusy"/>. The command
    /// gate on <see cref="IsLibraryIdle"/> already keeps the buttons from firing twice,
    /// but that only guards the UI door: the public entry points are reachable directly,
    /// and every operation here shares one busy flag, so a second run would clear it on
    /// its own exit while the first is still working.
    /// </remarks>
    private async Task<bool> RunActionServiceOperationAsync(
        Func<IActionService, Task> operation,
        string logLabel,
        string errorTitleKey)
    {
        if (IsBusy)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] {logLabel} refused: another operation is already running.");
            return false;
        }

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
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
    public async Task AddActionAsync()
    {
        if (ShowActionDialogAsync is null) return;

        var vm = new CommandActionDialogViewModel
        {
            DialogTitle = LocalizeKey("ToolCmdLibDialogTitleAdd"),
            Localizer = _localizer
        };
        vm.UseCategoryLabels(_categoryList, LocalizeKey);

        var saved = await ShowActionDialogAsync(vm);
        if (!saved) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var action = vm.ToAction();
                await actionService.CreateActionAsync(action);
                RememberCreatedCategory(action.Category);
                await ReloadAsync();
            },
            "Create action",
            "ToolCmdLibErrorTitle");
    }

    /// <summary>
    /// Opens the Add dialog prefilled with a command captured from a terminal, creates the
    /// action when the operator saves, and selects it.
    /// </summary>
    /// <remarks>
    /// <para>Prefilled from <see cref="CommandActionDialogViewModel.ForCapturedCommand"/>, in the
    /// category of the last action created in this session. Cancelling leaves the library as it
    /// was, on its list.</para>
    /// <para>Nothing is written to the command history: the command was captured, not run from
    /// the library.</para>
    /// </remarks>
    public async Task PrefillNewActionAsync(CapturedCommand capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (ShowActionDialogAsync is null) return;

        var vm = CommandActionDialogViewModel.ForCapturedCommand(capture, SessionMemory.LastCreatedCategory);
        vm.DialogTitle = LocalizeKey("ToolCmdLibDialogTitleAdd");
        vm.Localizer = _localizer;
        vm.AvailableCategories = _categoryList.ToList();

        var saved = await ShowActionDialogAsync(vm);
        if (!saved) return;

        string? createdId = null;
        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var action = vm.ToAction();
                await actionService.CreateActionAsync(action);
                RememberCreatedCategory(action.Category);
                createdId = action.Id;
                await ReloadAsync();
            },
            "Create captured action",
            "ToolCmdLibErrorTitle");

        SelectActionById(createdId);
    }

    /// <summary>
    /// The application-wide memory of the library, shared by every library tab; a private one
    /// when the container does not provide it.
    /// </summary>
    private CommandLibrarySessionMemory SessionMemory =>
        _sessionMemory ??= _serviceProvider.GetService<CommandLibrarySessionMemory>()
            ?? new CommandLibrarySessionMemory();

    private CommandLibrarySessionMemory? _sessionMemory;

    private void RememberCreatedCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            SessionMemory.LastCreatedCategory = category.Trim();
        }
    }

    /// <summary>
    /// Opens the Add dialog pre-filled with a copy of the selected action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offered for every action, including the ones the library ships. Those cannot be
    /// edited, so before this the only way to get a variant of one was to retype it, which
    /// is how people end up with a nearly-right command they typed from memory.
    /// </para>
    /// <para>
    /// Unlike Edit, this is not gated on the selection being editable. It is gated on there
    /// being a selection at all, and on no other library operation being in flight.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDuplicateSelected))]
    public async Task DuplicateSelectedAsync()
    {
        var entry = SelectedEntry;
        if (entry is null || ShowActionDialogAsync is null) return;

        var vm = CommandActionDialogViewModel.AsCopyOf(
            entry.Source,
            string.Format(LocalizeKey("ToolCmdLibDuplicateTitleFormat"), entry.Title));
        vm.DialogTitle = LocalizeKey("ToolCmdLibDialogTitleDuplicate");
        vm.Localizer = _localizer;
        vm.UseCategoryLabels(_categoryList, LocalizeKey);

        var saved = await ShowActionDialogAsync(vm);
        if (!saved) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var action = vm.ToAction();
                await actionService.CreateActionAsync(action);
                RememberCreatedCategory(action.Category);
                await ReloadAsync();
            },
            "Duplicate action",
            "ToolCmdLibErrorTitle");
    }

    /// <summary>
    /// True when there is an action to copy and nothing else is running.
    /// </summary>
    public bool CanDuplicateSelected => SelectedEntry is not null && IsLibraryIdle;

    /// <summary>
    /// Opens the Edit Action dialog for <paramref name="entry"/> and persists
    /// the changes when the user saves.
    /// </summary>
    public async Task EditActionAsync(CommandLibraryActionEntry? entry)
    {
        if (entry is null || ShowActionDialogAsync is null) return;

        var vm = CommandActionDialogViewModel.FromAction(entry.Pivot);
        vm.DialogTitle = LocalizeKey("ToolCmdLibDialogTitleEdit");
        vm.Localizer = _localizer;
        vm.UseCategoryLabels(_categoryList, LocalizeKey);

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
    /// Asks what to export, prompts for a destination path, and writes a JSON envelope
    /// containing the chosen actions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The choice comes first and defaults to the operator's own actions: the whole catalogue
    /// is mostly the seeded actions every installation already has, and sharing "my commands"
    /// used to mean sending all of them. "Current filter" exports what the list shows now,
    /// search included; "Everything" is the old behaviour.
    /// </para>
    /// <para>
    /// The secret warning is still shown once, after the choice and the path, because it
    /// applies to whatever is written.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
    public async Task ExportAsync()
    {
        if (ShowSaveFileDialog is null) return;

        var choice = await _dialogService.ShowChoiceAsync(
            LocalizeKey("ToolCmdLibExportScopeTitle"),
            LocalizeKey("ToolCmdLibExportScopeMessage"),
            [
                LocalizeKey("ToolCmdLibExportScopeMine"),
                LocalizeKey("ToolCmdLibExportScopeFiltered"),
                LocalizeKey("ToolCmdLibExportScopeAll"),
            ],
            (int)CommandLibraryExportScope.Mine,
            LocalizeKey("BtnCancel"));
        if (choice is not int index || !Enum.IsDefined(typeof(CommandLibraryExportScope), index)) return;
        var scope = (CommandLibraryExportScope)index;

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
                var actions = await SelectExportActionsAsync(scope, actionService);
                var runbooks = await SelectExportRunbooksAsync(scope);
                var count = await _transferService.ExportAsync(actions, runbooks, path);
                _dialogService.ShowInfo(
                    LocalizeKey("ToolCmdLibExportSuccess"),
                    string.Format(LocalizeKey("ToolCmdLibExportSuccessMessage"), count, path));
            },
            "Export",
            "ToolCmdLibExportError");
    }

    /// <summary>
    /// The actions an export of <paramref name="scope"/> writes.
    /// </summary>
    /// <remarks>
    /// "Mine" and "Everything" read the store, so an action added since the list loaded is not
    /// left out; "Current filter" is by definition what the list shows, so it reads the list.
    /// </remarks>
    internal async Task<IReadOnlyList<TwinShell.Core.Models.Action>> SelectExportActionsAsync(
        CommandLibraryExportScope scope,
        IActionService actionService)
    {
        if (scope == CommandLibraryExportScope.CurrentFilter)
        {
            return _allEntries
                .Where(ShouldShowAction)
                .Select(entry => entry.Source)
                .ToList();
        }

        var all = await actionService.GetAllActionsAsync();
        return scope == CommandLibraryExportScope.Mine
            ? all.Where(action => action.IsUserCreated).ToList()
            : all.ToList();
    }

    /// <summary>
    /// The runbooks an export of <paramref name="scope"/> writes: the operator's own for "My
    /// actions" and "Everything", none for "Current filter", which is about the actions the list
    /// shows. A library without a runbook store exports none.
    /// </summary>
    internal async Task<IReadOnlyList<TwinShell.Core.Models.CommandBatch>> SelectExportRunbooksAsync(
        CommandLibraryExportScope scope)
    {
        if (scope == CommandLibraryExportScope.CurrentFilter)
        {
            return [];
        }

        using var serviceScope = _serviceProvider.CreateScope();
        var batchService = serviceScope.ServiceProvider.GetService<IBatchService>();
        if (batchService is null)
        {
            return [];
        }

        return (await batchService.GetAllBatchesAsync())
            .Where(runbook => runbook.IsUserCreated)
            .ToList();
    }

    /// <summary>
    /// Prompts for a source file, parses the JSON envelope, and merges the
    /// contained actions into the database. System (seed) actions are never
    /// overwritten. Runbooks the file carries are merged by public id.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
    public async Task ImportAsync()
    {
        if (ShowOpenFileDialog is null) return;

        var path = ShowOpenFileDialog("JSON files (*.json)|*.json");
        if (string.IsNullOrEmpty(path)) return;

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                using var batchScope = _serviceProvider.CreateScope();
                var batchService = batchScope.ServiceProvider.GetService<IBatchService>();
                var result = await _transferService.ImportAsync(actionService, batchService, path);
                switch (result.Outcome)
                {
                    case CommandLibraryImportOutcome.FileTooLarge:
                        _dialogService.ShowError(
                            LocalizeKey("ToolCmdLibImportError"),
                            LocalizeKey("ToolCmdLibImportFileTooLarge"));
                        return;
                    case CommandLibraryImportOutcome.FileUnreadable:
                        _dialogService.ShowError(
                            LocalizeKey("ToolCmdLibImportError"),
                            LocalizeKey("ToolCmdLibImportFileUnreadable"));
                        return;
                    case CommandLibraryImportOutcome.InvalidFormat:
                        _dialogService.ShowError(
                            LocalizeKey("ToolCmdLibImportError"),
                            LocalizeKey("ToolCmdLibImportInvalidFormat"));
                        return;
                    default:
                        await ReloadAsync();
                        var runbookCount = result.RunbooksImported + result.RunbooksUpdated;
                        if (runbookCount > 0 && IsRunbooksVisible)
                        {
                            await LoadRunbooksAsync();
                        }

                        _dialogService.ShowInfo(
                            LocalizeKey("ToolCmdLibImportResultTitle"),
                            FormatImportSummary(result, runbookCount));
                        return;
                }
            },
            "Import",
            "ToolCmdLibImportError");
    }

    /// <summary>
    /// The import summary: the action counts, then, when the file carried runbooks that were
    /// added or updated, one line saying how many.
    /// </summary>
    private string FormatImportSummary(CommandLibraryImportResult result, int runbookCount)
    {
        var summary = FormatActionImportSummary(result);
        return runbookCount > 0
            ? summary + Environment.NewLine + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizeKey("ToolCmdLibImportRunbooksMessage"),
                runbookCount)
            : summary;
    }

    private string FormatActionImportSummary(CommandLibraryImportResult result)
        => string.Format(
            LocalizeKey("ToolCmdLibImportResultMessage"),
            _localizer.FormatCount(
                result.Imported,
                "ToolCmdLibImportCountImportedOne",
                "ToolCmdLibImportCountImported",
                result.Imported),
            _localizer.FormatCount(
                result.Updated,
                "ToolCmdLibImportCountUpdatedOne",
                "ToolCmdLibImportCountUpdated",
                result.Updated),
            _localizer.FormatCount(
                result.Skipped,
                "ToolCmdLibImportCountSkippedOne",
                "ToolCmdLibImportCountSkipped",
                result.Skipped));

    // ── Git Sync ──────────────────────────────────────────────────

    /// <summary>
    /// Performs a full Git sync (pull + merge + push) using the configured
    /// repository, then reloads the library. Surfaces the result through the
    /// injected dialog service.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
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
