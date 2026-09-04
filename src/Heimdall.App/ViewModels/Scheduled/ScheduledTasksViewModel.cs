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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Scheduled;

/// <summary>
/// View-model backing the Scheduled tab: list of persisted
/// <see cref="ScheduledTaskDto"/> entries + add/edit/delete commands +
/// ownership of the <see cref="TaskSchedulerService"/> background timer
/// that fires tasks when they become due.
/// </summary>
/// <remarks>
/// Composition: instantiated inside <see cref="MainViewModel"/>'s
/// constructor (<see cref="MainViewModel.Scheduled"/>) - no DI
/// registration. Creates its own
/// <see cref="TaskSchedulerService"/> instance in the constructor
/// (configured with <c>TasksProvider</c>, <c>TaskDueCallback</c> and
/// <c>PersistCallback</c>), populates <see cref="Tasks"/> from settings
/// via <see cref="Load"/>, and disposes the scheduler via
/// <see cref="Dispose"/> (called from <see cref="MainViewModel.Dispose"/>
/// and <c>App.OnExit</c> through the <c>StopScheduler</c> shim).
/// </remarks>
public sealed partial class ScheduledTasksViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly IConfigManager _configManager;
    private readonly TaskSchedulerService _taskScheduler;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Creates a new scheduled tasks VM and instantiates its owned
    /// <see cref="TaskSchedulerService"/> with the callbacks wired up.
    /// The scheduler is not started until <see cref="Load"/> is invoked
    /// from <see cref="MainViewModel.LoadAsync"/>.
    /// </summary>
    public ScheduledTasksViewModel(
        MainViewModel main,
        LocalizationManager localizer,
        IDialogService dialogService,
        IConfigManager configManager,
        IUiDispatcher uiDispatcher)
    {
        _main = main;
        _localizer = localizer;
        _dialogService = dialogService;
        _configManager = configManager;

        _taskScheduler = new TaskSchedulerService(uiDispatcher)
        {
            TasksProvider = () => Tasks.ToList(),
            TaskDueCallback = OnTaskDueAsync,
            PersistCallback = SaveAsync
        };
    }

    // ── Observable state ─────────────────────────────────────────────

    /// <summary>Persisted scheduled-task entries - bound by the Scheduled tab DataGrid.</summary>
    [ObservableProperty]
    private ObservableCollection<ScheduledTaskDto> _tasks = new();

    /// <summary>Row currently selected in the Scheduled tab DataGrid.</summary>
    [ObservableProperty]
    private ScheduledTaskDto? _selectedTask;

    /// <summary>True when there are no scheduled tasks.</summary>
    public bool HasNoTasks => Tasks.Count == 0;

    /// <summary>
    /// Generated partial: refreshes the <see cref="HasNoTasks"/>
    /// notification whenever the task collection is replaced.
    /// </summary>
    partial void OnTasksChanged(ObservableCollection<ScheduledTaskDto> value)
    {
        OnPropertyChanged(nameof(HasNoTasks));
    }

    // ── Commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Opens the scheduled-task dialog in Add mode, saves on OK, and
    /// surfaces the result in the shell status bar.
    /// </summary>
    [RelayCommand]
    private async Task AddTaskAsync(CancellationToken cancellationToken)
    {
        var vm = new ScheduledTaskDialogViewModel
        {
            DialogTitle = _localizer["ScheduledTaskDialogTitleAdd"]
        };

        await PopulateServersAsync(vm);

        var result = await _dialogService.ShowScheduledTaskDialogAsync(vm);
        if (result is null)
        {
            return;
        }

        TaskSchedulerService.ComputeNextRun(result.Task, DateTime.Now);
        Tasks.Add(result.Task);
        OnPropertyChanged(nameof(HasNoTasks));
        await SaveAsync();
        _main.StatusText = _localizer.Format("StatusScheduledTaskAdded", result.Task.ServerName);
    }

    /// <summary>
    /// Opens the scheduled-task dialog in Edit mode for
    /// <see cref="SelectedTask"/>, replaces the task in place on OK,
    /// saves, and surfaces the result in the shell status bar.
    /// </summary>
    [RelayCommand]
    private async Task EditTaskAsync(CancellationToken cancellationToken)
    {
        if (SelectedTask is null)
        {
            return;
        }

        var vm = ScheduledTaskDialogViewModel.FromDto(SelectedTask);
        vm.DialogTitle = _localizer["ScheduledTaskDialogTitleEdit"];
        await PopulateServersAsync(vm);
        vm.SelectServerById(SelectedTask.ServerId);

        var result = await _dialogService.ShowScheduledTaskDialogAsync(vm);
        if (result is null)
        {
            return;
        }

        // Replace the existing task in the collection
        var index = Tasks.IndexOf(SelectedTask);
        if (index >= 0)
        {
            TaskSchedulerService.ComputeNextRun(result.Task, DateTime.Now);
            Tasks[index] = result.Task;
            SelectedTask = result.Task;
        }

        await SaveAsync();
        _main.StatusText = _localizer.Format("StatusScheduledTaskUpdated", result.Task.ServerName);
    }

    /// <summary>
    /// Confirms and deletes the <see cref="SelectedTask"/>, persists the
    /// change, and surfaces the result in the shell status bar.
    /// </summary>
    [RelayCommand]
    private async Task DeleteTaskAsync(CancellationToken cancellationToken)
    {
        if (SelectedTask is null)
        {
            return;
        }

        var taskName = SelectedTask.ServerName;
        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmDeleteScheduledTaskTitle"],
            _localizer.Format("ConfirmDeleteScheduledTaskMessage", taskName),
            "danger");

        if (!confirmed)
        {
            return;
        }

        Tasks.Remove(SelectedTask);
        OnPropertyChanged(nameof(HasNoTasks));
        await SaveAsync();
        _main.StatusText = _localizer.Format("StatusScheduledTaskDeleted", taskName);
    }

    // ── Public lifecycle ─────────────────────────────────────────────

    /// <summary>
    /// Populates <see cref="Tasks"/> from the given settings snapshot,
    /// recomputes <c>NextRun</c> for any enabled task that lacks it
    /// (migration path for legacy entries), and starts the scheduler
    /// timer on the first call. Subsequent calls (tab switches, config
    /// reloads) re-populate the list but do not re-start the already
    /// running timer.
    /// </summary>
    public void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Tasks = new ObservableCollection<ScheduledTaskDto>(settings.ScheduledTasks);

        // Compute NextRun for any tasks that lack it (e.g., migrated from old format)
        var now = DateTime.Now;
        foreach (var task in Tasks)
        {
            if (task.NextRun is null && task.Enabled)
            {
                TaskSchedulerService.ComputeNextRun(task, now);
            }
        }

        if (!_started)
        {
            _taskScheduler.Start();
            _started = true;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    /// <summary>
    /// Populates the server-options list on the given dialog VM from the
    /// current server inventory (loaded fresh from disk to pick up any
    /// external edits).
    /// </summary>
    private async Task PopulateServersAsync(ScheduledTaskDialogViewModel vm)
    {
        var servers = await _configManager.LoadServersAsync();
        var options = servers
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ServerOption(
                s.Id,
                s.DisplayName,
                $"{s.DisplayName} ({s.ConnectionType})",
                s.ConnectionType ?? "SSH"))
            .ToList();

        vm.AvailableServers = new ObservableCollection<ServerOption>(options);
    }

    /// <summary>
    /// Persists the current <see cref="Tasks"/> collection back to
    /// <see cref="AppSettings.ScheduledTasks"/>.
    /// </summary>
    private async Task SaveAsync()
    {
        var settings = await _configManager.LoadSettingsAsync();
        settings.ScheduledTasks = [.. Tasks];
        await _configManager.SaveSettingsAsync(settings);
    }

    /// <summary>
    /// Called by <see cref="TaskSchedulerService"/> when a scheduled
    /// task is due. Resolves the target server (by Id first, then by
    /// display-name fallback) and triggers the standard connection flow.
    /// </summary>
    private async Task OnTaskDueAsync(ScheduledTaskDto task)
    {
        FileLogger.Info(
            $"Executing scheduled task '{task.ServerName}' (serverId={task.ServerId}, type={task.ConnectionType}).");

        _main.StatusText = _localizer.Format(
            "StatusScheduledTaskTriggered", task.ServerName, task.ConnectionType);

        // The rule lives in ScheduledTaskServerResolver, where it can be run by a test. It used
        // to fall back to the first profile with a matching DISPLAY NAME whenever the identifier
        // failed to resolve - and display names are not unique, so a task whose profile had been
        // deleted or re-identified opened a session on a different machine, unattended.
        // AllServers, not Servers: the latter is the sidebar's FILTERED view, and a task asks a
        // question about the inventory, not about what the user is currently looking at. Reading
        // the filtered view meant a filter could hide the profile a task names - after which the
        // name rule found a different profile of that name among the visible ones and connected
        // to it, which is the outcome the rule exists to prevent.
        var server = ScheduledTaskServerResolver.Resolve(
            task.ServerId,
            task.ServerName,
            _main.ServerList.AllServers,
            s => s.Id,
            s => s.DisplayName);

        if (server is null)
        {
            FileLogger.Warn(
                $"Scheduled task '{task.ServerName}': server not found in inventory. Skipping.");
            _main.StatusText = _localizer.Format("ErrorScheduledTaskFailed",
                $"Server '{task.ServerName}' not found");
            return;
        }

        try
        {
            _main.ServerList.ConnectCommand.Execute(server);
        }
        catch (Exception ex)
        {
            FileLogger.Error(
                $"Scheduled task '{task.ServerName}' connection failed: {ex.Message}");
            _main.StatusText = _localizer.Format("ErrorScheduledTaskFailed", ex.Message);
        }

        await Task.CompletedTask;
    }

    // ── IDisposable ──────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _taskScheduler.Stop();
        _taskScheduler.Dispose();
    }
}
