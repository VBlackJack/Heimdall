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
using TwinShell.Core.Models;

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// One row of the run view: a runbook step with its position, its title and its status.
/// </summary>
public sealed partial class RunbookRunStep : ObservableObject
{
    private readonly Func<string, string> _localize;

    internal RunbookRunStep(BatchCommandItem item, int number, string title, Func<string, string> localize)
    {
        Item = item;
        Number = number;
        Title = title;
        _localize = localize;
    }

    /// <summary>The stored step.</summary>
    public BatchCommandItem Item { get; }

    /// <summary>Position in the run, from 1.</summary>
    public int Number { get; }

    /// <summary>The action's title in the operator's language, or the literal command.</summary>
    public string Title { get; }

    /// <summary>True for a free command typed into the runbook rather than a library action.</summary>
    public bool IsLiteral => string.IsNullOrWhiteSpace(Item.ActionId);

    /// <summary>What has been done with the step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private RunbookStepStatus _status = RunbookStepStatus.Pending;

    /// <summary>True for the step the four run buttons act on.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Why the step cannot be sent as it stands, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _validationError = string.Empty;

    /// <summary>True when <see cref="ValidationError"/> has something to say.</summary>
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    /// <summary>The status in the operator's language.</summary>
    public string StatusText => _localize(StatusKey(Status));

    /// <summary>The locale key of a status word.</summary>
    internal static string StatusKey(RunbookStepStatus status) => status switch
    {
        RunbookStepStatus.Sent => "ToolCmdLibRunbookStatusSent",
        RunbookStepStatus.Inserted => "ToolCmdLibRunbookStatusInserted",
        RunbookStepStatus.Copied => "ToolCmdLibRunbookStatusCopied",
        RunbookStepStatus.Skipped => "ToolCmdLibRunbookStatusSkipped",
        RunbookStepStatus.FailedToSend => "ToolCmdLibRunbookStatusFailedToSend",
        _ => "ToolCmdLibRunbookStatusPending"
    };
}
