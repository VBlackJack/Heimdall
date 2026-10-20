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

/// <summary>
/// The declared operating system is a profile property, so the session dialog has to carry it
/// through an edit untouched and has to count changing it as an edit.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogRemoteOsTests
{
    [Fact]
    public void EditingAProfile_KeepsItsDeclaredOperatingSystem()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Windows over SSH",
            RemoteServer = "win-ssh.example.com",
            ConnectionType = "SSH",
            RemoteOs = RemoteOs.Windows
        });

        Assert.Equal(RemoteOs.Windows, vm.RemoteOs);
        Assert.Equal(RemoteOs.Windows, vm.ToDto().RemoteOs);
    }

    [Fact]
    public void ChangingTheOperatingSystem_ArmsTheUnsavedChangesGuard()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Host",
            RemoteServer = "host.example.com",
            ConnectionType = "SSH"
        });
        Assert.False(vm.IsDirty);

        vm.RemoteOs = RemoteOs.Linux;

        Assert.True(vm.IsDirty);
        Assert.Equal(RemoteOs.Linux, vm.ToDto().RemoteOs);
    }
}
