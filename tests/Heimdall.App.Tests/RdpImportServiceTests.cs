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
using System.Reflection;
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Rdp;

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
        using var fixture = new RdpImportFixture(
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = "{0} (Imported {1})" });
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
        using var fixture = new RdpImportFixture(
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = "{0} (Imported {1})" });
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

    [Fact]
    public async Task ApplyAsync_MappedRedirection_SurvivesConnectTimeResolution()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync(
            "Locked.rdp",
            "full address:s:rdp.example.com\nredirectclipboard:i:0\nenablecredsspsupport:i:0");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        await fixture.Service.ApplyAsync(
            preview,
            SelectOne(path, RdpConflictResolution.AutoRename),
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        var settings = new AppSettings { RdpDefaultRedirectClipboard = true, RdpDefaultNla = true };

        // The resolver is the surface that decides what the session actually gets. Asserting on the
        // candidate DTO alone cannot see a profile that still follows the application defaults.
        RdpRedirectionOptions redirections = RdpProfileResolver.BuildRedirections(servers[0], settings);

        Assert.False(redirections.Clipboard);
        Assert.False(redirections.Nla);
    }

    [Fact]
    public async Task PreviewAsync_AddressOnlyFile_KeepsFollowingGlobalDefaults()
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync("AddressOnly.rdp", "full address:s:rdp.example.com");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        // A file that carries no per-profile RDP setting keeps following the application defaults;
        // clearing the flag unconditionally would freeze today's defaults into the profile.
        Assert.True(preview.Entries[0].Candidate.RdpUseGlobalDefaults);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("gatewayusagemethod:i:0", null)]
    [InlineData("gatewayusagemethod:i:1", "gw.example.com")]
    public async Task PreviewAsync_Gateway_RequiresAnExplicitNonZeroUsageMethod(
        string usageMethodLine,
        string? expectedGateway)
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync(
            "Gateway.rdp",
            $"full address:s:rdp.example.com\ngatewayhostname:s:gw.example.com\n{usageMethodLine}");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        Assert.Equal(expectedGateway, preview.Entries[0].Candidate.RdpGateway);
    }

    [Theory]
    [InlineData("[2001:db8::1]:-1", DefaultPorts.Rdp, true)]
    [InlineData("[2001:db8::1]:0", DefaultPorts.Rdp, true)]
    [InlineData("[2001:db8::1]:70000", DefaultPorts.Rdp, true)]
    [InlineData("[2001:db8::1]:3390", 3390, false)]
    [InlineData("[2001:db8::1]", DefaultPorts.Rdp, false)]
    public async Task PreviewAsync_BracketedAddress_AppliesThePortRangeContract(
        string address,
        int expectedPort,
        bool expectSkippedMapping)
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync("Bracketed.rdp", $"full address:s:{address}");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        var entry = preview.Entries[0];

        Assert.Equal("[2001:db8::1]", entry.Candidate.RemoteServer);
        Assert.Equal(expectedPort, entry.Candidate.RemotePort);
        Assert.Equal(expectSkippedMapping, entry.SkippedMappings.Contains("full address port"));
    }

    [Theory]
    [InlineData("C:;", true)]
    [InlineData("*", false)]
    public async Task PreviewAsync_ScopedDriveRedirection_IsReportedAsALossyMapping(
        string drivesToRedirect,
        bool expectSkippedMapping)
    {
        using var fixture = new RdpImportFixture();
        var path = await fixture.WriteRdpAsync(
            "Drives.rdp",
            $"full address:s:rdp.example.com\ndrivestoredirect:s:{drivesToRedirect}");

        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);
        var entry = preview.Entries[0];

        Assert.True(entry.Candidate.RdpRedirectDrives);
        Assert.Equal(expectSkippedMapping, entry.SkippedMappings.Contains("drivestoredirect"));
    }

    [Fact]
    public async Task ApplyAsync_ReplaceConflict_KeepsTheNetworkPathAndCredentialLinkage()
    {
        using var fixture = new RdpImportFixture();
        await fixture.SaveServersAsync(new ServerProfileDto
        {
            Id = "keep-me",
            DisplayName = "Server01",
            ConnectionType = "RDP",
            RemoteServer = "10.0.0.5",
            SshGatewayId = "bastion-1",
            LocalPort = 13389,
            VaultEntryName = "Server01 (renamed)",
            UseDirectConnection = false
        });
        var path = await fixture.WriteRdpAsync("Server01.rdp", "full address:s:10.0.0.5");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        await fixture.Service.ApplyAsync(
            preview,
            SelectOne(path, RdpConflictResolution.Replace),
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        var replaced = Assert.Single(servers);

        // A .rdp file carries none of these, so Replace must not decide them.
        Assert.Equal("bastion-1", replaced.SshGatewayId);
        Assert.Equal(13389, replaced.LocalPort);
        Assert.Equal("Server01 (renamed)", replaced.VaultEntryName);
        Assert.False(replaced.UseDirectConnection);
    }

    [Fact]
    public async Task ApplyAsync_LateConflictOnAConflictFreeRow_RenamesInsteadOfSkippingSilently()
    {
        using var fixture = new RdpImportFixture(
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = "{0} (Imported {1})" });
        await fixture.SaveServersAsync(
            new ServerProfileDto { Id = "existing", DisplayName = "Server", ConnectionType = "RDP", RemoteServer = "old" });
        var first = await fixture.WriteRdpAsync("Server.rdp", "full address:s:a.example.com");
        var second = await fixture.WriteRdpAsync("Server (Imported 2).rdp", "full address:s:b.example.com");
        var preview = await fixture.Service.PreviewAsync([first, second], CancellationToken.None);

        // Exactly what the dialog submits: the conflicting row defaults to AutoRename, the
        // conflict-free row keeps the Skip default that was never displayed for it.
        Assert.True(preview.Entries[0].HasNameConflict);
        Assert.False(preview.Entries[1].HasNameConflict);

        var result = await fixture.Service.ApplyAsync(
            preview,
            new RdpImportSelection
            {
                Entries =
                [
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = first,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.AutoRename
                    },
                    new RdpImportSelectionEntry
                    {
                        SourceFilePath = second,
                        IsSelected = true,
                        ConflictResolution = RdpConflictResolution.Skip
                    }
                ]
            },
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();

        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(2, result.ImportedCount);
        Assert.Contains(servers, server => server.RemoteServer == "b.example.com");
    }

    [Fact]
    public async Task PreviewAsync_OversizedFile_IsRejectedWithoutLosingTheRestOfTheBatch()
    {
        using var fixture = new RdpImportFixture();
        var valid = await fixture.WriteRdpAsync("Valid.rdp", "full address:s:rdp.example.com");
        var oversized = fixture.WriteOversizedRdp("Huge.rdp");

        var preview = await fixture.Service.PreviewAsync([valid, oversized], CancellationToken.None);

        Assert.Single(preview.Entries);
        Assert.Equal("Valid", preview.Entries[0].ProposedName);
        Assert.Contains(oversized, preview.FilesUnreadable);
    }

    [Fact]
    public async Task ApplyAsync_AutoRename_TakesTheSuffixFromTheActiveLocale()
    {
        using var fixture = new RdpImportFixture(
            locale: "fr",
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = "{0} (Importe {1})" });
        await fixture.SaveServersAsync(
            new ServerProfileDto { Id = "existing", DisplayName = "Server", ConnectionType = "RDP", RemoteServer = "old" });
        var path = await fixture.WriteRdpAsync("Server.rdp", "full address:s:new.example.com");
        var preview = await fixture.Service.PreviewAsync([path], CancellationToken.None);

        await fixture.Service.ApplyAsync(
            preview,
            SelectOne(path, RdpConflictResolution.AutoRename),
            CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();

        Assert.Contains(servers, server => server.DisplayName == "Server (Importe 2)");
        Assert.DoesNotContain(servers, server => server.DisplayName.Contains("(Imported", StringComparison.Ordinal));
    }

    /// <summary>
    /// The .rdp import and the profile import reach the same display-name conflict from two
    /// entry points and have to print the same suffix for it. Both used to carry a verbatim copy
    /// of the rule - the locale key, the neutral fallback template, the missing-placeholder guard
    /// and the first suffix - so either copy could be edited alone and the two surfaces would
    /// disagree with nothing failing. The rule is one shared helper now; this fails if a caller
    /// reintroduces its own.
    /// </summary>
    [Theory]
    [InlineData(typeof(RdpImportService))]
    [InlineData(typeof(ProfileImportService))]
    public void ImportServices_DoNotCarryTheirOwnCopyOfTheAutoRenameRule(Type importService)
    {
        string[] ownedMembers = AutoRenameRuleMembers(importService);

        Assert.True(
            ownedMembers.Length == 0,
                importService.Name + " declares its own copy of the auto-rename rule ("
                + string.Join(", ", ownedMembers)
                + "). Both import paths must resolve the same conflict to the same name, so the "
                + "rule belongs to ImportAutoRename alone.");
    }

    // Positive control: the member names above are the ones the rule is actually made of, so an
    // empty result on an import service is a measurement and not a mistyped lookup.
    [Fact]
    public void TheSharedHelperIsTheOneCarryingTheAutoRenameRule()
    {
        string[] sharedMembers = AutoRenameRuleMembers(typeof(ImportAutoRename));

        Assert.Contains("NeutralRenameTemplate", sharedMembers);
        Assert.Contains("FirstAutoRenameSuffix", sharedMembers);
        Assert.Contains("RenameSuffixKey", sharedMembers);
    }

    [Fact]
    public void AutoRename_WithoutTheLocaleKey_FallsBackToTheNeutralTemplate()
    {
        using var fixture = new RdpImportFixture(
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = string.Empty });

        Assert.Equal("Server (2)", ImportAutoRename.Build("Server", [], fixture.Localizer));
    }

    [Fact]
    public void AutoRename_WithATemplateThatDropsTheNumber_FallsBackToTheNeutralTemplate()
    {
        using var fixture = new RdpImportFixture(
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = "{0} (Imported)" });

        // A template with no {1} never varies, so the collision walk below could not terminate.
        Assert.Equal(
            "Server (3)",
            ImportAutoRename.Build(
                "Server",
                [new ServerProfileDto { Id = "a", DisplayName = "server (2)" }],
                fixture.Localizer));
    }

    [Fact]
    public void AutoRename_WalksPastNamesTheInventoryAlreadyCarries()
    {
        using var fixture = new RdpImportFixture(
            localeOverrides: new Dictionary<string, string> { ["DialogImportRdpRenameSuffix"] = "{0} (Imported {1})" });

        Assert.Equal(
            "Server (Imported 4)",
            ImportAutoRename.Build(
                "Server",
                [
                    new ServerProfileDto { Id = "a", DisplayName = "Server (Imported 2)" },
                    new ServerProfileDto { Id = "b", DisplayName = "SERVER (IMPORTED 3)" }
                ],
                fixture.Localizer));
    }

    private static string[] AutoRenameRuleMembers(Type type)
        => type
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .Where(name => name is "BuildAutoRename"
                or "NeutralRenameTemplate"
                or "FirstAutoRenameSuffix"
                or "RenameSuffixKey")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static RdpImportSelection SelectOne(string path, RdpConflictResolution resolution) =>
        new()
        {
            Entries =
            [
                new RdpImportSelectionEntry
                {
                    SourceFilePath = path,
                    IsSelected = true,
                    ConflictResolution = resolution
                }
            ]
        };

    private sealed class RdpImportFixture : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "heimdall-b56-tests", Guid.NewGuid().ToString("N"));

        public ConfigManager ConfigManager { get; }

        public LocalizationManager Localizer { get; }

        public IRdpImportService Service { get; }

        public RdpImportFixture(
            string locale = "en",
            IReadOnlyDictionary<string, string>? localeOverrides = null)
        {
            ConfigManager = new ConfigManager(RootPath);
            Localizer = CreateLocalizerAsync(RootPath, locale, localeOverrides).GetAwaiter().GetResult();
            Service = new RdpImportService(ConfigManager, Localizer);
        }

        public async Task<string> WriteRdpAsync(string fileName, string content)
        {
            var path = Path.Combine(EnsureImportsDirectory(), fileName);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        /// <summary>
        /// Creates a .rdp file one byte above the import size cap without writing its content:
        /// the length is what the guard reads.
        /// </summary>
        public string WriteOversizedRdp(string fileName)
        {
            var path = Path.Combine(EnsureImportsDirectory(), fileName);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.SetLength(AppConstants.MaxImportFileSizeBytes + 1);
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

        private string EnsureImportsDirectory()
        {
            var importsDir = Path.Combine(RootPath, "imports");
            Directory.CreateDirectory(importsDir);
            return importsDir;
        }

        private static async Task<LocalizationManager> CreateLocalizerAsync(
            string rootPath,
            string locale,
            IReadOnlyDictionary<string, string>? localeOverrides)
        {
            var manager = new LocalizationManager();
            var shippedLocalesPath = Path.Combine(AppContext.BaseDirectory, "locales");

            if (localeOverrides is null || localeOverrides.Count == 0)
            {
                await manager.LoadAsync(shippedLocalesPath, locale);
                return manager;
            }

            // Keys a fix introduces reach locales/*.json through the release pipeline. Writing them
            // into a private copy of the locale file keeps the assertion on the code path instead of
            // on the merge state of the shared locale files.
            var localesPath = Path.Combine(rootPath, "locales");
            Directory.CreateDirectory(localesPath);
            var shipped = JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(Path.Combine(shippedLocalesPath, $"{locale}.json")))
                ?? [];

            foreach (var pair in localeOverrides)
            {
                shipped[pair.Key] = pair.Value;
            }

            await File.WriteAllTextAsync(
                Path.Combine(localesPath, $"{locale}.json"),
                JsonSerializer.Serialize(shipped));
            await manager.LoadAsync(localesPath, locale);
            return manager;
        }
    }
}
