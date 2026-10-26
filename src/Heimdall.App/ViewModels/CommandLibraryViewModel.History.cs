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

using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
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
    /// Whether the history panel shows only the rows sent or copied for the host this library
    /// was opened for. Checked by default; meaningful only when <see cref="HasTargetHost"/>.
    /// </summary>
    /// <remarks>
    /// Lives on the view model, not on the panel, so a reload of the rows after a clear or a
    /// new command keeps the operator's choice instead of snapping back to the default.
    /// </remarks>
    [ObservableProperty]
    private bool _historyThisHostOnly = true;

    private ICollectionView? _historyView;

    /// <summary>
    /// True when the library was opened for a host, which is when the "this host only" filter
    /// has something to compare against and is shown.
    /// </summary>
    public bool HasTargetHost => _targetHost is not null;

    /// <summary>
    /// The rows the history panel shows: <see cref="HistoryEntries"/> through
    /// <see cref="ShouldShowHistoryEntry"/>.
    /// </summary>
    /// <remarks>
    /// The collection's default view, which is the one the panel's list binds to when it binds
    /// <see cref="HistoryEntries"/>; filtering it here filters the panel without a second
    /// collection to keep in step.
    /// </remarks>
    public ICollectionView HistoryView
    {
        get
        {
            if (_historyView is null)
            {
                _historyView = CollectionViewSource.GetDefaultView(HistoryEntries);
                _historyView.Filter = item => item is CommandLibraryHistoryEntry entry && ShouldShowHistoryEntry(entry);
            }

            return _historyView;
        }
    }

    /// <summary>
    /// Whether a history row passes the "this host only" filter.
    /// </summary>
    /// <remarks>
    /// A row without a host - written before hosts were recorded, or from a library opened
    /// outside any session - is hidden while the filter is on: nothing says it went to this
    /// host, and showing it under that heading would claim it did. Host names compare without
    /// regard to case, as DNS does.
    /// </remarks>
    public bool ShouldShowHistoryEntry(CommandLibraryHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!HistoryThisHostOnly || _targetHost is null)
        {
            return true;
        }

        return string.Equals(entry.TargetHost, _targetHost, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnHistoryThisHostOnlyChanged(bool value) => RefreshHistoryView();

    /// <summary>
    /// Re-applies the history filter and updates the empty-state flag from what is visible.
    /// </summary>
    private void RefreshHistoryView()
    {
        HistoryView.Refresh();
        IsHistoryEmpty = HistoryView.IsEmpty;
    }

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
                    IsReadable = h.IsReadable,
                    ActionId = h.ActionId,
                    Platform = h.Platform,
                    Parameters = h.Parameters,
                    TargetHost = h.TargetHost,
                    IsExecuted = h.IsExecuted,
                    Localize = LocalizeKey,
                    // Explicit CurrentCulture, matching every other formatting site in the
                    // app. Heimdall has no mapping from its own locale to a CultureInfo,
                    // so introducing one here would make this the only surface that
                    // disagrees with the rest of the application.
                    Timestamp = h.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                });
            }

            RefreshHistoryView();
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
    /// Puts a history row back into the generator: selects the action it came from, picks
    /// the template for the platform it ran on, and restores the parameter values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the rest of the history work is for. Recording the real command made
    /// the panel able to say what was done; this makes it able to do it again, which is
    /// the reason to look at a history at all.
    /// </para>
    /// <para>
    /// Three things can have changed since the row was written, and each is handled rather
    /// than assumed away: the action can have been deleted, its templates can have changed
    /// platform, and its parameters can have been renamed or removed. Values whose
    /// parameter is gone are dropped silently - they have nowhere to go - and parameters
    /// the row does not mention keep whatever the template defaults them to, rather than
    /// being blanked. A replay that half-matches is still a better starting point than an
    /// empty form, as long as the command line then shows exactly what will run.
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void ReplayHistoryEntry(CommandLibraryHistoryEntry? entry)
    {
        if (entry is null || !entry.CanReplay) return;

        var target = _allEntries.FirstOrDefault(
            candidate => string.Equals(candidate.Source.Id, entry.ActionId, StringComparison.Ordinal));
        if (target is null)
        {
            Heimdall.Core.Logging.FileLogger.Info(
                $"[CommandLibrary] Replay skipped: action '{entry.ActionId}' no longer exists.");
            _dialogService.ShowWarning(
                LocalizeKey("ToolCmdLibHistoryReplayTitle"),
                LocalizeKey("ToolCmdLibHistoryReplayMissingAction"));
            return;
        }

        // Replaying the action already selected changes no selection, so the setter shows
        // nothing and the generator the open history had hidden stays hidden.
        if (ReferenceEquals(SelectedEntry, target))
        {
            ShowGeneratorForSelection();
        }
        else
        {
            SelectedEntry = target;
        }

        if (!IsGeneratorVisible) return;

        if (HasMultipleTemplates)
        {
            SelectTemplatePlatform(entry.Platform == TwinShell.Core.Enums.Platform.Windows);
        }

        foreach (var parameter in _parameters)
        {
            if (entry.Parameters.TryGetValue(parameter.Name, out var recorded))
            {
                parameter.Value = recorded;
            }
        }

        IsHistoryVisible = false;
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
    /// <para>
    /// <paramref name="isExecuted"/> is a parameter rather than a field read afterwards because
    /// only the gesture knows it: Send and Broadcast ran the command, Copy and Insert did not,
    /// and a flag set beside the call would be one more piece of state a later gesture could
    /// leave stale.
    /// </para>
    /// </remarks>
    /// <param name="isExecuted">True when the gesture ran the command in a terminal.</param>
    private void RecordHistory(bool isExecuted)
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

        WriteHistoryRow(action, command, parameters, template.Platform, isExecuted);
    }

    /// <summary>
    /// Writes one history row for <paramref name="action"/>, against the host this library was
    /// opened for, on a worker thread. The payload is passed in already captured.
    /// </summary>
    /// <remarks>
    /// Shared by the generator's gestures and a runbook run's steps, so a step sent from a
    /// runbook reads in the history panel exactly as the same command sent from the generator.
    /// </remarks>
    private void WriteHistoryRow(
        TwinShell.Core.Models.Action action,
        string command,
        Dictionary<string, string> parameters,
        TwinShell.Core.Enums.Platform platform,
        bool isExecuted)
    {
        var targetHost = _targetHost;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var historyService = scope.ServiceProvider.GetRequiredService<ICommandHistoryService>();
                await historyService.AddCommandAsync(
                    action.Id, command, parameters,
                    platform, action.Title, action.Category,
                    targetHost, isExecuted);
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
