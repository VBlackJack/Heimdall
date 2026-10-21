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

using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Pure import/export workflow for the Command Library, decoupled from WPF and
/// the dialog/busy-state plumbing. Performs only file IO, JSON parsing, and
/// <see cref="IActionService"/> calls so it is unit-testable in isolation.
/// </summary>
public interface ICommandLibraryTransferService
{
    /// <summary>
    /// Exports the given actions to a JSON envelope at the given path. Returns the exported
    /// count, which is also the envelope's <c>totalActions</c>. The caller chooses the set.
    /// </summary>
    Task<int> ExportAsync(IReadOnlyList<ActionModel> actions, string path);

    /// <summary>Parses and merges actions from the given JSON file into the database. System (non-user-created) actions are never overwritten.</summary>
    Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path);

    /// <summary>
    /// Exports the given actions and runbooks; the envelope carries the runbooks in an optional
    /// <c>runbooks</c> array. Returns the exported action count. A transfer that knows nothing of
    /// runbooks exports the actions alone.
    /// </summary>
    Task<int> ExportAsync(IReadOnlyList<ActionModel> actions, IReadOnlyList<CommandBatch> runbooks, string path)
        => ExportAsync(actions, path);

    /// <summary>
    /// Imports the actions, then merges the runbooks the file carries by public id into
    /// <paramref name="batchService"/> when one is given. A transfer that knows nothing of
    /// runbooks imports the actions alone.
    /// </summary>
    Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, IBatchService? batchService, string path)
        => ImportAsync(actionService, path);
}
