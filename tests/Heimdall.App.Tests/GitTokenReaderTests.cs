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
using Heimdall.App.Services;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies the vault-aware Git token reader: v2 when unlocked, legacy DPAPI when
/// no vault (backward compat), and Git deferred (null, no throw) when a v2 token
/// is read while locked. Resets the static CredentialProtector slots per test.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GitTokenReaderTests : IDisposable
{
    public GitTokenReaderTests()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(HmacIntegrity.GenerateRawKey());
    }

    public void Dispose()
    {
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(null);
    }

    [Fact]
    public void Decrypt_V2TokenWhileUnlocked_ReturnsPlaintext()
    {
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);
        var v2Token = CredentialProtector.Protect("ghp_unlocked_token");

        var result = GitTokenReader.Decrypt(v2Token);

        Assert.Equal("ghp_unlocked_token", result);
    }

    [Fact]
    public void Decrypt_LegacyDpapiToken_NoVault_ReturnsPlaintext()
    {
        // The current producer (TrySaveTokenAsync) stores plain DPAPI; the
        // vault-aware reader must still read it when no vault is configured.
        var legacyToken = DpapiProvider.Protect("ghp_legacy_token");

        var result = GitTokenReader.Decrypt(legacyToken);

        Assert.Equal("ghp_legacy_token", result);
    }

    [Fact]
    public void Decrypt_V2TokenWhileLocked_ReturnsNull_GitDeferred()
    {
        // Produce a v2 token, then lock the vault by clearing the DEK.
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);
        var v2Token = CredentialProtector.Protect("ghp_secret_token");
        CredentialProtector.ClearVaultKey();

        var result = GitTokenReader.Decrypt(v2Token);

        // Git unavailable until unlock: null, NOT a wrong/empty token, no throw escaping.
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decrypt_NullOrWhitespace_ReturnsNull(string? input)
    {
        Assert.Null(GitTokenReader.Decrypt(input));
    }
}
