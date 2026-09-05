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
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class VaultLifecycleServiceTests : IAsyncLifetime
{
    private static readonly Argon2idParameters FastParams = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);

    private const string StrongPassword = "StrongMaster1!Pass";
    private const string CitrixPlaintext = "C:\\SelfService.exe /launch ticket=xyz";

    private readonly List<VaultLifecycleService> _services = new();
    private string _tempDir = string.Empty;
    private ConfigManager _configManager = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Vault." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configManager = new ConfigManager(_tempDir);
        await _configManager.InitializeAsync();

        // xUnit drives this lifetime without a constructor, so the baseline is established
        // here rather than in a field initializer: no vault, no DEK, a fresh HMAC key.
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());
    }

    public Task DisposeAsync()
    {
        foreach (var service in _services)
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

    private VaultLifecycleService NewService(IVaultHelloService? vaultHelloService = null)
    {
        var service = new VaultLifecycleService(_configManager, vaultHelloService);
        _services.Add(service);
        return service;
    }

    private static char[] Pw(string s) => s.ToCharArray();

    private async Task SeedLegacyVaultAsync()
    {
        // No DEK set yet -> Protect produces legacy blobs.
        var profile = new ServerProfileDto
        {
            Id = "srv1",
            DisplayName = "Server 1",
            ConnectionType = "RDP",
            RdpPasswordEncrypted = CredentialProtector.Protect("rdp-pw"),
            SshPasswordEncrypted = CredentialProtector.Protect("ssh-pw"),
            SshKeyPassphraseEncrypted = CredentialProtector.Protect("ssh-pass"),
            WinRmPasswordEncrypted = CredentialProtector.Protect("winrm-pw"),
            FtpPasswordEncrypted = CredentialProtector.Protect("ftp-pw"),
            TelnetPasswordEncrypted = CredentialProtector.Protect("telnet-pw"),
            VncPassword = CredentialProtector.Protect("vnc-pw"),
            CitrixLaunchCommandLine = CitrixPlaintext,
        };

        await _configManager.SaveServersAsync(new List<ServerProfileDto> { profile });
        await _configManager.MergeSettingAsync(s =>
        {
            s.CmdLibGitSyncToken = DpapiProvider.Protect("git-token");
            s.CredentialProviderUnlockSecretEncrypted = CredentialProtector.Protect("unlock-secret");
        });
    }

    private async Task<VaultLifecycleService> SeedCompletedVaultAsync(
        IVaultHelloService? vaultHelloService = null)
    {
        await SeedLegacyVaultAsync();
        VaultLifecycleService service = NewService(vaultHelloService);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        return service;
    }

    private async Task PersistPlaintextCitrixTokenAsync()
    {
        List<ServerProfileDto> servers = await _configManager.LoadServersAsync();
        ServerProfileDto profile = Assert.Single(servers);
        profile.CitrixLaunchCommandLine = CitrixPlaintext;
        await _configManager.SaveServersAsync(servers);
    }

    [Fact]
    public async Task EnableAsync_MigratesWholeConfidentialSetToV2()
    {
        await SeedLegacyVaultAsync();

        await NewService().EnableAsync(Pw(StrongPassword), FastParams);

        var settings = await _configManager.LoadSettingsAsync();
        Assert.True(settings.VaultEnabled);
        Assert.Equal(VaultMigrationState.Complete, settings.VaultMigrationState);
        Assert.False(string.IsNullOrEmpty(settings.VaultWrappedDek));
        Assert.True(VaultSecretBlob.IsSecretBlob(settings.CmdLibGitSyncToken));
        Assert.True(VaultSecretBlob.IsSecretBlob(settings.CredentialProviderUnlockSecretEncrypted));

        var profile = (await _configManager.LoadServersAsync())[0];
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.RdpPasswordEncrypted));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.SshPasswordEncrypted));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.SshKeyPassphraseEncrypted));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.WinRmPasswordEncrypted));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.FtpPasswordEncrypted));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.TelnetPasswordEncrypted));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.VncPassword));
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.CitrixLaunchCommandLine));

        // The migrated values still decrypt to the original plaintext (DEK set).
        Assert.Equal("rdp-pw", CredentialProtector.Unprotect(profile.RdpPasswordEncrypted));
        Assert.Equal(CitrixPlaintext, CredentialProtector.Unprotect(profile.CitrixLaunchCommandLine));
    }

    [Fact]
    public async Task EnableAsync_WeakPassword_Throws_AndLeavesVaultDisabled()
    {
        await SeedLegacyVaultAsync();

        await Assert.ThrowsAsync<MasterPasswordPolicyException>(
            () => NewService().EnableAsync(Pw("weak"), FastParams));

        var settings = await _configManager.LoadSettingsAsync();
        Assert.False(settings.VaultEnabled);
    }

    [Fact]
    public async Task EnableAsync_AlreadyEnabled_Throws()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnableAsync(Pw(StrongPassword), FastParams));
    }

    [Fact]
    public async Task UnlockAsync_ResumesInterruptedForwardMigration()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        // Simulate a crash mid-migration: one field reverted to legacy + InProgress.
        service.Lock();
        var servers = await _configManager.LoadServersAsync();
        servers[0].RdpPasswordEncrypted = CredentialProtector.ProtectLegacy("rdp-pw");
        await _configManager.SaveServersAsync(servers);
        await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.InProgress);

        await NewService().UnlockAsync(Pw(StrongPassword));

        var settings = await _configManager.LoadSettingsAsync();
        Assert.Equal(VaultMigrationState.Complete, settings.VaultMigrationState);

        var profile = (await _configManager.LoadServersAsync())[0];
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.RdpPasswordEncrypted));
        Assert.Equal("rdp-pw", CredentialProtector.Unprotect(profile.RdpPasswordEncrypted));
    }

    [Fact]
    public async Task UnlockAsync_CompleteState_ReconcilesPersistedPlaintextCitrixToken()
    {
        VaultLifecycleService service = await SeedCompletedVaultAsync();
        service.Lock();
        await PersistPlaintextCitrixTokenAsync();

        await NewService().UnlockAsync(Pw(StrongPassword));

        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.Equal(VaultMigrationState.Complete, settings.VaultMigrationState);
        ServerProfileDto profile = Assert.Single(await _configManager.LoadServersAsync());
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.CitrixLaunchCommandLine));
        Assert.Equal(CitrixPlaintext, CredentialProtector.Unprotect(profile.CitrixLaunchCommandLine));
    }

    [Fact]
    public async Task UnlockWithHelloAsync_CompleteState_ReconcilesPersistedPlaintextCitrixToken()
    {
        FakeVaultHelloService hello = new();
        VaultLifecycleService service = await SeedCompletedVaultAsync(hello);
        await service.EnrollHelloAsync();
        service.Lock();
        await PersistPlaintextCitrixTokenAsync();

        VaultHelloUnlockResult result = await NewService(hello).UnlockWithHelloDetailedAsync();

        Assert.True(result.Succeeded);
        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.Equal(VaultMigrationState.Complete, settings.VaultMigrationState);
        ServerProfileDto profile = Assert.Single(await _configManager.LoadServersAsync());
        Assert.True(VaultSecretBlob.IsSecretBlob(profile.CitrixLaunchCommandLine));
        Assert.Equal(CitrixPlaintext, CredentialProtector.Unprotect(profile.CitrixLaunchCommandLine));
    }

    [Fact]
    public async Task UnlockAsync_CompleteStateWithProtectedInventory_DoesNotRewriteServerFile()
    {
        VaultLifecycleService service = await SeedCompletedVaultAsync();
        service.Lock();
        string serversPath = _configManager.ServersPath;
        DateTime sentinelWriteTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(serversPath, sentinelWriteTime);
        DateTime writeTimeBefore = File.GetLastWriteTimeUtc(serversPath);
        byte[] bytesBefore = await File.ReadAllBytesAsync(serversPath);

        await NewService().UnlockAsync(Pw(StrongPassword));

        byte[] bytesAfter = await File.ReadAllBytesAsync(serversPath);
        Assert.Equal(bytesBefore, bytesAfter);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(serversPath));
    }

    [Fact]
    public async Task UnlockAsync_WrongPassword_DoesNotReconcilePersistedPlaintextCitrixToken()
    {
        VaultLifecycleService service = await SeedCompletedVaultAsync();
        service.Lock();
        await PersistPlaintextCitrixTokenAsync();

        await Assert.ThrowsAsync<VaultUnlockException>(
            () => NewService().UnlockAsync(Pw("WrongMaster2!Pass")));

        Assert.False(CredentialProtector.IsVaultUnlocked);
        ServerProfileDto profile = Assert.Single(await _configManager.LoadServersAsync());
        Assert.Equal(CitrixPlaintext, profile.CitrixLaunchCommandLine);
    }

    [Fact]
    public async Task UnlockAsync_CompleteStateReconciliationFailure_RelocksVault()
    {
        VaultLifecycleService service = await SeedCompletedVaultAsync();
        service.Lock();
        await File.WriteAllTextAsync(_configManager.ServersPath, "{");

        await Assert.ThrowsAnyAsync<JsonException>(
            () => NewService().UnlockAsync(Pw(StrongPassword)));

        Assert.False(CredentialProtector.IsVaultUnlocked);
    }

    [Fact]
    public async Task DisableAsync_RoundTripsConfidentialSetBackToLegacy()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        await service.DisableAsync(Pw(StrongPassword));

        var settings = await _configManager.LoadSettingsAsync();
        Assert.False(settings.VaultEnabled);
        Assert.Null(settings.VaultWrappedDek);
        Assert.Equal(VaultMigrationState.None, settings.VaultMigrationState);
        // The token now reverses to the CredentialProtector legacy format (its
        // vault-aware reader/writer), not plain DPAPI; readable with no DEK.
        Assert.False(VaultSecretBlob.IsSecretBlob(settings.CmdLibGitSyncToken));
        Assert.Equal("git-token", CredentialProtector.Unprotect(settings.CmdLibGitSyncToken));
        Assert.Equal("unlock-secret", CredentialProtector.Unprotect(settings.CredentialProviderUnlockSecretEncrypted));

        Assert.False(CredentialProtector.IsVaultUnlocked);
        var profile = (await _configManager.LoadServersAsync())[0];
        Assert.False(VaultSecretBlob.IsSecretBlob(profile.RdpPasswordEncrypted));
        Assert.Equal("rdp-pw", CredentialProtector.Unprotect(profile.RdpPasswordEncrypted));
        Assert.Equal(CitrixPlaintext, profile.CitrixLaunchCommandLine);
        Assert.False(VaultSecretBlob.IsSecretBlob(profile.CitrixLaunchCommandLine));
    }

    [Fact]
    public async Task ChangeMasterPasswordAsync_NewUnlocksOldFails_SecretsUnchanged()
    {
        const string newPassword = "NewMaster2!Word";
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        await service.ChangeMasterPasswordAsync(Pw(StrongPassword), Pw(newPassword), FastParams);
        service.Lock();

        await NewService().UnlockAsync(Pw(newPassword));
        var profile = (await _configManager.LoadServersAsync())[0];
        Assert.Equal("rdp-pw", CredentialProtector.Unprotect(profile.RdpPasswordEncrypted));

        CredentialProtector.ClearVaultKey();
        await Assert.ThrowsAsync<VaultUnlockException>(
            () => NewService().UnlockAsync(Pw(StrongPassword)));
    }

    [Fact]
    public async Task EnrollHelloAsync_RequiresUnlockedVault()
    {
        await SeedLegacyVaultAsync();
        var service = NewService(new FakeVaultHelloService());
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        service.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnrollHelloAsync());
    }

    [Fact]
    public async Task EnrollHelloAsync_PersistsHelloMetadata()
    {
        await SeedLegacyVaultAsync();
        var hello = new FakeVaultHelloService();
        var service = NewService(hello);
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        await service.EnrollHelloAsync();

        var settings = await _configManager.LoadSettingsAsync();
        Assert.True(settings.VaultHelloEnrolled);
        Assert.False(string.IsNullOrWhiteSpace(settings.VaultId));
        Assert.False(string.IsNullOrWhiteSpace(settings.VaultHelloWrappedDek));
        Assert.False(string.IsNullOrWhiteSpace(settings.VaultHelloChallenge));
        Assert.False(string.IsNullOrWhiteSpace(settings.VaultHelloSalt));
        Assert.False(string.IsNullOrWhiteSpace(settings.VaultHelloCredentialName));
        Assert.False(string.IsNullOrWhiteSpace(settings.VaultHelloPublicKeyHash));
        Assert.Equal(1, hello.EnrollCalls);
    }

    [Fact]
    public async Task UnlockWithHelloAsync_Success_SetsVaultKey()
    {
        await SeedLegacyVaultAsync();
        var hello = new FakeVaultHelloService();
        var service = NewService(hello);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await service.EnrollHelloAsync();
        service.Lock();

        bool unlocked = await NewService(hello).UnlockWithHelloAsync();

        Assert.True(unlocked);
        Assert.True(CredentialProtector.IsVaultUnlocked);
        var profile = (await _configManager.LoadServersAsync())[0];
        Assert.Equal("rdp-pw", CredentialProtector.Unprotect(profile.RdpPasswordEncrypted));
    }

    [Fact]
    public async Task UnlockAsync_MasterPassword_StampsLastMasterUnlock()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        service.Lock();

        await NewService().UnlockAsync(Pw(StrongPassword));

        var settings = await _configManager.LoadSettingsAsync();
        Assert.NotNull(settings.VaultLastMasterUnlockUtc);
        Assert.True(settings.VaultLastMasterUnlockUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task UnlockWithHelloAsync_Success_DoesNotStampLastMasterUnlock()
    {
        await SeedLegacyVaultAsync();
        var hello = new FakeVaultHelloService();
        var service = NewService(hello);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await service.EnrollHelloAsync();
        service.Lock();

        var result = await NewService(hello).UnlockWithHelloDetailedAsync();

        Assert.True(result.Succeeded);
        var settings = await _configManager.LoadSettingsAsync();
        Assert.Null(settings.VaultLastMasterUnlockUtc);
    }

    [Fact]
    public async Task UnlockWithHelloAsync_Failure_StaysLockedAndReturnsFalse()
    {
        await SeedLegacyVaultAsync();
        var hello = new FakeVaultHelloService();
        var service = NewService(hello);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await service.EnrollHelloAsync();
        service.Lock();
        hello.UnlockFailure = VaultHelloFailureReason.UserCanceled;

        bool unlocked = await NewService(hello).UnlockWithHelloAsync();

        Assert.False(unlocked);
        Assert.False(CredentialProtector.IsVaultUnlocked);
    }

    [Fact]
    public async Task ChangeMasterPasswordAsync_LeavesHelloWrapperDecryptable()
    {
        const string newPassword = "NewMaster2!Word";
        await SeedLegacyVaultAsync();
        var hello = new FakeVaultHelloService();
        var service = NewService(hello);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await service.EnrollHelloAsync();
        var before = await _configManager.LoadSettingsAsync();
        var helloWrappedBefore = before.VaultHelloWrappedDek;

        await service.ChangeMasterPasswordAsync(Pw(StrongPassword), Pw(newPassword), FastParams);
        service.Lock();
        bool unlocked = await NewService(hello).UnlockWithHelloAsync();

        var after = await _configManager.LoadSettingsAsync();
        Assert.True(unlocked);
        Assert.Equal(helloWrappedBefore, after.VaultHelloWrappedDek);
        var profile = (await _configManager.LoadServersAsync())[0];
        Assert.Equal("rdp-pw", CredentialProtector.Unprotect(profile.RdpPasswordEncrypted));
    }

    [Fact]
    public async Task DisableAsync_RemovesHelloCredentialAndMetadata()
    {
        await SeedLegacyVaultAsync();
        var hello = new FakeVaultHelloService();
        var service = NewService(hello);
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        await service.EnrollHelloAsync();
        var enrolled = await _configManager.LoadSettingsAsync();

        await service.DisableAsync(Pw(StrongPassword));

        var settings = await _configManager.LoadSettingsAsync();
        Assert.Contains(enrolled.VaultHelloCredentialName!, hello.RemovedCredentialNames);
        Assert.False(settings.VaultHelloEnrolled);
        Assert.Null(settings.VaultHelloWrappedDek);
        Assert.Null(settings.VaultHelloChallenge);
        Assert.Null(settings.VaultHelloSalt);
        Assert.Null(settings.VaultHelloCredentialName);
        Assert.Null(settings.VaultHelloPublicKeyHash);
    }

    [Fact]
    public async Task ChangeMasterPasswordAsync_WeakNew_Throws()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        await Assert.ThrowsAsync<MasterPasswordPolicyException>(
            () => service.ChangeMasterPasswordAsync(Pw(StrongPassword), Pw("weak"), FastParams));
    }

    [Fact]
    public async Task ChangeMasterPasswordAsync_WrongOld_ThrowsVaultUnlock()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);

        await Assert.ThrowsAsync<VaultUnlockException>(
            () => service.ChangeMasterPasswordAsync(Pw("WrongMaster9!X"), Pw("NewMaster2!Word"), FastParams));
    }

    [Fact]
    public async Task UnlockAsync_WrongPassword_ThrowsAndStaysLocked()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        service.Lock();

        var unlocker = NewService();
        await Assert.ThrowsAsync<VaultUnlockException>(() => unlocker.UnlockAsync(Pw("WrongMaster9!X")));
        Assert.False(unlocker.IsUnlocked);
    }

    [Fact]
    public async Task UnlockAsync_CorruptedWrappedDek_ThrowsAndStaysLocked()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        service.Lock();

        var settings = await _configManager.LoadSettingsAsync();
        var raw = Convert.FromBase64String(settings.VaultWrappedDek!);
        raw[raw.Length / 2] ^= 0xFF;
        var corrupted = Convert.ToBase64String(raw);
        await _configManager.MergeSettingAsync(s => s.VaultWrappedDek = corrupted);

        var unlocker = NewService();
        await Assert.ThrowsAsync<VaultUnlockException>(() => unlocker.UnlockAsync(Pw(StrongPassword)));
        Assert.False(unlocker.IsUnlocked);
    }

    [Fact]
    public async Task DisableAsync_WrongPassword_ThrowsAndVaultStaysEnabled()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        service.Lock();

        await Assert.ThrowsAsync<VaultUnlockException>(
            () => NewService().DisableAsync(Pw("WrongMaster9!X")));

        var settings = await _configManager.LoadSettingsAsync();
        Assert.True(settings.VaultEnabled);
    }

    [Fact]
    public async Task Lock_WhileEnabled_MakesProtectFailClosed()
    {
        await SeedLegacyVaultAsync();
        var service = NewService();
        await service.EnableAsync(Pw(StrongPassword), FastParams);
        Assert.True(service.IsUnlocked);

        service.Lock();

        Assert.False(service.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => CredentialProtector.Protect("secret"));
    }

    private sealed class FakeVaultHelloService : IVaultHelloService
    {
        private static readonly byte[] Signature = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        private static readonly byte[] Salt = Enumerable.Range(0, VaultHelloProtector.SaltSizeBytes)
            .Select(i => (byte)(0xA0 + i)).ToArray();
        private static readonly byte[] Challenge = Enumerable.Range(0, VaultHelloProtector.ChallengeSizeBytes)
            .Select(i => (byte)(0x30 + i)).ToArray();

        public int EnrollCalls { get; private set; }

        public VaultHelloFailureReason? UnlockFailure { get; set; }

        public List<string> RemovedCredentialNames { get; } = new();

        public Task<bool> IsEnrollmentAvailableAsync(CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<VaultHelloEnrollment> EnrollAsync(ReadOnlyMemory<byte> dek, string vaultId, CancellationToken ct)
        {
            EnrollCalls++;
            var publicKeyHash = "FAKEPUBLICKEYHASH";
            var credentialName = VaultHelloProtector.CreateCredentialName(vaultId);
            var binding = new VaultHelloBinding(vaultId, publicKeyHash, Challenge.ToArray(), Salt.ToArray());
            var helloKek = VaultHelloProtector.DeriveHelloKek(Signature, Salt);
            try
            {
                var wrapped = VaultHelloProtector.WrapDek(dek.Span, helloKek, binding);
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
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(helloKek);
            }
        }

        public Task<VaultDekHolder> UnlockAsync(VaultHelloEnrollment stored, CancellationToken ct)
        {
            if (UnlockFailure is { } failure)
            {
                throw new VaultHelloException(failure);
            }

            var binding = stored.ToBinding();
            var helloKek = VaultHelloProtector.DeriveHelloKek(Signature, binding.Salt);
            try
            {
                return Task.FromResult(VaultHelloProtector.UnwrapDek(stored.WrappedDek, helloKek, binding));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(helloKek);
            }
        }

        public Task RemoveAsync(string credentialName, CancellationToken ct = default)
        {
            RemovedCredentialNames.Add(credentialName);
            return Task.CompletedTask;
        }
    }
}
