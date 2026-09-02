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
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.CronJob;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

public enum CronJobMode
{
    Paste = 0,
    Tasks = 1,
}

public sealed partial class CronJobViewModel : ObservableObject, IDisposable
{
    public const int NextRunsCount = 5;
    public const int LoadTimeoutSeconds = 30;

    private readonly ICronJobService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _isLoading;

    private readonly List<CrontabEntry> _parsedEntries = [];
    private DateTime _lastParseNow;
    private int _lastParsedCount;
    private int _lastTasksCount;
    private CronJobStatusKind _lastStatusKind = CronJobStatusKind.None;
    private bool _lastTasksErrorIsTimeout;

    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private int _modeIndex;
    public CronJobMode Mode => (CronJobMode)ModeIndex;

    [ObservableProperty] private string _crontabInputText = string.Empty;
    [ObservableProperty] private bool _hasCronError;
    [ObservableProperty] private string _cronErrorText = string.Empty;

    [ObservableProperty] private bool _hasTasksError;
    [ObservableProperty] private string _tasksErrorText = string.Empty;
    [ObservableProperty] private string _tasksLoadingText = string.Empty;

    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty] private CrontabDisplayEntry? _selectedCronEntry;
    [ObservableProperty] private string _detailHeaderText = string.Empty;
    [ObservableProperty] private string _detailScheduleText = string.Empty;
    [ObservableProperty] private string _detailCommandText = string.Empty;
    [ObservableProperty] private string _detailNextRunsText = string.Empty;
    public bool IsCronDetailVisible => SelectedCronEntry is not null;

    public CronJobViewModel(ICronJobService? service = null)
    {
        _service = service ?? new CronJobService();
        CronEntries = [];
        TaskEntries = [];
        CronEntries.CollectionChanged += OnCronEntriesCollectionChanged;
        TaskEntries.CollectionChanged += OnTaskEntriesCollectionChanged;
    }

    public ObservableCollection<CrontabDisplayEntry> CronEntries { get; }
    public ObservableCollection<WindowsTaskEntry> TaskEntries { get; }

    public bool IsLoadingTasks => _isLoading;

    public event EventHandler<string>? CopyResultsRequested;

    public void Initialize(LocalizationManager? localizer) => UpdateLocalizer(localizer);

    public void UpdateLocalizer(LocalizationManager? localizer)
    {
        if (ReferenceEquals(_localizer, localizer))
        {
            return;
        }

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        _localizer = localizer;
        if (_localizer is not null)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
        }

        RefreshLocalizedMessages();
    }

    [RelayCommand]
    private void Parse()
    {
        HasCronError = false;
        CronErrorText = string.Empty;
        SelectedCronEntry = null;
        CronEntries.Clear();
        _parsedEntries.Clear();

        var content = CrontabInputText;
        if (string.IsNullOrWhiteSpace(content))
        {
            HasCronError = true;
            CronErrorText = L("ToolCronJobErrorEmpty");
            return;
        }

        var entries = CrontabParser.Parse(content);
        if (entries.Count == 0)
        {
            HasCronError = true;
            CronErrorText = L("ToolCronJobErrorNoParsed");
            return;
        }

        _parsedEntries.AddRange(entries);
        _lastParseNow = DateTime.Now;
        _lastParsedCount = entries.Count;
        _lastStatusKind = CronJobStatusKind.Parsed;

        ProjectCronDisplayEntries();
        UpdateStatus();
    }

    [RelayCommand]
    private void ClearPaste()
    {
        CrontabInputText = string.Empty;
        SelectedCronEntry = null;
        CronEntries.Clear();
        _parsedEntries.Clear();
        HasCronError = false;
        CronErrorText = string.Empty;
        _lastParsedCount = 0;
        _lastStatusKind = CronJobStatusKind.None;
        StatusText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshTasks))]
    private async Task RefreshTasksAsync()
    {
        if (_disposed || _isLoading)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _cts.CancelAfter(TimeSpan.FromSeconds(LoadTimeoutSeconds));

        _isLoading = true;
        IsBusy = true;
        HasTasksError = false;
        TasksErrorText = string.Empty;
        TasksLoadingText = L("ToolCronJobTasksLoading");
        _lastTasksErrorIsTimeout = false;

        try
        {
            var tasks = await _service.LoadWindowsTasksAsync(_cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            TaskEntries.Clear();
            foreach (var task in tasks)
            {
                TaskEntries.Add(task);
            }

            _lastTasksCount = tasks.Count;
            _lastStatusKind = CronJobStatusKind.Tasks;
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            TasksErrorText = L("ToolCronJobErrorTimeout");
            HasTasksError = true;
            _lastTasksErrorIsTimeout = true;
        }
        catch (Exception ex)
        {
            TasksErrorText = string.Format(
                CultureInfo.InvariantCulture,
                L("ToolCronJobErrorFailed"),
                ex.Message);
            HasTasksError = true;
            FileLogger.Warn($"CronJobViewModel tasks load failed: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
            IsBusy = false;
            TasksLoadingText = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyAll))]
    private void CopyAll()
    {
        var payload = BuildClipboardText();
        if (!string.IsNullOrEmpty(payload))
        {
            CopyResultsRequested?.Invoke(this, payload);
        }
    }

    [RelayCommand]
    private void ToggleHelp() => IsHelpVisible = !IsHelpVisible;

    partial void OnSelectedCronEntryChanged(CrontabDisplayEntry? value)
    {
        OnPropertyChanged(nameof(IsCronDetailVisible));
        RecomputeDetail();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshTasksCommand.NotifyCanExecuteChanged();
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Mode));
        CopyAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnCrontabInputTextChanged(string value)
    {
        CopyAllCommand.NotifyCanExecuteChanged();
    }

    private void OnCronEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyAllCommand.NotifyCanExecuteChanged();
    }

    private void OnTaskEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyAllCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private bool CanRefreshTasks() => !IsBusy;

    private bool CanCopyAll() => Mode == CronJobMode.Paste
        ? CronEntries.Count > 0
        : TaskEntries.Count > 0;

    private void ProjectCronDisplayEntries()
    {
        var selectedSource = SelectedCronEntry?.Source;
        CronEntries.Clear();

        foreach (var parsed in _parsedEntries)
        {
            var fields = parsed.ScheduleFields();
            var description = CronDescriber.Describe(fields, L);
            var nextRuns = CronScheduleCalculator.CalculateNextRuns(fields, 1, _lastParseNow);
            var nextRun = nextRuns.Count > 0 ? nextRuns[0] : "-";
            var schedule = $"{parsed.Minute} {parsed.Hour} {parsed.DayOfMonth} {parsed.Month} {parsed.DayOfWeek}";
            CronEntries.Add(new CrontabDisplayEntry(schedule, parsed.Command, nextRun, description, parsed));
        }

        if (selectedSource is null)
        {
            return;
        }

        SelectedCronEntry = CronEntries.FirstOrDefault(entry => ReferenceEquals(entry.Source, selectedSource));
    }

    private void RecomputeDetail()
    {
        DetailHeaderText = L("ToolCronJobDetailHeader");

        if (SelectedCronEntry is null)
        {
            DetailScheduleText = string.Empty;
            DetailCommandText = string.Empty;
            DetailNextRunsText = string.Empty;
            return;
        }

        var source = SelectedCronEntry.Source;
        var fields = source.ScheduleFields();
        var nextRuns = CronScheduleCalculator.CalculateNextRuns(fields, NextRunsCount, _lastParseNow);
        var nextRunsText = nextRuns.Count > 0 ? string.Join("\n", nextRuns) : "-";
        var schedule = $"{source.Minute} {source.Hour} {source.DayOfMonth} {source.Month} {source.DayOfWeek}";

        DetailScheduleText = string.Format(
            CultureInfo.InvariantCulture,
            L("ToolCronJobDetailSchedule"),
            schedule);
        DetailCommandText = string.Format(
            CultureInfo.InvariantCulture,
            L("ToolCronJobDetailCommand"),
            source.Command);
        DetailNextRunsText = string.Format(
            CultureInfo.InvariantCulture,
            L("ToolCronJobDetailNextRuns"),
            nextRunsText);
    }

    private void UpdateStatus()
    {
        StatusText = _lastStatusKind switch
        {
            CronJobStatusKind.Parsed => string.Format(
                CultureInfo.InvariantCulture,
                L("ToolCronJobStatusParsed"),
                _lastParsedCount),
            CronJobStatusKind.Tasks => string.Format(
                CultureInfo.InvariantCulture,
                L("ToolCronJobStatusTasks"),
                _lastTasksCount),
            _ => string.Empty,
        };
    }

    private void RefreshLocalizedMessages()
    {
        HelpText = L("ToolHelpCRONJOB").Replace("\\n", "\n", StringComparison.Ordinal);
        DetailHeaderText = L("ToolCronJobDetailHeader");
        TasksLoadingText = _isLoading ? L("ToolCronJobTasksLoading") : string.Empty;

        if (_parsedEntries.Count > 0)
        {
            ProjectCronDisplayEntries();
        }

        RecomputeDetail();
        UpdateStatus();

        if (HasCronError && string.IsNullOrWhiteSpace(CrontabInputText))
        {
            CronErrorText = L("ToolCronJobErrorEmpty");
        }
        else if (HasCronError)
        {
            CronErrorText = L("ToolCronJobErrorNoParsed");
        }

        if (HasTasksError && _lastTasksErrorIsTimeout)
        {
            TasksErrorText = L("ToolCronJobErrorTimeout");
        }
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();

        if (Mode == CronJobMode.Paste)
        {
            foreach (var entry in CronEntries)
            {
                sb.Append(InputValidator.SanitizeCsvCell(entry.Schedule)).Append('\t')
                    .Append(InputValidator.SanitizeCsvCell(entry.Command)).Append('\t')
                    .Append(InputValidator.SanitizeCsvCell(entry.NextRun)).Append('\t')
                    .AppendLine(InputValidator.SanitizeCsvCell(entry.Description));
            }
        }
        else
        {
            foreach (var task in TaskEntries)
            {
                sb.Append(InputValidator.SanitizeCsvCell(task.Name)).Append('\t')
                    .Append(InputValidator.SanitizeCsvCell(task.Status)).Append('\t')
                    .Append(InputValidator.SanitizeCsvCell(task.NextRun)).Append('\t')
                    .Append(InputValidator.SanitizeCsvCell(task.LastRun)).Append('\t')
                    .AppendLine(InputValidator.SanitizeCsvCell(task.LastResult));
            }
        }

        return sb.ToString();
    }

    private string L(string key) => _localizer?[key] ?? key;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }

        CronEntries.CollectionChanged -= OnCronEntriesCollectionChanged;
        TaskEntries.CollectionChanged -= OnTaskEntriesCollectionChanged;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    private enum CronJobStatusKind
    {
        None,
        Parsed,
        Tasks,
    }
}
