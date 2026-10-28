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

using Microsoft.EntityFrameworkCore;
using TwinShell.Core.Interfaces;
using TwinShell.Persistence.Entities;

namespace TwinShell.Persistence.Repositories;

/// <summary>
/// Stores the deletes of shared entries in the <c>SyncTombstones</c> table.
/// </summary>
public sealed class SyncTombstoneRepository : ISyncTombstoneRepository
{
    private readonly TwinShellDbContext _context;

    public SyncTombstoneRepository(TwinShellDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyDictionary<Guid, DateTime>> GetAllAsync()
        => await _context.SyncTombstones
            .AsNoTracking()
            .ToDictionaryAsync(t => t.PublicId, t => t.DeletedAt);

    public async Task SetAsync(Guid publicId, DateTime deletedAt)
    {
        await RecordAsync(_context, publicId, deletedAt);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveAsync(Guid publicId)
    {
        SyncTombstoneEntity? entity = await _context.SyncTombstones.FindAsync(publicId);
        if (entity is not null)
        {
            _context.SyncTombstones.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Queues the delete of <paramref name="publicId"/> in <paramref name="context"/>, for the
    /// caller's <c>SaveChanges</c>: a repository that deletes a shared entry records it in the
    /// same save, so the delete and its tombstone land together.
    /// </summary>
    internal static async Task RecordAsync(TwinShellDbContext context, Guid publicId, DateTime deletedAt)
    {
        SyncTombstoneEntity? entity = await context.SyncTombstones.FindAsync(publicId);
        if (entity is null)
        {
            context.SyncTombstones.Add(new SyncTombstoneEntity { PublicId = publicId, DeletedAt = deletedAt });
        }
        else
        {
            entity.DeletedAt = deletedAt;
        }
    }
}
