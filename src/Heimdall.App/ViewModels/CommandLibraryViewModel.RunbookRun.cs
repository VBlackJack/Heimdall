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

namespace Heimdall.App.ViewModels;

/// <summary>
/// Runbook run partial of <see cref="CommandLibraryViewModel"/>: starts a run of the selected
/// runbook in the Runbooks panel, wires it to the terminal the library was opened from, and
/// records what it sends.
/// </summary>
public sealed partial class CommandLibraryViewModel
{
    /// <summary>The run shown in the Runbooks panel, or null while the panel lists runbooks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunbookRunVisible))]
    [NotifyPropertyChangedFor(nameof(IsRunbookListVisible))]
    private RunbookRunViewModel? _activeRun;

    /// <summary>True while a run occupies the Runbooks panel.</summary>
    public bool IsRunbookRunVisible => ActiveRun is not null;

    /// <summary>True while the Runbooks panel shows the list.</summary>
    public bool IsRunbookListVisible => ActiveRun is null;

    /// <summary>True when a runbook is selected, which is what Run needs.</summary>
    public bool HasSelectedRunbook => SelectedRunbook is not null;

    partial void RefreshRunbookRunSendState() => ActiveRun?.RefreshSendState();

    partial void OnSelectedRunbookChanged(RunbookListEntry? value)
    {
        OnPropertyChanged(nameof(HasSelectedRunbook));
        RunRunbookCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Switches the panel to a run of the selected runbook, on its first step, with its
    /// variables prefilled from the session.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRunbook))]
    public void RunRunbook()
    {
        var entry = SelectedRunbook;
        if (entry is null || _commandGenerator is null) return;

        var runbookId = entry.Source.Id;
        var run = new RunbookRunViewModel(entry.Source, new RunbookRunContext
        {
            FindAction = FindRunbookAction,
            Generator = _commandGenerator,
            DialogService = _dialogService,
            Localize = LocalizeKey,
            Platform = _sessionPlatform,
            TargetHost = _targetHost,
            Username = _username,
            Send = SendCommandHandler,
            Insert = InsertTextHandler,
            CanSendToTerminal = CanSendToTerminalProbe,
            SetClipboard = SetClipboardText,
            RecordHistory = RecordRunbookHistory,
            RunStarted = () => _ = MarkRunbookRun(runbookId)
        });
        run.StopRequested += OnRunbookStopRequested;
        ActiveRun = run;
    }

    private void OnRunbookStopRequested()
    {
        if (ActiveRun is not null)
        {
            ActiveRun.StopRequested -= OnRunbookStopRequested;
        }

        ActiveRun = null;
        _ = LoadRunbooksAsync();
    }

    private void RecordRunbookHistory(RunbookHistoryRecord record)
        => WriteHistoryRow(
            record.Action,
            record.Command,
            record.Parameters,
            record.Platform,
            record.IsExecuted);

    /// <summary>
    /// Stores the run's start as the runbook's last run. Fire and forget, like the history: a
    /// failure is logged and never interrupts the run. The returned task is for tests.
    /// </summary>
    /// <remarks>
    /// Through <see cref="IBatchService.MarkExecutedAsync"/>, never an update of the runbook: an
    /// update stamps UpdatedAt, which Git sync reads as a local edit newer than the remote copy.
    /// </remarks>
    internal Task MarkRunbookRun(string runbookId)
        => Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var batchService = scope.ServiceProvider.GetRequiredService<IBatchService>();
                await batchService.MarkExecutedAsync(runbookId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[CommandLibrary] Failed to record the runbook's last run: {ex.Message}");
            }
        });
}
