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

public class RdcManImporterTests
{
    private const string SampleRdg = """
        <?xml version="1.0" encoding="utf-8"?>
        <RDCMan programVersion="2.7" schemaVersion="3">
          <file>
            <name>MyConnections</name>
            <group>
              <name>Production</name>
              <server>
                <name>web01.example.com</name>
                <displayName>Web Server 01</displayName>
                <logonCredentials>
                  <userName>admin</userName>
                  <domain>corp.local</domain>
                </logonCredentials>
                <connectionSettings>
                  <port>3390</port>
                </connectionSettings>
              </server>
              <server>
                <name>db01.example.com</name>
                <displayName>Database 01</displayName>
              </server>
              <group>
                <name>DMZ</name>
                <server>
                  <name>fw01.example.com</name>
                  <displayName>Firewall 01</displayName>
                </server>
              </group>
            </group>
            <group>
              <name>Staging</name>
              <server>
                <name>staging01.example.com</name>
              </server>
            </group>
          </file>
        </RDCMan>
        """;

    [Fact]
    public void Parse_ValidRdg_ExtractsAllServers()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        Assert.Equal(4, result.Servers.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_ServerWithDisplayName_UsesDisplayName()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var web = result.Servers.First(s => s.RemoteServer == "web01.example.com");
        Assert.Equal("Web Server 01", web.DisplayName);
    }

    [Fact]
    public void Parse_ServerHostAndPort_Extracted()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var web = result.Servers.First(s => s.RemoteServer == "web01.example.com");
        Assert.Equal("web01.example.com", web.RemoteServer);
        Assert.Equal(3390, web.RemotePort);
    }

    [Fact]
    public void Parse_ServerWithCredentials_ExtractsUsernameWithDomain()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var web = result.Servers.First(s => s.RemoteServer == "web01.example.com");
        Assert.Equal("admin@corp.local", web.RdpUsername);
    }

    [Fact]
    public void Parse_ServerWithoutCredentials_UsernameIsNull()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var db = result.Servers.First(s => s.RemoteServer == "db01.example.com");
        Assert.Null(db.RdpUsername);
    }

    [Fact]
    public void Parse_ServerWithoutPort_DefaultsTo3389()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var db = result.Servers.First(s => s.RemoteServer == "db01.example.com");
        Assert.Equal(3389, db.RemotePort);
    }

    [Fact]
    public void Parse_TopLevelGroup_SetsGroupPath()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var db = result.Servers.First(s => s.RemoteServer == "db01.example.com");
        Assert.Equal("MyConnections/Production", db.Group);
    }

    [Fact]
    public void Parse_NestedGroup_BuildsFullGroupPath()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var fw = result.Servers.First(s => s.RemoteServer == "fw01.example.com");
        Assert.Equal("MyConnections/Production/DMZ", fw.Group);
    }

    [Fact]
    public void Parse_AllServersAreRdpType()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        Assert.All(result.Servers, s => Assert.Equal("RDP", s.ConnectionType));
    }

    [Fact]
    public void Parse_AllServersAreEmbedded()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        Assert.All(result.Servers, s => Assert.Equal("Embedded", s.RdpMode));
    }

    [Fact]
    public void Parse_AllServersUseDirectConnection()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        Assert.All(result.Servers, s => Assert.True(s.UseDirectConnection));
    }

    [Fact]
    public void Parse_AssignsUniqueIds()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var ids = result.Servers.Select(s => s.Id).ToHashSet();
        Assert.Equal(result.Servers.Count, ids.Count);
        Assert.All(result.Servers, s => Assert.False(string.IsNullOrEmpty(s.Id)));
    }

    [Fact]
    public void Parse_ServerWithoutDisplayName_UsesHostAsName()
    {
        var result = RdcManImporter.Parse(SampleRdg);

        var staging = result.Servers.First(s => s.RemoteServer == "staging01.example.com");
        Assert.Equal("staging01.example.com", staging.DisplayName);
    }

    [Fact]
    public void Parse_GatewaySettings_ExtractsGateway()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>GW Test</name>
                <server>
                  <name>internal.example.com</name>
                  <gatewaySettings>
                    <hostName>gw.example.com</hostName>
                  </gatewaySettings>
                </server>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        var server = result.Servers.Single();
        Assert.Equal("gw.example.com", server.RdpGateway);
    }

    [Fact]
    public void Parse_CredentialsWithoutDomain_UsernameOnly()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Test</name>
                <server>
                  <name>host.example.com</name>
                  <logonCredentials>
                    <userName>localadmin</userName>
                  </logonCredentials>
                </server>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        var server = result.Servers.Single();
        Assert.Equal("localadmin", server.RdpUsername);
    }

    [Fact]
    public void Parse_GroupLevelCredentials_InheritedByServer()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Test</name>
                <group>
                  <name>SharedCreds</name>
                  <logonCredentials>
                    <userName>groupuser</userName>
                    <domain>example.com</domain>
                  </logonCredentials>
                  <server>
                    <name>srv01.example.com</name>
                  </server>
                </group>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        var server = result.Servers.Single();
        Assert.Equal("groupuser@example.com", server.RdpUsername);
    }

    [Fact]
    public void Parse_GroupLevelConnectionSettings_InheritedByServer()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Test</name>
                <group>
                  <name>CustomPort</name>
                  <connectionSettings>
                    <port>3391</port>
                  </connectionSettings>
                  <server>
                    <name>srv01.example.com</name>
                  </server>
                </group>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        var server = result.Servers.Single();
        Assert.Equal(3391, server.RemotePort);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyResult()
    {
        var result = RdcManImporter.Parse(string.Empty);

        Assert.Empty(result.Servers);
    }

    [Fact]
    public void Parse_NullContent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RdcManImporter.Parse(null!));
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsWarning()
    {
        var result = RdcManImporter.Parse("<broken><xml");

        Assert.Empty(result.Servers);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Parse_ValidXmlWithoutServers_ReturnsEmpty()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Empty</name>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_ServerWithoutName_Skipped()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Test</name>
                <server>
                  <displayName>   </displayName>
                </server>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        Assert.Empty(result.Servers);
        Assert.Equal("Test", Assert.Single(result.SkippedServers).GroupPath);
    }

    // RDCMan 2.7+ (schemaVersion 3) writes the name of a file, group and server, and the display
    // name of a server, under a <properties> child; the flat layout above is schema 1.
    private const string PropertiesLayoutRdg = """
        <?xml version="1.0" encoding="utf-8"?>
        <RDCMan programVersion="2.7" schemaVersion="3">
          <file>
            <credentialsProfiles />
            <properties>
              <expanded>True</expanded>
              <name>Corp</name>
            </properties>
            <group>
              <properties>
                <expanded>False</expanded>
                <name>Production</name>
              </properties>
              <logonCredentials inherit="None">
                <userName>admin</userName>
                <domain>corp.local</domain>
              </logonCredentials>
              <server>
                <properties>
                  <displayName>Web Server 01</displayName>
                  <name>web01.example.com</name>
                </properties>
                <connectionSettings inherit="None">
                  <port>3390</port>
                </connectionSettings>
              </server>
              <server>
                <properties>
                  <name>db01.example.com</name>
                </properties>
              </server>
              <group>
                <properties>
                  <name>DMZ</name>
                </properties>
                <server>
                  <properties>
                    <displayName>Firewall 01</displayName>
                    <name>fw01.example.com</name>
                  </properties>
                </server>
              </group>
            </group>
          </file>
        </RDCMan>
        """;

    [Fact]
    public void Parse_PropertiesLayout_ReadsServerNamesAndDisplayNames()
    {
        var result = RdcManImporter.Parse(PropertiesLayoutRdg);

        Assert.Equal(3, result.Servers.Count);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.SkippedServers);

        var web = result.Servers.Single(s => s.RemoteServer == "web01.example.com");
        Assert.Equal("Web Server 01", web.DisplayName);
        Assert.Equal(3390, web.RemotePort);
        Assert.Equal("admin@corp.local", web.RdpUsername);

        var db = result.Servers.Single(s => s.RemoteServer == "db01.example.com");
        Assert.Equal("db01.example.com", db.DisplayName);

        var fw = result.Servers.Single(s => s.RemoteServer == "fw01.example.com");
        Assert.Equal("Firewall 01", fw.DisplayName);
    }

    [Fact]
    public void Parse_PropertiesLayout_ReadsFileAndGroupNamesIntoThePath()
    {
        var result = RdcManImporter.Parse(PropertiesLayoutRdg);

        Assert.Equal("Corp/Production", result.Servers.Single(s => s.RemoteServer == "web01.example.com").Group);
        Assert.Equal("Corp/Production/DMZ", result.Servers.Single(s => s.RemoteServer == "fw01.example.com").Group);
    }

    [Fact]
    public void Parse_PropertiesLayout_ServerWithNoNameAnywhere_IsSkippedWithAWarning()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <properties>
                  <name>Corp</name>
                </properties>
                <group>
                  <properties>
                    <name>Production</name>
                  </properties>
                  <server>
                    <properties>
                      <displayName>   </displayName>
                    </properties>
                  </server>
                  <server>
                    <properties>
                      <name>web01.example.com</name>
                    </properties>
                  </server>
                </group>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        Assert.Equal("web01.example.com", Assert.Single(result.Servers).RemoteServer);
        Assert.Equal("Corp/Production", Assert.Single(result.SkippedServers).GroupPath);
    }

    [Fact]
    public void Parse_FileAsRoot_ParsesCorrectly()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <file>
              <name>DirectFile</name>
              <server>
                <name>host.example.com</name>
              </server>
            </file>
            """;

        var result = RdcManImporter.Parse(rdg);

        Assert.Single(result.Servers);
        Assert.Equal("host.example.com", result.Servers[0].RemoteServer);
    }

    [Fact]
    public void Parse_MultipleFileNodes_AllParsed()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>File1</name>
                <server>
                  <name>srv1.example.com</name>
                </server>
              </file>
              <file>
                <name>File2</name>
                <server>
                  <name>srv2.example.com</name>
                </server>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        Assert.Equal(2, result.Servers.Count);
    }

    [Fact]
    public void Parse_EmptyGatewayHostname_NoGatewaySet()
    {
        var rdg = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Test</name>
                <server>
                  <name>host.example.com</name>
                  <gatewaySettings>
                    <hostName>   </hostName>
                  </gatewaySettings>
                </server>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(rdg);

        var server = result.Servers.Single();
        Assert.Null(server.RdpGateway);
    }
}
