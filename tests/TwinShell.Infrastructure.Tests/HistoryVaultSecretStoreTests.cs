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

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Heimdall.Core.Security.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TwinShell.Core.Enums;
using TwinShell.Core.Security;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Security;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// The history follows the vault in both directions: only the protected value behind the marker is
/// converted, rows without the marker are never touched, and a failure leaves every row as it was.
/// </summary>
public sealed class HistoryVaultSecretStoreTests : IAsyncLifetime
{
    private const string ActionId = "act-residue";
    private const string LegacyPrefix = "legacy:";

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(VaultCipher.KeySizeBytes);

    private string _root = string.Empty;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "heimdall_twinshell_vault_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "twinshell.db"),
            Pooling = false,
        }.ToString();

        await using TwinShellDbContext context = NewContext();
        await context.Database.EnsureCreatedAsync();
        context.Actions.Add(new ActionEntity
        {
            Id = ActionId,
            Title = "Residue action",
            Description = "Owner of the history rows",
            Category = "System",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    private TwinShellDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TwinShellDbContext>().UseSqlite(_connectionString).Options);

    private static string? ToVault(string? stored) =>
        stored is not null && stored.StartsWith(LegacyPrefix, StringComparison.Ordinal)
            ? VaultSecretBlob.Seal(Key, Encoding.UTF8.GetBytes(stored[LegacyPrefix.Length..]))
            : stored;

    private static string? ToLegacy(string? stored) =>
        VaultSecretBlob.IsSecretBlob(stored)
            ? LegacyPrefix + Encoding.UTF8.GetString(VaultSecretBlob.Open(Key, stored!))
            : stored;

    private static string Wrapped(string protectedValue) => HistorySecretEnvelope.Marker + protectedValue;

    private async Task AddRowAsync(string id, string command, string parametersJson)
    {
        await using TwinShellDbContext context = NewContext();
        context.CommandHistories.Add(new CommandHistoryEntity
        {
            Id = id,
            ActionId = ActionId,
            GeneratedCommand = command,
            ParametersJson = parametersJson,
            Platform = Platform.Linux,
            CreatedAt = DateTime.UtcNow,
            Category = "System",
            ActionTitle = "Residue action",
        });
        await context.SaveChangesAsync();
    }

    private async Task<(string Command, string Parameters)> ReadRowAsync(string id)
    {
        await using TwinShellDbContext context = NewContext();
        CommandHistoryEntity row = await context.CommandHistories.AsNoTracking().SingleAsync(r => r.Id == id);
        return (row.GeneratedCommand, row.ParametersJson);
    }

    [Fact]
    public async Task RoundTrip_ConvertsOnlyTheProtectedValueBehindTheMarker()
    {
        await AddRowAsync("clear", "uptime", "{}");
        await AddRowAsync("legacy", Wrapped(LegacyPrefix + "cmd"), Wrapped(LegacyPrefix + "args"));
        var store = new HistoryVaultSecretStore(NewContext);
        (await store.HoldsVaultSecretsAsync(CancellationToken.None)).Should().BeFalse();

        await store.ForwardAsync(ToVault, CancellationToken.None);

        (await store.HoldsVaultSecretsAsync(CancellationToken.None)).Should().BeTrue();
        (string command, string parameters) = await ReadRowAsync("legacy");
        command.Should().StartWith(HistorySecretEnvelope.Marker);
        VaultSecretBlob.IsSecretBlob(command[HistorySecretEnvelope.Marker.Length..]).Should().BeTrue();
        VaultSecretBlob.IsSecretBlob(parameters[HistorySecretEnvelope.Marker.Length..]).Should().BeTrue();
        (await ReadRowAsync("clear")).Should().Be(("uptime", "{}"));

        await store.ReverseAsync(ToLegacy, CancellationToken.None);

        (await store.HoldsVaultSecretsAsync(CancellationToken.None)).Should().BeFalse();
        (await ReadRowAsync("legacy")).Should().Be((Wrapped(LegacyPrefix + "cmd"), Wrapped(LegacyPrefix + "args")));
        (await ReadRowAsync("clear")).Should().Be(("uptime", "{}"));
    }

    [Fact]
    public async Task Holds_DetectsAVaultBlobInEitherWrappedField()
    {
        string blob = ToVault(LegacyPrefix + "x")!;
        await AddRowAsync("in-parameters", "uptime", Wrapped(blob));
        var store = new HistoryVaultSecretStore(NewContext);

        (await store.HoldsVaultSecretsAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Holds_IgnoresLegacyAndClearRows()
    {
        await AddRowAsync("clear", "uptime", "{}");
        await AddRowAsync("legacy", Wrapped("|HMAC|legacy-sealed"), "{}");
        var store = new HistoryVaultSecretStore(NewContext);

        (await store.HoldsVaultSecretsAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Forward_WithNothingToSeal_DoesNotCallTheTransform()
    {
        await AddRowAsync("clear", "uptime", "{}");
        await AddRowAsync("vault", Wrapped(ToVault(LegacyPrefix + "x")!), "{}");
        var store = new HistoryVaultSecretStore(NewContext);
        var calls = 0;

        await store.ForwardAsync(stored => { calls++; return stored; }, CancellationToken.None);

        calls.Should().Be(0, "every wrapped value is already in vault form");
        (await store.HasUnsealedSecretsAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasUnsealed_IsTrueForALegacyWrappedRow()
    {
        await AddRowAsync("legacy", Wrapped(LegacyPrefix + "x"), "{}");
        var store = new HistoryVaultSecretStore(NewContext);

        (await store.HasUnsealedSecretsAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Holds_IgnoresAValueThatMerelyStartsLikeABlob()
    {
        string lookAlike = Convert.ToBase64String([.. VaultSecretBlob.Magic, .. "host"u8.ToArray()]);
        await AddRowAsync("lookalike", Wrapped(lookAlike), "{}");
        var store = new HistoryVaultSecretStore(NewContext);

        (await store.HoldsVaultSecretsAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task FailingPass_LeavesEveryRowAsItWas()
    {
        await AddRowAsync("a", Wrapped(LegacyPrefix + "one"), "{}");
        await AddRowAsync("b", Wrapped(LegacyPrefix + "two"), "{}");
        await AddRowAsync("c", Wrapped(LegacyPrefix + "three"), "{}");
        var calls = 0;

        // One row per batch, so earlier batches are already saved inside the transaction when the
        // third conversion fails.
        var store = new HistoryVaultSecretStore(NewContext, batchSize: 1);
        Func<Task> pass = () => store.ForwardAsync(
            stored => ++calls == 3 ? throw new InvalidOperationException("fails") : ToVault(stored),
            CancellationToken.None);

        await pass.Should().ThrowAsync<InvalidOperationException>();

        foreach ((string id, string plain) in new[] { ("a", "one"), ("b", "two"), ("c", "three") })
        {
            (await ReadRowAsync(id)).Should().Be((Wrapped(LegacyPrefix + plain), "{}"));
        }
    }
}
