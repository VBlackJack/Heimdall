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
/// A workspace locked behind an overlay must not stay so once the vault that would unlock it is gone.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WorkspaceLockVaultGoneTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "Heimdall.LockGone." + Guid.NewGuid().ToString("N"));

    public WorkspaceLockVaultGoneTests()
    {
        Directory.CreateDirectory(_directory);
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());
    }

    public void Dispose()
    {
        CredentialProtectorStateScope.Reset();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Configure_VaultNoLongerEnabled_WhileLocked_ClearsTheLockAndRaisesTheChange()
    {
        using var lifecycle = new VaultLifecycleService(new ConfigManager(_directory));
        using var service = new WorkspaceLockService(lifecycle);
        service.Configure(vaultEnabled: true, autoLockIdleMinutes: 0, disconnectOnLock: false);
        service.Lock();
        Assert.True(service.IsWorkspaceLocked);
        var changes = 0;
        service.LockStateChanged += () => changes++;

        service.Configure(vaultEnabled: false, autoLockIdleMinutes: 0, disconnectOnLock: false);

        Assert.False(service.IsWorkspaceLocked);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Configure_VaultStillEnabled_WhileLocked_KeepsTheLock()
    {
        using var lifecycle = new VaultLifecycleService(new ConfigManager(_directory));
        using var service = new WorkspaceLockService(lifecycle);
        service.Configure(vaultEnabled: true, autoLockIdleMinutes: 0, disconnectOnLock: false);
        service.Lock();
        var changes = 0;
        service.LockStateChanged += () => changes++;

        service.Configure(vaultEnabled: true, autoLockIdleMinutes: 5, disconnectOnLock: false);

        Assert.True(service.IsWorkspaceLocked);
        Assert.Equal(0, changes);
    }
}
