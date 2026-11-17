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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The saved-password lookup notice only concerns protocols the credential provider is asked for.
/// </summary>
public sealed class CredentialLookupProtocolTests
{
    private const string Host = "elsewhere.example.com";
    private const string User = "someone";

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("Local")]
    [InlineData("Citrix")]
    [InlineData("CITRIX")]
    [InlineData("Telnet")]
    [InlineData("TELNET")]
    public void ImportedProfileWithNoLookup_ShowsNoNotice(string connectionType)
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Imported(connectionType));

        Assert.False(viewModel.ShowCredentialLookupNotice);
    }

    [Fact]
    public void ImportedSshProfile_ShowsTheNotice()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Imported("SSH"));

        Assert.True(viewModel.ShowCredentialLookupNotice);
    }

    [Fact]
    public void SwitchingTheTypeFromLocalToSsh_ShowsTheNotice_AndBackHidesIt()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Imported("Local"));
        List<string?> raised = [];
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(viewModel.ShowCredentialLookupNotice);

        viewModel.ConnectionType = "SSH";

        Assert.True(viewModel.ShowCredentialLookupNotice);
        Assert.Contains(nameof(ServerDialogViewModel.ShowCredentialLookupNotice), raised);
        Assert.False(viewModel.CredentialLookupConfirmed);

        viewModel.ConnectionType = "Local";

        Assert.False(viewModel.ShowCredentialLookupNotice);
        Assert.False(viewModel.CredentialLookupConfirmed);
    }

    [Fact]
    public void ProtocolHasLookup_AgreesWithTheCredentialTargetOfEveryKnownType()
    {
        foreach (string type in ConnectionTypeCatalog.CanonicalTypes)
        {
            ServerProfileDto profile = new()
            {
                ConnectionType = type,
                RemoteServer = Host,
                SshUsername = User,
                RdpUsername = User,
                FtpUsername = User,
                WebUsername = User,
                WinRmUsername = User,
                WinRmIdentityMode = WinRmIdentityMode.Credential,
            };

            Assert.True(
                CredentialLookupConfirmation.ProtocolHasLookup(type)
                    == (ServerListViewModel.GetCredentialTarget(profile) is not null),
                $"ProtocolHasLookup disagrees with GetCredentialTarget for {type}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TOOL")]
    public void ProtocolHasLookup_UnknownOrEmptyType_IsFalse(string? connectionType)
    {
        Assert.False(CredentialLookupConfirmation.ProtocolHasLookup(connectionType));
    }

    private static ServerProfileDto Imported(string connectionType) => new()
    {
        Id = "imported-1",
        DisplayName = "imported",
        ConnectionType = connectionType,
        RemoteServer = Host,
        VaultEntryName = "vault-entry",
        CredentialLookupConfirmed = false,
    };
}
