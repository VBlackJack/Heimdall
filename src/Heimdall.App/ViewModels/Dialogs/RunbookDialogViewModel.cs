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
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Editor of one runbook: its name, description and failure mode, its variables with their
/// defaults, and its ordered steps.
/// </summary>
/// <remarks>
/// Built without a window and without a service: the library hands it the picker gesture and an
/// action lookup, and reads <see cref="ToRunbook"/> back after Save.
/// </remarks>
public sealed partial class RunbookDialogViewModel : ObservableObject
{
    /// <summary>Index of <see cref="BatchExecutionMode.StopOnError"/> in the mode combo.</summary>
    internal const int ModeStopOnError = 0;

    /// <summary>Index of <see cref="BatchExecutionMode.ContinueOnError"/> in the mode combo.</summary>
    internal const int ModeContinueOnError = 1;

    private readonly CommandBatch? _source;
    private readonly Func<string, string> _localize;
    private readonly Func<string, ActionModel?> _findAction;

    private RunbookDialogViewModel(
        CommandBatch? source,
        Func<string, string> localize,
        Func<string, ActionModel?> findAction)
    {
        _source = source;
        _localize = localize ?? throw new ArgumentNullException(nameof(localize));
        _findAction = findAction ?? throw new ArgumentNullException(nameof(findAction));
        Steps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSteps));
    }

    /// <summary>The window title.</summary>
    public string DialogTitle { get; set; } = string.Empty;

    /// <summary>The runbook's name.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>What the runbook is for.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>0 = stop the run, 1 = carry on; stored and shown, the run itself is manual.</summary>
    [ObservableProperty]
    private int _executionModeIndex = ModeStopOnError;

    /// <summary>Why Save was refused, or null.</summary>
    [ObservableProperty]
    private string? _validationError;

    /// <summary>The variables, each with its default value.</summary>
    public ObservableCollection<RunbookVariableEntry> Variables { get; } = new();

    /// <summary>The steps, in run order.</summary>
    public ObservableCollection<RunbookStepEditorItem> Steps { get; } = new();

    /// <summary>True while the runbook has no step, which shows the empty-state line.</summary>
    public bool HasNoSteps => Steps.Count == 0;

    /// <summary>
    /// Opens the library picker and returns the chosen action and its parameter values, or null
    /// when the operator cancels. Installed by the library.
    /// </summary>
    public Func<Task<CommandLibraryPickerResult?>>? PickActionAsync { get; set; }

    /// <summary>The platform a new literal step starts on: the session's, or Linux.</summary>
    public bool NewLiteralIsWindows { get; set; }

    /// <summary>An editor for a new, empty runbook.</summary>
    public static RunbookDialogViewModel ForNew(
        Func<string, string> localize,
        Func<string, ActionModel?> findAction)
        => new(null, localize, findAction);

    /// <summary>An editor over a stored runbook.</summary>
    public static RunbookDialogViewModel FromRunbook(
        CommandBatch runbook,
        Func<string, string> localize,
        Func<string, ActionModel?> findAction)
    {
        ArgumentNullException.ThrowIfNull(runbook);

        var vm = new RunbookDialogViewModel(runbook, localize, findAction);
        vm.Load(runbook);
        return vm;
    }

    /// <summary>
    /// An editor pre-filled with a copy of <paramref name="runbook"/>, under
    /// <paramref name="name"/>; saving it creates a new runbook.
    /// </summary>
    public static RunbookDialogViewModel AsCopyOf(
        CommandBatch runbook,
        string name,
        Func<string, string> localize,
        Func<string, ActionModel?> findAction)
    {
        ArgumentNullException.ThrowIfNull(runbook);

        var vm = new RunbookDialogViewModel(null, localize, findAction);
        vm.Load(runbook);
        vm.Name = name;

        // The copy's steps are new steps: rebuilt without their stored identity.
        var copies = vm.Steps.Select(step => step.ToItem()).ToList();
        vm.Steps.Clear();
        foreach (var item in copies)
        {
            item.Id = Guid.NewGuid().ToString();
            item.BatchId = string.Empty;
            vm.Steps.Add(RunbookStepEditorItem.FromItem(item, vm.FindAction(item.ActionId)));
        }

        return vm;
    }

    private void Load(CommandBatch runbook)
    {
        Name = runbook.Name;
        Description = runbook.Description ?? string.Empty;
        ExecutionModeIndex = runbook.ExecutionMode == BatchExecutionMode.ContinueOnError
            ? ModeContinueOnError
            : ModeStopOnError;

        foreach (var (name, value) in runbook.Variables ?? [])
        {
            Variables.Add(new RunbookVariableEntry { Name = name, Value = value });
        }

        foreach (var item in runbook.Commands.OrderBy(command => command.Order))
        {
            Steps.Add(RunbookStepEditorItem.FromItem(item, FindAction(item.ActionId)));
        }
    }

    private ActionModel? FindAction(string? actionId)
        => string.IsNullOrWhiteSpace(actionId) ? null : _findAction(actionId);

    /// <summary>Adds an empty variable row.</summary>
    [RelayCommand]
    public void AddVariable() => Variables.Add(new RunbookVariableEntry());

    /// <summary>Removes a variable row.</summary>
    [RelayCommand]
    public void RemoveVariable(RunbookVariableEntry? entry)
    {
        if (entry is not null)
        {
            Variables.Remove(entry);
        }
    }

    /// <summary>Opens the library picker and adds the chosen action as the last step.</summary>
    [RelayCommand]
    public async Task AddActionAsync()
    {
        if (PickActionAsync is null)
        {
            return;
        }

        var picked = await PickActionAsync();
        if (picked is null)
        {
            return;
        }

        Steps.Add(RunbookStepEditorItem.ForAction(
            picked.ActionId,
            picked.ActionTitle,
            FindAction(picked.ActionId),
            picked.Parameters));
    }

    /// <summary>Adds an empty literal command as the last step.</summary>
    [RelayCommand]
    public void AddCommand() => Steps.Add(RunbookStepEditorItem.ForLiteral(NewLiteralIsWindows));

    /// <summary>Moves a step one place up.</summary>
    [RelayCommand]
    public void MoveUp(RunbookStepEditorItem? step)
    {
        var index = step is null ? -1 : Steps.IndexOf(step);
        if (index > 0)
        {
            Steps.Move(index, index - 1);
        }
    }

    /// <summary>Moves a step one place down.</summary>
    [RelayCommand]
    public void MoveDown(RunbookStepEditorItem? step)
    {
        var index = step is null ? -1 : Steps.IndexOf(step);
        if (index >= 0 && index < Steps.Count - 1)
        {
            Steps.Move(index, index + 1);
        }
    }

    /// <summary>Removes a step.</summary>
    [RelayCommand]
    public void RemoveStep(RunbookStepEditorItem? step)
    {
        if (step is not null)
        {
            Steps.Remove(step);
        }
    }

    /// <summary>
    /// Checks what Save needs: a name, valid and distinct variable names, and a command on
    /// every literal step. Sets <see cref="ValidationError"/> and returns false when one fails.
    /// </summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationError = _localize("ToolCmdLibRunbookErrorNameRequired");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var badNames = Variables
            .Select(variable => variable.Name.Trim())
            .Where(name => !RunbookVariables.IsValidName(name) || !seen.Add(name))
            .ToList();
        if (badNames.Count > 0)
        {
            ValidationError = string.Format(
                CultureInfo.CurrentCulture,
                _localize("ToolCmdLibRunbookErrorVariableName"),
                string.Join(", ", badNames.Select(name => name.Length == 0 ? "\"\"" : name)));
            return false;
        }

        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].IsLiteral && string.IsNullOrWhiteSpace(Steps[index].Command))
            {
                ValidationError = string.Format(
                    CultureInfo.CurrentCulture,
                    _localize("ToolCmdLibRunbookErrorEmptyCommand"),
                    index + 1);
                return false;
            }
        }

        ValidationError = null;
        return true;
    }

    /// <summary>
    /// The runbook the editor describes. An edit keeps the stored identity, creation date, last
    /// run and tags; a new runbook or a copy gets a fresh identity.
    /// </summary>
    public CommandBatch ToRunbook()
    {
        var runbook = new CommandBatch
        {
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            ExecutionMode = ExecutionModeIndex == ModeContinueOnError
                ? BatchExecutionMode.ContinueOnError
                : BatchExecutionMode.StopOnError,
            IsUserCreated = true,
            Variables = Variables
                .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
                .GroupBy(variable => variable.Name.Trim(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value ?? string.Empty, StringComparer.Ordinal),
            Commands = Steps.Select(step => step.ToItem()).ToList()
        };

        if (_source is not null)
        {
            runbook.Id = _source.Id;
            runbook.PublicId = _source.PublicId;
            runbook.CreatedAt = _source.CreatedAt;
            runbook.LastExecutedAt = _source.LastExecutedAt;
            runbook.Tags = [.. _source.Tags];
            runbook.IsUserCreated = _source.IsUserCreated;
        }

        foreach (var item in runbook.Commands)
        {
            item.BatchId = runbook.Id;
        }

        return runbook;
    }
}
