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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// Immutable display model for a single command history entry shown in the
/// history panel. Built once per <see cref="ICommandHistoryService"/> page load
/// and never mutated, so plain init-only properties are sufficient.
/// </summary>
public sealed class CommandLibraryHistoryEntry
{
    /// <summary>Title of the action that produced the command.</summary>
    public string ActionTitle { get; init; } = string.Empty;

    /// <summary>The actual generated command text (the value copied to clipboard).</summary>
    public string GeneratedCommand { get; init; } = string.Empty;

    /// <summary>Pre-formatted local timestamp string ("g" pattern).</summary>
    public string Timestamp { get; init; } = string.Empty;

    /// <summary>
    /// False when the stored command could not be opened, for instance because the
    /// master-password vault that sealed it is locked. <see cref="GeneratedCommand"/> is
    /// then empty and the row must not offer to copy it.
    /// </summary>
    /// <remarks>
    /// Carried rather than inferred from an empty command: the two are different facts,
    /// and a row that says "this cannot be shown" is honest where a blank line is just
    /// confusing. The title and the timestamp stay in clear precisely so such a row still
    /// tells the user what ran and when.
    /// </remarks>
    public bool IsReadable { get; init; } = true;

    /// <summary>True when this row has a command the user can act on.</summary>
    public bool IsUnreadable => !IsReadable;
}
