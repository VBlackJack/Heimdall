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

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Runbooks partial of <see cref="CommandLibraryViewModel"/>: the side panel that lists the
/// operator's runbooks and the New / Edit / Duplicate / Delete gestures over them.
/// </summary>
/// <remarks>
/// A runbook is a TwinShell <see cref="CommandBatch"/>: an ordered list of steps, each a library
/// action with its parameter values or a literal command. It is user data only; nothing seeds
/// one.
/// </remarks>
public sealed partial class CommandLibraryViewModel
{
    /// <summary>Whether the Runbooks panel is open.</summary>
    [ObservableProperty]
    private bool _isRunbooksVisible;

    /// <summary>True when the panel is open on a store with no runbook.</summary>
    [ObservableProperty]
    private bool _isRunbooksEmpty;

    /// <summary>The runbook the panel's buttons act on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditRunbookCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateRunbookCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunbookCommand))]
    private RunbookListEntry? _selectedRunbook;

    /// <summary>
    /// The platform the session this library was opened from runs, as
    /// <see cref="RemoteOsResolver"/> reads it; Auto when it cannot be told.
    /// </summary>
    private RemoteOs _sessionPlatform = RemoteOs.Auto;

    /// <summary>The runbooks, most recently changed first.</summary>
    public ObservableCollection<RunbookListEntry> Runbooks { get; } = new();

    /// <summary>
    /// Async callback the view installs to show the runbook editor. Returns true when the
    /// operator saved.
    /// </summary>
    public Func<RunbookDialogViewModel, Task<bool>>? ShowRunbookDialogAsync { get; set; }

    /// <summary>True when a runbook is selected and no other library operation is running.</summary>
    public bool CanChangeSelectedRunbook => SelectedRunbook is not null && IsLibraryIdle;

    /// <summary>
    /// Passes a change of the terminal probe on to a run in progress, whose Send and Insert
    /// read the same probe.
    /// </summary>
    partial void RefreshRunbookRunSendState();

    /// <summary>Re-evaluates the runbook gestures gated on <see cref="IsLibraryIdle"/>.</summary>
    private void NotifyRunbookCommands()
    {
        NewRunbookCommand.NotifyCanExecuteChanged();
        EditRunbookCommand.NotifyCanExecuteChanged();
        DuplicateRunbookCommand.NotifyCanExecuteChanged();
        DeleteRunbookCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Shows or hides the Runbooks panel. Opening it closes the generator and the history, the
    /// two other panels that share the bottom of the tool, and reloads the list.
    /// </summary>
    [RelayCommand]
    public async Task ToggleRunbooksAsync()
    {
        if (IsRunbooksVisible)
        {
            IsRunbooksVisible = false;
            return;
        }

        IsGeneratorVisible = false;
        IsHistoryVisible = false;
        IsRunbooksVisible = true;
        await LoadRunbooksAsync();
    }

    /// <summary>
    /// Reloads <see cref="Runbooks"/> from the store, keeping the selection by id.
    /// </summary>
    public async Task LoadRunbooksAsync()
    {
        var selectedId = SelectedRunbook?.Source.Id;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var batchService = scope.ServiceProvider.GetRequiredService<IBatchService>();
            var runbooks = (await batchService.GetAllBatchesAsync()).ToList();

            Runbooks.Clear();
            foreach (var runbook in runbooks)
            {
                Runbooks.Add(new RunbookListEntry(runbook, FormatStepCount(runbook), FormatLastRun(runbook)));
            }

            SelectedRunbook = Runbooks.FirstOrDefault(
                entry => string.Equals(entry.Source.Id, selectedId, StringComparison.Ordinal));
            IsRunbooksEmpty = Runbooks.Count == 0;
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Failed to load runbooks: {ex.Message}");
            _dialogService.ShowError(LocalizeKey("ToolCmdLibErrorTitle"), ex.Message);
        }
    }

    private string FormatStepCount(CommandBatch runbook)
        => _localizer.FormatCount(
            runbook.Commands.Count,
            "ToolCmdLibRunbookStepCountOne",
            "ToolCmdLibRunbookStepCount",
            runbook.Commands.Count);

    private string FormatLastRun(CommandBatch runbook)
        => runbook.LastExecutedAt is DateTime lastRun
            ? string.Format(
                CultureInfo.CurrentCulture,
                LocalizeKey("ToolCmdLibRunbookLastRunFormat"),
                lastRun.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : LocalizeKey("ToolCmdLibRunbookNeverRun");

    /// <summary>Opens the editor on a new runbook and stores it when the operator saves.</summary>
    [RelayCommand(CanExecute = nameof(IsLibraryIdle))]
    public async Task NewRunbookAsync()
    {
        var vm = PrepareRunbookDialog(
            RunbookDialogViewModel.ForNew(LocalizeKey, FindRunbookAction),
            "ToolCmdLibRunbookDialogTitleNew");
        await SaveRunbookFromDialogAsync(vm, isNew: true);
    }

    /// <summary>Opens the editor on the selected runbook and stores the changes.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelectedRunbook))]
    public async Task EditRunbookAsync()
    {
        var entry = SelectedRunbook;
        if (entry is null) return;

        var vm = PrepareRunbookDialog(
            RunbookDialogViewModel.FromRunbook(entry.Source, LocalizeKey, FindRunbookAction),
            "ToolCmdLibRunbookDialogTitleEdit");
        await SaveRunbookFromDialogAsync(vm, isNew: false);
    }

    /// <summary>Opens the editor on a copy of the selected runbook and stores it as a new one.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelectedRunbook))]
    public async Task DuplicateRunbookAsync()
    {
        var entry = SelectedRunbook;
        if (entry is null) return;

        var vm = PrepareRunbookDialog(
            RunbookDialogViewModel.AsCopyOf(
                entry.Source,
                string.Format(CultureInfo.CurrentCulture, LocalizeKey("ToolCmdLibDuplicateTitleFormat"), entry.Name),
                LocalizeKey,
                FindRunbookAction),
            "ToolCmdLibRunbookDialogTitleDuplicate");
        await SaveRunbookFromDialogAsync(vm, isNew: true);
    }

    /// <summary>Confirms, then deletes the selected runbook and its steps.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelectedRunbook))]
    public async Task DeleteRunbookAsync()
    {
        var entry = SelectedRunbook;
        if (entry is null) return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            LocalizeKey("ToolCmdLibRunbookDeleteConfirmTitle"),
            string.Format(CultureInfo.CurrentCulture, LocalizeKey("ToolCmdLibRunbookDeleteConfirmMessage"), entry.Name),
            "warning");
        if (!confirmed) return;

        if (await RunBatchServiceOperationAsync(
                batchService => batchService.DeleteBatchAsync(entry.Source.Id),
                "Delete runbook"))
        {
            SelectedRunbook = null;
            await LoadRunbooksAsync();
        }
    }

    private RunbookDialogViewModel PrepareRunbookDialog(RunbookDialogViewModel vm, string titleKey)
    {
        vm.DialogTitle = LocalizeKey(titleKey);
        vm.PickActionAsync = PickRunbookActionAsync;
        vm.NewLiteralIsWindows = _sessionPlatform == RemoteOs.Windows;
        return vm;
    }

    private async Task SaveRunbookFromDialogAsync(RunbookDialogViewModel vm, bool isNew)
    {
        if (ShowRunbookDialogAsync is null) return;
        if (!await ShowRunbookDialogAsync(vm)) return;

        var runbook = vm.ToRunbook();
        PrepareRunbookForSave(runbook);

        CommandBatch? saved = null;
        var ok = await RunBatchServiceOperationAsync(
            async batchService =>
            {
                if (isNew)
                {
                    saved = await batchService.CreateBatchAsync(runbook);
                }
                else
                {
                    await batchService.UpdateBatchAsync(runbook);
                    saved = runbook;
                }
            },
            isNew ? "Create runbook" : "Update runbook");
        if (!ok) return;

        await LoadRunbooksAsync();
        SelectedRunbook = Runbooks.FirstOrDefault(
            entry => string.Equals(entry.Source.Id, saved?.Id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Fills what a stored action step carries beside its values: the action's English title,
    /// the platform of the template it opens on in this session, and the command that template
    /// generates with the variables' defaults, kept as a cache for Git sync and for a reader of
    /// the export.
    /// </summary>
    internal void PrepareRunbookForSave(CommandBatch runbook)
    {
        ArgumentNullException.ThrowIfNull(runbook);

        foreach (var item in runbook.Commands)
        {
            if (string.IsNullOrWhiteSpace(item.ActionId)) continue;

            var entry = _allEntries.FirstOrDefault(
                candidate => string.Equals(candidate.Source.Id, item.ActionId, StringComparison.Ordinal));
            if (entry is null) continue;

            item.ActionTitle = entry.Pivot.Title;
            var template = SelectRunbookTemplate(entry.Source, _sessionPlatform);
            if (template is null) continue;

            item.Platform = template.Platform;
            var unknown = new HashSet<string>(StringComparer.Ordinal);
            var values = RunbookVariables.Resolve(item.Parameters, runbook.Variables, unknown);
            item.Command = TryGenerate(template, values) ?? template.CommandPattern;
        }
    }

    private string? TryGenerate(CommandTemplate template, Dictionary<string, string> values)
    {
        if (_commandGenerator is null) return null;

        try
        {
            return _commandGenerator.GenerateCommand(template, values);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The template an action step opens on for a session running <paramref name="platform"/>:
    /// the matching one, the other when the action has only one, and Windows first when the
    /// platform cannot be told, which is where the library's own list opens.
    /// </summary>
    internal static CommandTemplate? SelectRunbookTemplate(ActionModel action, RemoteOs platform)
    {
        ArgumentNullException.ThrowIfNull(action);

        return platform == RemoteOs.Linux
            ? action.LinuxCommandTemplate ?? action.WindowsCommandTemplate
            : action.WindowsCommandTemplate ?? action.LinuxCommandTemplate;
    }

    /// <summary>The loaded action with <paramref name="actionId"/>, in the operator's language.</summary>
    internal ActionModel? FindRunbookAction(string actionId)
        => _allEntries.FirstOrDefault(
            entry => string.Equals(entry.Source.Id, actionId, StringComparison.Ordinal))?.Source;

    /// <summary>
    /// Opens the library picker on this session's platform. No session value is prefilled: in a
    /// runbook a host or a user is better written as a variable, and a prefilled value would hide
    /// that choice.
    /// </summary>
    private Task<CommandLibraryPickerResult?> PickRunbookActionAsync()
    {
        var picker = new CommandLibraryPickerDialogViewModel(
            _localizer,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>())
        {
            RemoteOs = _sessionPlatform
        };
        return _dialogService.ShowCommandLibraryPickerAsync(picker);
    }

    /// <summary>
    /// Runs a runbook store operation inside a fresh scope under the library's shared busy flag,
    /// the same envelope the action operations use.
    /// </summary>
    private async Task<bool> RunBatchServiceOperationAsync(Func<IBatchService, Task> operation, string logLabel)
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
            await operation(scope.ServiceProvider.GetRequiredService<IBatchService>());
            return true;
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] {logLabel} failed: {ex.Message}");
            _dialogService.ShowError(LocalizeKey("ToolCmdLibErrorTitle"), ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
