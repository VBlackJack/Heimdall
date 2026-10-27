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
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// One step in the runbook editor: either a library action with a value per parameter, or a
/// literal command with the platform it is written for.
/// </summary>
public sealed partial class RunbookStepEditorItem : ObservableObject
{
    private readonly BatchCommandItem? _source;

    private RunbookStepEditorItem(BatchCommandItem? source, string? actionId, string actionTitle)
    {
        _source = source;
        ActionId = actionId;
        ActionTitle = actionTitle;
    }

    /// <summary>The referenced action's id, or null for a literal step.</summary>
    public string? ActionId { get; }

    /// <summary>The action's title as shown in the editor.</summary>
    public string ActionTitle { get; }

    /// <summary>True for a step that references a library action.</summary>
    public bool IsAction => !string.IsNullOrWhiteSpace(ActionId);

    /// <summary>True for a literal command.</summary>
    public bool IsLiteral => !IsAction;

    /// <summary>
    /// One entry per parameter the action's templates declare, Windows first, a name both
    /// declare listed once. A value may be <c>{{name}}</c>.
    /// </summary>
    public ObservableCollection<CommandLibraryParameterEntry> Parameters { get; } = new();

    /// <summary>True when the action declares parameters, which shows the values form.</summary>
    public bool HasParameters => Parameters.Count > 0;

    /// <summary>The literal command of a literal step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    private string _command = string.Empty;

    /// <summary>
    /// What the step's row says to a screen reader: the action's title, or the literal command,
    /// which is what the operator reads on the row.
    /// </summary>
    public string AccessibleLabel => IsAction ? ActionTitle : Command;

    /// <summary>True when the literal command is written for Windows, false for Linux.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinux))]
    private bool _isWindows;

    /// <summary>Inverse of <see cref="IsWindows"/>, for the Linux radio button.</summary>
    public bool IsLinux
    {
        get => !IsWindows;
        set
        {
            if (value == IsLinux)
            {
                return;
            }

            IsWindows = !value;
        }
    }

    /// <summary>Optional note on the step.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>A new step for an action picked from the library.</summary>
    public static RunbookStepEditorItem ForAction(
        string actionId,
        string actionTitle,
        ActionModel? action,
        IReadOnlyDictionary<string, string>? values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        var item = new RunbookStepEditorItem(null, actionId, action?.Title ?? actionTitle);
        item.FillParameters(action, values);
        return item;
    }

    /// <summary>A new empty literal step.</summary>
    public static RunbookStepEditorItem ForLiteral(bool isWindows)
        => new(null, null, string.Empty) { IsWindows = isWindows };

    /// <summary>An editor row for a stored step.</summary>
    public static RunbookStepEditorItem FromItem(BatchCommandItem source, ActionModel? action)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.ActionId))
        {
            return new RunbookStepEditorItem(source, null, string.Empty)
            {
                Command = source.Command,
                IsWindows = source.Platform == Platform.Windows,
                Description = source.Description ?? string.Empty
            };
        }

        var item = new RunbookStepEditorItem(source, source.ActionId, action?.Title ?? source.ActionTitle)
        {
            Description = source.Description ?? string.Empty
        };
        item.FillParameters(action, source.Parameters);
        return item;
    }

    /// <summary>
    /// The step to store. Its <see cref="BatchCommandItem.Order"/> is the one it was loaded with:
    /// the service numbers the steps from their position when the runbook is saved.
    /// </summary>
    public BatchCommandItem ToItem()
    {
        var item = new BatchCommandItem
        {
            Id = _source?.Id ?? Guid.NewGuid().ToString(),
            BatchId = _source?.BatchId ?? string.Empty,
            Order = _source?.Order ?? 0,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim()
        };

        if (IsAction)
        {
            item.ActionId = ActionId;
            item.ActionTitle = _source?.ActionTitle is { Length: > 0 } stored ? stored : ActionTitle;
            item.Command = _source?.Command ?? string.Empty;
            item.Platform = _source?.Platform ?? Platform.Both;
            item.Parameters = Parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => parameter.Value ?? string.Empty,
                StringComparer.Ordinal);
        }
        else
        {
            item.ActionTitle = string.Empty;
            item.Command = Command.Trim();
            item.Platform = IsWindows ? Platform.Windows : Platform.Linux;
        }

        return item;
    }

    private void FillParameters(ActionModel? action, IReadOnlyDictionary<string, string>? values)
    {
        var declared = new List<TemplateParameter>();
        foreach (var template in new[] { action?.WindowsCommandTemplate, action?.LinuxCommandTemplate })
        {
            foreach (var parameter in template?.Parameters ?? [])
            {
                if (parameter is not null
                    && !string.IsNullOrWhiteSpace(parameter.Name)
                    && !declared.Any(known => string.Equals(known.Name, parameter.Name, StringComparison.Ordinal)))
                {
                    declared.Add(parameter);
                }
            }
        }

        foreach (var parameter in declared)
        {
            var value = values is not null && values.TryGetValue(parameter.Name, out var stored)
                ? stored ?? string.Empty
                : parameter.DefaultValue ?? string.Empty;

            Parameters.Add(new CommandLibraryParameterEntry
            {
                Name = parameter.Name,
                Label = parameter.Label,
                Value = value,
                Required = parameter.Required,
                Description = parameter.Description,
                Type = parameter.Type ?? "string",
                // Free text even for a choice: the value may be a {{name}} reference, which no
                // list of allowed values contains. The generator checks the resolved value.
                AllowedValues = []
            });
        }

        // A value stored for a parameter the action no longer declares is kept, so a template
        // edit that brings the parameter back finds it again.
        if (values is not null)
        {
            foreach (var (name, value) in values)
            {
                if (!declared.Any(known => string.Equals(known.Name, name, StringComparison.Ordinal)))
                {
                    Parameters.Add(new CommandLibraryParameterEntry { Name = name, Label = name, Value = value ?? string.Empty });
                }
            }
        }
    }
}
