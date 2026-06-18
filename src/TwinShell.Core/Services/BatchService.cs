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

using System.Text.Json;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing command batches
/// </summary>
public sealed class BatchService : IBatchService
{
    private readonly IBatchRepository _repository;

    // PERFORMANCE: Use centralized JsonSerializerOptions to avoid recreation
    private static JsonSerializerOptions ExportJsonOptions => JsonOptionsHelper.CamelCaseForExport;
    private static JsonSerializerOptions ImportJsonOptions => JsonOptionsHelper.CamelCaseForImport;

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

        // Ensure order is correct
        for (int i = 0; i < batch.Commands.Count; i++)
        {
            batch.Commands[i].Order = i;
        }

        await _repository.UpdateAsync(batch).ConfigureAwait(false);
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

    public string ExportBatchToJson(CommandBatch batch)
    {
        return JsonSerializer.Serialize(batch, ExportJsonOptions);
    }

    public CommandBatch ImportBatchFromJson(string json)
    {
        var batch = JsonSerializer.Deserialize<CommandBatch>(json, ImportJsonOptions);

        if (batch == null)
        {
            throw new InvalidOperationException("Failed to deserialize batch from JSON");
        }

        // Generate new IDs to avoid conflicts
        batch.Id = Guid.NewGuid().ToString();
        batch.CreatedAt = DateTime.UtcNow;
        batch.UpdatedAt = DateTime.UtcNow;
        batch.LastExecutedAt = null;

        // Reset execution status and generate new IDs for commands
        for (int i = 0; i < batch.Commands.Count; i++)
        {
            var command = batch.Commands[i];
            command.Id = Guid.NewGuid().ToString();
            command.BatchId = batch.Id;
            command.Order = i;
            command.IsExecuted = false;
            command.ExecutionResult = null;
        }

        return batch;
    }
}
