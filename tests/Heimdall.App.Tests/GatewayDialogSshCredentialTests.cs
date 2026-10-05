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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

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

    // An empty box keeps what is stored, so a gateway moved to a key or an agent had no way to
    // stop sending its old password.
    [Fact]
    public void ClearStoredPassword_DropsTheStoredPasswordOnSave()
    {
        SshGatewayDto stored = LegacyGateway();
        stored.SshKeyPassphraseEncrypted = string.Empty;
        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(stored);
        Assert.True(vm.HasStoredPassword);

        vm.ClearStoredPasswordCommand.Execute(null);

        Assert.False(vm.HasStoredPassword);
        Assert.True(vm.IsDirty);
        Assert.Null(vm.ToDto().SshPasswordEncrypted);
    }

    // Forgetting the passphrase declares the field empty: the key has no passphrase, and the
    // login password is not offered in its place.
    [Fact]
    public void ClearStoredKeyPassphrase_SavesAnExplicitlyEmptyPassphrase()
    {
        SshGatewayDto stored = LegacyGateway();
        stored.SshKeyPassphraseEncrypted = "stored-passphrase";
        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(stored);
        Assert.True(vm.HasStoredKeyPassphrase);

        vm.ClearStoredKeyPassphraseCommand.Execute(null);

        SshGatewayDto dto = vm.ToDto();
        Assert.False(vm.HasStoredKeyPassphrase);
        Assert.Equal(string.Empty, dto.SshKeyPassphraseEncrypted);
        Assert.False(dto.UsesLegacySshCredentialMapping);
    }

    // The window title was bound to a property no caller assigned.
    [Theory]
    [InlineData(false, "GatewayDialogTitleAdd")]
    [InlineData(true, "GatewayDialogTitleEdit")]
    public async Task EnsureDialogTitle_NamesTheWindowFromTheMode(bool isEditMode, string key)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        GatewayDialogViewModel vm = isEditMode ? GatewayDialogViewModel.FromDto(LegacyGateway()) : new();
        vm.Localizer = localizer;

        vm.EnsureDialogTitle();

        Assert.Equal(localizer[key], vm.DialogTitle);
        Assert.False(string.IsNullOrWhiteSpace(vm.DialogTitle));
    }

    // Text the port box cannot convert never reaches the view model, so validation saw the
    // previous port and Save stored it.
    [Fact]
    public void ReportUnreadablePort_BlocksTheSave()
    {
        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(LegacyGateway());
        vm.ValidateCommand.Execute(null);
        Assert.Null(vm.ValidationError);

        vm.ReportUnreadablePort();

        Assert.NotNull(vm.PortError);
        Assert.Equal(vm.PortError, vm.ValidationError);
    }

    // The caller replaces the stored gateway with what the dialog returns, so a field the
    // dialog does not show has to ride through the edit or it is deleted.
    [Fact]
    public void FromDtoToDto_KeepsFieldsThisBuildDoesNotKnow()
    {
        SshGatewayDto stored = System.Text.Json.JsonSerializer.Deserialize<SshGatewayDto>(
            """{ "Id": "gw-1", "Name": "Bastion", "Host": "h", "User": "u", "FutureOption": 7 }""")!;

        SshGatewayDto saved = GatewayDialogViewModel.FromDto(stored).ToDto();

        Assert.Equal(7, saved.ExtensionData["FutureOption"].GetInt32());
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
