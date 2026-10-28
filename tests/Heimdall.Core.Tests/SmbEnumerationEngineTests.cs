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

using Heimdall.Core.Discovery;

namespace Heimdall.Core.Tests;

public sealed class SmbEnumerationEngineTests
{
    [Fact]
    public void FormatDialect_Smb202_ReturnsReadable()
    {
        Assert.Equal("SMB 2.0.2", SmbEnumerationEngine.FormatDialect(0x0202));
    }

    [Fact]
    public void FormatDialect_Smb1_ReturnsLegacyLabel()
    {
        Assert.Equal("SMBv1 (legacy)", SmbEnumerationEngine.FormatDialect(0x00FF));
    }

    [Fact]
    public void FormatDialect_Unknown_ReturnsHex()
    {
        Assert.Equal("0x9999", SmbEnumerationEngine.FormatDialect(0x9999));
    }

    [Fact]
    public void ExtractBracketedValue_SimpleLine_ReturnsValue()
    {
        Assert.Equal("WORKGROUP", SmbEnumerationEngine.ExtractBracketedValue("Domain=[WORKGROUP]", "Domain"));
    }

    [Theory]
    [InlineData("Domain=WORKGROUP", "Domain")]
    [InlineData("Server=[HOST", "Server")]
    [InlineData("", "Server")]
    [InlineData("Server=[HOST]", "")]
    public void ExtractBracketedValue_InvalidInputs_ReturnNull(string line, string key)
    {
        Assert.Null(SmbEnumerationEngine.ExtractBracketedValue(line, key));
    }

    [Fact]
    public void ExtractBracketedValue_CaseInsensitiveKey_Matches()
    {
        Assert.Equal("HOST", SmbEnumerationEngine.ExtractBracketedValue("server=[HOST]", "Server"));
    }

    [Fact]
    public void ParseTunnelOutputs_AllEmpty_ReturnsNoData()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, null, null);

        Assert.False(observations.HasAnyData);
        Assert.Null(observations.Domain);
    }

    [Fact]
    public void ParseTunnelOutputs_Smbclient_ExtractsDomainOsAndServer()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(
            "Domain=[WORKGROUP] OS=[Unix] Server=[SAMBASRV]",
            null,
            null);

        Assert.True(observations.HasAnyData);
        Assert.Equal("WORKGROUP", observations.Domain);
        Assert.Equal("Unix", observations.OsInfo);
        Assert.Equal("SAMBASRV", observations.ServerName);
    }

    [Fact]
    public void ParseTunnelOutputs_SmbclientNtStatus_Ignored()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(
            "NT_STATUS_ACCESS_DENIED",
            null,
            null);

        Assert.False(observations.HasAnyData);
    }

    /// <summary>
    /// What rpcclient's <c>srvinfo</c> prints (Samba's cmd_srvsvc.c): the server name opens a header
    /// line with its type flags and comment, then three <c>label : value</c> lines. There is no
    /// <c>server_name</c> field. Reproduced from the Samba documentation, not captured from a host.
    /// </summary>
    private const string SrvInfoSample =
        "\tHOST01         Wk Sv PrQ Unx NT SNT Samba Server\n"
        + "\tplatform_id     :\t500\n"
        + "\tos version      :\t6.1\n"
        + "\tserver type     :\t0x809a03\n";

    [Fact]
    public void ParseTunnelOutputs_Rpcclient_ExtractsServerNameFromTheHeaderLineAndOsVersion()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, SrvInfoSample, null);

        Assert.True(observations.HasAnyData);
        Assert.Equal("HOST01", observations.RpcServerName);
        Assert.Equal("6.1", observations.RpcOsVersion);
    }

    [Fact]
    public void ParseTunnelOutputs_Rpcclient_NoiseAboveTheHeaderLine_DoesNotBecomeTheName()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(
            null,
            "WARNING: something printed first\n" + SrvInfoSample,
            null);

        Assert.Equal("HOST01", observations.RpcServerName);
    }

    [Fact]
    public void ParseTunnelOutputs_Rpcclient_ASyntheticServerNameLine_IsNotAField()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(null, "server_name : HOST01", null);

        Assert.Null(observations.RpcServerName);
    }

    [Fact]
    public void ParseTunnelOutputs_Nmblookup_ExtractsHostDomainAndMac()
    {
        var observations = SmbEnumerationEngine.ParseTunnelOutputs(
            null,
            null,
            "HOST <00> - B <ACTIVE>\nWORKGROUP <00> - <GROUP> B <ACTIVE>\nMAC Address = 00-11-22-33-44-55");

        Assert.True(observations.HasAnyData);
        Assert.Equal("HOST", observations.NetBiosName);
        Assert.Equal("WORKGROUP", observations.NetBiosDomain);
        Assert.Equal("00-11-22-33-44-55", observations.MacAddress);
    }

    [Fact]
    public void BuildSecurityFindings_SigningRequiredAndModernDialect_ReturnsSuccesses()
    {
        var findings = SmbEnumerationEngine.BuildSecurityFindings(
            null,
            new SmbNegotiateInfo("guid", 0x0311, true, true, null, null, 0),
            false);

        Assert.Collection(
            findings,
            first =>
            {
                Assert.Equal(SmbFindingSeverity.Success, first.Severity);
                Assert.Equal("ToolSmbSigningEnabled", first.MessageKey);
            },
            second =>
            {
                Assert.Equal(SmbFindingSeverity.Success, second.Severity);
                Assert.Equal("ToolSmbModernDialect", second.MessageKey);
                Assert.Equal("SMB 3.1.1", Assert.Single(second.MessageArgs!));
            });
    }

    [Fact]
    public void BuildSecurityFindings_Smb1AndSigningDisabled_ReturnsWarningAndCritical()
    {
        var findings = SmbEnumerationEngine.BuildSecurityFindings(
            null,
            new SmbNegotiateInfo("guid", 0x00FF, false, false, null, null, 0),
            false);

        Assert.Collection(
            findings,
            first =>
            {
                Assert.Equal(SmbFindingSeverity.Warning, first.Severity);
                Assert.Equal("ToolSmbSigningDisabled", first.MessageKey);
            },
            second =>
            {
                Assert.Equal(SmbFindingSeverity.Critical, second.Severity);
                Assert.Equal("ToolSmbV1Detected", second.MessageKey);
            });
    }

    [Fact]
    public void BuildSecurityFindings_NetBiosFailed_AddsInfoFinding()
    {
        var findings = SmbEnumerationEngine.BuildSecurityFindings(null, null, true);

        var finding = Assert.Single(findings);
        Assert.Equal(SmbFindingSeverity.Info, finding.Severity);
        Assert.Equal("ToolSmbNetBiosFailed", finding.MessageKey);
    }

    [Fact]
    public void BuildResult_PopulatesIdentityFromNtlm()
    {
        var result = SmbEnumerationEngine.BuildResult(
            new NtlmInfo("HOST01", "DOMAIN", "host01.domain.local", "domain.local", "forest.local", "20348"),
            null,
            null,
            null,
            "00-11-22-33-44-55",
            false);

        Assert.Equal(SmbEnumerationSource.Direct, result.Source);
        Assert.Equal("HOST01", result.ComputerName);
        Assert.Equal("DOMAIN", result.Domain);
        Assert.Equal("host01.domain.local", result.DnsName);
        Assert.Equal("00-11-22-33-44-55", result.MacAddress);
        Assert.Null(result.Report);
    }

    [Fact]
    public void BuildResult_FallsBackToNetBiosWhenNtlmNull()
    {
        var result = SmbEnumerationEngine.BuildResult(
            null,
            null,
            "NBHOST",
            "WORKGROUP",
            null,
            true);

        Assert.Equal("NBHOST", result.ComputerName);
        Assert.Equal("WORKGROUP", result.Domain);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void BuildResult_PopulatesProtocolFieldsFromSmbNegotiate()
    {
        var now = new DateTime(2026, 4, 17, 10, 11, 12);
        var boot = now.AddHours(-2);

        var result = SmbEnumerationEngine.BuildResult(
            null,
            new SmbNegotiateInfo("guid-1", 0x0311, true, true, now, boot, 0),
            null,
            null,
            null,
            false);

        Assert.Equal((ushort)0x0311, result.DialectRaw);
        Assert.Equal("SMB 3.1.1", result.Dialect);
        Assert.True(result.SigningRequired);
        Assert.Equal("guid-1", result.ServerGuid);
        Assert.Equal(TimeSpan.Zero, result.SystemTime?.Offset);
        Assert.Equal(TimeSpan.Zero, result.BootTime?.Offset);
    }

    [Fact]
    public void BuildTunnelResult_PrefersRpcServerName()
    {
        var result = SmbEnumerationEngine.BuildTunnelResult(new SmbTunnelObservations(
            Domain: "WORKGROUP",
            OsInfo: "Unix",
            ServerName: "SAMBA",
            RpcServerName: "RPCHOST",
            RpcOsVersion: "10.0",
            NetBiosName: "NBHOST",
            NetBiosDomain: "NBDOM",
            MacAddress: "00-11-22-33-44-55",
            HasAnyData: true));

        Assert.Equal(SmbEnumerationSource.Tunnel, result.Source);
        Assert.Equal("RPCHOST", result.ComputerName);
        Assert.Equal("WORKGROUP", result.Domain);
        Assert.Equal("10.0", result.OsBuild);
        Assert.Null(result.Dialect);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BuildTunnelResult_EmptyObservations_ReturnsNullFields()
    {
        var result = SmbEnumerationEngine.BuildTunnelResult(new SmbTunnelObservations(
            null, null, null, null, null, null, null, null, false));

        Assert.Null(result.ComputerName);
        Assert.Null(result.Domain);
        Assert.Null(result.DialectRaw);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BuildReport_IncludesIdentitySectionAndFindings()
    {
        var result = SmbEnumerationEngine.BuildResult(
            new NtlmInfo("HOST01", "DOMAIN", "host.domain", "domain.local", "forest.local", "20348"),
            new SmbNegotiateInfo("guid", 0x0311, true, true, new DateTime(2026, 4, 17, 10, 0, 0), null, 0),
            null,
            null,
            "00-11-22-33-44-55",
            false);

        var report = SmbEnumerationEngine.BuildReport(result, key => $"[{key}]");

        Assert.Contains("[ToolSmbReportIdentity]", report, StringComparison.Ordinal);
        Assert.Contains("[ToolSmbComputerName]", report, StringComparison.Ordinal);
        Assert.Contains("[ToolSmbReportProtocol]", report, StringComparison.Ordinal);
        Assert.Contains("[ToolSmbReportFindings]", report, StringComparison.Ordinal);
        Assert.Contains("HOST01", report, StringComparison.Ordinal);
        Assert.Contains("[OK]", report, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReport_SkipsProtocolSectionForTunnelResult()
    {
        var result = SmbEnumerationEngine.BuildTunnelResult(new SmbTunnelObservations(
            Domain: "WORKGROUP",
            OsInfo: "Unix",
            ServerName: "SAMBA",
            RpcServerName: null,
            RpcOsVersion: null,
            NetBiosName: null,
            NetBiosDomain: null,
            MacAddress: null,
            HasAnyData: true));

        var report = SmbEnumerationEngine.BuildReport(result, key => key);

        Assert.Contains("ToolSmbReportIdentity", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolSmbReportProtocol", report, StringComparison.Ordinal);
    }
}
