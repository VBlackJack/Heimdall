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

using Heimdall.Core.Configuration;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultSchemaValidationTests
{
    [Fact]
    public void ValidateSettings_DisabledVault_IsValid()
    {
        var settings = new AppSettings();

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateSettings_EnabledVaultWithWrappedDek_IsValid()
    {
        var settings = new AppSettings
        {
            VaultEnabled = true,
            VaultWrappedDek = "some-wrapped-dek",
            VaultMigrationState = VaultMigrationState.Complete,
        };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateSettings_EnabledVaultWithoutWrappedDek_IsInvalid()
    {
        var settings = new AppSettings
        {
            VaultEnabled = true,
            VaultWrappedDek = null,
        };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.VaultWrappedDek), StringComparison.Ordinal));
    }
}
