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

using System.Text;
using Heimdall.Core.Discovery;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// Hostile input for the parsers behind the default-credential scanner, the cartography scanner and
/// the SMB enumeration service: replies that are truncated, oversized, malformed or built to look
/// like something they are not. Each parser must stay inside its input, report nothing it was not
/// told, and bound what it keeps.
/// </summary>
public class HostileNetworkReplyTests
{
    private const int FuzzIterations = 3000;
    private const int FuzzSeed = 20261028;
    private const int HugeLineCount = 200_000;

    // ── HTTP status line ────────────────────────────────────────────

    [Theory]
    [InlineData("HTTP/1.1 200 OK", true)]
    [InlineData("HTTP/1.1 200", true)]
    [InlineData("HTTP/1.0 301 Moved", true)]
    [InlineData("HTTP/1.1 399 Odd", true)]
    [InlineData("HTTP/1.1 200 OK\r\nServer: x\r\n\r\n", true)]
    [InlineData("HTTP/1.1 401 Unauthorized\r\nX-Note: 200 OK\r\n", false)]
    [InlineData("HTTP/1.1 199 Odd", false)]
    [InlineData("HTTP/1.1 400 Bad Request", false)]
    [InlineData("SSH-2.0-OpenSSH_9.6 200 OK", false)]
    [InlineData("220 FTP ready 200 OK", false)]
    [InlineData("garbage 200 OK", false)]
    [InlineData("HTTP/1.1 +200 OK", false)]
    [InlineData("HTTP/1.1 -200 OK", false)]
    [InlineData("HTTP/1.1  200 OK", false)]
    [InlineData("HTTP/1.1 2000 OK", false)]
    [InlineData("HTTP/1.1 20 OK", false)]
    [InlineData("HTTP/1.1 2e2 OK", false)]
    [InlineData("HTTP/1.1 \uFF12\uFF10\uFF10 OK", false)]
    [InlineData("\r\nHTTP/1.1 200 OK", false)]
    [InlineData("HTTP/1.1", false)]
    [InlineData("HTTP/", false)]
    public void IsHttpSuccessResponse_OnlyARealStatusLineCounts(string input, bool expected)
    {
        Assert.Equal(expected, DefaultCredentialEngine.IsHttpSuccessResponse(input));
    }

    [Fact]
    public void IsHttpSuccessResponse_AnOversizedReply_IsReadByItsFirstLine()
    {
        var reply = "HTTP/1.1 401 Unauthorized\r\n" + new string('A', 5_000_000);

        Assert.False(DefaultCredentialEngine.IsHttpSuccessResponse(reply));
    }

    // ── FTP reply ───────────────────────────────────────────────────

    [Theory]
    [InlineData("230 Login successful.\r\n", true)]
    [InlineData("230-Welcome\r\n230 Logged in\r\n", true)]
    [InlineData("331 Password required\r\n230 OK\r\n", true)]
    [InlineData("230", true)]
    [InlineData("530 Login incorrect. 2300 attempts left\r\n", false)]
    [InlineData("530 Login incorrect\r\n", false)]
    [InlineData("220 Welcome to server 230\r\n", false)]
    [InlineData("2300 not a reply code\r\n", false)]
    [InlineData("xx230 ", false)]
    [InlineData("", false)]
    public void IsFtpLoginAccepted_OnlyAReplyCodeAtTheStartOfALineCounts(string reply, bool expected)
    {
        Assert.Equal(expected, DefaultCredentialEngine.IsFtpLoginAccepted(reply));
    }

    // ── PostgreSQL authentication reply ─────────────────────────────

    [Fact]
    public void IsPostgresAuthenticationOk_AValidAuthenticationOk_IsAccepted()
    {
        Assert.True(DefaultCredentialEngine.IsPostgresAuthenticationOk(PostgresAuth(8, 0), 9));
    }

    [Theory]
    [InlineData(8, 5)]
    [InlineData(8, 3)]
    [InlineData(0, 0)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(-8, 0)]
    public void IsPostgresAuthenticationOk_AnotherCodeOrALengthThatDoesNotFit_IsRefused(int length, int code)
    {
        Assert.False(DefaultCredentialEngine.IsPostgresAuthenticationOk(PostgresAuth(length, code), 9));
    }

    [Fact]
    public void IsPostgresAuthenticationOk_ErrorMessageOrTruncatedOrOverclaimedRead_IsRefused()
    {
        var error = PostgresAuth(8, 0);
        error[0] = (byte)'E';
        Assert.False(DefaultCredentialEngine.IsPostgresAuthenticationOk(error, 9));

        var valid = PostgresAuth(8, 0);
        for (var length = 0; length < 9; length++)
        {
            Assert.False(DefaultCredentialEngine.IsPostgresAuthenticationOk(valid, length));
        }

        Assert.False(DefaultCredentialEngine.IsPostgresAuthenticationOk(valid, valid.Length + 1));
        Assert.False(DefaultCredentialEngine.IsPostgresAuthenticationOk(valid, int.MaxValue));
        Assert.False(DefaultCredentialEngine.IsPostgresAuthenticationOk(valid, -1));
    }

    // ── VNC handshake ───────────────────────────────────────────────

    [Theory]
    [InlineData("RFB 003.008\n", 8)]
    [InlineData("RFB 003.007\n", 7)]
    [InlineData("RFB 003.003\n", 3)]
    [InlineData("RFB 003.889\n", 889)]
    [InlineData("RFB 003.008\nextra bytes that follow", 8)]
    public void TryParseVncBanner_AValidBanner_YieldsTheMinorVersion(string banner, int minor)
    {
        var bytes = Encoding.ASCII.GetBytes(banner);

        Assert.True(DefaultCredentialEngine.TryParseVncBanner(bytes, bytes.Length, out var parsed));
        Assert.Equal(minor, parsed);
    }

    [Theory]
    [InlineData("RFB 3.8\n")]
    [InlineData("RFB 003.00X\n")]
    [InlineData("RFB 00X.008\n")]
    [InlineData("RFB 004.001\n")]
    [InlineData("RFB 003.008 ")]
    [InlineData("RFB 003-008\n")]
    [InlineData("rfb 003.008\n")]
    [InlineData("SSH-2.0-OpenSSH_9.6\r\n")]
    [InlineData("RFB 003.00")]
    [InlineData("")]
    public void TryParseVncBanner_AnythingElse_IsRefused(string banner)
    {
        var bytes = Encoding.ASCII.GetBytes(banner);

        Assert.False(DefaultCredentialEngine.TryParseVncBanner(bytes, bytes.Length, out _));
    }

    [Fact]
    public void TryParseVncBanner_ALengthBeyondTheBuffer_IsRefused()
    {
        var bytes = Encoding.ASCII.GetBytes("RFB 003.008\n");

        Assert.False(DefaultCredentialEngine.TryParseVncBanner(bytes, bytes.Length + 1, out _));
        Assert.False(DefaultCredentialEngine.TryParseVncBanner(bytes, int.MaxValue, out _));
        Assert.False(DefaultCredentialEngine.TryParseVncBanner(bytes, -1, out _));
    }

    [Theory]
    [InlineData(8, "RFB 003.008\n")]
    [InlineData(889, "RFB 003.008\n")]
    [InlineData(7, "RFB 003.007\n")]
    [InlineData(6, "RFB 003.003\n")]
    [InlineData(3, "RFB 003.003\n")]
    public void BuildVncVersionReply_AnswersWithAVersionTheScannerSpeaks(int serverMinor, string expected)
    {
        Assert.Equal(expected, Encoding.ASCII.GetString(DefaultCredentialEngine.BuildVncVersionReply(serverMinor)));
    }

    [Theory]
    [InlineData(new byte[] { 2, 1, 2 }, 8, true)]
    [InlineData(new byte[] { 1, 1 }, 7, true)]
    [InlineData(new byte[] { 2, 2, 16 }, 8, false)]
    [InlineData(new byte[] { 0, 0, 0, 0, 5, 0x62, 0x61, 0x64 }, 8, false)]
    [InlineData(new byte[] { 255, 1 }, 8, true)]
    [InlineData(new byte[] { 255 }, 8, false)]
    [InlineData(new byte[] { 3 }, 8, false)]
    [InlineData(new byte[] { 0, 0, 0, 1 }, 3, true)]
    [InlineData(new byte[] { 0, 0, 0, 2 }, 3, false)]
    [InlineData(new byte[] { 1, 0, 0, 0 }, 3, false)]
    [InlineData(new byte[] { 0, 0, 0 }, 3, false)]
    public void IsVncNoAuthOffered_ReadsTheSecurityHandshakeOfTheNegotiatedVersion(byte[] reply, int serverMinor, bool expected)
    {
        Assert.Equal(expected, DefaultCredentialEngine.IsVncNoAuthOffered(reply, reply.Length, serverMinor));
    }

    [Fact]
    public void IsVncNoAuthOffered_AReadLongerThanTheBufferOrNegative_IsRefused()
    {
        var reply = new byte[] { 1, 1 };

        Assert.False(DefaultCredentialEngine.IsVncNoAuthOffered(reply, 3, 8));
        Assert.False(DefaultCredentialEngine.IsVncNoAuthOffered(reply, 0, 8));
        Assert.False(DefaultCredentialEngine.IsVncNoAuthOffered(reply, -1, 8));
    }

    // ── SSH tunnel: ports, ping sweep, ARP, reverse DNS ─────────────

    [Fact]
    public void ParseOpenPorts_ReadsTheProbedPortsOnly_InOrderAndWithoutRepeats()
    {
        var probed = new HashSet<int> { 22, 80, 443, 3389 };

        var open = TunnelOutputParser.ParseOpenPorts("443\n22\r\n22\n 3389 \n8080\n", probed);

        Assert.Equal([443, 22, 3389], open);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-80")]
    [InlineData("65536")]
    [InlineData("70000")]
    [InlineData("99999999999999999999")]
    [InlineData("+80")]
    [InlineData("8 0")]
    [InlineData("0x50")]
    [InlineData("80.0")]
    [InlineData("80abc")]
    [InlineData("abc")]
    [InlineData("")]
    public void ParseOpenPorts_ALineThatIsNotAProbedPlainPort_IsIgnored(string line)
    {
        var probed = new HashSet<int> { 22, 80, 1, 65535 };

        Assert.Empty(TunnelOutputParser.ParseOpenPorts(line + "\n", probed));
    }

    [Fact]
    public void ParseOpenPorts_NullEmptyAndHugeOutput_AreHandled()
    {
        var probed = new HashSet<int> { 22 };
        Assert.Empty(TunnelOutputParser.ParseOpenPorts(null, probed));
        Assert.Empty(TunnelOutputParser.ParseOpenPorts(string.Empty, probed));

        var huge = string.Join('\n', Enumerable.Range(0, HugeLineCount).Select(index => (index % 70000).ToString()));
        Assert.Equal([22], TunnelOutputParser.ParseOpenPorts(huge, probed));
    }

    [Fact]
    public void ParseAliveHosts_KeepsOnlyAddressesOfTheScannedSubnet()
    {
        var subnet = new HashSet<string> { "192.0.2.1", "192.0.2.2", "192.0.2.3" };

        var alive = TunnelOutputParser.ParseAliveHosts(
            "192.0.2.2\r\n10.9.9.9\n 192.0.2.3 \n1\n192.0.2\nnot an address\n192.0.2.2\n::1\n", subnet);

        Assert.Equal(new[] { "192.0.2.2", "192.0.2.3" }.Order(), alive.Order());
    }

    [Fact]
    public void ParseArpHosts_ReadsTheFirstColumnAndKeepsOnlyTheScannedSubnet()
    {
        var subnet = new HashSet<string> { "192.0.2.1", "192.0.2.2" };
        const string table =
            "IP address       HW type     Flags       HW address            Mask     Device\n" +
            "192.0.2.1        0x1         0x2         aa:bb:cc:dd:ee:01     *        eth0\n" +
            "10.0.0.7         0x1         0x2         aa:bb:cc:dd:ee:02     *        eth0\n" +
            "   192.0.2.2   0x1\n" +
            "999.999.999.999  0x1         0x2         aa:bb:cc:dd:ee:03     *        eth0\n";

        var found = TunnelOutputParser.ParseArpHosts(table, subnet);

        Assert.Equal(new[] { "192.0.2.1", "192.0.2.2" }.Order(), found.Order());
    }

    [Fact]
    public void ParseReverseDns_ReadsPointersForTheAddressesAskedAbout()
    {
        var asked = new HashSet<string> { "192.0.2.1", "192.0.2.2", "192.0.2.3" };
        const string output =
            "192.0.2.1|1.2.0.192.in-addr.arpa domain name pointer gw.example.test.\n" +
            "192.0.2.2|Host 2.2.0.192.in-addr.arpa not found: 3(NXDOMAIN)\n" +
            "192.0.2.3|\n" +
            "10.9.9.9|9.9.9.10.in-addr.arpa domain name pointer elsewhere.example.test.\n" +
            "|x domain name pointer nobody.example.test.\n" +
            "no separator domain name pointer here\n";

        var map = TunnelOutputParser.ParseReverseDns(output, asked);

        Assert.Equal(new Dictionary<string, string> { ["192.0.2.1"] = "gw.example.test" }, map);
    }

    [Fact]
    public void ParseReverseDns_HostileNames_AreCleanedBoundedOrRefused()
    {
        var asked = new HashSet<string> { "192.0.2.1", "192.0.2.2", "192.0.2.3", "192.0.2.4" };
        var output =
            "192.0.2.1|x domain name pointer ev\u001b[31mil.example.test.\n" +
            "192.0.2.2|x domain name pointer " + new string('a', 5000) + ".\n" +
            "192.0.2.3|x domain name pointer two words.example.test.\n" +
            "192.0.2.4|x domain name pointer .\n";

        var map = TunnelOutputParser.ParseReverseDns(output, asked);

        Assert.Equal("ev[31mil.example.test", map["192.0.2.1"]);
        Assert.DoesNotContain("192.0.2.2", map.Keys);
        Assert.DoesNotContain("192.0.2.3", map.Keys);
        Assert.DoesNotContain("192.0.2.4", map.Keys);
    }

    // ── smbclient / rpcclient / nmblookup output ────────────────────

    [Fact]
    public void ParseTunnelOutputs_ValidOutputs_AreRead()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(
            "Domain=[WORKGROUP] OS=[Windows 10] Server=[Win10]",
            "\tDC01           Wk Sv PrQ NT SNT\n\tplatform_id     :\t500\n\tos version      :\t10.0\n",
            "\tDC01            <00> -         B <ACTIVE>\n\tWORKGROUP       <00> - <GROUP> B <ACTIVE>\n\tMAC Address = 00-0C-29-AB-CD-EF\n");

        Assert.True(observations.HasAnyData);
        Assert.Equal("WORKGROUP", observations.Domain);
        Assert.Equal("Windows 10", observations.OsInfo);
        Assert.Equal("Win10", observations.ServerName);
        Assert.Equal("DC01", observations.RpcServerName);
        Assert.Equal("10.0", observations.RpcOsVersion);
        Assert.Equal("DC01", observations.NetBiosName);
        Assert.Equal("WORKGROUP", observations.NetBiosDomain);
        Assert.Equal("00-0C-29-AB-CD-EF", observations.MacAddress);
    }

    [Theory]
    [InlineData("Domain=[")]
    [InlineData("Domain=[]")]
    [InlineData("Domain=")]
    [InlineData("Domain=[\u0007]")]
    public void ParseTunnelOutputs_ASmbclientLineWithoutAValue_IsNotData(string smbclient)
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(smbclient, null, null);

        Assert.False(observations.HasAnyData);
        Assert.Null(observations.Domain);
    }

    [Theory]
    [InlineData("platform_id")]
    [InlineData("platform_id:")]
    [InlineData("platform_id :   ")]
    [InlineData("os_version")]
    [InlineData("os version :")]
    public void ParseTunnelOutputs_AnRpcclientLabelWithoutAValue_IsNotDataAndNotItsOwnValue(string rpcclient)
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, rpcclient, null);

        Assert.False(observations.HasAnyData);
        Assert.Null(observations.RpcServerName);
        Assert.Null(observations.RpcOsVersion);
    }

    [Fact]
    public void ParseTunnelOutputs_ARpcclientValueKeepsItsOwnColons()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, "os version : 10.0:preview", null);

        Assert.Equal("10.0:preview", observations.RpcOsVersion);
    }

    [Theory]
    [InlineData("MAC Address")]
    [InlineData("MAC Address =")]
    [InlineData("MAC Address = not a mac")]
    [InlineData("MAC Address = 00-0C-29-AB-CD")]
    [InlineData("MAC Address = 00-0C-29-AB-CD-EF-01")]
    [InlineData("MAC Address = \u001b[2J00-0C-29-AB-CD-EF")]
    public void ParseTunnelOutputs_AMacAddressThatIsNotOne_IsIgnored(string nmblookup)
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, null, nmblookup);

        Assert.Null(observations.MacAddress);
        Assert.False(observations.HasAnyData);
    }

    [Fact]
    public void ParseTunnelOutputs_ANameLineWithoutAName_IsIgnored()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, null, "\t<00> -         B <ACTIVE>\n");

        Assert.Null(observations.NetBiosName);
        Assert.False(observations.HasAnyData);
    }

    [Fact]
    public void ParseTunnelOutputs_ControlCharactersAndOversizedValues_DoNotTravel()
    {
        var smbclient = "Domain=[\u001b[31mEVIL\r] OS=[" + new string('x', 100_000) + "] Server=[ok]";

        var observations = SmbEnumerationEngine.ParseTunnelOutputs(smbclient, null, null);

        Assert.Equal("[31mEVIL", observations.Domain);
        Assert.NotNull(observations.OsInfo);
        Assert.True(observations.OsInfo.Length <= 256, $"The OS string kept {observations.OsInfo.Length} characters.");
        Assert.Equal("ok", observations.ServerName);
    }

    [Fact]
    public void ParseTunnelOutputs_HugeOutputs_AreParsedWithoutIncident()
    {
        var huge = string.Join('\n', Enumerable.Repeat("noise noise noise", HugeLineCount));

        var observations = SmbEnumerationEngine.ParseTunnelOutputs(huge, huge, huge);

        Assert.False(observations.HasAnyData);
    }

    // ── NetBIOS NBSTAT reply ────────────────────────────────────────

    [Fact]
    public void ParseNetBiosResponse_ControlCharactersInANameAreDropped()
    {
        var (name, domain, _) = UdpProbeEngine.ParseNetBiosResponse(NetBiosResponse(("MY\u0007PC", 0x00, 0x0400), ("WORK\u001bGROUP", 0x00, 0x8400)));

        Assert.Equal("MYPC", name);
        Assert.Equal("WORKGROUP", domain);
    }

    [Fact]
    public void ParseNetBiosResponse_ANameOfOnlyControlCharacters_IsNotAName()
    {
        var (name, _, _) = UdpProbeEngine.ParseNetBiosResponse(NetBiosResponse(("\0\0\0\0\0", 0x00, 0x0400)));

        Assert.Null(name);
    }

    [Fact]
    public void ParseNetBiosResponse_EveryTruncationAndRandomCorruption_NeverThrows()
    {
        var valid = NetBiosResponse(("MYPC", 0x00, 0x0400), ("CONTOSO", 0x00, 0x8400));
        for (var length = 0; length <= valid.Length; length++)
        {
            var cut = valid.AsSpan(0, length).ToArray();
            var exception = Record.Exception(() => UdpProbeEngine.ParseNetBiosResponse(cut));
            Assert.True(exception is null, $"Truncation at {length} threw {exception}");
        }

        var random = new Random(FuzzSeed);
        for (var iteration = 0; iteration < FuzzIterations; iteration++)
        {
            var data = (byte[])valid.Clone();
            for (var edit = random.Next(1, 8); edit > 0; edit--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => UdpProbeEngine.ParseNetBiosResponse(data));
            Assert.True(exception is null, $"Iteration {iteration} threw {exception}: {Convert.ToHexString(data)}");
        }
    }

    // ── SNMP response ───────────────────────────────────────────────

    [Fact]
    public void ParseSnmpResponse_ValidResponse_IsRead()
    {
        var info = UdpProbeEngine.ParseSnmpResponse(SnmpResponse(uptimeTicks: [0x00, 0x01, 0x86, 0xA0], services: [72]));

        Assert.NotNull(info);
        Assert.Equal("Linux box", info.SysDescr);
        Assert.Equal("box", info.SysName);
        Assert.Equal(1000, info.SysUpTimeSeconds);
        Assert.Equal(72, info.SysServices);
    }

    [Fact]
    public void ParseSnmpResponse_AnUptimeThatWouldWrapAround_IsDropped()
    {
        var info = UdpProbeEngine.ParseSnmpResponse(SnmpResponse(
            uptimeTicks: [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], services: [72]));

        Assert.NotNull(info);
        Assert.Null(info.SysUpTimeSeconds);
        Assert.Equal(72, info.SysServices);
    }

    [Fact]
    public void ParseSnmpResponse_AnUptimeBeyondThirtyTwoBits_IsDropped()
    {
        var info = UdpProbeEngine.ParseSnmpResponse(SnmpResponse(
            uptimeTicks: [0x01, 0x00, 0x00, 0x00, 0x00], services: [72]));

        Assert.NotNull(info);
        Assert.Null(info.SysUpTimeSeconds);
    }

    [Fact]
    public void ParseSnmpResponse_TheLargestUptime_IsKept()
    {
        var info = UdpProbeEngine.ParseSnmpResponse(SnmpResponse(
            uptimeTicks: [0x00, 0xFF, 0xFF, 0xFF, 0xFF], services: [72]));

        Assert.NotNull(info);
        Assert.Equal((long?)(uint.MaxValue / 100), info.SysUpTimeSeconds);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xC8 })]
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void ParseSnmpResponse_ServicesOutsideTheSevenBitMask_AreDropped(byte[] services)
    {
        var info = UdpProbeEngine.ParseSnmpResponse(SnmpResponse(uptimeTicks: [0x00, 0x01], services: services));

        Assert.NotNull(info);
        Assert.Null(info.SysServices);
    }

    [Fact]
    public void ParseSnmpResponse_EveryTruncationAndRandomCorruption_NeverThrows()
    {
        var valid = SnmpResponse(uptimeTicks: [0x00, 0x01, 0x86, 0xA0], services: [72]);
        for (var length = 0; length <= valid.Length; length++)
        {
            var cut = valid.AsSpan(0, length).ToArray();
            var exception = Record.Exception(() => UdpProbeEngine.ParseSnmpResponse(cut));
            Assert.True(exception is null, $"Truncation at {length} threw {exception}");
        }

        var random = new Random(FuzzSeed);
        for (var iteration = 0; iteration < FuzzIterations; iteration++)
        {
            var data = (byte[])valid.Clone();
            for (var edit = random.Next(1, 8); edit > 0; edit--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => UdpProbeEngine.ParseSnmpResponse(data));
            Assert.True(exception is null, $"Iteration {iteration} threw {exception}: {Convert.ToHexString(data)}");
        }
    }

    /// <summary>
    /// A length longer than an int wrapped negative and <c>new byte[-1]</c> threw out of the codec,
    /// to callers that do not expect an exception from reading a datagram.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x02 })]
    [InlineData(new byte[] { 0x30, 0x84, 0x80, 0x00, 0x00, 0x00, 0x01, 0x02 })]
    [InlineData(new byte[] { 0x30, 0x85, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01 })]
    [InlineData(new byte[] { 0x30, 0x88, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    [InlineData(new byte[] { 0x30, 0xFF, 0x01 })]
    public void Asn1ReadTlv_AnOverflowingLength_IsBoundedByTheBufferAndNeverThrows(byte[] data)
    {
        var (total, value) = SnmpCodec.Asn1ReadTlv(data, 0);

        Assert.NotNull(value);
        Assert.True(total <= data.Length, $"Consumed {total} of {data.Length} bytes.");
        Assert.True(value.Length <= data.Length);
    }

    [Fact]
    public void ParseSnmpResponse_AnOverflowingLengthInsideAnOtherwiseValidResponse_NeverThrows()
    {
        var valid = SnmpResponse(uptimeTicks: [0x00, 0x01], services: [72]);
        foreach (var lengthByte in new byte[] { 0x84, 0x85, 0x88, 0xFF })
        {
            for (var position = 0; position < valid.Length - 6; position++)
            {
                var data = (byte[])valid.Clone();
                data[position] = lengthByte;
                for (var fill = 1; fill <= 5; fill++)
                {
                    data[position + fill] = 0xFF;
                }

                var exception = Record.Exception(() => UdpProbeEngine.ParseSnmpResponse(data));
                Assert.True(exception is null, $"Position {position} threw {exception}");
            }
        }
    }

    // ── Builders ────────────────────────────────────────────────────

    private static byte[] PostgresAuth(int length, int code)
    {
        var message = new byte[9];
        message[0] = (byte)'R';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1, 4), length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(5, 4), code);
        return message;
    }

    /// <summary>An NBSTAT response: header, echoed wildcard question, one answer holding the entries.</summary>
    private static byte[] NetBiosResponse(params (string Name, byte Suffix, ushort Flags)[] entries)
    {
        var packet = new List<byte>();
        packet.AddRange([0xAB, 0xCD, 0x84, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]);
        packet.Add(0x20);
        var wildcard = new byte[16];
        wildcard[0] = 0x2A;
        for (var index = 1; index < 16; index++)
        {
            wildcard[index] = 0x20;
        }

        foreach (var value in wildcard)
        {
            packet.Add((byte)('A' + ((value >> 4) & 0x0F)));
            packet.Add((byte)('A' + (value & 0x0F)));
        }

        packet.Add(0x00);
        packet.AddRange([0x00, 0x21, 0x00, 0x01]);
        packet.AddRange([0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]);
        packet.AddRange([0x00, (byte)(entries.Length * 18 + 7)]);
        packet.Add((byte)entries.Length);
        foreach (var (name, suffix, flags) in entries)
        {
            var padded = new byte[15];
            Array.Fill(padded, (byte)0x20);
            Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(name.Length, 15)).CopyTo(padded);
            packet.AddRange(padded);
            packet.Add(suffix);
            packet.Add((byte)(flags >> 8));
            packet.Add((byte)(flags & 0xFF));
        }

        packet.AddRange([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        return [.. packet];
    }

    private static byte[] Tlv(byte tag, byte[] content)
    {
        var length = content.Length < 0x80
            ? new[] { (byte)content.Length }
            : new byte[] { 0x82, (byte)(content.Length >> 8), (byte)(content.Length & 0xFF) };
        return [tag, .. length, .. content];
    }

    private static byte[] Varbind(byte oidLast, byte valueTag, byte[] value) =>
        Tlv(0x30, [.. Tlv(0x06, [0x2B, 0x06, 0x01, 0x02, 0x01, 0x01, oidLast, 0x00]), .. Tlv(valueTag, value)]);

    /// <summary>A GetResponse for the six system-group OIDs the scanner asks for.</summary>
    private static byte[] SnmpResponse(byte[] uptimeTicks, byte[] services)
    {
        byte[] varbinds =
        [
            .. Varbind(0x01, 0x04, Encoding.ASCII.GetBytes("Linux box")),
            .. Varbind(0x05, 0x04, Encoding.ASCII.GetBytes("box")),
            .. Varbind(0x06, 0x04, Encoding.ASCII.GetBytes("lab")),
            .. Varbind(0x02, 0x06, [0x2B, 0x06, 0x01, 0x04, 0x01, 0x09]),
            .. Varbind(0x03, 0x43, uptimeTicks),
            .. Varbind(0x07, 0x02, services),
        ];
        byte[] pdu = Tlv(0xA2, [.. Tlv(0x02, [0x01]), .. Tlv(0x02, [0x00]), .. Tlv(0x02, [0x00]), .. Tlv(0x30, varbinds)]);
        return Tlv(0x30, [.. Tlv(0x02, [0x01]), .. Tlv(0x04, Encoding.ASCII.GetBytes("public")), .. pdu]);
    }
}
