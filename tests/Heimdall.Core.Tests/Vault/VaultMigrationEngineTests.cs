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

[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class VaultMigrationEngineTests : IDisposable
{
    private readonly VaultDekHolder _dek;

    public VaultMigrationEngineTests()
    {
        CredentialProtector.Initialize(HmacIntegrity.GenerateRawKey());
        _dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultKey(_dek);
    }

    public void Dispose()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(null);
        _dek.Dispose();
    }

    // ── Forward (legacy -> v2) ──────────────────────────────────────────────

    [Fact]
    public void ForwardEncrypted_LegacyBlob_BecomesV2AndDecryptsToSamePlaintext()
    {
        // A legacy blob is produced with the DEK temporarily cleared.
        CredentialProtector.ClearVaultKey();
        var legacy = CredentialProtector.Protect("rdp-secret");
        CredentialProtector.SetVaultKey(_dek);

        var migrated = VaultMigrationEngine.ForwardEncrypted(legacy);

        Assert.True(VaultSecretBlob.IsSecretBlob(migrated));
        Assert.Equal("rdp-secret", CredentialProtector.Unprotect(migrated));
    }

    [Fact]
    public void ForwardEncrypted_AlreadyV2_IsUnchanged()
    {
        var v2 = CredentialProtector.Protect("already-v2");

        var migrated = VaultMigrationEngine.ForwardEncrypted(v2);

        Assert.Same(v2, migrated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForwardEncrypted_NullOrEmpty_IsUnchanged(string? value)
    {
        Assert.Equal(value, VaultMigrationEngine.ForwardEncrypted(value));
    }

    [Fact]
    public void ForwardPlaintext_BecomesV2AndDecryptsToSameValue()
    {
        const string plaintext = "C:\\SelfService.exe /launch token=abc";

        var migrated = VaultMigrationEngine.ForwardPlaintext(plaintext);

        Assert.True(VaultSecretBlob.IsSecretBlob(migrated));
        Assert.Equal(plaintext, CredentialProtector.Unprotect(migrated));
    }

    [Fact]
    public void ForwardPlaintext_AlreadyV2_IsUnchanged()
    {
        var v2 = CredentialProtector.Protect("citrix");

        Assert.Same(v2, VaultMigrationEngine.ForwardPlaintext(v2));
    }

    // ── Reverse (v2 -> legacy) ──────────────────────────────────────────────

    [Fact]
    public void ReverseToCredentialProtectorLegacy_V2_BecomesLegacyReadableWithoutDek()
    {
        var v2 = CredentialProtector.Protect("ssh-secret");

        var legacy = VaultMigrationEngine.ReverseToCredentialProtectorLegacy(v2);

        Assert.False(VaultSecretBlob.IsSecretBlob(legacy));

        // Readable with the DEK cleared (legacy path).
        CredentialProtector.ClearVaultKey();
        Assert.Equal("ssh-secret", CredentialProtector.Unprotect(legacy));
    }

    [Fact]
    public void ReverseToDpapi_V2_BecomesPlainDpapiReadable()
    {
        var v2 = CredentialProtector.Protect("git-token");

        var dpapi = VaultMigrationEngine.ReverseToDpapi(v2);

        Assert.False(VaultSecretBlob.IsSecretBlob(dpapi));
        // The consumer reads this field with plain DPAPI, not CredentialProtector.
        Assert.Equal("git-token", DpapiProvider.Unprotect(dpapi!));
    }

    [Fact]
    public void ReverseToPlaintext_V2_ReturnsRawPlaintext()
    {
        const string plaintext = "C:\\SelfService.exe /launch token=xyz";
        var v2 = CredentialProtector.Protect(plaintext);

        var reversed = VaultMigrationEngine.ReverseToPlaintext(v2);

        Assert.Equal(plaintext, reversed);
        Assert.False(VaultSecretBlob.IsSecretBlob(reversed));
    }

    [Fact]
    public void ReverseToCredentialProtectorLegacy_AlreadyLegacy_IsUnchanged()
    {
        CredentialProtector.ClearVaultKey();
        var legacy = CredentialProtector.Protect("already-legacy");
        CredentialProtector.SetVaultKey(_dek);

        Assert.Same(legacy, VaultMigrationEngine.ReverseToCredentialProtectorLegacy(legacy));
    }

    [Fact]
    public void ForwardThenReverse_RoundTripsToOriginalPlaintext()
    {
        CredentialProtector.ClearVaultKey();
        var legacy = CredentialProtector.Protect("round-trip-secret");
        CredentialProtector.SetVaultKey(_dek);

        var v2 = VaultMigrationEngine.ForwardEncrypted(legacy);
        var backToLegacy = VaultMigrationEngine.ReverseToCredentialProtectorLegacy(v2);

        CredentialProtector.ClearVaultKey();
        Assert.Equal("round-trip-secret", CredentialProtector.Unprotect(backToLegacy));
    }
}
