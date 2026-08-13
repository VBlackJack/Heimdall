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
using Heimdall.App.Services.Import;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class RdpImportServiceTests
{
    [Fact]
    public async Task PreviewAsync_MissingFile_IsReported()
    {
        using var fixture = new RdpImportFixture();
        var preview = await fixture.Service.PreviewAsync([Path.Combine(fixture.RootPath, "missing.rdp")], CancellationToken.None);

        Assert.Single(preview.FilesNotFound);
        Assert.Empty(preview.Entries);
    }

    [Fact]
    public async Task PreviewAsync_NonFilePath_IsReported()
    {
        using var fixture = new RdpImportFixture();
        var directoryPath = Path.Combine(fixture.RootPath, "folder.rdp");
        Directory.CreateDirectory(directoryPath);

        var preview = await fixture.Service.PreviewAsync([directoryPath], CancellationToken.None);

        Assert.Single(preview.FilesNotFound);
        Assert.Empty(preview.FilesUnreadable);
        Assert.Empty(preview.Entries);
    }

    [Fact]
    public async Task PreviewAsync_DerivesNameFromFilename()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync("Workstation01.rdp", "full address:s:rdp.example.com:3390");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        Assert.Equal("Workstation01", preview.Entries[0].ProposedName);
        Assert.Equal("rdp.example.com", preview.Entries[0].Candidate.RemoteServer);
        Assert.Equal(3390, preview.Entries[0].Candidate.RemotePort);
    }

    [Fact]
    public async Task PreviewAsync_GenericFilename_FallsBackToAlternateAddress()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync(
            "default.rdp",
            """
            alternate full address:s:jump-host
            full address:s:rdp.example.com
            """
        );

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        Assert.Equal("jump-host", preview.Entries[0].ProposedName);
    }

    [Fact]
    public async Task PreviewAsync_DetectsExistingConflict()
    {
        using var fixture = new RdpImportFixture();
        await fixture.SaveServersAsync(new ServerProfileDto { Id = Guid.NewGuid().ToString(), DisplayName = "Server01", ConnectionType = "RDP", RemoteServer = "old" });
        var path = await fixture.WriteRdpAsync("Server01.rdp", "full address:s:new.example.com");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        Assert.True(preview.Entries[0].HasNameConflict);
        Assert.Equal("Server01", preview.Entries[0].ConflictingExistingName);
    }

    [Fact]
    public async Task PreviewAsync_DetectsBatchConflict()
    {
        using var fixture = new RdpImportFixture();
        var first = await fixture.WriteRdpAsync("default.rdp", "alternate full address:s:dup.example.com\nfull address:s:a.example.com");
        var second = await fixture.WriteRdpAsync("connection.rdp", "alternate full address:s:dup.example.com\nfull address:s:b.example.com");

        var preview = await fixture.Service.PreviewAsync([first, second], CancellationToken.None);

        Assert.All(preview.Entries, entry => Assert.True(entry.HasNameConflict));
    }

    [Fact]
    public async Task PreviewAsync_PasswordBlob_IsSurfacedWithoutImportingIt()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync(
            "Blob.rdp",
            """
            full address:s:blob.example.com
            password 51:b:abcdef
            """
        );

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        Assert.True(preview.Entries[0].HasPasswordBlob);
        Assert.Null(preview.Entries[0].Candidate.RdpPasswordEncrypted);
    }

    [Fact]
    public async Task PreviewAsync_InvalidAddress_YieldsParseError()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync("Broken.rdp", "username:s:demo");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        Assert.True(preview.Entries[0].HasParseError);
        Assert.False(preview.Entries[0].Candidate.RemoteServer.Length > 0);
    }

    [Fact]
    public async Task PreviewAsync_MapsKnownRdpFields()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync(
            "Mapped.rdp",
            """
            full address:s:rdp.example.com:3391
            username:s:demo
            redirectclipboard:i:0
            redirectprinters:i:1
            redirectsmartcards:i:1
            drivestoredirect:s:*
            use multimon:i:1
            session bpp:i:24
            authentication level:i:2
            enablecredsspsupport:i:1
            gatewayhostname:s:rdgw.example.com
            gatewayusagemethod:i:1
            audiomode:i:1
            """
        );

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        var candidate = preview.Entries[0].Candidate;

        Assert.Equal("demo", candidate.RdpUsername);
        Assert.False(candidate.RdpRedirectClipboard);
        Assert.True(candidate.RdpRedirectPrinters);
        Assert.True(candidate.RdpRedirectSmartCards);
        Assert.True(candidate.RdpRedirectDrives);
        Assert.True(candidate.RdpMultiMonitor);
        Assert.Equal(24, candidate.RdpColorDepth);
        Assert.True(candidate.RdpNla);
        Assert.False(candidate.RdpStrictServerAuthentication);
        Assert.Equal("rdgw.example.com", candidate.RdpGateway);
        Assert.Equal(2, candidate.RdpAudioMode);
    }

    [Fact]
    public async Task PreviewAsync_MapsAuthenticationLevel1ToStrictServerAuthentication()
    {
        using RdpImportFixture fixture = new RdpImportFixture();
        string path = await fixture.WriteRdpAsync(
            "StrictAuth.rdp",
            """
            full address:s:rdp.example.com
            authentication level:i:1
            """
        );

        RdpImportPreview preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        ServerProfileDto candidate = preview.Entries[0].Candidate;

        // The level drives strict server authentication only; NLA keeps its default because the
        // file carries no enablecredsspsupport key.
        Assert.True(candidate.RdpNla);
        Assert.True(candidate.RdpStrictServerAuthentication);
    }

    [Fact]
    public async Task PreviewAsync_KeepsNlaEnabled_WhenCredSspIsOnAndServerAuthenticationIsNotRequired()
    {
        using RdpImportFixture fixture = new RdpImportFixture();
        string path = await fixture.WriteRdpAsync(
            "NlaWithoutServerAuth.rdp",
            """
            full address:s:rdp.example.com
            authentication level:i:0
            enablecredsspsupport:i:1
            """
        );

        RdpImportPreview preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        ServerProfileDto candidate = preview.Entries[0].Candidate;

        // Round-trip oracle: deriving NLA from the authentication level turned this profile into
        // NLA-disabled on re-import, a silent weakening. NLA now comes from enablecredsspsupport.
        Assert.True(candidate.RdpNla);
        Assert.False(candidate.RdpStrictServerAuthentication);
    }

    [Fact]
    public async Task PreviewAsync_KeepsNlaDefault_WhenCredSspKeyIsAbsent()
    {
        using RdpImportFixture fixture = new RdpImportFixture();
        string path = await fixture.WriteRdpAsync(
            "NoCredSspKey.rdp",
            """
            full address:s:rdp.example.com
            authentication level:i:0
            """
        );

        RdpImportPreview preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        ServerProfileDto candidate = preview.Entries[0].Candidate;

        // An absent key must not weaken the candidate implicitly.
        Assert.True(candidate.RdpNla);
        Assert.False(candidate.RdpStrictServerAuthentication);
    }

    [Fact]
    public async Task PreviewAsync_DisablesNla_WhenCredSspSupportIsExplicitlyOff()
    {
        using RdpImportFixture fixture = new RdpImportFixture();
        string path = await fixture.WriteRdpAsync(
            "CredSspOff.rdp",
            """
            full address:s:rdp.example.com
            authentication level:i:1
            enablecredsspsupport:i:0
            """
        );

        RdpImportPreview preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        ServerProfileDto candidate = preview.Entries[0].Candidate;

        Assert.False(candidate.RdpNla);
        Assert.True(candidate.RdpStrictServerAuthentication);
    }

    [Fact]
    public async Task PreviewAsync_KeepsDefaults_WhenValuesAreOutOfContract()
    {
        using RdpImportFixture fixture = new RdpImportFixture();
        string path = await fixture.WriteRdpAsync(
            "OutOfContract.rdp",
            """
            full address:s:rdp.example.com
            authentication level:i:7
            enablecredsspsupport:i:9
            """
        );

        RdpImportPreview preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        ServerProfileDto candidate = preview.Entries[0].Candidate;

        Assert.True(candidate.RdpNla);
        Assert.False(candidate.RdpStrictServerAuthentication);
    }

    [Fact]
    public async Task ApplyAsync_ImportsSelectedEntries()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync("ImportMe.rdp", "full address:s:rdp.example.com");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        var result = await fixture.Service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = path,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.AutoRename
                    }
                ]
            },
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Single(servers);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal("ImportMe", servers[0].DisplayName);
    }

    [Fact]
    public async Task ApplyAsync_SkipConflict_DoesNotMutateInventory()
    {
        using var fixture = new RdpImportFixture();
        await fixture.SaveServersAsync(new ServerProfileDto { Id = "existing", DisplayName = "Conflict", ConnectionType = "RDP", RemoteServer = "old" });
        var path = await fixture.WriteRdpAsync("Conflict.rdp", "full address:s:new.example.com");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        var result = await fixture.Service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = path,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.Skip
                    }
                ]
            },
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Single(servers);
        Assert.Equal("old", servers[0].RemoteServer);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public async Task ApplyAsync_ReplaceConflict_KeepsExistingId()
    {
        using var fixture = new RdpImportFixture();
        await fixture.SaveServersAsync(new ServerProfileDto { Id = "keep-me", DisplayName = "ReplaceMe", ConnectionType = "RDP", RemoteServer = "old" });
        var path = await fixture.WriteRdpAsync("ReplaceMe.rdp", "full address:s:new.example.com:3390");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        var result = await fixture.Service.ApplyAsync(
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

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Single(servers);
        Assert.Equal("keep-me", servers[0].Id);
        Assert.Equal("new.example.com", servers[0].RemoteServer);
        Assert.Equal(1, result.ReplacedCount);
    }

    [Fact]
    public async Task ApplyAsync_AutoRename_AppendsDeterministicSuffix()
    {
        using var fixture = new RdpImportFixture();
        await fixture.SaveServersAsync(new ServerProfileDto { Id = "existing", DisplayName = "Server", ConnectionType = "RDP", RemoteServer = "old" });
        var path = await fixture.WriteRdpAsync("Server.rdp", "full address:s:new.example.com");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        var result = await fixture.Service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = path,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.AutoRename
                    }
                ]
            },
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Contains(servers, server => server.DisplayName == "Server (Imported 2)");
        Assert.Equal(1, result.RenamedCount);
    }

    [Fact]
    public async Task ApplyAsync_AutoRename_RechecksRunningInventory()
    {
        using var fixture = new RdpImportFixture();
        await fixture.SaveServersAsync(
            new ServerProfileDto { Id = "one", DisplayName = "Server", ConnectionType = "RDP", RemoteServer = "old" },
            new ServerProfileDto { Id = "two", DisplayName = "Server (Imported 2)", ConnectionType = "RDP", RemoteServer = "older" });
        var path = await fixture.WriteRdpAsync("Server.rdp", "full address:s:new.example.com");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        await fixture.Service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = path,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.AutoRename
                    }
                ]
            },
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Contains(servers, server => server.DisplayName == "Server (Imported 3)");
    }

    [Fact]
    public async Task ApplyAsync_ParseError_IsSkippedWithWarning()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync("Broken.rdp", "username:s:demo");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        var result = await fixture.Service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = path,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.AutoRename
                    }
                ]
            },
            CancellationToken.None);

        Assert.Equal(1, result.SkippedCount);
        Assert.NotEmpty(result.Warnings);
    }

    private sealed class RdpImportFixture : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "heimdall-b56-tests", Guid.NewGuid().ToString("N"));

        public ConfigManager ConfigManager { get; }

        public LocalizationManager Localizer { get; }

        public IRdpImportService Service { get; }

        public RdpImportFixture()
        {
            ConfigManager = new ConfigManager(RootPath);
            Localizer = CreateLocalizerAsync().GetAwaiter().GetResult();
            Service = new RdpImportService(ConfigManager, Localizer);
        }

        public async Task<string> WriteRdpAsync(string fileName, string content)
        {
            var importsDir = Path.Combine(RootPath, "imports");
            Directory.CreateDirectory(importsDir);
            var path = Path.Combine(importsDir, fileName);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public async Task SaveServersAsync(params ServerProfileDto[] servers)
        {
            await ConfigManager.SaveServersAsync([.. servers]);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static async Task<LocalizationManager> CreateLocalizerAsync()
        {
            var manager = new LocalizationManager();
            await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            return manager;
        }
    }
}
