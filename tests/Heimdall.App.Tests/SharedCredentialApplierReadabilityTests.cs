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

using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// A remembered shared-session secret sealed with a vault key that no longer exists must be
/// forgotten and asked again, not kept as "readable behind a locked vault" for ever.
/// </summary>
public sealed class SharedCredentialApplierReadabilityTests
{
    private static string? Locked(string sealedSecret) => throw new VaultLockedException();

    [Fact]
    public void IsReadable_OrphanedBlobAfterDisable_IsUnreadable()
    {
        Assert.False(SharedCredentialApplier.IsReadable(Locked, "blob", vaultEnabled: false));
    }

    [Fact]
    public void IsReadable_LockedVault_StaysReadable()
    {
        Assert.True(SharedCredentialApplier.IsReadable(Locked, "blob", vaultEnabled: true));
    }

    [Fact]
    public void IsReadable_OpenedSecret_IsReadable()
    {
        Assert.True(SharedCredentialApplier.IsReadable(_ => "plain", "blob", vaultEnabled: false));
    }

    [Fact]
    public void IsReadable_UnopenableSecret_IsUnreadable()
    {
        Assert.False(SharedCredentialApplier.IsReadable(_ => null, "blob", vaultEnabled: true));
    }
}
