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

        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(HmacIntegrity.GenerateRawKey());

        _lifecycle = new VaultLifecycleService(_configManager);
        await _lifecycle.EnableAsync(MasterPassword.ToCharArray(), FastParams); // leaves the vault unlocked
    }

    public Task DisposeAsync()
    {
        _lifecycle.Dispose();
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(null);
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
}
