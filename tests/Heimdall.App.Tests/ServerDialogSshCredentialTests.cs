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

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogSshCredentialTests
{
    [Fact]
    public void SshKeyPathCleared_ClearsKeyPassphrase()
    {
        var vm = new ServerDialogViewModel
        {
            SshKeyPath = @"C:\keys\id_rsa",
            SshKeyPassphrase = "secret",
            ExistingSshKeyPassphraseEncrypted = "encrypted"
        };

        vm.SshKeyPath = "";

        Assert.False(vm.HasSshKeyPath);
        Assert.Equal("", vm.SshKeyPassphrase);
        Assert.Null(vm.ExistingSshKeyPassphraseEncrypted);
    }

    [Fact]
    public void ToDto_KeyPathWithEmptyKeyPassphrase_WritesExplicitEmptyMarker()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "SSH",
            RemoteServer = "host",
            ConnectionType = "SSH",
            SshUsername = "user",
            SshKeyPath = @"C:\keys\id_rsa",
            SshPassword = "login-password"
        };

        var dto = vm.ToDto();

        Assert.Equal(@"C:\keys\id_rsa", dto.SshKeyPath);
        Assert.Equal("", dto.SshKeyPassphraseEncrypted);
        Assert.True(dto.HasSshKeyPassphraseEncryptedField);
        Assert.False(dto.UsesLegacySshCredentialMapping);
        Assert.NotNull(dto.SshPasswordEncrypted);
    }

    [Fact]
    public void FromDto_LegacyProfile_DoesNotInventKeyPassphrase()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Legacy",
            RemoteServer = "host",
            ConnectionType = "SSH",
            SshKeyPath = @"C:\keys\id_rsa",
            SshPasswordEncrypted = "encrypted-password"
        };

        var vm = ServerDialogViewModel.FromDto(dto);

        Assert.Equal(@"C:\keys\id_rsa", vm.SshKeyPath);
        Assert.Equal("encrypted-password", vm.ExistingSshPasswordEncrypted);
        Assert.Null(vm.ExistingSshKeyPassphraseEncrypted);
        Assert.Equal("", vm.SshKeyPassphrase);
    }

    // A session saved before the passphrase field existed offers its stored password to the key,
    // and that mapping is read off the field being absent. Saving it back with an empty field
    // declared switched the mapping off, so renaming the session was enough to stop its
    // encrypted key from loading.
    [Fact]
    public void FromDtoToDto_LegacyProfileEditedWithoutPassphrase_KeepsLegacyMapping()
    {
        ServerProfileDto legacy = LegacyProfile();
        Assert.True(legacy.UsesLegacySshCredentialMapping);

        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(legacy);
        vm.DisplayName = "Renamed";

        ServerProfileDto dto = vm.ToDto();

        Assert.Equal("Renamed", dto.DisplayName);
        Assert.False(dto.HasSshKeyPassphraseEncryptedField);
        Assert.True(dto.UsesLegacySshCredentialMapping);
    }

    // A profile that declared an empty passphrase must not become a legacy one through an edit.
    [Fact]
    public void FromDtoToDto_ProfileDeclaringAnEmptyPassphrase_StaysDeclared()
    {
        ServerProfileDto declared = LegacyProfile();
        declared.SshKeyPassphraseEncrypted = string.Empty;

        ServerProfileDto dto = ServerDialogViewModel.FromDto(declared).ToDto();

        Assert.True(dto.HasSshKeyPassphraseEncryptedField);
        Assert.False(dto.UsesLegacySshCredentialMapping);
    }

    private static ServerProfileDto LegacyProfile() => new()
    {
        Id = "legacy",
        DisplayName = "Legacy",
        RemoteServer = "host",
        ConnectionType = "SSH",
        SshUsername = "user",
        SshKeyPath = @"C:\keys\id_rsa",
        SshPasswordEncrypted = "encrypted-password"
    };
}
