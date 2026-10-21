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

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Repository for batch persistence
/// </summary>
public interface IBatchRepository
{
    /// <summary>
    /// Gets all batches
    /// </summary>
    Task<IEnumerable<CommandBatch>> GetAllAsync();

    /// <summary>
    /// Gets a batch by ID
    /// </summary>
    Task<CommandBatch?> GetByIdAsync(string id);

    /// <summary>
    /// Adds a new batch
    /// </summary>
    Task AddAsync(CommandBatch batch);

    /// <summary>
    /// Updates an existing batch
    /// </summary>
    Task UpdateAsync(CommandBatch batch);

    /// <summary>
    /// Records when a batch was last run, and nothing else: a run is not an edit, so the
    /// batch's UpdatedAt, which Git sync compares to detect local changes, is left as it is.
    /// No-op when the batch does not exist.
    /// </summary>
    Task MarkExecutedAsync(string id, DateTime executedAtUtc);

    /// <summary>
    /// Deletes a batch
    /// </summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// Searches batches by name or description
    /// </summary>
    Task<IEnumerable<CommandBatch>> SearchAsync(string query);

    /// <summary>
    /// Gets a batch by its public ID (for GitOps sync)
    /// </summary>
    Task<CommandBatch?> GetByPublicIdAsync(Guid publicId);
}
