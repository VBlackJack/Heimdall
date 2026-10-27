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
/// Repository implementation for CommandTemplate persistence
/// </summary>
public sealed class CommandTemplateRepository : ICommandTemplateRepository
{
    private readonly TwinShellDbContext _context;

    public CommandTemplateRepository(TwinShellDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IEnumerable<CommandTemplate>> GetAllAsync()
    {
        var entities = await _context.CommandTemplates
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync();

        return entities.Select(CommandTemplateMapper.ToModel);
    }

    public async Task<CommandTemplate?> GetByIdAsync(string id)
    {
        var entity = await _context.CommandTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        return entity == null ? null : CommandTemplateMapper.ToModel(entity);
    }

    public async Task<CommandTemplate?> GetByPublicIdAsync(Guid publicId)
    {
        var entity = await _context.CommandTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.PublicId == publicId);

        return entity == null ? null : CommandTemplateMapper.ToModel(entity);
    }

    public async Task<string?> GetIdByPublicIdAsync(Guid publicId)
    {
        return await _context.CommandTemplates
            .AsNoTracking()
            .Where(t => t.PublicId == publicId)
            .Select(t => t.Id)
            .FirstOrDefaultAsync();
    }

    public async Task AddAsync(CommandTemplate template)
    {
        var entity = CommandTemplateMapper.ToEntity(template);
        _context.CommandTemplates.Add(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(CommandTemplate template)
    {
        var existingEntity = await _context.CommandTemplates
            .FirstOrDefaultAsync(t => t.Id == template.Id);

        if (existingEntity == null)
        {
            throw new InvalidOperationException($"CommandTemplate with ID '{template.Id}' not found");
        }

        existingEntity.Name = template.Name;
        existingEntity.Platform = template.Platform;
        existingEntity.CommandPattern = template.CommandPattern;
        existingEntity.ParametersJson = System.Text.Json.JsonSerializer.Serialize(template.Parameters);

        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.CommandTemplates
            .FirstOrDefaultAsync(t => t.Id == id);

        if (entity != null)
        {
            // The delete reaches the other installs through its sync tombstone.
            await SyncTombstoneRepository.RecordAsync(_context, entity.PublicId, DateTime.UtcNow);
            _context.CommandTemplates.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<bool> ExistsAsync(string id)
    {
        return await _context.CommandTemplates
            .AsNoTracking()
            .AnyAsync(t => t.Id == id);
    }

    public async Task<int> CountAsync()
    {
        return await _context.CommandTemplates.CountAsync();
    }
}
