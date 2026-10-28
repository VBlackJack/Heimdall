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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Pure engine that consolidates raw SMB/NTLM probe payloads into a
/// <see cref="SmbEnumerationResult"/>, builds security findings, and formats
/// the clipboard report. I/O-free and locale-neutral.
/// </summary>
public static class SmbEnumerationEngine
{
    private const ushort DialectSmb202 = 0x0202;
    private const ushort DialectSmb21 = 0x0210;
    private const ushort DialectSmb30 = 0x0300;
    private const ushort DialectSmb302 = 0x0302;
    private const ushort DialectSmb311 = 0x0311;
    private const ushort DialectSmb1 = 0x00FF;

    internal const ushort MinSecureDialect = DialectSmb202;

    /// <summary>
    /// Formats a 16-bit SMB dialect revision code to a human-readable string.
    /// </summary>
    public static string FormatDialect(ushort dialect) => dialect switch
    {
        DialectSmb202 => "SMB 2.0.2",
        DialectSmb21 => "SMB 2.1",
        DialectSmb30 => "SMB 3.0",
        DialectSmb302 => "SMB 3.0.2",
        DialectSmb311 => "SMB 3.1.1",
        DialectSmb1 => "SMBv1 (legacy)",
        _ => string.Format(CultureInfo.InvariantCulture, "0x{0:X4}", dialect),
    };

    /// <summary>
    /// Extracts a value from a <c>key=[value]</c> style line.
    /// </summary>
    public static string? ExtractBracketedValue(string? line, string? key)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var prefix = $"{key}=[";
        var idx = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + prefix.Length;
        var end = line.IndexOf(']', start);
        return end > start ? line[start..end] : null;
    }

    /// <summary>
    /// The text after the first colon of a <c>label : value</c> line, cleaned; null when the line
    /// has no colon or nothing after it. Without that a label alone (<c>server_name</c>) was
    /// returned as the value.
    /// </summary>
    private static string? ValueAfterColon(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? null : ReplyText.Clean(line[(colon + 1)..]);
    }

    /// <summary>The label of the first <c>label : value</c> line rpcclient's srvinfo prints.</summary>
    private const string RpcPlatformIdLabel = "platform_id";

    /// <summary>
    /// The server name of the header line of rpcclient's <c>srvinfo</c>, which reads
    /// <c>SERVER   Wk Sv PrQ Unx NT SNT comment</c>: its first token. Null when there is no such
    /// line, or when its first token is a label or an error prefix rather than a name.
    /// </summary>
    private static string? HostNameFromSrvInfoHeader(string? headerLine)
    {
        // The header carries no colon; every other line srvinfo prints is a "label : value" line.
        if (string.IsNullOrWhiteSpace(headerLine) || headerLine.Contains(':'))
        {
            return null;
        }

        var token = headerLine.Split(
            [' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return ReplyText.Clean(token);
    }

    /// <summary>Six hexadecimal pairs separated by hyphens or colons.</summary>
    private static readonly Regex MacAddressPattern = new(
        "^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Parses the stdout of the three tunnel commands into a structured observation record.
    /// </summary>
    public static SmbTunnelObservations ParseTunnelOutputs(
        string? smbclientOutput,
        string? rpcclientOutput,
        string? nmblookupOutput)
    {
        string? domain = null;
        string? osInfo = null;
        string? serverName = null;
        string? rpcServerName = null;
        string? rpcOsVersion = null;
        string? netBiosName = null;
        string? netBiosDomain = null;
        string? macAddress = null;
        var hasAnyData = false;

        if (!string.IsNullOrWhiteSpace(smbclientOutput)
            && !smbclientOutput.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            && !smbclientOutput.Contains("NT_STATUS_", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in smbclientOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lineDomain = ReplyText.Clean(ExtractBracketedValue(trimmed, "Domain"));
                var lineOs = ReplyText.Clean(ExtractBracketedValue(trimmed, "OS"));
                var lineServer = ReplyText.Clean(ExtractBracketedValue(trimmed, "Server"));
                domain = lineDomain ?? domain;
                osInfo = lineOs ?? osInfo;
                serverName = lineServer ?? serverName;
                hasAnyData |= lineDomain is not null || lineOs is not null || lineServer is not null;
            }
        }

        if (!string.IsNullOrWhiteSpace(rpcclientOutput)
            && !rpcclientOutput.Contains("NT_STATUS_", StringComparison.OrdinalIgnoreCase))
        {
            string? previousLine = null;
            foreach (var line in rpcclientOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.StartsWith(RpcPlatformIdLabel, StringComparison.OrdinalIgnoreCase))
                {
                    // srvinfo prints no "server_name" field: the server's name opens the line above
                    // the platform_id label, ahead of its type flags and comment.
                    var name = HostNameFromSrvInfoHeader(previousLine);
                    rpcServerName = name ?? rpcServerName;
                    hasAnyData |= name is not null;
                }
                else if (trimmed.Contains("os_version", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("os version", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ValueAfterColon(trimmed);
                    rpcOsVersion = value ?? rpcOsVersion;
                    hasAnyData |= value is not null;
                }

                previousLine = trimmed;
            }
        }

        if (!string.IsNullOrWhiteSpace(nmblookupOutput)
            && !nmblookupOutput.Contains("name_query failed", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in nmblookupOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("<00>", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("MAC", StringComparison.OrdinalIgnoreCase))
                {
                    var namePart = ReplyText.Clean(trimmed.Split('<')[0]);
                    if (namePart is null)
                    {
                        continue;
                    }

                    if (trimmed.Contains("<GROUP>", StringComparison.OrdinalIgnoreCase))
                    {
                        netBiosDomain ??= namePart;
                    }
                    else
                    {
                        netBiosName ??= namePart;
                    }

                    hasAnyData = true;
                }
                else if (trimmed.StartsWith("MAC Address", StringComparison.OrdinalIgnoreCase))
                {
                    var separator = trimmed.IndexOf('=');
                    var candidate = separator < 0 ? null : trimmed[(separator + 1)..].Trim();
                    if (candidate is not null && MacAddressPattern.IsMatch(candidate))
                    {
                        macAddress = candidate;
                        hasAnyData = true;
                    }
                }
            }
        }

        return new SmbTunnelObservations(
            Domain: domain,
            OsInfo: osInfo,
            ServerName: serverName,
            RpcServerName: rpcServerName,
            RpcOsVersion: rpcOsVersion,
            NetBiosName: netBiosName,
            NetBiosDomain: netBiosDomain,
            MacAddress: macAddress,
            HasAnyData: hasAnyData);
    }

    /// <summary>
    /// Builds locale-neutral security findings from raw probe data.
    /// </summary>
    public static IReadOnlyList<SmbFinding> BuildSecurityFindings(
        NtlmInfo? ntlm,
        SmbNegotiateInfo? smb,
        bool netBiosFailed)
    {
        _ = ntlm;
        var findings = new List<SmbFinding>();

        if (smb is not null)
        {
            findings.Add(smb.SigningRequired
                ? new SmbFinding(SmbFindingSeverity.Success, "ToolSmbSigningEnabled")
                : new SmbFinding(SmbFindingSeverity.Warning, "ToolSmbSigningDisabled"));

            if (smb.DialectRevision < MinSecureDialect)
            {
                findings.Add(new SmbFinding(SmbFindingSeverity.Critical, "ToolSmbV1Detected"));
            }
            else
            {
                findings.Add(new SmbFinding(
                    SmbFindingSeverity.Success,
                    "ToolSmbModernDialect",
                    [FormatDialect(smb.DialectRevision)]));
            }
        }

        if (netBiosFailed)
        {
            findings.Add(new SmbFinding(SmbFindingSeverity.Info, "ToolSmbNetBiosFailed"));
        }

        return findings;
    }

    /// <summary>
    /// Consolidates direct-probe payloads into a locale-neutral result.
    /// </summary>
    public static SmbEnumerationResult BuildResult(
        NtlmInfo? ntlm,
        SmbNegotiateInfo? smb,
        string? netBiosName,
        string? netBiosDomain,
        string? netBiosMac,
        bool netBiosFailed)
    {
        var findings = BuildSecurityFindings(ntlm, smb, netBiosFailed);

        return new SmbEnumerationResult(
            Source: SmbEnumerationSource.Direct,
            ComputerName: ntlm?.NetBiosComputerName ?? netBiosName,
            Domain: ntlm?.NetBiosDomainName ?? netBiosDomain,
            DnsName: ntlm?.DnsComputerName,
            DnsDomain: ntlm?.DnsDomainName,
            Forest: ntlm?.DnsForestName,
            OsBuild: ntlm?.OsBuild,
            MacAddress: netBiosMac,
            DialectRaw: smb?.DialectRevision,
            Dialect: smb is null ? null : FormatDialect(smb.DialectRevision),
            SigningRequired: smb?.SigningRequired,
            ServerGuid: smb?.ServerGuid,
            SystemTime: WrapUtc(smb?.SystemTime),
            BootTime: WrapUtc(smb?.ServerStartTime),
            Findings: findings,
            Report: null);
    }

    /// <summary>
    /// Consolidates tunnel observations into the same locale-neutral result shape.
    /// </summary>
    public static SmbEnumerationResult BuildTunnelResult(SmbTunnelObservations observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return new SmbEnumerationResult(
            Source: SmbEnumerationSource.Tunnel,
            ComputerName: observations.RpcServerName ?? observations.ServerName ?? observations.NetBiosName,
            Domain: observations.Domain ?? observations.NetBiosDomain,
            DnsName: null,
            DnsDomain: null,
            Forest: null,
            OsBuild: observations.RpcOsVersion ?? observations.OsInfo,
            MacAddress: observations.MacAddress,
            DialectRaw: null,
            Dialect: null,
            SigningRequired: null,
            ServerGuid: null,
            SystemTime: null,
            BootTime: null,
            Findings: [],
            Report: null);
    }

    /// <summary>
    /// Builds the localized clipboard report for a result.
    /// </summary>
    public static string BuildReport(SmbEnumerationResult result, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localize);

        var na = localize("ToolSmbNotAvailable");
        var sb = new StringBuilder();

        sb.AppendLine(localize("ToolSmbReportIdentity"));
        AppendRow(sb, localize("ToolSmbComputerName"), result.ComputerName ?? na);
        AppendRow(sb, localize("ToolSmbDomain"), result.Domain ?? na);
        AppendRow(sb, localize("ToolSmbDnsName"), result.DnsName ?? na);
        AppendRow(sb, localize("ToolSmbDnsDomain"), result.DnsDomain ?? na);
        AppendRow(sb, localize("ToolSmbForest"), result.Forest ?? na);
        AppendRow(sb, localize("ToolSmbOsBuild"), result.OsBuild ?? na);
        AppendRow(sb, localize("ToolSmbMac"), result.MacAddress ?? na);

        if (result.DialectRaw is not null)
        {
            sb.AppendLine();
            sb.AppendLine(localize("ToolSmbReportProtocol"));
            AppendRow(sb, localize("ToolSmbDialect"), result.Dialect ?? na);
            AppendRow(
                sb,
                localize("ToolSmbSigning"),
                result.SigningRequired == true ? localize("ToolSmbYes")
                    : result.SigningRequired == false ? localize("ToolSmbNo")
                    : na);
            AppendRow(sb, localize("ToolSmbServerGuid"), result.ServerGuid ?? na);
            AppendRow(
                sb,
                localize("ToolSmbSystemTime"),
                result.SystemTime?.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture) ?? na);
            AppendRow(
                sb,
                localize("ToolSmbBootTime"),
                result.BootTime?.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture) ?? na);
        }

        if (result.Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(localize("ToolSmbReportFindings"));
            foreach (var finding in result.Findings)
            {
                sb.AppendLine($"{SeverityPrefix(finding.Severity)} {FormatFindingMessage(finding, localize)}");
            }
        }

        return sb.ToString();
    }

    private static string FormatFindingMessage(SmbFinding finding, Func<string, string> localize)
    {
        var template = localize(finding.MessageKey);
        if (finding.MessageArgs is not { Count: > 0 })
        {
            return template;
        }

        return string.Format(template, finding.MessageArgs.Cast<object>().ToArray());
    }

    private static string SeverityPrefix(SmbFindingSeverity severity) => severity switch
    {
        SmbFindingSeverity.Success => "[OK]  ",
        SmbFindingSeverity.Warning => "[WARN]",
        SmbFindingSeverity.Critical => "[CRIT]",
        _ => "[INFO]",
    };

    private static void AppendRow(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"{label,-14}: {value}");
    }

    private static DateTimeOffset? WrapUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var utc = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }
}

/// <summary>
/// Origin of an <see cref="SmbEnumerationResult"/>.
/// </summary>
public enum SmbEnumerationSource
{
    Direct,
    Tunnel,
}

/// <summary>
/// Severity of an <see cref="SmbFinding"/>.
/// </summary>
public enum SmbFindingSeverity
{
    Info,
    Success,
    Warning,
    Critical,
}

/// <summary>
/// A single security finding, locale-neutral.
/// </summary>
public sealed record SmbFinding(
    SmbFindingSeverity Severity,
    string MessageKey,
    IReadOnlyList<string>? MessageArgs = null);

/// <summary>
/// Parsed observations from the three tunnel commands.
/// </summary>
public sealed record SmbTunnelObservations(
    string? Domain,
    string? OsInfo,
    string? ServerName,
    string? RpcServerName,
    string? RpcOsVersion,
    string? NetBiosName,
    string? NetBiosDomain,
    string? MacAddress,
    bool HasAnyData);

/// <summary>
/// Consolidated SMB enumeration output.
/// </summary>
public sealed record SmbEnumerationResult(
    SmbEnumerationSource Source,
    string? ComputerName,
    string? Domain,
    string? DnsName,
    string? DnsDomain,
    string? Forest,
    string? OsBuild,
    string? MacAddress,
    ushort? DialectRaw,
    string? Dialect,
    bool? SigningRequired,
    string? ServerGuid,
    DateTimeOffset? SystemTime,
    DateTimeOffset? BootTime,
    IReadOnlyList<SmbFinding> Findings,
    string? Report);
