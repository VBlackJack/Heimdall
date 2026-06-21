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

public sealed class ServerDialogVaultEntryNameTests
{
    [Fact]
    public void ToDto_WithVaultEntryName_PersistsValue()
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "Prod DB",
            RemoteServer = "db.example.com",
            ConnectionType = "RDP",
            VaultEntryName = "team-vault/prod-db"
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.Equal("team-vault/prod-db", dto.VaultEntryName);
    }

    [Fact]
    public void ToDto_BlankVaultEntryName_PersistsNull()
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "Prod DB",
            RemoteServer = "db.example.com",
            ConnectionType = "RDP",
            VaultEntryName = "   "
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.Null(dto.VaultEntryName);
    }

    [Fact]
    public void FromDto_LoadsVaultEntryName()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Prod DB",
            RemoteServer = "db.example.com",
            ConnectionType = "RDP",
            VaultEntryName = "team-vault/prod-db"
        });

        Assert.Equal("team-vault/prod-db", vm.VaultEntryName);
    }

    [Fact]
    public void FromDto_NullVaultEntryName_LoadsEmptyString()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Prod DB",
            RemoteServer = "db.example.com",
            ConnectionType = "RDP",
            VaultEntryName = null
        });

        Assert.Equal(string.Empty, vm.VaultEntryName);
    }

    [Fact]
    public void DialogRoundTrip_PreservesVaultEntryName()
    {
        ServerProfileDto original = new()
        {
            DisplayName = "Prod DB",
            RemoteServer = "db.example.com",
            ConnectionType = "RDP",
            VaultEntryName = "team-vault/prod-db"
        };

        ServerProfileDto roundTripped = ServerDialogViewModel.FromDto(original).ToDto();

        Assert.Equal(original.VaultEntryName, roundTripped.VaultEntryName);
    }
}
