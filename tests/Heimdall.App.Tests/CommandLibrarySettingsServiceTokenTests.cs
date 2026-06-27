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
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies the vault-aware Git token WRITE: a token saved while the vault is
/// active is a v2 (HMS1) blob, a token saved without a vault is legacy, and both
/// round-trip through the vault-aware reader.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class CommandLibrarySettingsServiceTokenTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigManager _configManager;
    private readonly CommandLibrarySettingsService _service;

    public CommandLibrarySettingsServiceTokenTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Token." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configManager = new ConfigManager(_tempDir);
        _configManager.InitializeAsync().GetAwaiter().GetResult();
        _service = new CommandLibrarySettingsService(_configManager, new LocalizationManager());

        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(HmacIntegrity.GenerateRawKey());
    }

    public void Dispose()
    {
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
    }

    [Fact]
    public async Task TrySaveToken_VaultActive_WritesV2Blob_RoundTrips()
    {
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);

        var saved = await _service.TrySaveTokenAsync("ghp_under_vault");

        Assert.True(saved);
        var settings = await _configManager.LoadSettingsAsync();
        Assert.True(VaultSecretBlob.IsSecretBlob(settings.CmdLibGitSyncToken));
        Assert.Equal("ghp_under_vault", GitTokenReader.Decrypt(settings.CmdLibGitSyncToken));
    }

    [Fact]
    public async Task TrySaveToken_NoVault_WritesLegacy_RoundTrips()
    {
        var saved = await _service.TrySaveTokenAsync("ghp_no_vault");

        Assert.True(saved);
        var settings = await _configManager.LoadSettingsAsync();
        Assert.False(VaultSecretBlob.IsSecretBlob(settings.CmdLibGitSyncToken));
        Assert.Equal("ghp_no_vault", GitTokenReader.Decrypt(settings.CmdLibGitSyncToken));
    }
}
