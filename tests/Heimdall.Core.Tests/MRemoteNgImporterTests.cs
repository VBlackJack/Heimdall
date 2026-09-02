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

namespace Heimdall.Core.Tests;

public class MRemoteNgImporterTests
{
    private const string SampleXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org"
                          Name="Connections"
                          Export="false"
                          EncryptionEngine="AES"
                          BlockCipherMode="GCM"
                          KdfIterations="1000"
                          FullFileEncryption="false"
                          Protected="ProtectedValue"
                          ConfVersion="2.6">
          <Node Name="Production" Type="Container" Expanded="True">
            <Node Name="Web Server"
                  Type="Connection"
                  Hostname="web01.example.com"
                  Protocol="RDP"
                  Port="3389"
                  Username="admin"
                  Domain="corp.local"
                  Description="Main web server"
                  Colors="Colors32Bit"
                  Resolution="SmartSize"
                  RedirectClipboard="True"
                  RedirectDiskDrives="False"
                  RedirectPrinters="True"
                  RedirectSmartCards="False"
                  RedirectAudioCapture="False"
                  RDGatewayHostname="gw.example.com" />
            <Node Name="Linux" Type="Container">
              <Node Name="App Server"
                    Type="Connection"
                    Hostname="app01.example.com"
                    Protocol="SSH2"
                    Port="2222"
                    Username="deploy" />
            </Node>
          </Node>
          <Node Name="VNC Box"
                Type="Connection"
                Hostname="vnc.example.com"
                Protocol="VNC"
                Port="5901" />
          <Node Name="Switch"
                Type="Connection"
                Hostname="switch01.local"
                Protocol="Telnet"
                Port="23"
                Username="cisco" />
        </mrng:Connections>
        """;

    [Fact]
    public void Parse_ValidXml_ExtractsAllConnections()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        Assert.Equal(4, result.Servers.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_RdpConnection_ExtractsFields()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var web = result.Servers.First(s => s.DisplayName == "Web Server");
        Assert.Equal("web01.example.com", web.RemoteServer);
        Assert.Equal(3389, web.RemotePort);
        Assert.Equal("RDP", web.ConnectionType);
        Assert.Equal("admin@corp.local", web.RdpUsername);
    }

    [Fact]
    public void Parse_RdpConnection_ExtractsRdpSettings()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var web = result.Servers.First(s => s.DisplayName == "Web Server");
        Assert.Equal(32, web.RdpColorDepth);
        Assert.True(web.RdpDynamicResolution);
        Assert.True(web.RdpRedirectClipboard);
        Assert.False(web.RdpRedirectDrives);
        Assert.True(web.RdpRedirectPrinters);
        Assert.False(web.RdpRedirectSmartCards);
        Assert.False(web.RdpAudioCapture);
        Assert.Equal("Embedded", web.RdpMode);
    }

    [Fact]
    public void Parse_RdpConnection_ExtractsGateway()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var web = result.Servers.First(s => s.DisplayName == "Web Server");
        Assert.Equal("gw.example.com", web.RdpGateway);
    }

    [Fact]
    public void Parse_RdpConnection_ExtractsDescription()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var web = result.Servers.First(s => s.DisplayName == "Web Server");
        Assert.Equal("Main web server", web.Tags);
    }

    [Fact]
    public void Parse_SshConnection_ExtractsFields()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var app = result.Servers.First(s => s.DisplayName == "App Server");
        Assert.Equal("app01.example.com", app.RemoteServer);
        Assert.Equal(2222, app.SshPort);
        Assert.Equal("SSH", app.ConnectionType);
        Assert.Equal("deploy", app.SshUsername);
        Assert.Equal("Embedded", app.SshMode);
    }

    [Fact]
    public void Parse_VncConnection_ExtractsFields()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var vnc = result.Servers.First(s => s.DisplayName == "VNC Box");
        Assert.Equal("vnc.example.com", vnc.RemoteServer);
        Assert.Equal(5901, vnc.VncPort);
        Assert.Equal("VNC", vnc.ConnectionType);
    }

    [Fact]
    public void Parse_TelnetConnection_ExtractsFields()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var telnet = result.Servers.First(s => s.DisplayName == "Switch");
        Assert.Equal("switch01.local", telnet.RemoteServer);
        Assert.Equal(23, telnet.TelnetPort);
        Assert.Equal("Telnet", telnet.ConnectionType);
        Assert.Equal("cisco", telnet.TelnetUsername);
    }

    [Fact]
    public void Parse_TopLevelGroup_SetsGroupPath()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var web = result.Servers.First(s => s.DisplayName == "Web Server");
        Assert.Equal("Production", web.Group);
    }

    [Fact]
    public void Parse_NestedContainers_BuildsFullGroupPath()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var app = result.Servers.First(s => s.DisplayName == "App Server");
        Assert.Equal("Production/Linux", app.Group);
    }

    [Fact]
    public void Parse_TopLevelConnection_GroupIsNull()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var vnc = result.Servers.First(s => s.DisplayName == "VNC Box");
        Assert.Null(vnc.Group);
    }

    [Fact]
    public void Parse_AllServersUseDirectConnection()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        Assert.All(result.Servers, s => Assert.True(s.UseDirectConnection));
    }

    [Fact]
    public void Parse_AssignsUniqueIds()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var ids = result.Servers.Select(s => s.Id).ToHashSet();
        Assert.Equal(result.Servers.Count, ids.Count);
        Assert.All(result.Servers, s => Assert.False(string.IsNullOrEmpty(s.Id)));
    }

    [Fact]
    public void Parse_FullFileEncryption_ReturnsWarning()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org"
                              Name="Connections"
                              FullFileEncryption="true"
                              ConfVersion="2.6">
            </mrng:Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        Assert.Empty(result.Servers);
        Assert.Single(result.Warnings);
        Assert.Contains("encrypted", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyResult()
    {
        var result = MRemoteNgImporter.Parse(string.Empty);

        Assert.Empty(result.Servers);
    }

    [Fact]
    public void Parse_NullContent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MRemoteNgImporter.Parse(null!));
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsWarning()
    {
        var result = MRemoteNgImporter.Parse("<broken><xml");

        Assert.Empty(result.Servers);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Parse_Ssh1Protocol_MappedToSsh()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="Legacy SSH"
                    Type="Connection"
                    Hostname="legacy.example.com"
                    Protocol="SSH1"
                    Port="22"
                    Username="root" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("SSH", server.ConnectionType);
        Assert.Equal("root", server.SshUsername);
    }

    [Fact]
    public void Parse_RloginProtocol_MappedToTelnet()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="Rlogin Host"
                    Type="Connection"
                    Hostname="rlogin.example.com"
                    Protocol="Rlogin"
                    Port="513"
                    Username="user" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("Telnet", server.ConnectionType);
    }

    [Fact]
    public void Parse_IntAppProtocol_MappedToLocal()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="External App"
                    Type="Connection"
                    Hostname="localhost"
                    Protocol="IntApp" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("Local", server.ConnectionType);
    }

    [Fact]
    public void Parse_UnknownProtocol_FallsBackToRdp()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="Unknown"
                    Type="Connection"
                    Hostname="host.example.com"
                    Protocol="SomeNewProtocol"
                    Port="9999" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("RDP", server.ConnectionType);
    }

    [Fact]
    public void Parse_MissingProtocolAttribute_DefaultsToRdp()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="No Protocol"
                    Type="Connection"
                    Hostname="host.example.com" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("RDP", server.ConnectionType);
    }

    [Fact]
    public void Parse_MissingPortAttribute_AppliesDefaultPort()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="SSH No Port"
                    Type="Connection"
                    Hostname="host.example.com"
                    Protocol="SSH2" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal(22, server.SshPort);
    }

    [Fact]
    public void Parse_ConnectionWithoutHostnameOrName_Skipped()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Type="Connection" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        Assert.Empty(result.Servers);
    }

    [Fact]
    public void Parse_ConnectionWithNameButNoHostname_UsesNameAsHost()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="myserver"
                    Type="Connection"
                    Protocol="RDP" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("myserver", server.RemoteServer);
        Assert.Equal("myserver", server.DisplayName);
    }

    [Fact]
    public void Parse_VncConnection_NoUsername()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="VNC Server"
                    Type="Connection"
                    Hostname="vnc.example.com"
                    Protocol="VNC"
                    Port="5900"
                    Username="ignored" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        // VNC does not use usernames
        Assert.Null(server.RdpUsername);
        Assert.Null(server.SshUsername);
        Assert.Null(server.TelnetUsername);
    }

    [Fact]
    public void Parse_RdpColorDepthVariants_MappedCorrectly()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="C16" Type="Connection" Hostname="h1" Protocol="RDP" Colors="Colors16Bit" />
              <Node Name="C24" Type="Connection" Hostname="h2" Protocol="RDP" Colors="Colors24Bit" />
              <Node Name="C15" Type="Connection" Hostname="h3" Protocol="RDP" Colors="Colors15Bit" />
              <Node Name="C256" Type="Connection" Hostname="h4" Protocol="RDP" Colors="Colors256" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        Assert.Equal(16, result.Servers.First(s => s.DisplayName == "C16").RdpColorDepth);
        Assert.Equal(24, result.Servers.First(s => s.DisplayName == "C24").RdpColorDepth);
        Assert.Equal(15, result.Servers.First(s => s.DisplayName == "C15").RdpColorDepth);
        Assert.Equal(8, result.Servers.First(s => s.DisplayName == "C256").RdpColorDepth);
    }

    [Fact]
    public void Parse_EmptyContainer_NoServersAdded()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="Empty Group" Type="Container" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_DeeplyNestedContainers_BuildsFullPath()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="A" Type="Container">
                <Node Name="B" Type="Container">
                  <Node Name="C" Type="Container">
                    <Node Name="Deep Server"
                          Type="Connection"
                          Hostname="deep.example.com"
                          Protocol="SSH2"
                          Port="22" />
                  </Node>
                </Node>
              </Node>
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("A/B/C", server.Group);
    }

    [Fact]
    public void Parse_HttpProtocol_FallsBackToRdp()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="Web UI"
                    Type="Connection"
                    Hostname="web.example.com"
                    Protocol="HTTP"
                    Port="8080" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal("RDP", server.ConnectionType);
    }

    [Fact]
    public void Parse_RdpConnectionCarryingSettings_StopsFollowingGlobalDefaults()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var server = result.Servers.Single(s => s.DisplayName == "Web Server");

        // The redirections below are only read by the connect-time resolver once the profile
        // stops following the application defaults; while the flag stayed at its default every
        // value this importer maps was thrown away.
        Assert.False(server.RdpUseGlobalDefaults);
        Assert.True(server.RdpRedirectPrinters);
        Assert.False(server.RdpRedirectDrives);
    }

    [Theory]
    // Attributes that write a per-profile RDP field: the profile must stop following the defaults.
    [InlineData("RedirectClipboard=\"False\"", false)]
    [InlineData("RedirectDiskDrives=\"True\"", false)]
    [InlineData("RedirectPrinters=\"True\"", false)]
    [InlineData("RedirectSmartCards=\"True\"", false)]
    [InlineData("RedirectAudioCapture=\"True\"", false)]
    [InlineData("Colors=\"Colors16Bit\"", false)]
    [InlineData("Resolution=\"SmartSize\"", false)]
    // Attributes that write nothing per-profile: the entry keeps following the defaults. The
    // gateway is read by the resolver in both branches, so it is not a per-profile setting.
    [InlineData("", true)]
    [InlineData("Resolution=\"FullScreen\"", true)]
    [InlineData("RDGatewayHostname=\"gw.example.com\"", true)]
    public void Parse_RdpConnection_FollowsGlobalDefaultsOnlyWhenNoRdpSettingWasMapped(
        string rdpAttributes,
        bool expectedUseGlobalDefaults)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Test" FullFileEncryption="false" ConfVersion="2.6">
              <Node Name="Bare"
                    Type="Connection"
                    Hostname="bare.example.com"
                    Protocol="RDP"
                    Port="3389"
                    {rdpAttributes} />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        var server = result.Servers.Single();
        Assert.Equal(expectedUseGlobalDefaults, server.RdpUseGlobalDefaults);
    }

    [Fact]
    public void Parse_NonRdpConnection_KeepsFollowingGlobalDefaults()
    {
        var result = MRemoteNgImporter.Parse(SampleXml);

        var server = result.Servers.Single(s => s.DisplayName == "App Server");

        Assert.True(server.RdpUseGlobalDefaults);
    }
}
