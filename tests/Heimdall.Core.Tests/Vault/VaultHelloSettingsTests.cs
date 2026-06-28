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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultHelloSettingsTests
{
    [Fact]
    public void LegacySettings_DefaultToNotEnrolled()
    {
        var json = "{}";

        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.False(settings.VaultHelloEnrolled);
        Assert.Null(settings.VaultHelloWrappedDek);
        Assert.Equal(0, settings.VaultHelloMaxDaysBeforeMasterPassword);
    }

    [Fact]
    public void EnrolledSettings_RoundTrip()
    {
        var settings = new AppSettings
        {
            VaultId = "vault-id",
            VaultHelloEnrolled = true,
            VaultHelloWrappedDek = "wrapped",
            VaultHelloChallenge = "challenge",
            VaultHelloSalt = "salt",
            VaultHelloCredentialName = "credential",
            VaultHelloPublicKeyHash = "hash",
            VaultHelloMaxDaysBeforeMasterPassword = 30
        };

        var json = JsonSerializer.Serialize(settings);
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(roundTrip.VaultHelloEnrolled);
        Assert.Equal("vault-id", roundTrip.VaultId);
        Assert.Equal("wrapped", roundTrip.VaultHelloWrappedDek);
        Assert.Equal("challenge", roundTrip.VaultHelloChallenge);
        Assert.Equal("salt", roundTrip.VaultHelloSalt);
        Assert.Equal("credential", roundTrip.VaultHelloCredentialName);
        Assert.Equal("hash", roundTrip.VaultHelloPublicKeyHash);
        Assert.Equal(30, roundTrip.VaultHelloMaxDaysBeforeMasterPassword);
    }

    [Fact]
    public void ValidateSettings_EnrolledWithoutWrappedDek_IsInvalid()
    {
        var settings = EnrolledSettings();
        settings.VaultHelloWrappedDek = null;

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.VaultHelloWrappedDek), StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateSettings_EnrolledWithoutVaultId_IsInvalid()
    {
        var settings = EnrolledSettings();
        settings.VaultId = null;

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.VaultId), StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateSettings_EnrolledWithMetadata_IsValid()
    {
        var result = SchemaValidator.ValidateSettings(EnrolledSettings());

        Assert.True(result.IsValid);
    }

    private static AppSettings EnrolledSettings()
    {
        return new AppSettings
        {
            VaultId = "vault-id",
            VaultHelloEnrolled = true,
            VaultHelloWrappedDek = "wrapped",
            VaultHelloChallenge = "challenge",
            VaultHelloSalt = "salt",
            VaultHelloCredentialName = "credential",
            VaultHelloPublicKeyHash = "hash"
        };
    }
}
