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

using FluentAssertions;
using Heimdall.Core.Security.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Repositories;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// The collection of the classes that hold the exclusive side of the process-wide seal gate, so
/// they never overlap one another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class VaultSealGateCollection
{
    /// <summary>The collection name shared by the members.</summary>
    public const string Name = "VaultSealGate";
}

/// <summary>
/// Every history write seals a value and stores it in one scope of the seal gate: while the
/// exclusive side is held (a disable withdrawing the key) the write waits, and it lands once the
/// side is released. A write that skips the gate could seal with a key that is about to vanish.
/// </summary>
/// <remarks>
/// Each test catches the removal of the gate entry of one method: the write would finish while the
/// exclusive side is still held.
/// </remarks>
[Collection(VaultSealGateCollection.Name)]
public sealed class CommandHistorySealGateTests : IAsyncLifetime
{
    private const string ActionId = "act-seal-gate";
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private string _root = string.Empty;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "heimdall_twinshell_sealgate_" + Guid.NewGuid().ToString("N"));
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
            Title = "Seal gate action",
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

    [Fact]
    public async Task AddAsync_WaitsForTheExclusiveSide_ThenLands()
    {
        await using TwinShellDbContext context = NewContext();
        CommandHistoryRepository repository = NewRepository(context);

        await AssertWaitsForTheExclusiveSideAsync(() => repository.AddAsync(Sample("add-1")), "add-1");

        (await CountAsync("add-1")).Should().Be(1);
    }

    [Fact]
    public async Task AddRangeAsync_WaitsForTheExclusiveSide_ThenLands()
    {
        await using TwinShellDbContext context = NewContext();
        CommandHistoryRepository repository = NewRepository(context);

        await AssertWaitsForTheExclusiveSideAsync(
            () => repository.AddRangeAsync([Sample("range-1"), Sample("range-2")]), "range-1");

        (await CountAsync("range-1")).Should().Be(1);
        (await CountAsync("range-2")).Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_WaitsForTheExclusiveSide_ThenLands()
    {
        await using (TwinShellDbContext seed = NewContext())
        {
            await NewRepository(seed).AddAsync(Sample("update-1"));
        }

        string before = await StoredCommandAsync("update-1");
        CommandHistory changed = Sample("update-1");
        changed.GeneratedCommand = "uptime -p";
        await using TwinShellDbContext context = NewContext();
        CommandHistoryRepository repository = NewRepository(context);

        await AssertWaitsForTheExclusiveSideAsync(() => repository.UpdateAsync(changed), "update-none");

        (await StoredCommandAsync("update-1")).Should().NotBe(before);
    }

    private async Task AssertWaitsForTheExclusiveSideAsync(Func<Task> write, string landedId)
    {
        IDisposable exclusive = await VaultSealGate.Shared.EnterExclusiveAsync(Bound);
        Task running;
        bool finishedWhileHeld;
        int landedWhileHeld;
        try
        {
            running = Task.Run(write);
            finishedWhileHeld = await Task.WhenAny(running, Task.Delay(HoldTime)) == running;
            landedWhileHeld = await CountAsync(landedId);
        }
        finally
        {
            exclusive.Dispose();
        }

        await running.WaitAsync(Bound);
        finishedWhileHeld.Should().BeFalse("the write has to wait for the exclusive side of the seal gate");
        landedWhileHeld.Should().Be(0, "the write must not land while the exclusive side is held");
    }

    private TwinShellDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TwinShellDbContext>().UseSqlite(_connectionString).Options);

    private static CommandHistoryRepository NewRepository(TwinShellDbContext context) =>
        new(context, NullLogger<CommandHistoryRepository>.Instance, new ReversibleProtector());

    private async Task<int> CountAsync(string id)
    {
        await using TwinShellDbContext context = NewContext();
        return await context.CommandHistories.CountAsync(row => row.Id == id);
    }

    private async Task<string> StoredCommandAsync(string id)
    {
        await using TwinShellDbContext context = NewContext();
        CommandHistoryEntity row = await context.CommandHistories.AsNoTracking().SingleAsync(r => r.Id == id);
        return row.GeneratedCommand;
    }

    private static CommandHistory Sample(string id) => new()
    {
        Id = id,
        ActionId = ActionId,
        GeneratedCommand = "uptime",
        Platform = Platform.Linux,
        Category = "System",
        ActionTitle = "Seal gate action",
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class ReversibleProtector : ISecretProtector
    {
        private const string Wrapper = "reversed:";

        public string Protect(string plainText) =>
            Wrapper + new string(plainText.Reverse().ToArray());

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith(Wrapper, StringComparison.Ordinal)
                ? new string(protectedValue[Wrapper.Length..].Reverse().ToArray())
                : null;
    }
}
