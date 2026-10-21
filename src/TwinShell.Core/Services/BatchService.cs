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

using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing command batches
/// </summary>
public sealed class BatchService : IBatchService
{
    private readonly IBatchRepository _repository;

    public BatchService(IBatchRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<IEnumerable<CommandBatch>> GetAllBatchesAsync()
    {
        return await _repository.GetAllAsync().ConfigureAwait(false);
    }

    public async Task<CommandBatch?> GetBatchByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id).ConfigureAwait(false);
    }

    public async Task<CommandBatch?> GetBatchByPublicIdAsync(Guid publicId)
    {
        return await _repository.GetByPublicIdAsync(publicId).ConfigureAwait(false);
    }

    public async Task<CommandBatch> CreateBatchAsync(CommandBatch batch)
    {
        batch.Id = Guid.NewGuid().ToString();
        batch.CreatedAt = DateTime.UtcNow;
        batch.UpdatedAt = DateTime.UtcNow;
        batch.IsUserCreated = true;

        // Ensure all commands have IDs and proper batch reference
        for (int i = 0; i < batch.Commands.Count; i++)
        {
            var command = batch.Commands[i];
            if (string.IsNullOrEmpty(command.Id))
            {
                command.Id = Guid.NewGuid().ToString();
            }
            command.BatchId = batch.Id;
            command.Order = i;
        }

        await _repository.AddAsync(batch).ConfigureAwait(false);
        return batch;
    }

    public async Task UpdateBatchAsync(CommandBatch batch)
    {
        batch.UpdatedAt = DateTime.UtcNow;

        // The list position is the order the caller gave the steps; the stored Order is what
        // a reload sorts by, so it is renumbered here rather than trusted from the caller.
        for (int i = 0; i < batch.Commands.Count; i++)
        {
            batch.Commands[i].Order = i;
        }

        await _repository.UpdateAsync(batch).ConfigureAwait(false);
    }

    public async Task MarkExecutedAsync(string id, DateTime executedAtUtc)
    {
        await _repository.MarkExecutedAsync(id, executedAtUtc).ConfigureAwait(false);
    }

    public async Task DeleteBatchAsync(string id)
    {
        await _repository.DeleteAsync(id).ConfigureAwait(false);
    }

    public async Task<IEnumerable<CommandBatch>> SearchBatchesAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetAllBatchesAsync().ConfigureAwait(false);
        }

        return await _repository.SearchAsync(query).ConfigureAwait(false);
    }
}
