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

using Heimdall.Core.Security.Vault;
using Microsoft.EntityFrameworkCore;
using TwinShell.Core.Security;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Persistence.Security;

/// <summary>
/// Seals the wrapped fields of the command history with the vault key when the vault is enabled,
/// and returns them to the legacy form when it is disabled.
/// </summary>
/// <remarks>
/// A wrapped value is the history marker followed by the protected value, in either of the two
/// fields that can carry what the user typed. Only the protected value is converted: the marker
/// stays, so the history keeps recognising its own rows. Rows without the marker are never touched.
/// The rewrite runs in one transaction, in batches, so a failure leaves every row as it was.
/// </remarks>
public sealed class HistoryVaultSecretStore : IVaultSecretStore
{
    /// <summary>The locale key naming this store.</summary>
    public const string NameKey = "VaultStoreCommandHistory";

    private const string EmptyParametersJson = "{}";

    /// <summary>How many rows are loaded and saved at a time.</summary>
    public const int DefaultBatchSize = 200;

    private readonly Func<TwinShellDbContext> _contextFactory;
    private readonly int _batchSize;

    /// <param name="contextFactory">Creates a context over the history database; each is disposed after one pass.</param>
    /// <param name="batchSize">How many rows are loaded and saved at a time.</param>
    public HistoryVaultSecretStore(Func<TwinShellDbContext> contextFactory, int batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _contextFactory = contextFactory;
        _batchSize = batchSize;
    }

    /// <inheritdoc/>
    public string StoreNameKey => NameKey;

    /// <inheritdoc/>
    public Task ForwardAsync(Func<string?, string?> toVault, CancellationToken cancellationToken) =>
        TransformAsync(toVault, NeedsSealing, cancellationToken);

    /// <inheritdoc/>
    public Task ReverseAsync(Func<string?, string?> toLegacy, CancellationToken cancellationToken) =>
        TransformAsync(toLegacy, HoldsVaultForm, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> HoldsVaultSecretsAsync(CancellationToken cancellationToken)
    {
        await using TwinShellDbContext context = _contextFactory();
        int markerLength = HistorySecretEnvelope.Marker.Length;
        string vaultPrefix = VaultPrefix;
        int vaultLength = vaultPrefix.Length;

        // The prefix test is exact (substr compares case-sensitively; LIKE does not); the
        // well-formedness test follows in memory on the few rows that pass it.
        var candidates = context.CommandHistories
            .AsNoTracking()
            .Where(row => row.GeneratedCommand.Substring(0, vaultLength) == vaultPrefix
                || row.ParametersJson.Substring(0, vaultLength) == vaultPrefix)
            .Select(row => new { row.GeneratedCommand, row.ParametersJson })
            .AsAsyncEnumerable();

        await foreach (var row in candidates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (HoldsVaultBlob(row.GeneratedCommand, markerLength) || HoldsVaultBlob(row.ParametersJson, markerLength))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> HasUnsealedSecretsAsync(CancellationToken cancellationToken)
    {
        await using TwinShellDbContext context = _contextFactory();
        return await NeedsSealing(context).AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The history marker followed by the text every vault secret blob starts with.</summary>
    internal static string VaultPrefix => HistorySecretEnvelope.Marker + VaultSecretBlob.EncodedPrefix;

    /// <summary>Rows with a wrapped value that is not in vault form: the forward pass has work to do.</summary>
    private static IQueryable<CommandHistoryEntity> NeedsSealing(TwinShellDbContext context)
    {
        string marker = HistorySecretEnvelope.Marker;
        int markerLength = marker.Length;
        string vaultPrefix = VaultPrefix;
        int vaultLength = vaultPrefix.Length;
        return context.CommandHistories
            .AsNoTracking()
            .Where(row =>
                (row.GeneratedCommand.Substring(0, markerLength) == marker
                    && row.GeneratedCommand.Substring(0, vaultLength) != vaultPrefix)
                || (row.ParametersJson.Substring(0, markerLength) == marker
                    && row.ParametersJson.Substring(0, vaultLength) != vaultPrefix));
    }

    /// <summary>Rows with a wrapped value in vault form: the reverse pass has work to do.</summary>
    private static IQueryable<CommandHistoryEntity> HoldsVaultForm(TwinShellDbContext context)
    {
        string vaultPrefix = VaultPrefix;
        int vaultLength = vaultPrefix.Length;
        return context.CommandHistories
            .AsNoTracking()
            .Where(row => row.GeneratedCommand.Substring(0, vaultLength) == vaultPrefix
                || row.ParametersJson.Substring(0, vaultLength) == vaultPrefix);
    }

    /// <inheritdoc/>
    public async Task DropUnreadableAsync(Func<string?, bool> isUnreadable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isUnreadable);
        await ForEachBatchAsync(
            HoldsVaultForm,
            (context, rows) =>
            {
                foreach (CommandHistoryEntity row in rows)
                {
                    bool commandLost = IsUnreadableWrapped(row.GeneratedCommand, isUnreadable);
                    bool parametersLost = IsUnreadableWrapped(row.ParametersJson, isUnreadable);

                    // The row goes only when nothing readable remains in it; a readable field is kept.
                    if (commandLost && parametersLost)
                    {
                        context.CommandHistories.Remove(row);
                        continue;
                    }

                    if (commandLost)
                    {
                        row.GeneratedCommand = CommandHistoryMapper.UnreadableCommand;
                    }

                    if (parametersLost)
                    {
                        row.ParametersJson = EmptyParametersJson;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private Task TransformAsync(
        Func<string?, string?> transform,
        Func<TwinShellDbContext, IQueryable<CommandHistoryEntity>> rowsToConvert,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return ForEachBatchAsync(
            rowsToConvert,
            (_, rows) =>
            {
                foreach (CommandHistoryEntity row in rows)
                {
                    row.GeneratedCommand = Convert(row.GeneratedCommand, transform);
                    row.ParametersJson = Convert(row.ParametersJson, transform);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="change"/> over the rows <paramref name="rowsToConvert"/> selects, one
    /// batch and one transaction at a time. A pass that stops part way leaves whole batches done and
    /// the rest untouched; the conversions are idempotent, so the next pass finishes it.
    /// </summary>
    private async Task ForEachBatchAsync(
        Func<TwinShellDbContext, IQueryable<CommandHistoryEntity>> rowsToConvert,
        Action<TwinShellDbContext, List<CommandHistoryEntity>> change,
        CancellationToken cancellationToken)
    {
        await using TwinShellDbContext context = _contextFactory();

        // The usual case, at every unlock, is that there is nothing to do: that is one cheap read and
        // no write transaction.
        if (!await rowsToConvert(context).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        List<string> ids = await rowsToConvert(context)
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (string[] batch in ids.Chunk(_batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Loaded inside the batch's transaction: a row changed since the ids were read is seen as it is.
            List<CommandHistoryEntity> rows = await context.CommandHistories
                .Where(row => batch.Contains(row.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            change(context, rows);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
        }
    }

    private static bool IsUnreadableWrapped(string stored, Func<string?, bool> isUnreadable) =>
        HistorySecretEnvelope.IsSealed(stored)
        && isUnreadable(stored[HistorySecretEnvelope.Marker.Length..]);

    private static string Convert(string stored, Func<string?, string?> transform)
    {
        if (!HistorySecretEnvelope.IsSealed(stored))
        {
            return stored;
        }

        string inner = stored[HistorySecretEnvelope.Marker.Length..];
        string? next = transform(inner);
        return string.IsNullOrEmpty(next) || string.Equals(next, inner, StringComparison.Ordinal)
            ? stored
            : HistorySecretEnvelope.Marker + next;
    }

    private static bool HoldsVaultBlob(string stored, int markerLength) =>
        HistorySecretEnvelope.IsSealed(stored)
        && VaultConfigurationScan.IsVaultSecret(stored[markerLength..]);
}
