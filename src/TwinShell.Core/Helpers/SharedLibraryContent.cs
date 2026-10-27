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

namespace TwinShell.Core.Helpers;

/// <summary>
/// What of the command library is the operator's own and travels to other installs: the
/// shipped catalogue does not, because every install ships it.
/// </summary>
/// <remarks>
/// One predicate for the Git sync export and the library file export's "My actions", so the
/// two never disagree on what an operator made.
/// </remarks>
public static class SharedLibraryContent
{
    public static bool IsShared(Models.Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.IsUserCreated;
    }

    public static bool IsShared(CommandBatch runbook)
    {
        ArgumentNullException.ThrowIfNull(runbook);
        return runbook.IsUserCreated;
    }

    public static bool IsShared(CustomCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return !category.IsSystemCategory;
    }
}
