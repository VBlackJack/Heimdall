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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.CommandLibrary;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Command history partial of <see cref="CommandLibraryViewModel"/>.
/// Records executions and exposes the recent-history listing for the panel.
/// </summary>
public sealed partial class CommandLibraryViewModel
{
    /// <summary>
    /// Maximum number of entries returned by <see cref="LoadHistoryAsync"/>.
    /// Mirrors the original code-behind value so the panel paginates the
    /// same way.
    /// </summary>
    private const int HistoryPageSize = 50;

    /// <summary>
    /// True when the history panel is open but the backing store is empty;
    /// drives the empty-state label inside the panel.
    /// </summary>
    [ObservableProperty]
    private bool _isHistoryEmpty;

    /// <summary>
    /// Reloads the bound <see cref="HistoryEntries"/> collection from the
    /// history service. Safe to call even when the history panel is hidden.
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var historyService = scope.ServiceProvider.GetRequiredService<ICommandHistoryService>();
            var recent = (await historyService.GetRecentAsync(HistoryPageSize)).ToList();

            HistoryEntries.Clear();
            foreach (var h in recent)
            {
                HistoryEntries.Add(new CommandLibraryHistoryEntry
                {
                    ActionTitle = h.ActionTitle,
                    GeneratedCommand = h.GeneratedCommand,
                    Timestamp = h.CreatedAt.ToLocalTime().ToString("g")
                });
            }

            IsHistoryEmpty = HistoryEntries.Count == 0;
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Failed to load history: {ex.Message}");
            _dialogService.ShowError(LocalizeKey("ToolCmdLibErrorTitle"), ex.Message);
        }
    }

    /// <summary>
    /// Confirms with the user, then clears all command history.
    /// </summary>
    [RelayCommand]
    public async Task ClearHistoryAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            LocalizeKey("ToolCmdLibHistoryClearTitle"),
            LocalizeKey("ToolCmdLibHistoryClearConfirm"),
            "warning");
        if (!confirmed) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var historyService = scope.ServiceProvider.GetRequiredService<ICommandHistoryService>();
            await historyService.ClearAllAsync();
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Failed to clear history: {ex.Message}");
            _dialogService.ShowError(LocalizeKey("ToolCmdLibErrorTitle"), ex.Message);
        }
    }

    /// <summary>
    /// Records the active action in the persistent history. Fire-and-forget;
    /// failures are logged but never surfaced to the user.
    /// </summary>
    /// <remarks>
    /// The history insert runs on a worker thread so the Copy/Send hot path
    /// stays snappy. A new DI scope is created inside the <see cref="Task.Run"/>
    /// closure to avoid sharing a <c>DbContext</c> with the UI thread. The
    /// payload is built by <see cref="BuildSecretFreeHistoryPayload"/> so that
    /// user-entered parameter values never reach the unencrypted history store.
    /// </remarks>
    private void RecordHistory()
    {
        var action = _selectedAction;
        var template = _activeTemplate;
        if (action is null || template is null) return;

        var (command, parameters) = BuildSecretFreeHistoryPayload(template);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var historyService = scope.ServiceProvider.GetRequiredService<ICommandHistoryService>();
                await historyService.AddCommandAsync(
                    action.Id, command, parameters,
                    template.Platform, action.Title, action.Category);
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[CommandLibrary] Failed to record history: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Builds the history payload for the active selection. The un-substituted
    /// template pattern is stored and parameter values are intentionally dropped,
    /// so user-entered secrets never reach the unencrypted history store
    /// (<c>twinshell.db</c>). The entry still records which action ran and when.
    /// </summary>
    internal static (string Command, Dictionary<string, string> Parameters) BuildSecretFreeHistoryPayload(
        CommandTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (template.CommandPattern, new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
