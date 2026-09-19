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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.Core.Security.Vault;
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
                    // Explicit CurrentCulture, matching every other formatting site in the
                    // app. Heimdall has no mapping from its own locale to a CultureInfo,
                    // so introducing one here would make this the only surface that
                    // disagrees with the rest of the application.
                    Timestamp = h.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
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
    /// <para>
    /// The history insert runs on a worker thread so the Copy/Send hot path stays snappy.
    /// A new DI scope is created inside the <see cref="Task.Run"/> closure to avoid
    /// sharing a <c>DbContext</c> with the UI thread.
    /// </para>
    /// <para>
    /// The command and the parameter values are what the user typed, and they are now
    /// recorded as they are. They do not reach the database in clear: the persistence
    /// layer seals both fields (see <c>HistorySecretEnvelope</c>). This replaces a payload
    /// that stored the un-substituted pattern and dropped every value, which kept the
    /// database safe by making the history unable to say what had been run.
    /// </para>
    /// <para>
    /// The payload is captured here, on the UI thread, before the worker starts: the
    /// parameter entries are observable and the user is free to keep editing them the
    /// instant Copy returns.
    /// </para>
    /// </remarks>
    private void RecordHistory()
    {
        var action = _selectedAction;
        var template = _activeTemplate;
        if (action is null || template is null) return;

        var command = GeneratedCommand;
        if (string.IsNullOrEmpty(command)) return;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in _parameters)
        {
            parameters[parameter.Name] = parameter.Value;
        }

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
            catch (VaultLockedException)
            {
                // Nothing is written while the vault is locked. Recording the secret-free
                // payload instead would put two fidelities in one list with nothing to
                // tell them apart, and writing the real one is what the vault forbids.
                // A gap the user can explain beats a row that quietly means less.
                Heimdall.Core.Logging.FileLogger.Info(
                    "[CommandLibrary] History not recorded: the vault is locked.");
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[CommandLibrary] Failed to record history: {ex.Message}");
            }
        });
    }
}
