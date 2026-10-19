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

using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public class MobaXtermImporterTests
{
    private const string ResolvedLogin = "jdoe";

    // Real session lines put the ICON first and the TYPE second: 126/117/130/140 are icons, 0 is
    // SSH. The older fixtures here had 109/91/140/130/128/98 standing for the protocol.
    private const string SampleIni = """
        [Bookmarks]
        SubRep=Production
        ImgNum=42
        WebServer= #126#0%web01.example.com%22%admin%%0%-1%%%%%-1%0%0%%%-1%0%0%0%%1080%%0%0%1
        DbServer= #117#0%db01.example.com%2222%root%%0%-1%%%%%-1%0%0%%%-1%0%0%0%%1080%%0%0%1
        WinBox= #91#4%win01.example.com%3389%administrator%
        [Bookmarks_1]
        SubRep=Staging
        ImgNum=41
        StagingShell= #140#0%staging.example.com%22%deploy%
        StagingBox= #130#0%box.example.com%2022%ops%
        """;

    private static MobaXtermImportResult ParseFixture(Func<string, string?>? resolver = null) =>
        MobaXtermImporter.Parse(
            MobaXtermSharedSessionsFixture.Content,
            new MobaXtermImportOptions { VariableResolver = resolver });

    private static string? ResolveDas(string name) =>
        string.Equals(name, "DAS", StringComparison.Ordinal) ? ResolvedLogin : null;

    // --- Shared session file (anonymized real structure) ---

    [Fact]
    public void Fixture_EverySessionIsImported_NoneSkipped()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.Equal(MobaXtermSharedSessionsFixture.SessionCount, result.Servers.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Fixture_CustomIcons_DoNotDecideTheSessionType()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.All(result.Servers, server => Assert.Equal("SSH", server.ConnectionType));
        Assert.All(result.Servers, server => Assert.Equal("Embedded", server.SshMode));
    }

    [Fact]
    public void Fixture_SessionNameDifferentFromHost_KeepsBoth()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        ServerProfileDto server = Assert.Single(
            result.Servers,
            s => s.DisplayName == MobaXtermSharedSessionsFixture.RenamedSessionName);
        Assert.Equal(MobaXtermSharedSessionsFixture.RenamedSessionHost, server.RemoteServer);
    }

    [Fact]
    public void Fixture_NestedFolders_MapToSlashGroups()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.Equal(
            new string?[] { "Old/PROD", "PROD", "PROD/Proxies", "Tools" },
            result.Servers.Select(s => s.Group).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("srv-db01-old", Assert.Single(result.Servers, s => s.Group == "Old/PROD" && s.DisplayName == "srv-db01").RemoteServer);
    }

    [Fact]
    public void Fixture_Gateways_DeduplicatedAndReferenced()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.Equal(2, result.Gateways.Count);
        SshGatewayDto first = Assert.Single(result.Gateways, g => g.Host == MobaXtermSharedSessionsFixture.FirstGatewayHost);
        SshGatewayDto second = Assert.Single(result.Gateways, g => g.Host == MobaXtermSharedSessionsFixture.SecondGatewayHost);
        Assert.Equal(MobaXtermSharedSessionsFixture.FirstGatewayPort, first.Port);
        Assert.Equal(MobaXtermSharedSessionsFixture.SecondGatewayPort, second.Port);
        Assert.Equal(ResolvedLogin, first.User);
        Assert.Equal(ResolvedLogin, second.User);

        Assert.Equal(
            MobaXtermSharedSessionsFixture.FirstGatewaySessionCount,
            result.Servers.Count(s => s.SshGatewayId == first.Id));
        Assert.Equal(
            MobaXtermSharedSessionsFixture.SecondGatewaySessionCount,
            result.Servers.Count(s => s.SshGatewayId == second.Id));
        Assert.All(
            result.Servers.Where(s => s.SshGatewayId is not null),
            s => Assert.False(s.UseDirectConnection));
    }

    [Fact]
    public void Fixture_EmptyGatewayHost_IsADirectConnection()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        ServerProfileDto direct = Assert.Single(
            result.Servers,
            s => s.DisplayName == MobaXtermSharedSessionsFixture.DirectSessionName);
        Assert.Null(direct.SshGatewayId);
        Assert.True(direct.UseDirectConnection);
    }

    [Fact]
    public void Fixture_NoKeyPathIsImported()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.All(result.Servers, s => Assert.Null(s.SshKeyPath));
        Assert.All(result.Gateways, g => Assert.Null(g.KeyPath));
    }

    [Fact]
    public void Fixture_VariableResolved_OnSessionAndGatewayUsers()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.All(result.Servers, s => Assert.Equal(ResolvedLogin, s.SshUsername));
        Assert.DoesNotContain(result.Warnings, w => w.Kind == MobaXtermImportWarningKind.UnresolvedVariable);
    }

    [Fact]
    public void Fixture_VariableUnresolved_KeptAsWrittenAndWarnedPerSession()
    {
        MobaXtermImportResult result = ParseFixture(resolver: null);

        Assert.Equal(MobaXtermSharedSessionsFixture.SessionCount, result.Servers.Count);
        Assert.All(result.Servers, s => Assert.Equal("[DAS]", s.SshUsername));
        Assert.All(result.Gateways, g => Assert.Equal("[DAS]", g.User));
        Assert.Equal(MobaXtermSharedSessionsFixture.SessionCount, result.Warnings.Count);
        Assert.All(result.Warnings, warning =>
        {
            Assert.Equal(MobaXtermImportWarningKind.UnresolvedVariable, warning.Kind);
            Assert.Equal("DAS", warning.Detail);
            Assert.True(warning.SessionImported);
        });
    }

    [Fact]
    public void Fixture_LoginCommand_BecomesOnePostConnectStep_Unconfirmed()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);
        var defaults = new PostConnectStep();

        ServerProfileDto server = Assert.Single(
            result.Servers,
            s => s.Group == "PROD" && s.DisplayName == "srv-db01");
        PostConnectStep step = Assert.Single(server.PostConnectSteps);
        Assert.Equal("sudo su - www", step.Input);
        Assert.Equal(defaults.DelayMs, step.DelayMs);
        Assert.Equal(defaults.Enabled, step.Enabled);
        Assert.Equal(defaults.OnFailure, step.OnFailure);
        Assert.Null(step.CommandLibraryId);
        Assert.Equal(string.Empty, server.PostConnectCommand);
        Assert.False(server.ExecutionConfirmed);
        Assert.True(ProfileExecutionTrust.RequiresPostConnectConfirmation(server));

        ServerProfileDto appServer = Assert.Single(result.Servers, s => s.DisplayName == "srv-app01");
        Assert.Equal("sudo su - app_prod", Assert.Single(appServer.PostConnectSteps).Input);
    }

    [Fact]
    public void Fixture_EmptyLoginCommand_AddsNoStep()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        ServerProfileDto server = Assert.Single(result.Servers, s => s.DisplayName == "srv-hap01");
        Assert.Empty(server.PostConnectSteps);
        Assert.False(ProfileExecutionTrust.RequiresPostConnectConfirmation(server));
    }

    [Fact]
    public void Fixture_Comment_MapsToCommaSeparatedTags()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.Equal("ssl-front-x, email-x", Assert.Single(result.Servers, s => s.DisplayName == MobaXtermSharedSessionsFixture.RenamedSessionName).Tags);
        Assert.Equal("database, legacy", Assert.Single(result.Servers, s => s.DisplayName == "srv-app01").Tags);
        Assert.Equal("ha-proxy", Assert.Single(result.Servers, s => s.DisplayName == "srv-hap01").Tags);
        Assert.Null(Assert.Single(result.Servers, s => s.Group == "Old/PROD" && s.DisplayName == "srv-db01").Tags);
    }

    [Fact]
    public void Fixture_IdsAreStableAcrossTwoParses()
    {
        MobaXtermImportResult first = ParseFixture(ResolveDas);
        MobaXtermImportResult second = ParseFixture(ResolveDas);

        Assert.Equal(first.Servers.Select(s => s.Id), second.Servers.Select(s => s.Id));
        Assert.Equal(first.Gateways.Select(g => g.Id), second.Gateways.Select(g => g.Id));
        Assert.Equal(first.Servers.Select(s => s.SshGatewayId), second.Servers.Select(s => s.SshGatewayId));
    }

    [Fact]
    public void Fixture_IdsAreUniqueAndCarryTheCallerPrefix()
    {
        const string prefix = "shared-test-";
        MobaXtermImportResult result = MobaXtermImporter.Parse(
            MobaXtermSharedSessionsFixture.Content,
            new MobaXtermImportOptions { IdPrefix = prefix, VariableResolver = ResolveDas });

        List<string> ids = [.. result.Servers.Select(s => s.Id), .. result.Gateways.Select(g => g.Id)];
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.StartsWith(prefix, id, StringComparison.Ordinal));
    }

    [Fact]
    public void Fixture_SameSessionNameInTwoFolders_GetsTwoIds()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        List<ServerProfileDto> namesakes = result.Servers.Where(s => s.DisplayName == "srv-db01").ToList();
        Assert.Equal(2, namesakes.Count);
        Assert.NotEqual(namesakes[0].Id, namesakes[1].Id);
    }

    [Fact]
    public void Fixture_GatewayId_DependsOnTheResolvedUser()
    {
        MobaXtermImportResult resolved = ParseFixture(ResolveDas);
        MobaXtermImportResult unresolved = ParseFixture(resolver: null);

        Assert.NotEqual(
            resolved.Gateways.Select(g => g.Id).Order(StringComparer.Ordinal),
            unresolved.Gateways.Select(g => g.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Fixture_SetsOriginToImportMobaXterm()
    {
        MobaXtermImportResult result = ParseFixture(ResolveDas);

        Assert.All(result.Servers, s => Assert.Equal(ProfileOrigin.ImportMobaXterm, s.Origin));
    }

    // --- Session type and warnings ---

    [Fact]
    public void Parse_UnsupportedSessionType_WarnsWithSectionNameAndCode()
    {
        const string ini = """
            [Bookmarks_7]
            SubRep=Lab
            ImgNum=42
            BrowserSession= #126#999%host.example.com%8080%user%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Empty(result.Servers);
        MobaXtermImportWarning warning = Assert.Single(result.Warnings);
        Assert.Equal("Bookmarks_7", warning.Section);
        Assert.Equal("BrowserSession", warning.SessionName);
        Assert.Equal(MobaXtermImportWarningKind.UnsupportedSessionType, warning.Kind);
        Assert.Equal("999", warning.Detail);
        Assert.False(warning.SessionImported);
    }

    [Fact]
    public void Parse_WslSession_SkippedWithWarning()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            WSL-Dev-box=#151#14%Dev-box-daemon%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Empty(result.Servers);
        MobaXtermImportWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(MobaXtermImportWarningKind.UnsupportedSessionType, warning.Kind);
        Assert.Equal("14", warning.Detail);
    }

    [Fact]
    public void Parse_SessionWithoutHost_SkippedWithWarning()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            NoHost= #109#0%%22%user%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Empty(result.Servers);
        Assert.Equal(MobaXtermImportWarningKind.MissingHost, Assert.Single(result.Warnings).Kind);
    }

    [Fact]
    public void Parse_MalformedLine_SkippedWithWarning()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            NotASession=just some text
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Empty(result.Servers);
        MobaXtermImportWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(MobaXtermImportWarningKind.MalformedSessionLine, warning.Kind);
        Assert.Equal("NotASession", warning.SessionName);
    }

    [Fact]
    public void Parse_DuplicateNameInSameFolder_SecondSkippedWithWarning()
    {
        const string ini = """
            [Bookmarks_1]
            SubRep=PROD
            ImgNum=42
            srv-db01=#126#0%first.example.com%22%user%
            [Bookmarks_2]
            SubRep=PROD
            ImgNum=42
            srv-db01=#126#0%second.example.com%22%user%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Equal("first.example.com", Assert.Single(result.Servers).RemoteServer);
        MobaXtermImportWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(MobaXtermImportWarningKind.DuplicateSession, warning.Kind);
        Assert.Equal("Bookmarks_2", warning.Section);
    }

    [Fact]
    public void Parse_UnusableGatewayHost_SkippedRatherThanConnectedDirect()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            srv-x=#126#0%srv-x.example.com%22%user%%0%-1%%bad host!%22%user%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Gateways);
        MobaXtermImportWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(MobaXtermImportWarningKind.InvalidGatewayHost, warning.Kind);
        Assert.Equal("bad host!", warning.Detail);
    }

    [Fact]
    public void Parse_GatewayPortEmpty_DefaultsToSsh()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            srv-x=#126#0%srv-x.example.com%22%user%%0%-1%%gw.example.test%%gwuser%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        SshGatewayDto gateway = Assert.Single(result.Gateways);
        Assert.Equal(DefaultPorts.Ssh, gateway.Port);
        Assert.Equal("gwuser", gateway.User);
        Assert.Equal("gwuser@gw.example.test", gateway.Name);
    }

    [Fact]
    public void Parse_GatewayNonDefaultPort_InGatewayName()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            srv-x=#126#0%srv-x.example.com%22%user%%0%-1%%gw.example.test%2222%gwuser%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Equal("gwuser@gw.example.test:2222", Assert.Single(result.Gateways).Name);
    }

    [Fact]
    public void Parse_UnresolvedVariableInsideALogin_KeptAndWarnedOnce()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            srv-x=#126#0%srv-x.example.com%22%adm_[DAS]%%0%-1%%gw.example.test%22%[DAS]%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Equal("adm_[DAS]", Assert.Single(result.Servers).SshUsername);
        Assert.Equal(MobaXtermImportWarningKind.UnresolvedVariable, Assert.Single(result.Warnings).Kind);
    }

    [Fact]
    public void Parse_VariableInsideALogin_ResolvedInPlace()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            srv-x=#126#0%srv-x.example.com%22%adm_[DAS]%
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(
            ini,
            new MobaXtermImportOptions { VariableResolver = ResolveDas });

        Assert.Equal("adm_" + ResolvedLogin, Assert.Single(result.Servers).SshUsername);
        Assert.Empty(result.Warnings);
    }

    // --- Basic SSH and RDP mapping ---

    [Fact]
    public void Parse_SshSession_ExtractsHostPortUser()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        var webServer = result.Servers.First(s => s.DisplayName == "WebServer");
        Assert.Equal("web01.example.com", webServer.RemoteServer);
        Assert.Equal(22, webServer.SshPort);
        Assert.Equal("admin", webServer.SshUsername);
        Assert.Equal("SSH", webServer.ConnectionType);
        Assert.Equal("Production", webServer.Group);
    }

    [Fact]
    public void Parse_SshSession_CustomPort()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        var dbServer = result.Servers.First(s => s.DisplayName == "DbServer");
        Assert.Equal("db01.example.com", dbServer.RemoteServer);
        Assert.Equal(2222, dbServer.SshPort);
        Assert.Equal("root", dbServer.SshUsername);
    }

    [Fact]
    public void Parse_RdpSession_TypeFour_ExtractsFields()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        var winBox = result.Servers.First(s => s.DisplayName == "WinBox");
        Assert.Equal("win01.example.com", winBox.RemoteServer);
        Assert.Equal(3389, winBox.RemotePort);
        Assert.Equal("administrator", winBox.RdpUsername);
        Assert.Equal("RDP", winBox.ConnectionType);
        Assert.Equal("Production", winBox.Group);
        Assert.Equal("Embedded", winBox.RdpMode);
    }

    [Fact]
    public void Parse_IconCodesOfOtherProtocols_WithTypeZero_AreSsh()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        Assert.Equal("SSH", result.Servers.First(s => s.DisplayName == "StagingShell").ConnectionType);
        Assert.Equal("SSH", result.Servers.First(s => s.DisplayName == "StagingBox").ConnectionType);
        Assert.Equal(2022, result.Servers.First(s => s.DisplayName == "StagingBox").SshPort);
    }

    [Fact]
    public void Parse_MultipleSections_ReturnsAllServers()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        Assert.Equal(5, result.Servers.Count);
        Assert.Equal(3, result.Servers.Count(s => s.Group == "Production"));
        Assert.Equal(2, result.Servers.Count(s => s.Group == "Staging"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_WithCredentialSections_CountsStoredCredentials()
    {
        const string ini = """
            [Bookmarks]
            SubRep=Production
            ImgNum=42
            WebServer= #109#0%web01.example.com%22%admin%
            [Passwords]
            web01.example.com=encrypted-password-1
            db01.example.com=encrypted-password-2
            [Credentials]
            production-admin=encrypted-credential-1
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(ini);

        Assert.Equal(3, result.StoredCredentialCount);
        ServerProfileDto server = Assert.Single(result.Servers);
        Assert.Equal("WebServer", server.DisplayName);
    }

    [Fact]
    public void Parse_WithoutCredentialSections_StoredCredentialCountIsZero()
    {
        MobaXtermImportResult result = MobaXtermImporter.Parse(SampleIni);

        Assert.Equal(0, result.StoredCredentialCount);
    }

    [Fact]
    public void Parse_AssignsUniqueIds()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        var ids = result.Servers.Select(s => s.Id).ToHashSet();
        Assert.Equal(result.Servers.Count, ids.Count);
        Assert.All(result.Servers, s => Assert.StartsWith(MobaXtermImporter.DefaultIdPrefix, s.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyResult()
    {
        var result = MobaXtermImporter.Parse(string.Empty);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Gateways);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_NullContent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MobaXtermImporter.Parse(null!));
    }

    [Fact]
    public void Parse_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MobaXtermImporter.Parse(string.Empty, null!));
    }

    [Fact]
    public void Parse_NonBookmarkSections_AreIgnored()
    {
        var ini = """
            [Misc]
            SomeKey=SomeValue
            [Bookmarks]
            SubRep=
            ImgNum=42
            Server1= #109#0%host.example.com%22%user%
            [SharedSettings]
            AllowedEditors=editor-a
            """;

        var result = MobaXtermImporter.Parse(ini);

        Assert.Single(result.Servers);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_SshKeyPathField_IsNeverImported()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            LocalKey= #109#0%host.example.com%22%user%%C:\Users\me\.ssh\id_rsa
            UncKey= #109#0%host2.example.com%22%user%%\\fileserver\share\id_rsa
            """;

        var result = MobaXtermImporter.Parse(ini);

        Assert.Equal(2, result.Servers.Count);
        Assert.All(result.Servers, s => Assert.Null(s.SshKeyPath));
    }

    [Fact]
    public void Parse_SshKeyPathWithTraversal_Rejected()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            EvilKey= #109#0%host.example.com%22%user%%..\..\etc\passwd
            """;

        var result = MobaXtermImporter.Parse(ini);

        var server = result.Servers.Single();
        Assert.Null(server.SshKeyPath);
    }

    [Fact]
    public void Parse_SshDefaultPort_WhenPortEmpty()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            Server1= #109#0%host.example.com%%user%
            """;

        var result = MobaXtermImporter.Parse(ini);

        var server = result.Servers.Single();
        Assert.Equal(22, server.SshPort);
    }

    [Fact]
    public void Parse_EmptyFolder_SetsGroupToNull()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            Server1= #109#0%host.example.com%22%user%
            """;

        var result = MobaXtermImporter.Parse(ini);

        Assert.Null(result.Servers.Single().Group);
    }

    [Fact]
    public void Parse_NoGatewayFields_IsADirectConnection()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        Assert.All(result.Servers, s => Assert.True(s.UseDirectConnection));
        Assert.All(result.Servers, s => Assert.Null(s.SshGatewayId));
        Assert.Empty(result.Gateways);
    }

    [Fact]
    public void Parse_SshEmbeddedMode_SetByDefault()
    {
        var result = MobaXtermImporter.Parse(SampleIni);

        var sshServers = result.Servers.Where(s => s.ConnectionType == "SSH");
        Assert.All(sshServers, s => Assert.Equal("Embedded", s.SshMode));
    }

    // --- Session line reader ---

    [Fact]
    public void TryReadSessionLine_SplitsIconTypeFieldsAndComment()
    {
        Assert.True(MobaXtermImporter.TryReadSessionLine(
            "#126#0%host%22%user#MobaFont%10#0#a, b",
            out MobaXtermImporter.SessionLine line));

        Assert.Equal("126", line.Icon);
        Assert.Equal("0", line.TypeText);
        Assert.Equal(new[] { "host", "22", "user" }, line.Fields);
        Assert.Equal("a, b", line.Comment);
    }

    [Fact]
    public void TryReadSessionLine_ShortLine_HasNoComment()
    {
        Assert.True(MobaXtermImporter.TryReadSessionLine(" #109#0%host%22%user%", out MobaXtermImporter.SessionLine line));

        Assert.Equal("0", line.TypeText);
        Assert.Null(line.Comment);
    }

    [Fact]
    public void TryReadSessionLine_InvalidInput_ReturnsFalse()
    {
        Assert.False(MobaXtermImporter.TryReadSessionLine("no hash here", out _));
        Assert.False(MobaXtermImporter.TryReadSessionLine(string.Empty, out _));
        Assert.False(MobaXtermImporter.TryReadSessionLine("#126", out _));
        Assert.False(MobaXtermImporter.TryReadSessionLine("#126#%host", out _));
    }

    // --- Security: path traversal ---

    [Fact]
    public void SanitizeGroupName_PathTraversal_Stripped()
    {
        Assert.Equal("Startup", MobaXtermImporter.SanitizeGroupName(@"..\..\..\Startup"));
        Assert.Equal("Startup", MobaXtermImporter.SanitizeGroupName("../../../Startup"));
    }

    [Fact]
    public void SanitizeGroupName_BackslashHierarchy_ConvertedToSlash()
    {
        Assert.Equal("INFRA/A_GATEWAYS", MobaXtermImporter.SanitizeGroupName(@"INFRA\A_GATEWAYS"));
        Assert.Equal("Parent/Child/Leaf", MobaXtermImporter.SanitizeGroupName(@"Parent\Child\Leaf"));
    }

    [Fact]
    public void SanitizeGroupName_ForwardSlashHierarchy_Preserved()
    {
        Assert.Equal("PROD/Linux/Web", MobaXtermImporter.SanitizeGroupName("PROD/Linux/Web"));
    }

    [Fact]
    public void SanitizeGroupName_NormalName_Unchanged()
    {
        Assert.Equal("Production", MobaXtermImporter.SanitizeGroupName("Production"));
        Assert.Equal("My Servers", MobaXtermImporter.SanitizeGroupName("My Servers"));
    }

    [Fact]
    public void SanitizeGroupName_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MobaXtermImporter.SanitizeGroupName(""));
        Assert.Equal(string.Empty, MobaXtermImporter.SanitizeGroupName("   "));
    }

    // --- Edge cases: INI comments ---

    [Fact]
    public void Parse_IniComments_AreIgnored()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ; This is a comment
            ImgNum=42
            Server1= #109#0%host.example.com%22%user%
            """;

        var result = MobaXtermImporter.Parse(ini);

        Assert.Single(result.Servers);
    }

    // --- Edge cases: special characters in session names ---

    [Fact]
    public void Parse_SpecialCharsInSessionName_Preserved()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            Server [Prod] (main)= #109#0%host.example.com%22%user%
            """;

        var result = MobaXtermImporter.Parse(ini);

        Assert.Single(result.Servers);
        Assert.Equal("Server [Prod] (main)", result.Servers[0].DisplayName);
    }

    // --- Edge cases: truncated field data ---

    [Fact]
    public void Parse_TruncatedFields_HostOnly()
    {
        var ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            Minimal= #109#0%host.example.com
            """;

        var result = MobaXtermImporter.Parse(ini);

        var server = result.Servers.Single();
        Assert.Equal("host.example.com", server.RemoteServer);
        Assert.Equal(22, server.SshPort);
        Assert.Null(server.SshUsername);
    }

    // --- Edge cases: duplicate section names ---

    [Fact]
    public void Parse_DuplicateSectionNames_BothKept()
    {
        var ini = """
            [Bookmarks]
            SubRep=First
            ImgNum=42
            Server1= #109#0%first.example.com%22%user%
            [Bookmarks]
            SubRep=Second
            ImgNum=42
            Server2= #109#0%second.example.com%22%user%
            """;

        var result = MobaXtermImporter.Parse(ini);

        // A repeated section header used to overwrite the first section and drop its sessions.
        Assert.Equal(
            new[] { "first.example.com", "second.example.com" },
            result.Servers.Select(s => s.RemoteServer));
        Assert.Empty(result.Warnings);
    }

    // --- Security: group path traversal is sanitized ---

    [Fact]
    public void Parse_GroupPathTraversal_Sanitized()
    {
        var ini = """
            [Bookmarks]
            SubRep=..\..\Startup
            ImgNum=42
            Server1= #109#0%host.example.com%22%user%
            """;

        var result = MobaXtermImporter.Parse(ini);

        var server = result.Servers.Single();
        Assert.DoesNotContain("..", server.Group);
    }

    // --- Realistic MobaXterm .mobaconf format (anonymised) ---

    [Fact]
    public void Parse_RealMobaconfFormat_SshWithGateway()
    {
        var ini = """
            [Bookmarks_2]
            SubRep=INFRA\A_GATEWAYS
            ImgNum=41
            gw01  (GW)=#109#0%gw01%22%root%%-1%-1%%bastion.example.internal%22%emp001%0%0%0%%%-1%0%0%0%%1080%%0%0%1%%0%%%%0%-1%-1%0%%#MobaFont%10%0%0%-1%15%248,248,242%40,42,54%153,153,153%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1%-1%#0# #-1
            """;

        var result = MobaXtermImporter.Parse(ini);

        var server = result.Servers.Single();
        Assert.Equal("gw01", server.RemoteServer);
        Assert.Equal(22, server.SshPort);
        Assert.Equal("root", server.SshUsername);
        Assert.Equal("SSH", server.ConnectionType);
        Assert.Equal("INFRA/A_GATEWAYS", server.Group);
        Assert.Null(server.Tags);

        SshGatewayDto gateway = Assert.Single(result.Gateways);
        Assert.Equal("bastion.example.internal", gateway.Host);
        Assert.Equal(22, gateway.Port);
        Assert.Equal("emp001", gateway.User);
        Assert.Equal(gateway.Id, server.SshGatewayId);
        Assert.False(server.UseDirectConnection);
    }

    [Fact]
    public void Parse_RealMobaconfFormat_RdpWithDomainUser()
    {
        var ini = """
            [Bookmarks_3]
            SubRep=INFRA\B_SERVICES
            ImgNum=41
            srv-rdp01 (WSUS)=#91#4%srv-rdp01%3389%Administrator@admin.example.internal%0%-1%-1%-1%-1%0%0%-1%%bastion.example.internal%22%emp001%0%0%%-1%%-1%-1%0%-1%0%-1%0%0%0%0%#MobaFont%10%0%0
            """;

        var result = MobaXtermImporter.Parse(ini);

        var server = result.Servers.Single();
        Assert.Equal("srv-rdp01", server.RemoteServer);
        Assert.Equal(3389, server.RemotePort);
        Assert.Equal("Administrator@admin.example.internal", server.RdpUsername);
        Assert.Equal("RDP", server.ConnectionType);
        Assert.Equal("INFRA/B_SERVICES", server.Group);

        // The same fixture carries the RDP session's SSH gateway at fields 12 to 14.
        SshGatewayDto gateway = Assert.Single(result.Gateways);
        Assert.Equal("bastion.example.internal", gateway.Host);
        Assert.Equal("emp001", gateway.User);
        Assert.Equal(gateway.Id, server.SshGatewayId);
        Assert.False(server.UseDirectConnection);
    }

    // --- Telnet, VNC and SFTP (measured sessions: see MobaXtermSharedSessionsFixture.ProtocolsContent) ---

    private static MobaXtermImportResult ParseProtocols() =>
        MobaXtermImporter.Parse(MobaXtermSharedSessionsFixture.ProtocolsContent);

    private static ServerProfileDto Imported(MobaXtermImportResult result, string name) =>
        Assert.Single(result.Servers, server => server.DisplayName == name);

    private static MobaXtermImportWarning Skipped(MobaXtermImportResult result, string name) =>
        Assert.Single(result.Warnings, warning => warning.SessionName == name);

    private static void AssertDirectAndEmpty(MobaXtermImportResult result, ServerProfileDto server)
    {
        Assert.Empty(result.Gateways);
        Assert.True(server.UseDirectConnection);
        Assert.Null(server.SshGatewayId);
        Assert.Empty(server.PostConnectSteps);
        Assert.Null(server.RdpUsername);
        Assert.Null(server.TelnetUsername);
        Assert.Equal(ProfileOrigin.ImportMobaXterm, server.Origin);
    }

    // Producer: MobaXtermImporter.ParseSession through the Telnet [1] layout (host 0, port 1,
    // ApplyProtocolSettings writes TelnetPort); trigger: the two measured Telnet sessions, whose
    // ports are not 23, so a constant or a shifted port index fails.
    [Theory]
    [InlineData("tel-a", "telnet-a.example.test", 2323)]
    [InlineData("tel-b", "telnet-b.example.test", 2324)]
    public void Telnet_MeasuredSession_ImportsHostAndPortOnTheTelnetProperties(string name, string host, int port)
    {
        MobaXtermImportResult result = ParseProtocols();

        ServerProfileDto server = Imported(result, name);
        Assert.Equal(ConnectionTypeCatalog.Canonicalize("TELNET"), server.ConnectionType);
        Assert.Equal(host, server.RemoteServer);
        Assert.Equal(port, server.TelnetPort);
        Assert.Equal(port, server.RemotePort);
        Assert.False(server.HasSshPortField);
        Assert.Equal(DefaultPorts.Vnc, server.VncPort);
        Assert.Null(server.SshUsername);
        AssertDirectAndEmpty(result, server);
    }

    // Producer: MobaXtermImporter.ParseSession through the VNC [5] layout (host 0, port 1, no
    // user: index 11 is an Xvnc option); trigger: the two measured VNC sessions.
    [Theory]
    [InlineData("vnc-a", "vnc-a.example.test", 5911, "vncusera")]
    [InlineData("vnc-b", "vnc-b.example.test", 5912, "vncuserb")]
    public void Vnc_MeasuredSession_ImportsHostAndPort_AndNeverTheXvncLogin(string name, string host, int port, string xvncLogin)
    {
        MobaXtermImportResult result = ParseProtocols();

        ServerProfileDto server = Imported(result, name);
        Assert.Equal(ConnectionTypeCatalog.Canonicalize("VNC"), server.ConnectionType);
        Assert.Equal(host, server.RemoteServer);
        Assert.Equal(port, server.VncPort);
        Assert.Equal(port, server.RemotePort);
        Assert.False(server.HasSshPortField);
        Assert.Equal(DefaultPorts.Telnet, server.TelnetPort);
        Assert.Null(server.SshUsername);
        AssertDirectAndEmpty(result, server);
        Assert.DoesNotContain(xvncLogin, System.Text.Json.JsonSerializer.Serialize(server), StringComparison.Ordinal);
    }

    // Producer: MobaXtermImporter.ParseSession through the SFTP [7] layout (host 0, port 1, user
    // 2); trigger: the measured SFTP session. The port goes through the SshPort setter, so the
    // profile declares it.
    [Fact]
    public void Sftp_MeasuredSession_ImportsHostPortAndUserOnTheSshProperties()
    {
        MobaXtermImportResult result = ParseProtocols();

        ServerProfileDto server = Imported(result, "sftp-a");
        Assert.Equal(ConnectionTypeCatalog.Canonicalize("SFTP"), server.ConnectionType);
        Assert.Equal("sftp-a.example.test", server.RemoteServer);
        Assert.Equal(2202, server.SshPort);
        Assert.Equal(2202, server.RemotePort);
        Assert.True(server.HasSshPortField);
        Assert.Equal("sftpusera", server.SshUsername);
        Assert.Equal(DefaultPorts.Telnet, server.TelnetPort);
        Assert.Equal(DefaultPorts.Vnc, server.VncPort);
        Assert.Empty(result.Gateways);
        Assert.True(server.UseDirectConnection);
        Assert.Null(server.SshGatewayId);
        Assert.Empty(server.PostConnectSteps);
        Assert.Null(server.RdpUsername);
        Assert.Null(server.TelnetUsername);
    }

    // Producer: the three layouts again, on SYNTHETIC lines where every unread field is a sentinel
    // (u3, u4...). A layout shifted by one lands on a sentinel and the exact assertions fail; no
    // string of the profile may hold a sentinel.
    [Theory]
    [InlineData("syn-telnet", "syn-telnet.example.test", 2325, null)]
    [InlineData("syn-vnc", "syn-vnc.example.test", 5913, null)]
    [InlineData("syn-sftp", "syn-sftp.example.test", 2203, "synsftpu")]
    public void SyntheticLine_EachLayoutReadsItsOwnFieldsOnly(string name, string host, int port, string? sshUser)
    {
        MobaXtermImportResult result = ParseProtocols();

        ServerProfileDto server = Imported(result, name);
        Assert.Equal(host, server.RemoteServer);
        Assert.Equal(port, server.RemotePort);
        Assert.Equal(sshUser, server.SshUsername);
        Assert.Equal(name == "syn-telnet" ? port : DefaultPorts.Telnet, server.TelnetPort);
        Assert.Equal(name == "syn-vnc" ? port : DefaultPorts.Vnc, server.VncPort);
        Assert.Equal(name == "syn-sftp", server.HasSshPortField);

        System.Text.RegularExpressions.Regex sentinel = new(@"\bu\d+\b");
        foreach (System.Reflection.PropertyInfo property in typeof(ServerProfileDto)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0))
        {
            string? value = (string?)property.GetValue(server);
            Assert.False(
                value is not null && sentinel.IsMatch(value),
                $"{name}: {property.Name} holds a field the layout must not read: {value}");
        }
    }

    // Producer: MobaXtermImporter.ParsePort through the three layouts; trigger: a session whose
    // port field is empty gets the protocol's own default, not another protocol's.
    [Fact]
    public void EmptyPortField_FallsBackToTheProtocolDefault()
    {
        MobaXtermImportResult result = ParseProtocols();

        ServerProfileDto telnet = Imported(result, "noport-telnet");
        Assert.Equal(DefaultPorts.Telnet, telnet.TelnetPort);
        Assert.Equal(DefaultPorts.Telnet, telnet.RemotePort);
        Assert.False(telnet.HasSshPortField);

        ServerProfileDto vnc = Imported(result, "noport-vnc");
        Assert.Equal(DefaultPorts.Vnc, vnc.VncPort);
        Assert.Equal(DefaultPorts.Vnc, vnc.RemotePort);
        Assert.False(vnc.HasSshPortField);

        ServerProfileDto sftp = Imported(result, "noport-sftp");
        Assert.Equal(DefaultPorts.Sftp, sftp.SshPort);
        Assert.Equal(DefaultPorts.Sftp, sftp.RemotePort);
        Assert.True(sftp.HasSshPortField);
    }

    // Producer: MobaXtermImporter.ParseSession, the jump host refusal; trigger: a Telnet and a VNC
    // session that name a jump host. Heimdall cannot route either protocol through a gateway
    // (ProtocolCapabilities.SupportsSshGateway), and importing them direct would go around it.
    [Theory]
    [InlineData("telnet-gw", "jumpt.example.test")]
    [InlineData("vnc-gw", "jumpv.example.test")]
    public void TelnetOrVncThroughAJumpHost_IsNotImported_AndNoGatewayIsCreated(string name, string jumpHost)
    {
        MobaXtermImportResult result = ParseProtocols();

        Assert.DoesNotContain(result.Servers, server => server.DisplayName == name);
        MobaXtermImportWarning warning = Skipped(result, name);
        Assert.Equal(MobaXtermImportWarningKind.JumpHostNotSupported, warning.Kind);
        Assert.Equal(jumpHost, warning.Detail);
        Assert.False(warning.SessionImported);
        Assert.Empty(result.Gateways);
    }

    // Producer: MobaXtermImporter.ParseSession, the proxy refusal; trigger: SFTP (measured) and
    // Telnet and VNC (synthetic, no jump host) sessions with a proxy, and a VNC session with a
    // proxy type but no proxy host.
    [Theory]
    [InlineData("sftp-px", "proxys.example.test")]
    [InlineData("syn-telnet-proxy", "proxyt2.example.test")]
    [InlineData("syn-vnc-proxy", "proxyv2.example.test")]
    [InlineData("syn-vnc-proxytype", "")]
    [InlineData("syn-telnet-proxytype", "")]
    [InlineData("syn-sftp-proxytype", "")]
    public void SessionThroughAProxy_IsNotImported_AndNamesTheProxy(string name, string proxyHost)
    {
        MobaXtermImportResult result = ParseProtocols();

        Assert.DoesNotContain(result.Servers, server => server.DisplayName == name);
        MobaXtermImportWarning warning = Skipped(result, name);
        Assert.Equal(MobaXtermImportWarningKind.ProxyNotSupported, warning.Kind);
        Assert.Equal(proxyHost, warning.Detail);
        Assert.Empty(result.Gateways);
    }

    // Producer: MobaXtermImporter.ParseSession through the Telnet [1] layout, UserIndex null:
    // Telnet asks for its login in the terminal, so the login field is not read, and a [VAR] in it
    // raises no UnresolvedVariable warning for a value nobody consumes.
    [Fact]
    public void Telnet_LoginField_IsNotReadNorResolved()
    {
        const string content = """
            [Bookmarks_1]
            SubRep=Telnet
            tel-var= #98#1%telnet-var.example.test%23%[DAS]%%2%%%%%0%0%%1080%%%0%-1%0#MobaFont%10#0#
            """;

        MobaXtermImportResult result = MobaXtermImporter.Parse(content);

        ServerProfileDto server = Assert.Single(result.Servers);
        Assert.Null(server.TelnetUsername);
        Assert.Null(server.SshUsername);
        Assert.DoesNotContain(result.Warnings, warning => warning.Kind == MobaXtermImportWarningKind.UnresolvedVariable);
        Assert.Empty(result.Warnings);
    }

    // Producer: MobaXtermImporter.ApplyProtocolSettings, the default arm, reached through Parse:
    // a layout whose connection type has no case surfaces as a ParseError warning. Every type code
    // the importer recognises (one that is not reported UnsupportedSessionType) is walked with a
    // minimal line, so a layout added without its case fails here.
    [Fact]
    public void EveryRecognisedTypeCode_ParsesWithoutAParseError()
    {
        const int highestTypeCodeProbed = 99;
        int recognised = 0;
        for (int code = 0; code <= highestTypeCodeProbed; code++)
        {
            string content = $"[Bookmarks_1]\nSubRep=\nwalk= #1#{code}%walk.example.test%\n";

            MobaXtermImportResult result = MobaXtermImporter.Parse(content);

            if (result.Warnings.Any(warning => warning.Kind == MobaXtermImportWarningKind.UnsupportedSessionType))
            {
                continue;
            }

            recognised++;
            Assert.DoesNotContain(
                result.Warnings,
                warning => warning.Kind == MobaXtermImportWarningKind.ParseError);
            Assert.Single(result.Servers);
        }

        // Positive control: SSH, Telnet, RDP, VNC and SFTP were reached, so the loop walked something.
        Assert.Equal(5, recognised);
    }

    // Producer: MobaXtermImporter.CanCarryGateway, derived from the layout table and
    // ProtocolCapabilities.SupportsSshGateway.
    [Theory]
    [InlineData("SSH", true)]
    [InlineData("ssh", true)]
    [InlineData("RDP", true)]
    [InlineData("SFTP", false)]
    [InlineData("Telnet", false)]
    [InlineData("VNC", false)]
    [InlineData("WEB", false)]
    public void CanCarryGateway_OnlyTypesWithAJumpHostSlotAndGatewayRouting(string type, bool expected)
    {
        Assert.Equal(expected, MobaXtermImporter.CanCarryGateway(type));
    }

    // Producer: MobaXtermImporter.ParseSession, unmapped type codes; trigger: the measured FTP [6]
    // and WSL [14] sessions stay skipped, and ClassifySkippedType words them.
    [Theory]
    [InlineData("ftp-a", "6", MobaXtermSkippedType.Ftp)]
    [InlineData("wsl", "14", MobaXtermSkippedType.Wsl)]
    public void FtpAndWsl_StayUnsupported_WithAKnownReason(string name, string code, MobaXtermSkippedType expected)
    {
        MobaXtermImportResult result = ParseProtocols();

        Assert.DoesNotContain(result.Servers, server => server.DisplayName == name);
        MobaXtermImportWarning warning = Skipped(result, name);
        Assert.Equal(MobaXtermImportWarningKind.UnsupportedSessionType, warning.Kind);
        Assert.Equal(code, warning.Detail);
        Assert.Equal(expected, MobaXtermImporter.ClassifySkippedType(warning.Detail));
    }

    [Theory]
    [InlineData("999", MobaXtermSkippedType.Unknown)]
    [InlineData("", MobaXtermSkippedType.Unknown)]
    [InlineData(null, MobaXtermSkippedType.Unknown)]
    [InlineData("fourteen", MobaXtermSkippedType.Unknown)]
    [InlineData("6", MobaXtermSkippedType.Ftp)]
    [InlineData("14", MobaXtermSkippedType.Wsl)]
    public void ClassifySkippedType_NamesOnlyTheTypesItKnows(string? code, MobaXtermSkippedType expected)
    {
        Assert.Equal(expected, MobaXtermImporter.ClassifySkippedType(code));
    }

    // Producer: the whole ProtocolsContent file. Every session is either imported or reported, so
    // a new skipped line cannot hide behind the count of imported ones.
    [Fact]
    public void ProtocolsFile_EverySessionIsEitherImportedOrReported()
    {
        MobaXtermImportResult result = ParseProtocols();

        Assert.Equal(MobaXtermSharedSessionsFixture.ProtocolsImportedCount, result.Servers.Count);
        Assert.Equal(MobaXtermSharedSessionsFixture.ProtocolsSkippedCount, result.Warnings.Count);
        Assert.Equal(
            new[] { "Telnet", "VNC", "SFTP" }.Order(StringComparer.Ordinal),
            result.Servers.Select(server => server.ConnectionType).Distinct().Order(StringComparer.Ordinal));
    }
}
