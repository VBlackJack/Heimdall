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
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;
using IDialogService = Heimdall.App.Services.IDialogService;

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// What a run needs from the library it was started from: how to find an action and generate
/// its command, how to reach the terminal, and where to record what was sent.
/// </summary>
public sealed class RunbookRunContext
{
    /// <summary>The loaded action with an id, or null when it is gone.</summary>
    public required Func<string, ActionModel?> FindAction { get; init; }

    /// <summary>The library's command generator.</summary>
    public required ICommandGeneratorService Generator { get; init; }

    /// <summary>Asks the dangerous-command confirmation.</summary>
    public required IDialogService DialogService { get; init; }

    /// <summary>Resolves a locale key.</summary>
    public required Func<string, string> Localize { get; init; }

    /// <summary>
    /// The platform of the session, as <see cref="RemoteOsResolver"/> reads it; picks the
    /// template each action step opens on.
    /// </summary>
    public RemoteOs Platform { get; init; } = RemoteOs.Auto;

    /// <summary>The session's host, which fills a host-named variable.</summary>
    public string? TargetHost { get; init; }

    /// <summary>The session's user name, which fills a user-named variable.</summary>
    public string? Username { get; init; }

    /// <summary>Submits a command to the session's terminal, as the library's Send does.</summary>
    public Action<string>? Send { get; init; }

    /// <summary>Types a command at the prompt without submitting it.</summary>
    public Action<string>? Insert { get; init; }

    /// <summary>True when the tab currently holds a terminal that can receive text.</summary>
    public Func<bool>? CanSendToTerminal { get; init; }

    /// <summary>Writes the clipboard; false when it was unavailable.</summary>
    public Func<string, bool>? SetClipboard { get; init; }

    /// <summary>Writes one history row for a step handled from a library action.</summary>
    public Action<RunbookHistoryRecord>? RecordHistory { get; init; }

    /// <summary>Called once, when the first step of the run is handled.</summary>
    public System.Action? RunStarted { get; init; }
}

/// <summary>
/// One history row a run asks the library to write: the action, the command it became, the
/// values it was given, the platform of its template, and whether it ran in the terminal.
/// </summary>
public sealed record RunbookHistoryRecord(
    ActionModel Action,
    string Command,
    Dictionary<string, string> Parameters,
    Platform Platform,
    bool IsExecuted);

/// <summary>
/// One run of a runbook, step by step, with the operator reading the output between steps.
/// </summary>
/// <remarks>
/// <para>
/// The run never advances by itself. A terminal is written to, not read from, so there is no
/// exit code to stop on: the operator presses Send, Insert, Copy or Skip for each step, and Stop
/// when something went wrong. <see cref="BatchExecutionMode"/> is shown for that reason, and
/// enforced by the operator.
/// </para>
/// <para>
/// A dangerous action step asks the same confirmation as the library's Send. A literal step
/// always asks: it bypassed the generator's validation and escaping, the rule the library
/// already applies to an example pasted into the generator.
/// </para>
/// </remarks>
public sealed partial class RunbookRunViewModel : ObservableObject
{
    private readonly RunbookRunContext _context;
    private ActionModel? _currentAction;
    private CommandTemplate? _currentTemplate;
    private Dictionary<string, string> _currentValues = new(StringComparer.Ordinal);
    private bool _started;

    /// <summary>Starts a run of <paramref name="runbook"/> on its first step.</summary>
    public RunbookRunViewModel(CommandBatch runbook, RunbookRunContext context)
    {
        ArgumentNullException.ThrowIfNull(runbook);
        _context = context ?? throw new ArgumentNullException(nameof(context));

        Runbook = runbook;
        Title = string.Format(
            CultureInfo.CurrentCulture,
            _context.Localize("ToolCmdLibRunbookRunTitleFormat"),
            runbook.Name);
        ExecutionModeText = _context.Localize(runbook.ExecutionMode == BatchExecutionMode.ContinueOnError
            ? "ToolCmdLibRunbookModeContinueOnError"
            : "ToolCmdLibRunbookModeStopOnError");

        foreach (var (name, defaultValue) in runbook.Variables ?? [])
        {
            var entry = new RunbookVariableEntry { Name = name, Value = InitialValue(name, defaultValue) };
            entry.PropertyChanged += OnVariableChanged;
            Variables.Add(entry);
        }

        var number = 1;
        foreach (var item in runbook.Commands.OrderBy(command => command.Order))
        {
            Steps.Add(new RunbookRunStep(item, number++, StepTitle(item), _context.Localize));
        }

        if (Steps.Count == 0)
        {
            IsFinished = true;
        }
        else
        {
            Steps[0].IsCurrent = true;
        }

        Regenerate();
    }

    /// <summary>The runbook being run.</summary>
    public CommandBatch Runbook { get; }

    /// <summary>"Running: name", in the operator's language.</summary>
    public string Title { get; }

    /// <summary>The stored failure mode, in words.</summary>
    public string ExecutionModeText { get; }

    /// <summary>The variables with the values this run uses.</summary>
    public ObservableCollection<RunbookVariableEntry> Variables { get; } = new();

    /// <summary>True when the runbook declares variables, which shows their form.</summary>
    public bool HasVariables => Variables.Count > 0;

    /// <summary>The steps, in run order.</summary>
    public ObservableCollection<RunbookRunStep> Steps { get; } = new();

    /// <summary>Index of the step the buttons act on.</summary>
    [ObservableProperty]
    private int _currentIndex;

    /// <summary>The current step's command, read-only on screen.</summary>
    [ObservableProperty]
    private string _currentCommand = string.Empty;

    /// <summary>Why the current step cannot be sent, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentValidationError))]
    private string _currentValidationError = string.Empty;

    /// <summary>True when the current step produced a command that can go to the terminal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanInsert))]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(InsertCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private bool _isCurrentCommandValid;

    /// <summary>True once every step has been handled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanInsert))]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    [NotifyPropertyChangedFor(nameof(CanSkip))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(InsertCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipCommand))]
    private bool _isFinished;

    /// <summary>True when <see cref="CurrentValidationError"/> has something to say.</summary>
    public bool HasCurrentValidationError => !string.IsNullOrEmpty(CurrentValidationError);

    /// <summary>The step the buttons act on, or null once the run is finished.</summary>
    public RunbookRunStep? CurrentStep => IsFinished ? null : Steps[CurrentIndex];

    /// <summary>True when the tab holds a terminal that can receive the run's commands.</summary>
    public bool HasTerminal => _context.Send is not null && (_context.CanSendToTerminal?.Invoke() ?? false);

    /// <summary>True when there is no terminal to send to, which shows the copy hint.</summary>
    public bool ShowNoTerminalHint => !HasTerminal;

    /// <summary>
    /// Send is enabled under the library's own condition: a valid command and a terminal.
    /// </summary>
    public bool CanSend => !IsFinished && IsCurrentCommandValid && HasTerminal;

    /// <summary>Insert needs what Send needs, and an insert handler.</summary>
    public bool CanInsert => CanSend && _context.Insert is not null;

    /// <summary>Copy works without a terminal, which is how a run proceeds over RDP or SFTP.</summary>
    public bool CanCopy => !IsFinished && IsCurrentCommandValid && _context.SetClipboard is not null;

    /// <summary>Any step not yet handled can be skipped.</summary>
    public bool CanSkip => !IsFinished;

    /// <summary>Raised by Stop; the library closes the run view.</summary>
    public event System.Action? StopRequested;

    /// <summary>
    /// Re-reads the terminal probe, which is not observable, after the tab's layout changed.
    /// </summary>
    public void RefreshSendState()
    {
        OnPropertyChanged(nameof(HasTerminal));
        OnPropertyChanged(nameof(ShowNoTerminalHint));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanInsert));
        SendCommand.NotifyCanExecuteChanged();
        InsertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Submits the current step to the terminal after the confirmation its level asks for, records
    /// it in history as executed, and moves to the next step. A refusal leaves the step pending.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (!CanSend) return;

        var step = Steps[CurrentIndex];
        var command = CurrentCommand;
        var level = step.IsLiteral
            ? CriticalityLevel.Dangerous
            : _currentAction?.Level ?? CriticalityLevel.Dangerous;
        if (!await DangerousCommandGuard.ConfirmIfDangerousAsync(level, _context.DialogService, _context.Localize))
        {
            return;
        }

        try
        {
            _context.Send!(command);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Runbook step {step.Number} could not be sent: {ex.Message}");
            step.Status = RunbookStepStatus.FailedToSend;
            return;
        }

        Complete(step, RunbookStepStatus.Sent, command, isExecuted: true);
    }

    /// <summary>
    /// Types the current step at the prompt without running it and moves on. Never asks the
    /// dangerous confirmation, as the library's Insert: nothing runs until the operator's Enter.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    public void Insert()
    {
        if (!CanInsert) return;

        var step = Steps[CurrentIndex];
        var command = CurrentCommand;
        try
        {
            _context.Insert!(command);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Runbook step {step.Number} could not be inserted: {ex.Message}");
            step.Status = RunbookStepStatus.FailedToSend;
            return;
        }

        Complete(step, RunbookStepStatus.Inserted, command, isExecuted: false);
    }

    /// <summary>Copies the current step's command and moves on: the run's path without a terminal.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    public void Copy()
    {
        if (!CanCopy) return;

        var step = Steps[CurrentIndex];
        var command = CurrentCommand;
        if (!_context.SetClipboard!(command)) return;

        Complete(step, RunbookStepStatus.Copied, command, isExecuted: false);
    }

    /// <summary>Passes over the current step.</summary>
    [RelayCommand(CanExecute = nameof(CanSkip))]
    public void Skip()
    {
        if (!CanSkip) return;

        Steps[CurrentIndex].Status = RunbookStepStatus.Skipped;
        Advance();
    }

    /// <summary>Ends the run; the steps already handled stay as they are.</summary>
    [RelayCommand]
    public void Stop() => StopRequested?.Invoke();

    private void Complete(RunbookRunStep step, RunbookStepStatus status, string command, bool isExecuted)
    {
        step.Status = status;

        if (!step.IsLiteral && _currentAction is not null && _currentTemplate is not null)
        {
            // Literal steps write no history row: a history row belongs to a library action.
            _context.RecordHistory?.Invoke(new RunbookHistoryRecord(
                _currentAction,
                command,
                new Dictionary<string, string>(_currentValues, StringComparer.Ordinal),
                _currentTemplate.Platform,
                isExecuted));
        }

        if (!_started)
        {
            _started = true;
            _context.RunStarted?.Invoke();
        }

        Advance();
    }

    private void Advance()
    {
        Steps[CurrentIndex].IsCurrent = false;
        if (CurrentIndex + 1 >= Steps.Count)
        {
            IsFinished = true;
        }
        else
        {
            CurrentIndex++;
            Steps[CurrentIndex].IsCurrent = true;
        }

        OnPropertyChanged(nameof(CurrentStep));
        Regenerate();
    }

    private void OnVariableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunbookVariableEntry.Value))
        {
            Regenerate();
        }
    }

    /// <summary>
    /// Rebuilds the current step's command from its stored values with the run's variables
    /// substituted, and says why it cannot be sent when it cannot.
    /// </summary>
    internal void Regenerate()
    {
        _currentAction = null;
        _currentTemplate = null;
        _currentValues = new Dictionary<string, string>(StringComparer.Ordinal);

        if (IsFinished)
        {
            CurrentCommand = string.Empty;
            CurrentValidationError = string.Empty;
            IsCurrentCommandValid = false;
            return;
        }

        var step = Steps[CurrentIndex];
        var (command, error) = step.IsLiteral ? ResolveLiteral(step.Item) : ResolveAction(step.Item);

        CurrentCommand = command;
        CurrentValidationError = error;
        step.ValidationError = error;
        IsCurrentCommandValid = string.IsNullOrEmpty(error) && !string.IsNullOrWhiteSpace(command);
    }

    private (string Command, string Error) ResolveLiteral(BatchCommandItem item)
        => string.IsNullOrWhiteSpace(item.Command)
            ? (string.Empty, _context.Localize("ToolCmdLibRunbookStepEmpty"))
            : (item.Command, string.Empty);

    private (string Command, string Error) ResolveAction(BatchCommandItem item)
    {
        var action = _context.FindAction(item.ActionId!);
        if (action is null)
        {
            return (item.Command, _context.Localize("ToolCmdLibRunbookStepActionMissing"));
        }

        var template = CommandLibraryViewModel.SelectRunbookTemplate(action, _context.Platform);
        if (template is null)
        {
            return (item.Command, _context.Localize("ToolCmdLibRunbookStepNoTemplate"));
        }

        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var values = RunbookVariables.Resolve(item.Parameters, CurrentVariableValues(), unknown);
        _currentAction = action;
        _currentTemplate = template;
        _currentValues = values;

        if (unknown.Count > 0)
        {
            // The reference stays as written in the value, so the command shows it.
            return (TryGenerate(template, values) ?? template.CommandPattern, string.Format(
                CultureInfo.CurrentCulture,
                _context.Localize("ToolCmdLibRunbookUnknownVariables"),
                string.Join(", ", unknown.Select(RunbookVariables.FormatReference))));
        }

        try
        {
            if (_context.Generator.ValidateParameters(template, values, out var errors) && errors.Count == 0)
            {
                return (_context.Generator.GenerateCommand(template, values), string.Empty);
            }

            return (template.CommandPattern, string.Join(Environment.NewLine, errors.Select(static error => $"- {error}")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return (template.CommandPattern, string.Format(
                CultureInfo.CurrentCulture,
                _context.Localize("ToolCmdLibGenerateError"),
                ex.Message));
        }
    }

    private string? TryGenerate(CommandTemplate template, Dictionary<string, string> values)
    {
        try
        {
            return _context.Generator.GenerateCommand(template, values);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private Dictionary<string, string> CurrentVariableValues()
        => Variables
            .GroupBy(variable => variable.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// A variable named like a host or a user starts on the session's host or user name, the
    /// way the library prefills those parameters; any other starts on its default.
    /// </summary>
    private string InitialValue(string name, string? defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(_context.TargetHost) && CommandLibraryViewModel.IsHostParameter(name, type: null))
        {
            return _context.TargetHost!;
        }

        if (!string.IsNullOrWhiteSpace(_context.Username) && CommandLibraryViewModel.IsUserParameter(name))
        {
            return _context.Username!;
        }

        return defaultValue ?? string.Empty;
    }

    private string StepTitle(BatchCommandItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ActionId))
        {
            return item.Command;
        }

        return _context.FindAction(item.ActionId)?.Title
            ?? (string.IsNullOrWhiteSpace(item.ActionTitle) ? item.ActionId : item.ActionTitle);
    }
}
