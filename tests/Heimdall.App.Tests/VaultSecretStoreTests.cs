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
using System.Text;
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>Transforms shaped like the engine's, over a fixed key, so no global state is touched.</summary>
internal static class VaultTestTransforms
{
    private const string LegacyPrefix = "legacy:";
    private static readonly byte[] Key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public static string Legacy(string plain) => LegacyPrefix + plain;

    public static string? ToVault(string? stored) =>
        stored is not null && stored.StartsWith(LegacyPrefix, StringComparison.Ordinal)
            ? Heimdall.Core.Security.Vault.VaultSecretBlob.Seal(
                Key, System.Text.Encoding.UTF8.GetBytes(stored[LegacyPrefix.Length..]))
            : stored;

    public static string? ToLegacy(string? stored) =>
        Heimdall.Core.Security.Vault.VaultSecretBlob.IsSecretBlob(stored)
            ? LegacyPrefix + System.Text.Encoding.UTF8.GetString(
                Heimdall.Core.Security.Vault.VaultSecretBlob.Open(Key, stored!))
            : stored;
}

/// <summary>
/// The stores outside the configuration files follow the vault in both directions, and a store
/// that cannot be read says so instead of answering that it holds nothing.
/// </summary>
public sealed class VaultSecretStoreTests : IDisposable
{
    private const string SourceId = "sourcea1";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "Heimdall.VaultStores." + Guid.NewGuid().ToString("N"));

    public VaultSecretStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private string SharedPath => Path.Combine(_directory, SharedSessionSecretStore.FileName);

    private string PresetPath => Path.Combine(_directory, PasswordPresetStorage.PresetsFileName);

    [Fact]
    public async Task SharedSessionStore_RoundTripsLegacyToVaultToLegacy()
    {
        var secrets = new SharedSessionSecretStore(SharedPath);
        var store = new SharedSessionVaultSecretStore(secrets);
        await secrets.RememberAsync(SharedSecretKind.Session, SourceId, "key-1", VaultTestTransforms.Legacy("pw-1"));
        await secrets.RememberAsync(SharedSecretKind.Gateway, SourceId, "key-2", VaultTestTransforms.Legacy("pw-2"));
        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));

        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);

        Assert.True(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        Assert.True(VaultSecretBlob.IsSecretBlob(secrets.Find(SharedSecretKind.Session, SourceId, "key-1")));
        string forwardedOnDisk = File.ReadAllText(SharedPath);

        // Forwarding again changes nothing: the pass is idempotent.
        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);
        Assert.Equal(forwardedOnDisk, File.ReadAllText(SharedPath));

        await store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None);

        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        Assert.Equal(VaultTestTransforms.Legacy("pw-1"), secrets.Find(SharedSecretKind.Session, SourceId, "key-1"));
        Assert.Equal(VaultTestTransforms.Legacy("pw-2"), secrets.Find(SharedSecretKind.Gateway, SourceId, "key-2"));

        // A store read back from the file, as after a restart, sees the same.
        var reread = new SharedSessionSecretStore(SharedPath);
        Assert.Equal(VaultTestTransforms.Legacy("pw-1"), reread.Find(SharedSecretKind.Session, SourceId, "key-1"));
    }

    [Fact]
    public async Task SharedSessionStore_AbsentFile_IsANoOp()
    {
        var store = new SharedSessionVaultSecretStore(new SharedSessionSecretStore(SharedPath));

        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);
        await store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None);

        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        Assert.False(File.Exists(SharedPath));
    }

    [Fact]
    public async Task SharedSessionStore_UnreadableFile_FailsClosedEvenWithAWarmCache()
    {
        var secrets = new SharedSessionSecretStore(SharedPath);
        var store = new SharedSessionVaultSecretStore(secrets);

        // Warm cache: the entries are in memory, so a cache-based answer would say "clean".
        await secrets.RememberAsync(SharedSecretKind.Session, SourceId, "key-1", VaultTestTransforms.ToVault(VaultTestTransforms.Legacy("pw"))!);
        Assert.NotNull(secrets.Find(SharedSecretKind.Session, SourceId, "key-1"));
        byte[] before = File.ReadAllBytes(SharedPath);

        using (new FileStream(SharedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.HoldsVaultSecretsAsync(CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(
                () => store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None));
        }

        Assert.Equal(before, File.ReadAllBytes(SharedPath));
    }

    [Fact]
    public async Task SharedSessionStore_CorruptFile_ReportsResidueAndIsNotRewritten()
    {
        const string corrupt = "{ \"FormatVersion\": 1, \"Entries\": [ not json";
        await File.WriteAllTextAsync(SharedPath, corrupt);
        var store = new SharedSessionVaultSecretStore(new SharedSessionSecretStore(SharedPath));

        Assert.True(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<JsonException>(
            () => store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None));

        Assert.Equal(corrupt, await File.ReadAllTextAsync(SharedPath));
    }

    [Fact]
    public async Task PresetStore_RoundTripsWithTheByteOrderMarkTheStorageWrites()
    {
        var store = new PasswordPresetVaultSecretStore(_directory);
        await File.WriteAllTextAsync(PresetPath, VaultTestTransforms.Legacy("presets"), Encoding.UTF8);
        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));

        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);

        Assert.True(await store.HoldsVaultSecretsAsync(CancellationToken.None));

        await store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None);

        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        Assert.Equal(VaultTestTransforms.Legacy("presets"), (await File.ReadAllTextAsync(PresetPath)).Trim());
    }

    [Fact]
    public async Task SharedSessionStore_ReportsALegacySecretAsUnsealed_ThenSealed()
    {
        var secrets = new SharedSessionSecretStore(SharedPath);
        var store = new SharedSessionVaultSecretStore(secrets);
        Assert.False(await store.HasUnsealedSecretsAsync(CancellationToken.None));
        await secrets.RememberAsync(SharedSecretKind.Session, SourceId, "key-1", VaultTestTransforms.Legacy("pw-1"));
        Assert.True(await store.HasUnsealedSecretsAsync(CancellationToken.None));

        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);

        Assert.False(await store.HasUnsealedSecretsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PresetStore_ReportsALegacyFileAsUnsealed_ThenSealed()
    {
        var store = new PasswordPresetVaultSecretStore(_directory);
        await File.WriteAllTextAsync(PresetPath, VaultTestTransforms.Legacy("presets"), Encoding.UTF8);
        Assert.True(await store.HasUnsealedSecretsAsync(CancellationToken.None));

        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);

        Assert.False(await store.HasUnsealedSecretsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PresetStore_WaitsForTheLockTheStorageShares()
    {
        await File.WriteAllTextAsync(PresetPath, VaultTestTransforms.Legacy("presets"), Encoding.UTF8);
        var store = new PasswordPresetVaultSecretStore(_directory);
        SemaphoreSlim gate = PasswordPresetFileGate.For(PresetPath);

        await gate.WaitAsync();
        Task pass = store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);
        await Task.Delay(150);
        bool finishedWhileHeld = pass.IsCompleted;
        gate.Release();
        await pass;

        Assert.False(finishedWhileHeld);
        Assert.True(await store.HoldsVaultSecretsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PresetStore_AbsentFile_IsANoOp()
    {
        var store = new PasswordPresetVaultSecretStore(_directory);

        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);
        await store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None);

        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        Assert.False(File.Exists(PresetPath));
    }

    [Theory]
    [InlineData("{\"Presets\":[]}")]
    [InlineData("[]")]
    [InlineData("unrecognised content that is not a sealed value")]
    public async Task PresetStore_PlainOrUnrecognisedFile_IsNotRewritten(string content)
    {
        await File.WriteAllTextAsync(PresetPath, content, Encoding.UTF8);
        byte[] before = await File.ReadAllBytesAsync(PresetPath);
        var store = new PasswordPresetVaultSecretStore(_directory);

        Assert.False(await store.HoldsVaultSecretsAsync(CancellationToken.None));
        await store.ForwardAsync(VaultTestTransforms.ToVault, CancellationToken.None);
        await store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None);

        Assert.Equal(before, await File.ReadAllBytesAsync(PresetPath));
    }

    [Fact]
    public async Task PresetStore_UnreadableFile_FailsClosed()
    {
        await File.WriteAllTextAsync(PresetPath, VaultTestTransforms.ToVault(VaultTestTransforms.Legacy("p")));
        byte[] before = await File.ReadAllBytesAsync(PresetPath);
        var store = new PasswordPresetVaultSecretStore(_directory);

        using (new FileStream(PresetPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.HoldsVaultSecretsAsync(CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(
                () => store.ReverseAsync(VaultTestTransforms.ToLegacy, CancellationToken.None));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(PresetPath));
    }
}
