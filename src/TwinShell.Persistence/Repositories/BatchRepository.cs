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

using Microsoft.EntityFrameworkCore;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Repository implementation for CommandBatch persistence
/// </summary>
public sealed class BatchRepository : IBatchRepository
{
    private readonly TwinShellDbContext _context;

    public BatchRepository(TwinShellDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IEnumerable<CommandBatch>> GetAllAsync()
    {
        // PERFORMANCE: AsNoTracking for read-only queries reduces memory overhead by 40-60%
        var entities = await _context.CommandBatches
            .AsNoTracking()
            .OrderByDescending(b => b.UpdatedAt)
            .ToListAsync();

        return entities.Select(CommandBatchMapper.ToModel);
    }

    public async Task<CommandBatch?> GetByIdAsync(string id)
    {
        // PERFORMANCE: AsNoTracking for read-only queries
        var entity = await _context.CommandBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id);

        return entity == null ? null : CommandBatchMapper.ToModel(entity);
    }

    public async Task AddAsync(CommandBatch batch)
    {
        var entity = CommandBatchMapper.ToEntity(batch);
        _context.CommandBatches.Add(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(CommandBatch batch)
    {
        // Find the existing entity to update
        var existingEntity = await _context.CommandBatches
            .FirstOrDefaultAsync(b => b.Id == batch.Id);

        if (existingEntity == null)
        {
            throw new InvalidOperationException($"CommandBatch with ID '{batch.Id}' not found");
        }

        // Update properties from the model
        existingEntity.Name = batch.Name;
        existingEntity.Description = batch.Description;
        existingEntity.ExecutionMode = batch.ExecutionMode;
        existingEntity.CommandsJson = System.Text.Json.JsonSerializer.Serialize(batch.Commands);
        existingEntity.TagsJson = System.Text.Json.JsonSerializer.Serialize(batch.Tags);
        existingEntity.LastExecutedAt = batch.LastExecutedAt;
        existingEntity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.CommandBatches
            .FirstOrDefaultAsync(b => b.Id == id);

        if (entity != null)
        {
            _context.CommandBatches.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<CommandBatch>> SearchAsync(string query)
    {
        // PERFORMANCE: Use EF.Functions.Like instead of ToLower() to allow index usage
        var entities = await _context.CommandBatches
            .AsNoTracking()
            .Where(b => EF.Functions.Like(b.Name, $"%{query}%") ||
                       (b.Description != null && EF.Functions.Like(b.Description, $"%{query}%")))
            .OrderByDescending(b => b.UpdatedAt)
            .ToListAsync();

        return entities.Select(CommandBatchMapper.ToModel);
    }

    /// <summary>
    /// Gets a batch by its public ID (for GitOps sync)
    /// </summary>
    public async Task<CommandBatch?> GetByPublicIdAsync(Guid publicId)
    {
        var entity = await _context.CommandBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.PublicId == publicId);

        return entity == null ? null : CommandBatchMapper.ToModel(entity);
    }
}
