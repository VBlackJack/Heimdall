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
using System.Text;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

/// <summary>
/// Exercises the version-aware v2 vault path of <see cref="CredentialProtector"/>.
/// Shares the <see cref="CredentialProtectorStaticCollection"/> so it never runs
/// concurrently with other classes that drive the static HMAC/DEK slots; a
/// <see cref="CredentialProtectorStateScope"/> pins the baseline around each test.
/// </summary>
[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class CredentialProtectorVaultTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void Protect_WithDekSet_ProducesV2SecretBlob()
    {
        using var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);

        var blob = CredentialProtector.Protect("credential");

        Assert.True(VaultSecretBlob.IsSecretBlob(blob));
        Assert.DoesNotContain("|HMAC|", blob);
    }

    [Fact]
    public void ProtectThenUnprotect_WithDekSet_RoundTrips()
    {
        using var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);

        var blob = CredentialProtector.Protect("round-trip-éŋ-✓");
        var recovered = CredentialProtector.Unprotect(blob);

        Assert.Equal("round-trip-éŋ-✓", recovered);
    }

    [Fact]
    public void UnprotectToBytes_WithDekSet_RoundTrips()
    {
        using var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);
        var blob = CredentialProtector.Protect("byte-secret");

        var bytes = CredentialProtector.UnprotectToBytes(blob);

        Assert.NotNull(bytes);
        Assert.Equal("byte-secret", Encoding.UTF8.GetString(bytes!));
        CryptographicOperations.ZeroMemory(bytes!);
    }

    [Fact]
    public void Protect_WithoutDek_NoHmac_ProducesLegacyDpapiBlob()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.Initialize(null);

        var blob = CredentialProtector.Protect("legacy");

        Assert.False(VaultSecretBlob.IsSecretBlob(blob));
        Assert.True(CredentialProtector.IsLegacyFormat(blob));
    }

    [Fact]
    public void Protect_WithoutDek_WithHmac_ProducesLegacyHmacBlob()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.Initialize(HmacIntegrity.GenerateRawKey());

        var blob = CredentialProtector.Protect("legacy-hmac");

        Assert.False(VaultSecretBlob.IsSecretBlob(blob));
        Assert.Contains("|HMAC|", blob);
    }

    [Fact]
    public void Unprotect_LegacyBlob_WithDekSet_StillReads()
    {
        // Produce a legacy blob with no DEK, then unlock the vault and confirm
        // the legacy blob is still readable (migration read).
        CredentialProtector.Initialize(null);
        var legacyBlob = CredentialProtector.Protect("legacy-secret");

        using var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);

        var recovered = CredentialProtector.Unprotect(legacyBlob);

        Assert.Equal("legacy-secret", recovered);
    }

    [Fact]
    public void Unprotect_V2Blob_WhenLocked_ThrowsVaultLocked_NotNull()
    {
        using var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);
        var blob = CredentialProtector.Protect("secret");

        CredentialProtector.ClearVaultKey();

        // Downgrade resistance: NOT null, NOT legacy-parsed — an explicit locked signal.
        Assert.Throws<VaultLockedException>(() => CredentialProtector.Unprotect(blob));
    }

    [Fact]
    public void Unprotect_V2Blob_WithDisposedDek_ThrowsVaultLocked()
    {
        var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);
        var blob = CredentialProtector.Protect("secret");

        holder.Dispose();

        Assert.Throws<VaultLockedException>(() => CredentialProtector.Unprotect(blob));
    }

    [Fact]
    public void UnprotectToBytes_V2Blob_WhenLocked_ThrowsVaultLocked()
    {
        using var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);
        var blob = CredentialProtector.Protect("secret");

        CredentialProtector.ClearVaultKey();

        Assert.Throws<VaultLockedException>(() => CredentialProtector.UnprotectToBytes(blob));
    }

    [Fact]
    public void Unprotect_V2Blob_WithWrongDek_FailsClosed()
    {
        using var holder1 = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder1);
        var blob = CredentialProtector.Protect("secret");

        using var holder2 = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder2);

        // A v2 blob under the wrong DEK throws (fail-closed), never returns null.
        Assert.ThrowsAny<CryptographicException>(() => CredentialProtector.Unprotect(blob));
    }

    [Fact]
    public void IsVaultUnlocked_ReflectsHolderState()
    {
        CredentialProtector.ClearVaultKey();
        Assert.False(CredentialProtector.IsVaultUnlocked);

        var holder = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(holder);
        Assert.True(CredentialProtector.IsVaultUnlocked);

        holder.Dispose();
        Assert.False(CredentialProtector.IsVaultUnlocked);
    }
}
