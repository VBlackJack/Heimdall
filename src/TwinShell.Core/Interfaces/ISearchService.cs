/*
 * Copyright 2025 Julien Bombled
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
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for searching actions
/// </summary>
public interface ISearchService
{
    Task<IEnumerable<ActionModel>> SearchAsync(
        IEnumerable<ActionModel> actions,
        string searchTerm);

    /// <summary>
    /// Searches actions, matching a category by its stored value and by the label
    /// <paramref name="categoryLabel"/> gives it.
    /// </summary>
    /// <remarks>
    /// A shipped category is stored as a key ("security") and shown as a localized label;
    /// the operator types what they see. An implementation that has no use for the label
    /// searches as <see cref="SearchAsync(IEnumerable{ActionModel}, string)"/> does.
    /// </remarks>
    Task<IEnumerable<ActionModel>> SearchAsync(
        IEnumerable<ActionModel> actions,
        string searchTerm,
        Func<string, string>? categoryLabel)
        => SearchAsync(actions, searchTerm);
}
