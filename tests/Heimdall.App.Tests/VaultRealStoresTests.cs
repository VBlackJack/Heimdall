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

using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Security;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The three real stores, driven by the engine's own forward and reverse functions and by the
/// lifecycle service, over temporary files and a temporary SQLite database.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class VaultRealStoresTests : IAsyncLifetime
{
    private static readonly Argon2idParameters FastParams = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);

    private const string StrongPassword = "StrongMaster1!Pass";
    private const string SourceId = "sourcea1";
    private const string ActionId = "act-real-stores";

    private string _root = string.Empty;
    private string _connectionString = string.Empty;
    private ConfigManager _config = null!;
    private SharedSessionSecretStore _shared = null!;
    private VaultLifecycleService? _service;

    private string SharedPath => Path.Combine(_root, SharedSessionSecretStore.FileName);

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "Heimdall.RealStores." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());
        _config = new ConfigManager(Path.Combine(_root, "config"));
        await _config.InitializeAsync();
        _shared = new SharedSessionSecretStore(SharedPath);

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
            Title = "Real stores action",
            Description = "Owner of the history rows",
            Category = "System",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _service?.Dispose();
        CredentialProtectorStateScope.Reset();
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

    private static char[] Pw() => StrongPassword.ToCharArray();

    private IVaultSecretStore[] RealStores() =>
    [
        new SharedSessionVaultSecretStore(_shared),
        new PasswordPresetVaultSecretStore(_root),
        new HistoryVaultSecretStore(NewContext),
    ];

    private async Task AddHistoryRowAsync(string id, string command)
    {
        await using TwinShellDbContext context = NewContext();
        context.CommandHistories.Add(new CommandHistoryEntity
        {
            Id = id,
            ActionId = ActionId,
            GeneratedCommand = command,
            ParametersJson = "{}",
            Platform = Platform.Linux,
            CreatedAt = DateTime.UtcNow,
            Category = "System",
            ActionTitle = "Real stores action",
        });
        await context.SaveChangesAsync();
    }

    private async Task<string> HistoryCommandAsync(string id)
    {
        await using TwinShellDbContext context = NewContext();
        return (await context.CommandHistories.AsNoTracking().SingleAsync(row => row.Id == id)).GeneratedCommand;
    }

    private static string Wrapped(string plain) => HistorySecretEnvelope.Marker + CredentialProtector.Protect(plain);

    private static string Unwrapped(string stored) =>
        CredentialProtector.Unprotect(stored[HistorySecretEnvelope.Marker.Length..])!;

    [Fact]
    public async Task EveryRealStore_RoundTripsThroughTheEnginesOwnFunctions()
    {
        // Sealed the legacy way: no vault yet.
        await _shared.RememberAsync(SharedSecretKind.Session, SourceId, "key-1", CredentialProtector.Protect("shared-pw"));
        string presetFile = Path.Combine(_root, PasswordPresetStorage.PresetsFileName);
        await File.WriteAllTextAsync(presetFile, CredentialProtector.Protect("{\"Presets\":[]}"));
        await AddHistoryRowAsync("h1", Wrapped("history-cmd"));
        IVaultSecretStore[] stores = RealStores();

        using VaultDekHolder dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);
        foreach (IVaultSecretStore store in stores)
        {
            await store.ForwardAsync(VaultMigrationEngine.ForwardEncrypted, CancellationToken.None);
            Assert.True(await store.HoldsVaultSecretsAsync(CancellationToken.None), store.StoreNameKey);
            Assert.False(await store.HasUnsealedSecretsAsync(CancellationToken.None), store.StoreNameKey);
        }

        Assert.Equal("shared-pw", CredentialProtector.Unprotect(_shared.Find(SharedSecretKind.Session, SourceId, "key-1")));
        Assert.Equal("history-cmd", Unwrapped(await HistoryCommandAsync("h1")));

        foreach (IVaultSecretStore store in stores)
        {
            await store.ReverseAsync(VaultMigrationEngine.ReverseToCredentialProtectorLegacy, CancellationToken.None);
        }

        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        foreach (IVaultSecretStore store in stores)
        {
            Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None), store.StoreNameKey);
        }

        Assert.Equal("shared-pw", CredentialProtector.Unprotect(_shared.Find(SharedSecretKind.Session, SourceId, "key-1")));
        Assert.Equal("{\"Presets\":[]}", CredentialProtector.Unprotect((await File.ReadAllTextAsync(presetFile)).Trim()));
        Assert.Equal("history-cmd", Unwrapped(await HistoryCommandAsync("h1")));
    }

    [Fact]
    public async Task Lifecycle_WithTheThreeRealStores_EnableWriteDisableReadBack()
    {
        string presetFile = Path.Combine(_root, PasswordPresetStorage.PresetsFileName);
        await _shared.RememberAsync(SharedSecretKind.Session, SourceId, "before", CredentialProtector.Protect("shared-before"));
        await File.WriteAllTextAsync(presetFile, CredentialProtector.Protect("{\"Presets\":[]}"));
        await AddHistoryRowAsync("before", Wrapped("history-before"));
        _service = new VaultLifecycleService(_config, null, RealStores());

        await _service.EnableAsync(Pw(), FastParams);

        // Written while unlocked, as the application does: sealed with the vault key as it is written.
        await _shared.RememberAsync(SharedSecretKind.Session, SourceId, "during", CredentialProtector.Protect("shared-during"));
        new PasswordPresetStorage(_root).Save(new PasswordGeneratorStore());
        await AddHistoryRowAsync("during", Wrapped("history-during"));
        foreach (IVaultSecretStore store in _service.SecretStores)
        {
            Assert.True(await store.HoldsVaultSecretsAsync(CancellationToken.None), store.StoreNameKey);
        }

        await _service.DisableAsync(Pw());

        Assert.False(CredentialProtector.IsVaultUnlocked);
        Assert.False((await _config.LoadSettingsAsync()).VaultEnabled);
        foreach (IVaultSecretStore store in _service.SecretStores)
        {
            Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None), store.StoreNameKey);
        }

        Assert.Equal("shared-before", CredentialProtector.Unprotect(_shared.Find(SharedSecretKind.Session, SourceId, "before")));
        Assert.Equal("shared-during", CredentialProtector.Unprotect(_shared.Find(SharedSecretKind.Session, SourceId, "during")));
        Assert.Equal("history-before", Unwrapped(await HistoryCommandAsync("before")));
        Assert.Equal("history-during", Unwrapped(await HistoryCommandAsync("during")));
        Assert.NotNull(CredentialProtector.Unprotect((await File.ReadAllTextAsync(presetFile)).Trim()));
    }

    [Fact]
    public void ServiceRegistration_GivesTheLifecycleServiceTheThreeStores()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigManager>(_config);
        services.AddSingleton(_shared);
        VaultServiceRegistration.Register(services, _root);
        TwinShellBootstrapper.RegisterVaultStore(services, Path.Combine(_root, "twinshell.db"));

        using ServiceProvider provider = services.BuildServiceProvider();
        VaultLifecycleService lifecycle = provider.GetRequiredService<VaultLifecycleService>();

        Assert.Same(lifecycle, provider.GetRequiredService<VaultLifecycleService>());
        Assert.Equal(
            new[]
            {
                SharedSessionVaultSecretStore.NameKey,
                PasswordPresetVaultSecretStore.NameKey,
                HistoryVaultSecretStore.NameKey,
            }.Order(),
            lifecycle.SecretStores.Select(store => store.StoreNameKey).Order());
    }
}
