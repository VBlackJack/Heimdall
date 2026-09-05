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
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

/// <summary>
/// Verifies the write-downgrade guard: Protect must never emit a weaker legacy
/// blob while a vault is configured but locked.
/// </summary>
[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class CredentialProtectorWriteDowngradeTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void Protect_VaultEnabledButLocked_ThrowsVaultLocked()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(true);

        Assert.Throws<VaultLockedException>(() => CredentialProtector.Protect("secret"));
    }

    [Fact]
    public void Protect_VaultNotEnabled_ProducesLegacyBlob()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(null);

        var blob = CredentialProtector.Protect("secret");

        Assert.False(VaultSecretBlob.IsSecretBlob(blob));
        Assert.True(CredentialProtector.IsLegacyFormat(blob));
    }

    [Fact]
    public void Protect_VaultEnabledAndUnlocked_ProducesV2Blob()
    {
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);

        var blob = CredentialProtector.Protect("secret");

        Assert.True(VaultSecretBlob.IsSecretBlob(blob));
    }

    [Fact]
    public void ProtectLegacy_IgnoresVaultState_AlwaysLegacy()
    {
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);

        var blob = CredentialProtector.ProtectLegacy("secret");

        Assert.False(VaultSecretBlob.IsSecretBlob(blob));
    }
}
