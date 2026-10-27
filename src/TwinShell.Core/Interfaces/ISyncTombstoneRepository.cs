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

namespace TwinShell.Core.Interfaces;

/// <summary>
/// The shared entries this database deleted, by public id, with the time of each delete.
/// </summary>
/// <remarks>
/// Written by the repositories when the operator deletes a user action, template, runbook or
/// category, and kept: a delete reaches the other installs through the sync folder's
/// <c>_deleted.json</c>, which the export rebuilds from these and from what the folder holds.
/// </remarks>
public interface ISyncTombstoneRepository
{
    Task<IReadOnlyDictionary<Guid, DateTime>> GetAllAsync();

    /// <summary>Records, or moves, the delete of <paramref name="publicId"/>.</summary>
    Task SetAsync(Guid publicId, DateTime deletedAt);

    /// <summary>Forgets the delete of <paramref name="publicId"/>: an edit made after it won.</summary>
    Task RemoveAsync(Guid publicId);
}
