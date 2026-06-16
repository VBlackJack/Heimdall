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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Commands partial of <see cref="CommandLibraryViewModel"/>: copy/send,
/// CRUD, favorites, sync, import/export, and panel toggles.
/// </summary>
public sealed partial class CommandLibraryViewModel
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ── Copy / Send / Clipboard helpers ───────────────────────────

    /// <summary>
    /// Copies the current generated command to the clipboard, records a
    /// history entry, and triggers the visual feedback animation.
    /// </summary>
    [RelayCommand]
    public void Copy()
    {
        if (string.IsNullOrEmpty(GeneratedCommand)) return;
        SetClipboardText?.Invoke(GeneratedCommand);
        ShowCopyFeedback?.Invoke("copy");
        RecordHistory(GeneratedCommand);
    }

    /// <summary>
    /// Invokes the registered Send-to-Terminal handler with the current
    /// generated command and records the action in history.
    /// </summary>
    [RelayCommand]
    public void Send()
    {
        if (string.IsNullOrEmpty(GeneratedCommand) || SendCommandHandler is null) return;
        SendCommandHandler(GeneratedCommand);
        ShowCopyFeedback?.Invoke("send");
        RecordHistory(GeneratedCommand);
    }

    /// <summary>
    /// Replaces the generator output with an example command and copies it
    /// to the clipboard in one gesture (used by the example copy button).
    /// </summary>
    [RelayCommand]
    public void CopyExample(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;
        SetClipboardText?.Invoke(command);
        ShowCopyFeedback?.Invoke("example");
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
        SetClipboardText?.Invoke(command);
        TriggerHistoryCopyFeedback();
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

        await RunActionServiceOperationAsync(
            async actionService =>
            {
                var actions = (await actionService.GetAllActionsAsync()).ToList();

                var envelope = new
                {
                    schemaVersion = "1.0",
                    exportDate = DateTime.UtcNow,
                    totalActions = actions.Count,
                    actions
                };

                var json = JsonSerializer.Serialize(envelope, ExportOptions);
                await File.WriteAllTextAsync(path, json);

                _dialogService.ShowInfo(
                    LocalizeKey("ToolCmdLibExportSuccess"),
                    string.Format(LocalizeKey("ToolCmdLibExportSuccessMessage"), actions.Count, path));
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
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > AppConstants.MaxImportFileSizeBytes)
                {
                    _dialogService.ShowError(
                        LocalizeKey("ToolCmdLibImportError"),
                        LocalizeKey("ToolCmdLibImportFileTooLarge"));
                    return;
                }

                var json = await File.ReadAllTextAsync(path);
                var actions = ParseImportJson(json);
                if (actions is null || actions.Count == 0)
                {
                    _dialogService.ShowError(
                        LocalizeKey("ToolCmdLibImportError"),
                        LocalizeKey("ToolCmdLibImportInvalidFormat"));
                    return;
                }

                int imported = 0, updated = 0, skipped = 0;

                foreach (var action in actions)
                {
                    if (string.IsNullOrWhiteSpace(action.Title)
                        || string.IsNullOrWhiteSpace(action.Category)
                        || action.Title.Length > 200
                        || action.Category.Length > 100)
                    {
                        skipped++;
                        continue;
                    }

                    var existing = await actionService.GetActionByPublicIdAsync(action.PublicId)
                        ?? await actionService.GetActionByIdAsync(action.Id);
                    if (existing is not null)
                    {
                        if (existing.IsUserCreated)
                        {
                            action.Id = existing.Id;
                            action.PublicId = existing.PublicId;
                            action.IsUserCreated = existing.IsUserCreated;
                            action.UpdatedAt = DateTime.UtcNow;
                            await actionService.UpdateActionAsync(action);
                            updated++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    else
                    {
                        action.IsUserCreated = true;
                        action.CreatedAt = DateTime.UtcNow;
                        action.UpdatedAt = DateTime.UtcNow;
                        await actionService.CreateActionAsync(action);
                        imported++;
                    }
                }

                await ReloadAsync();
                _dialogService.ShowInfo(
                    LocalizeKey("ToolCmdLibImportResultTitle"),
                    string.Format(
                        LocalizeKey("ToolCmdLibImportResultMessage"), imported, updated, skipped));
            },
            "Import",
            "ToolCmdLibImportError");
    }

    private static List<ActionModel>? ParseImportJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Bulk format: { "actions": [...] }
            if (root.TryGetProperty("actions", out var actionsElement)
                && actionsElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<ActionModel>>(
                    actionsElement.GetRawText(), ImportOptions);
            }

            // Single-action format: { "id": "...", "title": "..." }
            if (root.TryGetProperty("id", out _) && root.TryGetProperty("title", out _))
            {
                var single = JsonSerializer.Deserialize<ActionModel>(json, ImportOptions);
                return single is not null ? [single] : null;
            }

            return null;
        }
        catch
        {
            return null;
        }
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

            if (result.ErrorCode == GitSyncErrorCode.Cancelled)
            {
                var message = string.IsNullOrWhiteSpace(result.Message)
                    ? LocalizeKey("ToolCmdLibSyncCancelledMessage")
                    : result.Message;
                _dialogService.ShowInfo(
                    LocalizeKey("ToolCmdLibSyncCancelled"),
                    message);
            }
            else if (result.Success)
            {
                var hasWarnings = result.Warnings?.Count > 0;
                if (hasWarnings)
                {
                    _dialogService.ShowWarning(
                        LocalizeKey("ToolCmdLibSyncPartial"),
                        result.Message ?? LocalizeKey("ToolCmdLibSyncComplete"));
                }
                else
                {
                    _dialogService.ShowInfo(
                        LocalizeKey("ToolCmdLibSyncComplete"),
                        result.Message ?? LocalizeKey("ToolCmdLibSyncComplete"));
                }
            }
            else
            {
                _dialogService.ShowError(
                    LocalizeKey("ToolCmdLibSyncError"),
                    result.ErrorDetails ?? result.Message ?? LocalizeKey("ToolCmdLibSyncError"));
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
