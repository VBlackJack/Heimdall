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

using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace Heimdall.App.Services.Import;

/// <summary>
/// A runbook as the transfer envelope carries it: what describes the runbook and its steps, and
/// nothing local to one installation (no database id, no run state).
/// </summary>
internal sealed class RunbookTransferModel
{
    /// <summary>The identity an import merges by.</summary>
    public Guid PublicId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The stored failure mode, by name.</summary>
    public string? ExecutionMode { get; set; }

    /// <summary>The variables with their default values.</summary>
    public Dictionary<string, string>? Variables { get; set; }

    public List<string>? Tags { get; set; }

    /// <summary>The steps, in run order.</summary>
    public List<RunbookStepTransferModel>? Steps { get; set; }

    /// <summary>The envelope form of a stored runbook.</summary>
    public static RunbookTransferModel FromRunbook(CommandBatch runbook)
    {
        ArgumentNullException.ThrowIfNull(runbook);

        return new RunbookTransferModel
        {
            PublicId = runbook.PublicId,
            Name = runbook.Name,
            Description = runbook.Description,
            ExecutionMode = runbook.ExecutionMode.ToString(),
            Variables = new Dictionary<string, string>(runbook.Variables, StringComparer.Ordinal),
            Tags = [.. runbook.Tags],
            Steps = runbook.Commands
                .OrderBy(step => step.Order)
                .Select(step => new RunbookStepTransferModel
                {
                    ActionId = step.ActionId,
                    ActionTitle = step.ActionTitle,
                    Command = step.Command,
                    Platform = step.Platform.ToString(),
                    Description = step.Description,
                    Parameters = new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal)
                })
                .ToList()
        };
    }

    /// <summary>
    /// The runbook an import stores. Values outside their domain fall back: an unreadable failure
    /// mode to stop, an unreadable platform to Both, and a variable whose name is not a name is
    /// dropped, since no step could refer to it.
    /// </summary>
    public CommandBatch ToRunbook()
    {
        var steps = (Steps ?? [])
            .Where(step => step is not null)
            .Select((step, index) => new BatchCommandItem
            {
                Order = index,
                ActionId = string.IsNullOrWhiteSpace(step.ActionId) ? null : step.ActionId,
                ActionTitle = step.ActionTitle ?? string.Empty,
                Command = step.Command ?? string.Empty,
                Platform = Enum.TryParse<Platform>(step.Platform, ignoreCase: true, out var platform)
                    && Enum.IsDefined(platform)
                    ? platform
                    : Platform.Both,
                Description = step.Description,
                Parameters = new Dictionary<string, string>(
                    step.Parameters ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal)
            })
            .ToList();

        return new CommandBatch
        {
            PublicId = PublicId,
            Name = Name.Trim(),
            Description = Description,
            ExecutionMode = Enum.TryParse<BatchExecutionMode>(ExecutionMode, ignoreCase: true, out var mode)
                && Enum.IsDefined(mode)
                ? mode
                : BatchExecutionMode.StopOnError,
            Variables = (Variables ?? new Dictionary<string, string>())
                .Where(variable => RunbookVariables.IsValidName(variable.Key))
                .ToDictionary(variable => variable.Key, variable => variable.Value ?? string.Empty, StringComparer.Ordinal),
            Tags = [.. Tags ?? []],
            Commands = steps,
            IsUserCreated = true
        };
    }
}

/// <summary>One runbook step as the transfer envelope carries it.</summary>
internal sealed class RunbookStepTransferModel
{
    public string? ActionId { get; set; }

    public string? ActionTitle { get; set; }

    public string? Command { get; set; }

    public string? Platform { get; set; }

    public string? Description { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }
}
