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
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Session;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

public sealed class CredentialLookupConfirmationTests
{
    [Fact]
    public void LookupTitle_UnconfirmedProfile_IsTheHost()
    {
        ServerProfileDto profile = ImportedLikeProfile();

        Assert.Equal("elsewhere.example.com", ServerListViewModel.CredentialLookupTitle(profile));
    }

    [Fact]
    public void LookupTitle_ConfirmedProfile_IsTheVaultEntryName()
    {
        ServerProfileDto profile = ImportedLikeProfile();
        profile.CredentialLookupConfirmed = true;

        Assert.Equal("vault-entry", ServerListViewModel.CredentialLookupTitle(profile));
    }

    [Fact]
    public void LookupTitle_AfterADialogSave_IsTheVaultEntryNameAgain()
    {
        ServerProfileDto imported = ImportedLikeProfile();
        Assert.Equal("elsewhere.example.com", ServerListViewModel.CredentialLookupTitle(imported));

        ServerProfileDto saved = ServerDialogViewModel.FromDto(imported).ToDto();

        Assert.True(saved.CredentialLookupConfirmed);
        Assert.Equal("vault-entry", ServerListViewModel.CredentialLookupTitle(saved));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LookupTitle_SharedProfile_IsTheHostWhateverTheSourceSaysAboutConfirmation(bool confirmed)
    {
        ServerProfileDto source = ImportedLikeProfile();
        source.Id = SharedSessionIds.PrefixFor("teamsource") + "0123abcd";
        source.CredentialLookupConfirmed = confirmed;

        ServerProfileDto shared = Assert.IsType<ServerProfileDto>(SharedProfileSanitizer.Sanitize(source));

        Assert.Equal(confirmed, shared.CredentialLookupConfirmed);
        Assert.Equal("elsewhere.example.com", ServerListViewModel.CredentialLookupTitle(shared));
    }

    [Fact]
    public void ScannedHost_IsDiscoveredUnconfirmed()
    {
        ServerProfileDto profile = NetworkScannerService.BuildDiscoveredProfile(
            new NetworkScanner.ScanResult("10.0.0.9", true, 3, "prod-db.attacker.example", [22]));

        Assert.False(profile.CredentialLookupConfirmed);
        Assert.Equal("10.0.0.9", ServerListViewModel.CredentialLookupTitle(profile));
    }

    [Fact]
    public void CompanionSftpProfile_KeepsTheLookupConfirmation()
    {
        ServerProfileDto ssh = ImportedLikeProfile();
        ssh.ConnectionType = "SSH";

        Assert.False(SessionCoordinator.CreateCompanionSftpProfile(ssh, "sftp-1").CredentialLookupConfirmed);

        ssh.CredentialLookupConfirmed = true;
        Assert.True(SessionCoordinator.CreateCompanionSftpProfile(ssh, "sftp-2").CredentialLookupConfirmed);
    }

    [Fact]
    public void NameBasedTitleComparison_IgnoresCase()
    {
        ServerProfileDto profile = new() { DisplayName = "Box.Example.com", RemoteServer = "box.example.com" };

        Assert.False(CredentialLookupPolicy.NameBasedTitleDiffersFromHost(profile));
    }

    [Fact]
    public async Task RdpReplace_KeepsTheLinkageButClearsTheConfirmation()
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-lookup-confirm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            ConfigManager configManager = new(root);
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            RdpImportService service = new(configManager, localizer);
            await configManager.SaveServersAsync(
            [
                new ServerProfileDto
                {
                    Id = "keep-me",
                    DisplayName = "Server01",
                    ConnectionType = "RDP",
                    RemoteServer = "10.0.0.5",
                    VaultEntryName = "Server01 (renamed)"
                }
            ]);
            string importsDirectory = Path.Combine(root, "imports");
            Directory.CreateDirectory(importsDirectory);
            string path = Path.Combine(importsDirectory, "Server01.rdp");
            await File.WriteAllTextAsync(path, "full address:s:evil.example.com");
            RdpImportPreview preview = await service.PreviewAsync([path], CancellationToken.None);

            _ = await service.ApplyAsync(
                preview,
                new RdpImportSelection
                {
                    Entries =
                    [
                        new RdpImportSelectionEntry
                        {
                            SourceFilePath = path,
                            IsSelected = true,
                            ConflictResolution = RdpConflictResolution.Replace
                        }
                    ]
                },
                CancellationToken.None);

            ServerProfileDto replaced = Assert.Single(await configManager.LoadServersAsync());
            Assert.Equal("evil.example.com", replaced.RemoteServer);
            Assert.Equal("Server01 (renamed)", replaced.VaultEntryName);
            Assert.False(replaced.CredentialLookupConfirmed);
            Assert.Equal("evil.example.com", ServerListViewModel.CredentialLookupTitle(replaced));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static ServerProfileDto ImportedLikeProfile() => new()
    {
        Id = "imported-1",
        DisplayName = "prod-db",
        ConnectionType = "SSH",
        RemoteServer = "elsewhere.example.com",
        VaultEntryName = "vault-entry",
        CredentialLookupConfirmed = false,
    };
}
