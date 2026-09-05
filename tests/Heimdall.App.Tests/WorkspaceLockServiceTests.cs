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
using System.Runtime.Versioning;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// Lock/unlock state transitions, the D3 disconnect policy, and the only-when-enabled
/// guard for <see cref="WorkspaceLockService"/>. Uses a real enabled vault on a temp
/// config; serialized via the CredentialProtector static collection.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WorkspaceLockServiceTests : IAsyncLifetime
{
    private static readonly Argon2idParameters FastParams = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);
    private const string MasterPassword = "LockTest2026!Pass";

    private string _tempDir = string.Empty;
    private ConfigManager _configManager = null!;
    private VaultLifecycleService _lifecycle = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Lock." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configManager = new ConfigManager(_tempDir);
        await _configManager.InitializeAsync();

        // xUnit drives this lifetime without a constructor, so the baseline is established
        // here rather than in a field initializer: no vault, no DEK, a fresh HMAC key.
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());

        _lifecycle = new VaultLifecycleService(_configManager);
        await _lifecycle.EnableAsync(MasterPassword.ToCharArray(), FastParams); // leaves the vault unlocked
    }

    public Task DisposeAsync()
    {
        _lifecycle.Dispose();
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

    private WorkspaceLockService NewService(bool vaultEnabled = true, int idleMinutes = 0, bool disconnectOnLock = false)
    {
        var service = new WorkspaceLockService(_lifecycle);
        service.Configure(vaultEnabled, idleMinutes, disconnectOnLock);
        return service;
    }

    // The enrolment moved from the main view model onto this service. Type-metadata guards prove the
    // dependency exists; they cannot tell a real delegation from a method that returns a completed task.
    // These exercise the actual lifecycle with a fake Hello platform, so a hollowed-out delegation fails.
    [Fact]
    public async Task EnrollHelloAsync_UnlockedVault_DelegatesAndPersistsTheEnrollment()
    {
        RecordingHelloService hello = new();
        using VaultLifecycleService lifecycle = new(_configManager, hello);
        await lifecycle.UnlockAsync(MasterPassword.ToCharArray());
        Assert.True(lifecycle.IsUnlocked);

        using WorkspaceLockService service = new(lifecycle);
        service.Configure(vaultEnabled: true, autoLockIdleMinutes: 0, disconnectOnLock: false);
        int lockStateEvents = 0;
        service.LockStateChanged += () => lockStateEvents++;

        await service.EnrollHelloAsync();

        // The platform was actually asked to enrol, exactly once.
        Assert.Equal(1, hello.EnrollCalls);

        // ...and its answer was persisted, checked on sentinel values only this fake produces.
        AppSettings persisted = await _configManager.LoadSettingsAsync();
        Assert.True(persisted.VaultHelloEnrolled);
        Assert.Equal(RecordingHelloService.SentinelWrappedDek, persisted.VaultHelloWrappedDek);
        Assert.Equal(RecordingHelloService.SentinelCredentialName, persisted.VaultHelloCredentialName);
        Assert.Equal(RecordingHelloService.SentinelPublicKeyHash, persisted.VaultHelloPublicKeyHash);

        // Enrolment is not an unlock: the locked state is untouched and nobody is told otherwise.
        Assert.False(service.IsWorkspaceLocked);
        Assert.Equal(0, lockStateEvents);
    }

    // A locked workspace must not enrol: the vault has no usable key, and the refusal has to come from
    // the real lifecycle rather than from a guard this service invented.
    [Fact]
    public async Task EnrollHelloAsync_LockedWorkspace_PropagatesTheRefusalAndStaysLocked()
    {
        RecordingHelloService hello = new();
        using VaultLifecycleService lifecycle = new(_configManager, hello);
        await lifecycle.UnlockAsync(MasterPassword.ToCharArray());

        using WorkspaceLockService service = new(lifecycle);
        service.Configure(vaultEnabled: true, autoLockIdleMinutes: 0, disconnectOnLock: false);
        service.Lock();
        Assert.True(service.IsWorkspaceLocked);

        // Subscribed after the initial lock, so only a spurious later transition would be counted.
        int lockStateEvents = 0;
        service.LockStateChanged += () => lockStateEvents++;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnrollHelloAsync());

        Assert.Equal(0, hello.EnrollCalls);
        Assert.True(service.IsWorkspaceLocked);
        Assert.Equal(0, lockStateEvents);
    }

    // The token must reach the platform, not be dropped on the way.
    [Fact]
    public async Task EnrollHelloAsync_CancelledToken_DoesNotReachThePlatform()
    {
        RecordingHelloService hello = new();
        using VaultLifecycleService lifecycle = new(_configManager, hello);
        await lifecycle.UnlockAsync(MasterPassword.ToCharArray());

        using WorkspaceLockService service = new(lifecycle);
        service.Configure(vaultEnabled: true, autoLockIdleMinutes: 0, disconnectOnLock: false);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EnrollHelloAsync(cts.Token));

        Assert.Equal(0, hello.EnrollCalls);
    }

    [Fact]
    public void Lock_WhenEnabledAndUnlocked_SetsLockedAndZeroesDek()
    {
        using var service = NewService();
        var events = 0;
        service.LockStateChanged += () => events++;

        service.Lock();

        Assert.True(service.IsWorkspaceLocked);
        Assert.False(CredentialProtector.IsVaultUnlocked); // DEK zeroed
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task UnlockAsync_CorrectPassword_ClearsLocked()
    {
        using var service = NewService();
        service.Lock();

        await service.UnlockAsync(MasterPassword.ToCharArray());

        Assert.False(service.IsWorkspaceLocked);
        Assert.True(CredentialProtector.IsVaultUnlocked);
    }

    [Fact]
    public async Task UnlockAsync_WrongPassword_StaysLocked()
    {
        using var service = NewService();
        service.Lock();

        await Assert.ThrowsAsync<VaultUnlockException>(
            () => service.UnlockAsync("wrong-password".ToCharArray()));

        Assert.True(service.IsWorkspaceLocked);
    }

    [Fact]
    public void Lock_DisconnectOnLock_InvokesDisconnector()
    {
        using var service = NewService(disconnectOnLock: true);
        var disconnected = false;
        service.SetSessionDisconnector(() => disconnected = true);

        service.Lock();

        Assert.True(disconnected);
    }

    [Fact]
    public void Lock_SurviveAndMask_DoesNotInvokeDisconnector()
    {
        using var service = NewService(disconnectOnLock: false);
        var disconnected = false;
        service.SetSessionDisconnector(() => disconnected = true);

        service.Lock();

        Assert.False(disconnected);
    }

    [Fact]
    public void Lock_NoVaultEnabled_IsNoOp()
    {
        using var service = NewService(vaultEnabled: false);

        service.Lock();

        Assert.False(service.IsWorkspaceLocked);
    }

    [Fact]
    public void CanLock_OnlyWhenEnabledAndUnlocked()
    {
        using var enabled = NewService();
        Assert.True(enabled.CanLock);
        enabled.Lock();
        Assert.False(enabled.CanLock); // already locked

        using var disabled = NewService(vaultEnabled: false);
        Assert.False(disabled.CanLock);
    }

    /// <summary>
    /// A Hello platform that records enrolments and returns recognisable values.
    /// </summary>
    /// <remarks>
    /// The sentinels are what make the persistence assertions meaningful: they can only have come from
    /// here, so finding them in the settings proves the enrolment travelled the whole path.
    /// </remarks>
    private sealed class RecordingHelloService : IVaultHelloService
    {
        internal const string SentinelWrappedDek = "SENTINEL-WRAPPED-DEK";
        internal const string SentinelCredentialName = "SENTINEL-CREDENTIAL";
        internal const string SentinelPublicKeyHash = "SENTINEL-PUBKEY-HASH";

        public int EnrollCalls { get; private set; }

        public Task<bool> IsEnrollmentAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<VaultHelloEnrollment> EnrollAsync(
            ReadOnlyMemory<byte> dek,
            string vaultId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            EnrollCalls++;
            return Task.FromResult(new VaultHelloEnrollment(
                vaultId,
                SentinelWrappedDek,
                "SENTINEL-CHALLENGE",
                "SENTINEL-SALT",
                SentinelCredentialName,
                SentinelPublicKeyHash));
        }

        public Task<VaultDekHolder> UnlockAsync(VaultHelloEnrollment stored, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveAsync(string credentialName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
