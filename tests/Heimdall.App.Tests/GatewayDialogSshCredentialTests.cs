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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

// Encrypts SSH credentials through CredentialProtector (via ToDto), so it shares the
// static CredentialProtector state with the vault tests and must not run concurrently.
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class GatewayDialogSshCredentialTests
{
    [Fact]
    public void KeyPathCleared_ClearsKeyPassphrase()
    {
        var vm = new GatewayDialogViewModel
        {
            KeyPath = @"C:\keys\gw.ppk",
            KeyPassphrase = "secret",
            ExistingSshKeyPassphraseEncrypted = "encrypted"
        };

        vm.KeyPath = "";

        Assert.False(vm.HasKeyPath);
        Assert.Equal("", vm.KeyPassphrase);
        Assert.Null(vm.ExistingSshKeyPassphraseEncrypted);
    }

    [Fact]
    public void ToDto_KeyPathWithEmptyKeyPassphrase_WritesExplicitEmptyMarker()
    {
        var vm = new GatewayDialogViewModel
        {
            Name = "Gateway",
            Host = "gateway.example.com",
            User = "user",
            KeyPath = @"C:\keys\gw.ppk",
            Password = "login-password"
        };

        var dto = vm.ToDto();

        Assert.Equal(@"C:\keys\gw.ppk", dto.KeyPath);
        Assert.Equal("", dto.SshKeyPassphraseEncrypted);
        Assert.True(dto.HasSshKeyPassphraseEncryptedField);
        Assert.False(dto.UsesLegacySshCredentialMapping);
        Assert.NotNull(dto.SshPasswordEncrypted);
    }

    // A gateway written before the passphrase field existed offers its stored password as the
    // key passphrase, and that mapping is read off the field being absent. Saving it back with an
    // empty field declared switched the mapping off, so renaming such a gateway was enough to
    // make its encrypted key fail to load at the next connection.
    [Fact]
    public void FromDtoToDto_LegacyGatewayEditedWithoutPassphrase_KeepsLegacyMapping()
    {
        SshGatewayDto legacy = LegacyGateway();
        Assert.True(legacy.UsesLegacySshCredentialMapping);

        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(legacy);
        vm.Name = "Renamed";

        SshGatewayDto dto = vm.ToDto();

        Assert.Equal("Renamed", dto.Name);
        Assert.Equal("legacy-password", dto.SshPasswordEncrypted);
        Assert.False(dto.HasSshKeyPassphraseEncryptedField);
        Assert.True(dto.UsesLegacySshCredentialMapping);
    }

    // Typing a passphrase is the user moving the gateway to the explicit field.
    [Fact]
    public void FromDtoToDto_LegacyGatewayGivenAPassphrase_DeclaresTheField()
    {
        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(LegacyGateway());
        vm.KeyPassphrase = "typed-passphrase";

        SshGatewayDto dto = vm.ToDto();

        Assert.True(dto.HasSshKeyPassphraseEncryptedField);
        Assert.False(string.IsNullOrEmpty(dto.SshKeyPassphraseEncrypted));
        Assert.False(dto.UsesLegacySshCredentialMapping);
    }

    // The other direction: a gateway that declared an empty passphrase must not be turned into
    // a legacy one by an edit, or its login password would start being offered to the key.
    [Fact]
    public void FromDtoToDto_GatewayDeclaringAnEmptyPassphrase_StaysDeclared()
    {
        SshGatewayDto declared = LegacyGateway();
        declared.SshKeyPassphraseEncrypted = string.Empty;
        Assert.False(declared.UsesLegacySshCredentialMapping);

        SshGatewayDto dto = GatewayDialogViewModel.FromDto(declared).ToDto();

        Assert.True(dto.HasSshKeyPassphraseEncryptedField);
        Assert.False(dto.UsesLegacySshCredentialMapping);
    }

    private static SshGatewayDto LegacyGateway() => new()
    {
        Id = "gw-legacy",
        Name = "Legacy",
        Host = "gateway.example.com",
        User = "user",
        KeyPath = @"C:\keys\gw.ppk",
        SshPasswordEncrypted = "legacy-password"
    };
}
