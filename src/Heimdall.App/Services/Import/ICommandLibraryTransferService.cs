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
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Pure import/export workflow for the Command Library, decoupled from WPF and
/// the dialog/busy-state plumbing. Performs only file IO, JSON parsing, and
/// <see cref="IActionService"/> calls so it is unit-testable in isolation.
/// </summary>
public interface ICommandLibraryTransferService
{
    /// <summary>Exports all actions to a JSON envelope at the given path. Returns the exported count.</summary>
    Task<int> ExportAsync(IActionService actionService, string path);

    /// <summary>Parses and merges actions from the given JSON file into the database. System (non-user-created) actions are never overwritten.</summary>
    Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path);
}
