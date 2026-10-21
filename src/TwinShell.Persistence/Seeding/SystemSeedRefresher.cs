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
using TwinShell.Core.Helpers;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Mappers;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Persistence.Seeding;

/// <summary>
/// What a seed refresh did, in rows.
/// </summary>
/// <param name="Applied">False when the stored hash already matched and nothing was written.</param>
/// <param name="Inserted">System actions that were not in the database before.</param>
/// <param name="Updated">System actions rewritten in place from the new seed.</param>
/// <param name="Removed">System actions the new seed no longer ships.</param>
/// <param name="FavoritesRemoved">Favorites left pointing at no action, removed.</param>
public sealed record SeedRefreshResult(
    bool Applied,
    int Inserted,
    int Updated,
    int Removed,
    int FavoritesRemoved)
{
    /// <summary>The result of a refresh that found the seed unchanged.</summary>
    public static SeedRefreshResult Unchanged { get; } = new(false, 0, 0, 0, 0);
}

/// <summary>
/// Brings the shipped (system) actions of an existing database in line with the seed the
/// running build carries.
/// </summary>
/// <remarks>
/// <para>
/// The first launch used to be the only time the seed reached a database: seeding ran when
/// the Actions table was empty, so no seed fix since an operator's first launch had ever
/// reached their copy. The content hash of the seed directory is now stored in
/// <c>SeedState</c>, and a different hash, or none on a table that is not empty, replaces the
/// system actions with the new set.
/// </para>
/// <para>
/// The replacement has the effect of deleting every action with <c>IsUserCreated = false</c>
/// and inserting the new seed, but it is done row by row: an action whose id the new seed
/// still ships is rewritten in place, so its favorites and its command history, which
/// cascade from the action row, survive. Only an action the seed no longer ships is deleted,
/// with its translations, its favorites and its history. Its public id and creation date are
/// kept, because a sync folder knows the action by the former.
/// </para>
/// <para>
/// User-created actions are never touched, except that one still carrying an old shipped
/// category string gets its key (<see cref="CategoryMigration"/>), so it lands in the same
/// group as the shipped actions. A seed action whose id an operator's own action already
/// uses is skipped. Everything runs in one transaction: a failure rolls back and leaves the
/// previous catalogue, and the previous hash, in place.
/// </para>
/// </remarks>
public sealed class SystemSeedRefresher
{
    /// <summary>The <c>SeedState</c> key under which the applied hash is stored.</summary>
    public const string SeedHashKey = "SeedHash";

    private readonly TwinShellDbContext _context;

    public SystemSeedRefresher(TwinShellDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Returns the hash of the seed last applied to this database, or null when none was
    /// recorded (a database seeded before the hash existed, or never seeded).
    /// </summary>
    public async Task<string?> ReadAppliedHashAsync(CancellationToken cancellationToken = default)
    {
        SeedStateEntity? state = await _context.SeedStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SeedHashKey, cancellationToken);
        return state?.Value;
    }

    /// <summary>
    /// Replaces the system actions with <paramref name="seed"/> when
    /// <paramref name="seedHash"/> differs from the hash last applied.
    /// </summary>
    /// <param name="seed">The parsed seed actions, all system actions.</param>
    /// <param name="seedHash">The content hash of the seed directory they came from.</param>
    /// <param name="cancellationToken">Cancels before the commit.</param>
    public async Task<SeedRefreshResult> RefreshAsync(
        IReadOnlyList<ActionModel> seed,
        string seedHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedHash);

        string? appliedHash = await ReadAppliedHashAsync(cancellationToken);
        if (string.Equals(appliedHash, seedHash, StringComparison.Ordinal))
        {
            return SeedRefreshResult.Unchanged;
        }

        _context.ChangeTracker.Clear();
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            SeedRefreshResult result = await ReplaceSystemActionsAsync(seed, seedHash, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _context.ChangeTracker.Clear();
        }
    }

    private async Task<SeedRefreshResult> ReplaceSystemActionsAsync(
        IReadOnlyList<ActionModel> seed,
        string seedHash,
        CancellationToken cancellationToken)
    {
        List<ActionEntity> stored = await _context.Actions.ToListAsync(cancellationToken);
        HashSet<string> userActionIds = stored
            .Where(a => a.IsUserCreated)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, ActionEntity> systemActions = stored
            .Where(a => !a.IsUserCreated)
            .ToDictionary(a => a.Id, StringComparer.Ordinal);

        List<ActionModel> incoming = seed
            .Where(action => !userActionIds.Contains(action.Id))
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        HashSet<string> incomingIds = incoming.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        HashSet<string> oldSystemTemplateIds = systemActions.Values
            .SelectMany(a => new[] { a.WindowsCommandTemplateId, a.LinuxCommandTemplateId })
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<string, CommandTemplateEntity> incomingTemplates = incoming
            .SelectMany(a => new[] { a.WindowsCommandTemplate, a.LinuxCommandTemplate })
            .OfType<Core.Models.CommandTemplate>()
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => CommandTemplateMapper.ToEntity(g.Last()), StringComparer.Ordinal);

        await UpsertTemplatesAsync(incomingTemplates, cancellationToken);

        int inserted = 0;
        int updated = 0;
        var translations = new List<ActionTranslationEntity>();

        foreach (ActionModel action in incoming)
        {
            action.IsUserCreated = false;
            ActionEntity entity = ActionMapper.ToEntity(action);
            translations.AddRange(entity.Translations);
            entity.Translations = new();

            if (systemActions.TryGetValue(action.Id, out ActionEntity? existing))
            {
                entity.PublicId = existing.PublicId;
                entity.CreatedAt = existing.CreatedAt;
                _context.Entry(existing).CurrentValues.SetValues(entity);
                updated++;
            }
            else
            {
                _context.Actions.Add(entity);
                inserted++;
            }
        }

        List<ActionEntity> retired = systemActions.Values
            .Where(a => !incomingIds.Contains(a.Id))
            .ToList();
        _context.Actions.RemoveRange(retired);

        List<string> touchedIds = systemActions.Keys.Union(incomingIds, StringComparer.Ordinal).ToList();
        await _context.ActionTranslations
            .Where(t => touchedIds.Contains(t.ActionId))
            .ExecuteDeleteAsync(cancellationToken);
        _context.ActionTranslations.AddRange(translations);

        await WriteHashAsync(seedHash, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        await RemoveOrphanTemplatesAsync(oldSystemTemplateIds, incomingTemplates.Keys, cancellationToken);
        await MigrateUserCategoriesAsync(cancellationToken);

        // The action delete cascades to favorites in the database; this covers a database
        // whose foreign keys were not enforced when the rows were written.
        int favoritesRemoved = await _context.UserFavorites
            .Where(f => !_context.Actions.Any(a => a.Id == f.ActionId))
            .ExecuteDeleteAsync(cancellationToken);

        return new SeedRefreshResult(true, inserted, updated, retired.Count, favoritesRemoved);
    }

    private async Task UpsertTemplatesAsync(
        Dictionary<string, CommandTemplateEntity> incomingTemplates,
        CancellationToken cancellationToken)
    {
        List<string> incomingIds = incomingTemplates.Keys.ToList();
        Dictionary<string, CommandTemplateEntity> existing = await _context.CommandTemplates
            .Where(t => incomingIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, StringComparer.Ordinal, cancellationToken);

        foreach ((string id, CommandTemplateEntity template) in incomingTemplates)
        {
            if (existing.TryGetValue(id, out CommandTemplateEntity? current))
            {
                // A sync folder references a template by its public id: keep it.
                template.PublicId = current.PublicId;
                _context.Entry(current).CurrentValues.SetValues(template);
            }
            else
            {
                _context.CommandTemplates.Add(template);
            }
        }
    }

    private async Task RemoveOrphanTemplatesAsync(
        HashSet<string> oldSystemTemplateIds,
        IEnumerable<string> keptTemplateIds,
        CancellationToken cancellationToken)
    {
        List<string> candidates = oldSystemTemplateIds
            .Except(keptTemplateIds, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        await _context.CommandTemplates
            .Where(t => candidates.Contains(t.Id)
                && !_context.Actions.Any(a => a.WindowsCommandTemplateId == t.Id || a.LinuxCommandTemplateId == t.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task MigrateUserCategoriesAsync(CancellationToken cancellationToken)
    {
        foreach ((string legacy, string key) in CategoryMigration.Legacy)
        {
            await _context.Actions
                .Where(a => a.IsUserCreated && a.Category == legacy)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Category, key), cancellationToken);
        }
    }

    private async Task WriteHashAsync(string seedHash, CancellationToken cancellationToken)
    {
        SeedStateEntity? state = await _context.SeedStates
            .FirstOrDefaultAsync(s => s.Key == SeedHashKey, cancellationToken);
        if (state is null)
        {
            _context.SeedStates.Add(new SeedStateEntity { Key = SeedHashKey, Value = seedHash });
        }
        else
        {
            state.Value = seedHash;
        }
    }
}
