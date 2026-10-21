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

using TwinShell.Core.Models;

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// One row of the Runbooks panel: the runbook's name, how many steps it has, and when it last ran.
/// </summary>
public sealed class RunbookListEntry
{
    /// <summary>Creates a row over a stored runbook with its two worded columns already formatted.</summary>
    public RunbookListEntry(CommandBatch source, string stepCountText, string lastRunText)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        StepCountText = stepCountText;
        LastRunText = lastRunText;
    }

    /// <summary>The stored runbook.</summary>
    public CommandBatch Source { get; }

    /// <summary>The runbook's name.</summary>
    public string Name => Source.Name;

    /// <summary>"3 steps", in the operator's language.</summary>
    public string StepCountText { get; }

    /// <summary>"Last run ..." or "Never run", in the operator's language.</summary>
    public string LastRunText { get; }
}
