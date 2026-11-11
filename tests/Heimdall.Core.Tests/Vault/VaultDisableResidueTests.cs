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

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

/// <summary>
/// The vault converts every store in both directions, and disabling it is refused, with the vault
/// left as protective as it was, whenever something would be lost with the key.
/// </summary>
[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class VaultDisableResidueTests : IAsyncLifetime
{
    private static readonly Argon2idParameters FastParams = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);

    private const string StrongPassword = "StrongMaster1!Pass";
    private const string GatewayPassword = "gateway-pw";
    private const string GatewayPassphrase = "gateway-pass";
    private const string OtherGatewayPassword = "other-gateway-pw";
    private const string ConfigurationStoreKey = "VaultStoreConfiguration";
    private const string ServerSecret = "rdp-pw";

    private readonly List<VaultLifecycleService> _services = new();
    private string _tempDir = string.Empty;
    private ConfigManager _configManager = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.VaultResidue." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configManager = new ConfigManager(_tempDir);
        await _configManager.InitializeAsync();
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());
    }

    public Task DisposeAsync()
    {
        foreach (VaultLifecycleService service in _services)
        {
            service.Dispose();
        }

        CredentialProtectorStateScope.Reset();

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    private static char[] Pw(string s) => s.ToCharArray();

    private VaultLifecycleService NewService(IVaultHelloService? hello, params IVaultSecretStore[] stores)
    {
        var service = new VaultLifecycleService(_configManager, hello, stores);
        _services.Add(service);
        return service;
    }

    private async Task<VaultLifecycleService> EnabledServiceAsync(
        IVaultHelloService? hello = null,
        params IVaultSecretStore[] stores)
    {
        VaultLifecycleService service = NewService(hello, stores);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        return service;
    }

    private async Task SaveServerSecretAsync()
    {
        await _configManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = "srv1",
                DisplayName = "Server 1",
                ConnectionType = "RDP",
                RdpPasswordEncrypted = CredentialProtector.Protect(ServerSecret),
            },
        ]);
    }

    private async Task<string?> ServerSecretAsync() =>
        (await _configManager.LoadServersAsync())[0].RdpPasswordEncrypted;

    private static string RandomVaultBlob() =>
        VaultSecretBlob.Seal(RandomNumberGenerator.GetBytes(VaultCipher.KeySizeBytes), "x"u8);

    /// <summary>Writes gateways while the vault is unlocked, so their secrets are sealed with its key.</summary>
    private async Task WriteGatewaysWhileUnlockedAsync()
    {
        await _configManager.MergeSettingAsync(s =>
        {
            s.SshGateways.Add(new SshGatewayDto
            {
                Id = "gw1",
                Name = "Bastion",
                Host = "bastion.example.test",
                User = "ops",
                SshPasswordEncrypted = CredentialProtector.Protect(GatewayPassword),
                SshKeyPassphraseEncrypted = CredentialProtector.Protect(GatewayPassphrase),
            });
            s.SshGateways.Add(new SshGatewayDto
            {
                Id = "gw2",
                Name = "Second",
                Host = "second.example.test",
                User = "ops",
                SshPasswordEncrypted = CredentialProtector.Protect(OtherGatewayPassword),
            });
        });
    }

    [Fact]
    public async Task EnableAsync_SealsGatewaysAndForwardsEveryStore()
    {
        await _configManager.MergeSettingAsync(s => s.SshGateways.Add(new SshGatewayDto
        {
            Id = "gw1",
            Host = "bastion.example.test",
            User = "ops",
            SshPasswordEncrypted = CredentialProtector.Protect(GatewayPassword),
        }));
        await SaveServerSecretAsync();
        var first = new FakeStore("StoreKeyOne") { Value = CredentialProtector.Protect("one") };
        var second = new FakeStore("StoreKeyTwo") { Value = CredentialProtector.Protect("two") };

        await EnabledServiceAsync(null, first, second);

        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.True(VaultSecretBlob.IsSecretBlob(settings.SshGateways[0].SshPasswordEncrypted));
        Assert.Equal(1, first.ForwardCalls);
        Assert.Equal(1, second.ForwardCalls);
        Assert.True(VaultSecretBlob.IsSecretBlob(first.Value));
        Assert.True(VaultSecretBlob.IsSecretBlob(second.Value));
        Assert.Equal("one", CredentialProtector.Unprotect(first.Value));

        // The last good copy no longer holds the pre-vault file.
        Assert.Equal(
            File.ReadAllText(_configManager.ServersPath),
            File.ReadAllText(ConfigManager.BackupPathFor(_configManager.ServersPath)));
    }

    [Fact]
    public async Task DisableAsync_ReversesGatewaySecrets()
    {
        VaultLifecycleService service = await EnabledServiceAsync();
        await WriteGatewaysWhileUnlockedAsync();
        AppSettings before = await _configManager.LoadSettingsAsync();
        Assert.True(VaultSecretBlob.IsSecretBlob(before.SshGateways[0].SshPasswordEncrypted));
        bool secondDeclaredPassphraseBefore = before.SshGateways[1].HasSshKeyPassphraseEncryptedField;

        await service.DisableAsync(Pw(StrongPassword));

        AppSettings after = await _configManager.LoadSettingsAsync();
        SshGatewayDto first = after.SshGateways[0];
        Assert.False(VaultSecretBlob.IsSecretBlob(first.SshPasswordEncrypted));
        Assert.False(VaultSecretBlob.IsSecretBlob(first.SshKeyPassphraseEncrypted));
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(first.SshPasswordEncrypted));
        Assert.Equal(GatewayPassphrase, CredentialProtector.Unprotect(first.SshKeyPassphraseEncrypted));

        SshGatewayDto second = after.SshGateways[1];
        Assert.Equal(OtherGatewayPassword, CredentialProtector.Unprotect(second.SshPasswordEncrypted));
        Assert.Null(second.SshKeyPassphraseEncrypted);
        Assert.Equal(secondDeclaredPassphraseBefore, second.HasSshKeyPassphraseEncryptedField);
    }

    [Fact]
    public async Task DisableAsync_ReversesEveryStoreThenErases()
    {
        var first = new FakeStore("StoreKeyOne");
        var second = new FakeStore("StoreKeyTwo");
        VaultLifecycleService service = await EnabledServiceAsync(null, first, second);
        first.Value = CredentialProtector.Protect("one");
        second.Value = CredentialProtector.Protect("two");
        Assert.True(VaultSecretBlob.IsSecretBlob(first.Value));

        await service.DisableAsync(Pw(StrongPassword));

        Assert.Equal(1, first.ReverseCalls);
        Assert.Equal(1, second.ReverseCalls);
        Assert.False(CredentialProtector.IsVaultUnlocked);
        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.False(settings.VaultEnabled);
        Assert.Equal(VaultMigrationState.None, settings.VaultMigrationState);
        Assert.False(VaultSecretBlob.IsSecretBlob(first.Value));
        Assert.Equal("one", CredentialProtector.Unprotect(first.Value));
        Assert.Equal("two", CredentialProtector.Unprotect(second.Value));
    }

    [Fact]
    public async Task DisableAsync_GatewaySecretWrittenWhileUnlocked_IsReadableAfterDisable()
    {
        var presets = new FakeStore("StorePresets");
        var history = new FakeStore("StoreHistory");
        var shared = new FakeStore("StoreShared");
        VaultLifecycleService service = await EnabledServiceAsync(null, presets, history, shared);
        await WriteGatewaysWhileUnlockedAsync();
        await SaveServerSecretAsync();
        presets.Value = CredentialProtector.Protect("preset-secret");
        history.Value = CredentialProtector.Protect("history-secret");
        shared.Value = CredentialProtector.Protect("shared-secret");

        await service.DisableAsync(Pw(StrongPassword));

        Assert.False(CredentialProtector.IsVaultUnlocked);
        Assert.False(CredentialProtector.IsVaultEnabled);
        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.False(settings.VaultEnabled);
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(settings.SshGateways[0].SshPasswordEncrypted));
        Assert.Equal(ServerSecret, CredentialProtector.Unprotect(await ServerSecretAsync()));
        Assert.Equal("preset-secret", CredentialProtector.Unprotect(presets.Value));
        Assert.Equal("history-secret", CredentialProtector.Unprotect(history.Value));
        Assert.Equal("shared-secret", CredentialProtector.Unprotect(shared.Value));
    }

    [Fact]
    public async Task DisableAsync_StoreStillSealedAfterReverse_RefusesKeepsKeyAndHelloAndReseals()
    {
        var hello = new RecordingHelloService();
        var stuck = new FakeStore("StoreKeyStuck") { StuckSealed = true };
        var other = new FakeStore("StoreKeyOther");
        VaultLifecycleService service = await EnabledServiceAsync(hello, stuck, other);
        await service.EnrollHelloAsync();
        await SaveServerSecretAsync();
        stuck.Value = CredentialProtector.Protect("stuck");
        other.Value = CredentialProtector.Protect("other");
        AppSettings before = await _configManager.LoadSettingsAsync();

        VaultResidueException ex = await Assert.ThrowsAsync<VaultResidueException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Equal(new[] { "StoreKeyStuck" }, ex.StoreNameKeys);
        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.True(after.VaultEnabled);
        Assert.Equal(VaultMigrationState.Complete, after.VaultMigrationState);
        Assert.Equal(before.VaultWrappedDek, after.VaultWrappedDek);
        Assert.True(after.VaultHelloEnrolled);
        Assert.Equal(before.VaultHelloWrappedDek, after.VaultHelloWrappedDek);
        Assert.Empty(hello.RemovedCredentialNames);
        Assert.True(CredentialProtector.IsVaultEnabled);
        Assert.True(CredentialProtector.IsVaultUnlocked);

        // Everything that was converted has been sealed again.
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
        Assert.True(VaultSecretBlob.IsSecretBlob(other.Value));
        Assert.Equal(ServerSecret, CredentialProtector.Unprotect(await ServerSecretAsync()));
    }

    [Fact]
    public async Task DisableAsync_ResidueInConfiguration_RefusesAndReseals()
    {
        VaultLifecycleService service = await EnabledServiceAsync();
        await SaveServerSecretAsync();

        // A field this build does not know, sealed with the vault key: no reverse pass reaches it.
        string stray = CredentialProtector.Protect("stray-secret");
        Assert.True(VaultSecretBlob.IsSecretBlob(stray));
        await _configManager.MergeSettingAsync(s =>
            s.ExtensionData["FutureSecret"] = JsonSerializer.SerializeToElement(stray));

        VaultResidueException ex = await Assert.ThrowsAsync<VaultResidueException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Contains(ConfigurationStoreKey, ex.StoreNameKeys);
        Assert.True((await _configManager.LoadSettingsAsync()).VaultEnabled);
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
    }

    [Fact]
    public async Task DisableAsync_StoreThrowsInReverse_RestoresAndRethrows()
    {
        var failing = new FakeStore("StoreKeyFailing") { ThrowOnReverse = new IOException("locked") };
        VaultLifecycleService service = await EnabledServiceAsync(null, failing);
        await SaveServerSecretAsync();

        VaultStoreException ex = await Assert.ThrowsAsync<VaultStoreException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Equal("StoreKeyFailing", ex.StoreNameKey);
        Assert.IsType<IOException>(ex.InnerException);
        await AssertVaultStillOnAndSealedAsync();
    }

    [Fact]
    public async Task DisableAsync_StoreThrowsInCheck_RestoresAndRethrows()
    {
        var failing = new FakeStore("StoreKeyFailing") { ThrowOnHolds = new IOException("locked") };
        VaultLifecycleService service = await EnabledServiceAsync(null, failing);
        await SaveServerSecretAsync();

        VaultStoreException ex = await Assert.ThrowsAsync<VaultStoreException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Equal("StoreKeyFailing", ex.StoreNameKey);

        await AssertVaultStillOnAndSealedAsync();
    }

    [Fact]
    public async Task DisableAsync_HelloCredentialRemovalFails_StillDisables()
    {
        var hello = new RecordingHelloService { ThrowOnRemove = new IOException("credential store unavailable") };
        VaultLifecycleService service = await EnabledServiceAsync(hello);
        await service.EnrollHelloAsync();
        await SaveServerSecretAsync();

        await service.DisableAsync(Pw(StrongPassword));

        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.False(after.VaultEnabled);
        Assert.False(after.VaultHelloEnrolled);
        Assert.Equal(ServerSecret, CredentialProtector.Unprotect(await ServerSecretAsync()));
    }

    [Fact]
    public async Task DisableAsync_BackupRefreshFails_LeavesHelloAndTheVaultIntact()
    {
        var hello = new RecordingHelloService();
        var failing = new DecoratedConfigManager(_configManager);
        var service = new VaultLifecycleService(failing, hello);
        _services.Add(service);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await service.EnrollHelloAsync();
        await SaveServerSecretAsync();
        failing.FailRefresh = true;

        await Assert.ThrowsAsync<IOException>(() => service.DisableAsync(Pw(StrongPassword)));

        Assert.Empty(hello.RemovedCredentialNames);
        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.True(after.VaultEnabled);
        Assert.True(after.VaultHelloEnrolled);
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
        Assert.True(CredentialProtector.IsVaultUnlocked);
    }

    [Fact]
    public async Task DisableAsync_ResidueAppearsOnlyInTheSecondCheck_RestoresTheWrappedKeyAndHello()
    {
        var hello = new RecordingHelloService();
        var late = new FakeStore("StoreKeyLate") { ResidueOnHoldsCall = 2 };
        VaultLifecycleService service = await EnabledServiceAsync(hello, late);
        await service.EnrollHelloAsync();
        await SaveServerSecretAsync();
        AppSettings before = await _configManager.LoadSettingsAsync();

        VaultResidueException ex = await Assert.ThrowsAsync<VaultResidueException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Equal(new[] { "StoreKeyLate" }, ex.StoreNameKeys);
        Assert.True(ex.ProtectionRestored);
        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.True(after.VaultEnabled);
        Assert.Equal(before.VaultWrappedDek, after.VaultWrappedDek);
        Assert.Equal(before.VaultId, after.VaultId);
        Assert.Equal(VaultMigrationState.Complete, after.VaultMigrationState);
        Assert.True(after.VaultHelloEnrolled);
        Assert.Equal(before.VaultHelloWrappedDek, after.VaultHelloWrappedDek);
        Assert.Empty(hello.RemovedCredentialNames);
        Assert.True(CredentialProtector.IsVaultUnlocked);
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
    }

    [Fact]
    public async Task DisableAsync_ExceptionRightAfterTheErase_WrappedKeyComesBack_AndSecretsAreSealedAgain()
    {
        var decorated = new DecoratedConfigManager(_configManager);
        var late = new FakeStore("StoreKeyLate");
        var service = new VaultLifecycleService(decorated, null, [late]);
        _services.Add(service);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await SaveServerSecretAsync();
        late.ThrowOnHoldsCall = 2;

        VaultStoreException ex = await Assert.ThrowsAsync<VaultStoreException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Equal("StoreKeyLate", ex.StoreNameKey);
        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.True(after.VaultEnabled);
        Assert.False(string.IsNullOrEmpty(after.VaultWrappedDek));
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
        Assert.Equal(ServerSecret, CredentialProtector.Unprotect(await ServerSecretAsync()));
    }

    [Fact]
    public async Task DisableAsync_ExceptionAfterTheErase_WhenTheWrappedKeyCannotBeWrittenBack_SealsNothing()
    {
        var decorated = new DecoratedConfigManager(_configManager);
        var late = new FakeStore("StoreKeyLate") { Value = null };
        var service = new VaultLifecycleService(decorated, null, [late]);
        _services.Add(service);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await SaveServerSecretAsync();
        late.Value = CredentialProtector.Protect("late-secret");

        // The second look fails, and from then on no settings write succeeds: the wrapped key
        // cannot come back, so sealing anything would lose it.
        late.OnHolds = call =>
        {
            if (call == 2)
            {
                decorated.FailMerges = true;
                throw new IOException("locked");
            }
        };

        await Assert.ThrowsAsync<VaultStoreException>(() => service.DisableAsync(Pw(StrongPassword)));

        decorated.FailMerges = false;
        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.False(after.VaultEnabled);
        Assert.True(string.IsNullOrEmpty(after.VaultWrappedDek));
        Assert.False(CredentialProtector.IsVaultEnabled);

        // Every secret is readable with no key anywhere.
        Assert.False(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
        Assert.Equal(ServerSecret, CredentialProtector.Unprotect(await ServerSecretAsync()));
        Assert.Equal("late-secret", CredentialProtector.Unprotect(late.Value));
    }

    [Fact]
    public async Task DisableAsync_RestoreItselfFails_SaysProtectionIsNotFullyRestored()
    {
        var stuck = new FakeStore("StoreKeyStuck") { StuckSealed = true };
        var failing = new FakeStore("StoreKeyFailing");
        VaultLifecycleService service = await EnabledServiceAsync(null, stuck, failing);
        stuck.Value = CredentialProtector.Protect("stuck");
        failing.ThrowOnForward = new IOException("locked");

        VaultResidueException ex = await Assert.ThrowsAsync<VaultResidueException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Equal(new[] { "StoreKeyStuck" }, ex.StoreNameKeys);
        Assert.False(ex.ProtectionRestored);
        Assert.Equal(VaultMigrationState.InProgress, (await _configManager.LoadSettingsAsync()).VaultMigrationState);
    }

    [Fact]
    public async Task Lock_RequestedDuringADisable_IsDeferred_AndDoesNotBreakTheRestore()
    {
        VaultLifecycleService? service = null;
        var stuck = new FakeStore("StoreKeyStuck")
        {
            StuckSealed = true,
            OnHolds = call =>
            {
                if (call == 1)
                {
                    // An idle auto-lock firing in the middle of the operation.
                    service!.Lock();
                }
            },
        };
        service = await EnabledServiceAsync(null, stuck);
        await SaveServerSecretAsync();
        stuck.Value = CredentialProtector.Protect("stuck");

        VaultResidueException ex = await Assert.ThrowsAsync<VaultResidueException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        // The restore ran with the key still held: the secrets are sealed again.
        Assert.True(ex.ProtectionRestored);
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
        Assert.True(VaultSecretBlob.IsSecretBlob(stuck.Value));

        // And the lock that was asked for happened once the operation ended.
        Assert.False(service.IsUnlocked);
        Assert.False(CredentialProtector.IsVaultUnlocked);
        Assert.True(CredentialProtector.IsVaultEnabled);
    }

    [Fact]
    public async Task Lock_WhenIdle_LocksImmediately()
    {
        VaultLifecycleService service = await EnabledServiceAsync();

        service.Lock();

        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public async Task DisableAsync_AStringThatMerelyStartsLikeABlob_IsNotResidue()
    {
        VaultLifecycleService service = await EnabledServiceAsync();
        string lookAlike = Convert.ToBase64String([.. VaultSecretBlob.Magic, .. "host"u8.ToArray()]);
        Assert.True(VaultSecretBlob.IsSecretBlob(lookAlike), "the premise: the magic alone matches");
        await _configManager.MergeSettingAsync(s => s.SshGateways.Add(new SshGatewayDto
        {
            Id = "gw1",
            Name = lookAlike,
            Host = "bastion.example.test",
            User = "ops",
        }));

        await service.DisableAsync(Pw(StrongPassword));

        Assert.False((await _configManager.LoadSettingsAsync()).VaultEnabled);
    }

    [Fact]
    public void IsVaultSecret_NeedsAWellFormedBlob()
    {
        string real = RandomVaultBlob();
        string prefixOnly = Convert.ToBase64String([.. VaultSecretBlob.Magic, VaultSecretBlob.CurrentFormatVersion]);

        Assert.True(VaultConfigurationScan.IsVaultSecret(real));
        Assert.False(VaultConfigurationScan.IsVaultSecret("SE1TMAIN"));
        Assert.False(VaultConfigurationScan.IsVaultSecret(prefixOnly));
        Assert.False(VaultConfigurationScan.IsVaultSecret(null));
        Assert.False(VaultConfigurationScan.IsVaultSecret("bastion.example.test"));
    }

    [Fact]
    public void EncodedPrefix_IsWhatEverySealedBlobStartsWith()
    {
        Assert.Equal(8, VaultSecretBlob.EncodedPrefix.Length);
        for (var round = 0; round < 20; round++)
        {
            Assert.StartsWith(VaultSecretBlob.EncodedPrefix, RandomVaultBlob(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnlockAsync_StoreLeftUnsealed_IsReportedOncePerSession()
    {
        var stubborn = new FakeStore("StoreKeyStubborn") { HasUnsealed = true };
        VaultLifecycleService service = await EnabledServiceAsync(null, stubborn);
        var reports = new List<IReadOnlyList<string>>();
        service.UnsealedStoresDetected += reports.Add;
        service.Lock();

        await service.UnlockAsync(Pw(StrongPassword));
        await service.PostUnlockSealingTask;
        service.Lock();
        await service.UnlockAsync(Pw(StrongPassword));
        await service.PostUnlockSealingTask;

        Assert.Single(reports);
        Assert.Equal(new[] { "StoreKeyStubborn" }, reports[0]);
        Assert.Equal(new[] { "StoreKeyStubborn" }, service.UnsealedStoreNameKeys);
    }

    [Fact]
    public async Task UnlockAsync_ReturnsWithoutWaitingForTheStores()
    {
        var slow = new FakeStore("StoreKeySlow");
        VaultLifecycleService service = await EnabledServiceAsync(null, slow);
        service.Lock();
        var release = new TaskCompletionSource();
        slow.ForwardGate = release.Task;
        slow.ForwardCalls = 0;

        await service.UnlockAsync(Pw(StrongPassword));

        Assert.True(service.IsUnlocked);
        Assert.False(service.PostUnlockSealingTask.IsCompleted);
        release.SetResult();
        await service.PostUnlockSealingTask;
        Assert.Equal(1, slow.ForwardCalls);
    }

    [Fact]
    public async Task DisableAsync_FinalMergeAborts_WhenAVaultBlobAppearsAfterTheCheck()
    {
        var decorated = new DecoratedConfigManager(_configManager);
        var service = new VaultLifecycleService(decorated);
        _services.Add(service);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await SaveServerSecretAsync();

        // A write already in flight lands between the check and the erase.
        string late = RandomVaultBlob();
        decorated.OnRefresh = () => _configManager.MergeSettingAsync(s =>
            s.ExtensionData["LateSecret"] = JsonSerializer.SerializeToElement(late));

        VaultResidueException ex = await Assert.ThrowsAsync<VaultResidueException>(
            () => service.DisableAsync(Pw(StrongPassword)));

        Assert.Contains(ConfigurationStoreKey, ex.StoreNameKeys);
        AppSettings after = await _configManager.LoadSettingsAsync();
        Assert.True(after.VaultEnabled);
        Assert.False(string.IsNullOrEmpty(after.VaultWrappedDek));
        Assert.True(CredentialProtector.IsVaultUnlocked);
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
    }

    [Fact]
    public async Task DisableAsync_WrappedKeysAreNotMistakenForSecrets()
    {
        var hello = new RecordingHelloService();
        VaultLifecycleService service = await EnabledServiceAsync(hello);
        await service.EnrollHelloAsync();
        AppSettings enrolled = await _configManager.LoadSettingsAsync();
        Assert.False(string.IsNullOrEmpty(enrolled.VaultWrappedDek));
        Assert.False(string.IsNullOrEmpty(enrolled.VaultHelloWrappedDek));
        Assert.False(VaultSecretBlob.IsSecretBlob(enrolled.VaultWrappedDek));
        Assert.False(VaultSecretBlob.IsSecretBlob(enrolled.VaultHelloWrappedDek));

        await service.DisableAsync(Pw(StrongPassword));

        Assert.False((await _configManager.LoadSettingsAsync()).VaultEnabled);
    }

    [Fact]
    public async Task DisableAsync_RefreshesServersBackupToLegacy()
    {
        VaultLifecycleService service = await EnabledServiceAsync();
        await SaveServerSecretAsync();
        string backupPath = ConfigManager.BackupPathFor(_configManager.ServersPath);

        await service.DisableAsync(Pw(StrongPassword));

        string backup = File.ReadAllText(backupPath);
        Assert.Contains("RdpPasswordEncrypted", backup, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(backup);
        Assert.False(VaultConfigurationScan.ContainsSecretBlob(document.RootElement));
        Assert.Equal(File.ReadAllText(_configManager.ServersPath), backup);
    }

    [Fact]
    public async Task DisableAsync_ProtectDuringCheck_FailsClosed()
    {
        Exception? seen = null;
        string? written = null;
        var store = new FakeStore("StoreKeyClean")
        {
            OnHolds = _ =>
            {
                try
                {
                    written = CredentialProtector.Protect("late-write");
                }
                catch (Exception ex)
                {
                    seen = ex;
                }
            },
        };
        VaultLifecycleService service = await EnabledServiceAsync(null, store);

        await service.DisableAsync(Pw(StrongPassword));

        Assert.IsType<VaultLockedException>(seen);
        Assert.Null(written);
    }

    [Fact]
    public async Task UnlockAsync_ForwardsStores_AndSurvivesStoreFailure()
    {
        var sealedLater = new FakeStore("StoreKeyOne");
        var failing = new FakeStore("StoreKeyFailing");
        VaultLifecycleService service = await EnabledServiceAsync(null, sealedLater, failing);
        failing.ThrowOnForward = new IOException("locked");
        service.Lock();

        // Left in the legacy form by an older version, while the vault was locked or absent.
        sealedLater.Value = CredentialProtector.ProtectLegacy("legacy-one");
        sealedLater.ForwardCalls = 0;

        await service.UnlockAsync(Pw(StrongPassword));
        await service.PostUnlockSealingTask;

        Assert.True(service.IsUnlocked);
        Assert.Equal(1, sealedLater.ForwardCalls);
        Assert.True(VaultSecretBlob.IsSecretBlob(sealedLater.Value));
        Assert.Equal("legacy-one", CredentialProtector.Unprotect(sealedLater.Value));
    }

    [Fact]
    public async Task UnlockAsync_MarkerInProgressAfterACrashedDisable_IsClearedAndStoresForwarded()
    {
        var store = new FakeStore("StoreKeyOne");
        VaultLifecycleService service = await EnabledServiceAsync(null, store);
        await SaveServerSecretAsync();
        service.Lock();

        // What a crash in the middle of a disable leaves: the marker, the key, a half-reversed store.
        await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.InProgress);
        store.Value = null;
        store.ForwardCalls = 0;

        await service.UnlockAsync(Pw(StrongPassword));
        await service.PostUnlockSealingTask;

        Assert.Equal(1, store.ForwardCalls);
        Assert.Equal(VaultMigrationState.Complete, (await _configManager.LoadSettingsAsync()).VaultMigrationState);
    }

    [Fact]
    public async Task UnlockAsync_StoreFailureWhileResuming_KeepsTheMarker()
    {
        var failing = new FakeStore("StoreKeyFailing") { ThrowOnForward = new IOException("locked") };
        VaultLifecycleService service = NewService(null, failing);
        failing.ThrowOnForward = null;
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        service.Lock();
        await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.InProgress);
        failing.ThrowOnForward = new IOException("locked");

        await service.UnlockAsync(Pw(StrongPassword));
        await service.PostUnlockSealingTask;

        Assert.True(service.IsUnlocked);
        Assert.Equal(VaultMigrationState.InProgress, (await _configManager.LoadSettingsAsync()).VaultMigrationState);
    }

    [Fact]
    public async Task RefreshLastGoodCopiesAsync_DoesNotReplaceAGoodCopyWithAnUnreadableDocument()
    {
        await SaveServerSecretAsync();
        await SaveServerSecretAsync();
        string backupPath = ConfigManager.BackupPathFor(_configManager.ServersPath);
        string good = File.ReadAllText(backupPath);
        File.WriteAllText(_configManager.ServersPath, "\0\0\0 not json");

        await _configManager.RefreshLastGoodCopiesAsync();

        Assert.Equal(good, File.ReadAllText(backupPath));
    }

    private async Task AssertVaultStillOnAndSealedAsync()
    {
        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.True(settings.VaultEnabled);
        Assert.False(string.IsNullOrEmpty(settings.VaultWrappedDek));
        Assert.Equal(VaultMigrationState.Complete, settings.VaultMigrationState);
        Assert.True(CredentialProtector.IsVaultEnabled);
        Assert.True(CredentialProtector.IsVaultUnlocked);
        Assert.True(VaultSecretBlob.IsSecretBlob(await ServerSecretAsync()));
        Assert.True(VaultSecretBlob.IsSecretBlob(CredentialProtector.Protect("still-sealed")));
    }

    /// <summary>A configuration manager that can be told to fail, delegating everything else.</summary>
    private sealed class DecoratedConfigManager : IConfigManager
    {
        private readonly ConfigManager _inner;

        public DecoratedConfigManager(ConfigManager inner) => _inner = inner;

        public bool FailMerges { get; set; }

        public bool FailRefresh { get; set; }

        public Func<Task>? OnRefresh { get; set; }

        public string ConfigPath => _inner.ConfigPath;

        public string SettingsPath => _inner.SettingsPath;

        public string ServersPath => _inner.ServersPath;

        public event Action<AppSettings>? SettingsChanged
        {
            add => _inner.SettingsChanged += value;
            remove => _inner.SettingsChanged -= value;
        }

        public Task InitializeAsync() => _inner.InitializeAsync();

        public Task<AppSettings> LoadSettingsAsync() => _inner.LoadSettingsAsync();

        public Task SaveSettingsAsync(AppSettings settings) => _inner.SaveSettingsAsync(settings);

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            _inner.MergeHostKeyAsync(hostPortKey, fingerprint);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) =>
            _inner.MergeTrustedHostKeysAsync(entries);

        public Task MergeSettingAsync(Action<AppSettings> mutate) =>
            FailMerges ? Task.FromException(new IOException("settings are locked")) : _inner.MergeSettingAsync(mutate);

        public Task<List<ServerProfileDto>> LoadServersAsync() => _inner.LoadServersAsync();

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            _inner.MutateServersAsync(mutate);

        public Task SaveServersAsync(List<ServerProfileDto> servers) => _inner.SaveServersAsync(servers);

        public async Task RefreshLastGoodCopiesAsync()
        {
            if (OnRefresh is not null)
            {
                await OnRefresh().ConfigureAwait(false);
            }

            if (FailRefresh)
            {
                throw new IOException("copy is locked");
            }

            await _inner.RefreshLastGoodCopiesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A store holding one protected value, converted with the transforms it is given.</summary>
    private sealed class FakeStore : IVaultSecretStore
    {
        public FakeStore(string storeNameKey) => StoreNameKey = storeNameKey;

        public string StoreNameKey { get; }

        public string? Value { get; set; }

        public int ForwardCalls { get; set; }

        public int ReverseCalls { get; set; }

        public bool StuckSealed { get; init; }

        public Exception? ThrowOnForward { get; set; }

        public Exception? ThrowOnReverse { get; init; }

        public Exception? ThrowOnHolds { get; init; }

        public Action<int>? OnHolds { get; set; }

        public int HoldsCalls { get; private set; }

        public int? ResidueOnHoldsCall { get; init; }

        public int? ThrowOnHoldsCall { get; set; }

        public bool HasUnsealed { get; init; }

        public Task? ForwardGate { get; set; }

        public async Task ForwardAsync(Func<string?, string?> toVault, CancellationToken cancellationToken)
        {
            ForwardCalls++;
            if (ThrowOnForward is not null)
            {
                throw ThrowOnForward;
            }

            if (ForwardGate is not null)
            {
                await ForwardGate.ConfigureAwait(false);
            }

            Value = toVault(Value);
        }

        public Task<bool> HasUnsealedSecretsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(HasUnsealed);

        public Task ReverseAsync(Func<string?, string?> toLegacy, CancellationToken cancellationToken)
        {
            ReverseCalls++;
            if (ThrowOnReverse is not null)
            {
                throw ThrowOnReverse;
            }

            if (!StuckSealed)
            {
                Value = toLegacy(Value);
            }

            return Task.CompletedTask;
        }

        public Task<bool> HoldsVaultSecretsAsync(CancellationToken cancellationToken)
        {
            HoldsCalls++;
            OnHolds?.Invoke(HoldsCalls);
            if (ThrowOnHolds is not null || ThrowOnHoldsCall == HoldsCalls)
            {
                throw ThrowOnHolds ?? new IOException("locked");
            }

            if (ResidueOnHoldsCall is { } call)
            {
                return Task.FromResult(HoldsCalls == call);
            }

            return Task.FromResult(VaultSecretBlob.IsSecretBlob(Value));
        }
    }

    private sealed class RecordingHelloService : IVaultHelloService
    {
        private static readonly byte[] Signature = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        private static readonly byte[] Salt = Enumerable.Range(0, VaultHelloProtector.SaltSizeBytes)
            .Select(i => (byte)(0xA0 + i)).ToArray();
        private static readonly byte[] Challenge = Enumerable.Range(0, VaultHelloProtector.ChallengeSizeBytes)
            .Select(i => (byte)(0x30 + i)).ToArray();

        public List<string> RemovedCredentialNames { get; } = new();

        public Exception? ThrowOnRemove { get; init; }

        public Func<Task>? OnRemove { get; set; }

        public Task<bool> IsEnrollmentAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<VaultHelloEnrollment> EnrollAsync(ReadOnlyMemory<byte> dek, string vaultId, CancellationToken ct)
        {
            const string publicKeyHash = "FAKEPUBLICKEYHASH";
            string credentialName = VaultHelloProtector.CreateCredentialName(vaultId);
            var binding = new VaultHelloBinding(vaultId, publicKeyHash, Challenge.ToArray(), Salt.ToArray());
            byte[] helloKek = VaultHelloProtector.DeriveHelloKek(Signature, Salt);
            try
            {
                string wrapped = VaultHelloProtector.WrapDek(dek.Span, helloKek, binding);
                return Task.FromResult(new VaultHelloEnrollment(
                    vaultId,
                    wrapped,
                    Convert.ToBase64String(Challenge),
                    Convert.ToBase64String(Salt),
                    credentialName,
                    publicKeyHash));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(helloKek);
            }
        }

        public Task<VaultDekHolder> UnlockAsync(VaultHelloEnrollment stored, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task RemoveAsync(string credentialName, CancellationToken ct = default)
        {
            if (ThrowOnRemove is not null)
            {
                throw ThrowOnRemove;
            }

            if (OnRemove is not null)
            {
                await OnRemove().ConfigureAwait(false);
            }

            RemovedCredentialNames.Add(credentialName);
        }
    }
}
