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
}
