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

namespace Heimdall.Core.Discovery;

/// <summary>
/// Defines the parameters for a network cartography scan.
/// </summary>
public sealed record ScanProfile(
    string Subnet,
    ScanDepth Depth,
    int[]? CustomPorts,
    int MaxConcurrency,
    int TimeoutMs,
    bool SkipPing,
    bool ReverseDns);

/// <summary>
/// Controls the breadth of the port scan.
/// </summary>
public enum ScanDepth { Quick, Standard, Deep }

/// <summary>
/// Complete snapshot of a network scan run.
/// </summary>
public sealed record NetworkScanSnapshot(
    string Id,
    DateTime Timestamp,
    ScanProfile Profile,
    string? GatewayName,
    TimeSpan Duration,
    List<HostScanResult> Hosts,
    List<VlanInfo>? DetectedVlans = null);

/// <summary>
/// SNMP system information retrieved via SNMPv2c GET.
/// </summary>
public sealed record SnmpInfo(
    string? SysDescr,
    string? SysName,
    string? SysLocation,
    string? SysObjectId = null,
    long? SysUpTimeSeconds = null,
    int? SysServices = null);

/// <summary>
/// NTLM challenge information extracted from SMB2/HTTP NTLMSSP exchange.
/// Provides hostname, domain, and OS build without credentials.
/// </summary>
public sealed record NtlmInfo(
    string? NetBiosComputerName,
    string? NetBiosDomainName,
    string? DnsComputerName,
    string? DnsDomainName,
    string? DnsForestName,
    string? OsBuild);

/// <summary>
/// SMB2 Negotiate response metadata extracted without authentication.
/// </summary>
public sealed record SmbNegotiateInfo(
    string? ServerGuid,
    ushort DialectRevision,
    bool SigningRequired,
    bool SigningEnabled,
    DateTime? SystemTime,
    DateTime? ServerStartTime,
    uint Capabilities);

/// <summary>
/// HTTP fingerprint evidence collected from cookies, error pages, and URL probes.
/// </summary>
public sealed record HttpFingerprint(
    string? Framework,
    string? ProductUrl,
    string? ProductName);

/// <summary>
/// SSDP/UPnP device information retrieved via M-SEARCH multicast discovery.
/// </summary>
public sealed record SsdpInfo(
    string? DeviceType,
    string? FriendlyName,
    string? Manufacturer,
    string? ModelName,
    string? Server,
    string? ModelNumber = null,
    string? SerialNumber = null,
    string? PresentationUrl = null);

/// <summary>
/// Inferred operating system with confidence and detection source.
/// </summary>
public sealed record OsFingerprint(
    string OsGuess,
    string Source,
    int Confidence);

/// <summary>
/// Aggregated scan result for a single host.
/// </summary>
public sealed record HostScanResult(
    string IpAddress,
    string? Hostname,
    bool IsAlive,
    long PingLatencyMs,
    List<ServiceResult> Services,
    RoleMatch? PrimaryRole,
    List<RoleMatch> AllRoles,
    string? MacAddress = null,
    string? Manufacturer = null,
    OsFingerprint? OsFingerprint = null,
    string? NetBiosName = null,
    string? NetBiosDomain = null,
    SnmpInfo? SnmpInfo = null,
    List<string>? MdnsServices = null,
    Dictionary<string, string>? HttpHeaders = null,
    SsdpInfo? SsdpInfo = null,
    NtlmInfo? NtlmInfo = null,
    string? SshHashFingerprint = null,
    int? FaviconHash = null,
    SmbNegotiateInfo? SmbInfo = null,
    HttpFingerprint? HttpFingerprint = null)
{
    /// <summary>
    /// Returns true if the host has any useful scan data beyond just an IP address.
    /// Used to filter out hosts discovered by ARP/DNS/NBNS that have no open ports
    /// and no identifying metadata.
    /// </summary>
    public bool HasMeaningfulData =>
        Services.Count > 0
        || Hostname is not null
        || PrimaryRole is not null
        || (Manufacturer is not null && Manufacturer != "-")
        || NetBiosName is not null
        || SnmpInfo is not null
        || (MdnsServices is { Count: > 0 })
        || SsdpInfo is not null
        || NtlmInfo is not null
        || OsFingerprint is not null;
}

/// <summary>
/// Result of probing a single port on a host.
/// </summary>
public sealed record ServiceResult(
    int Port,
    bool IsOpen,
    string? ServiceName,
    string? Banner,
    string? Version,
    long ResponseTimeMs,
    CertificateInfo? Certificate = null,
    Dictionary<string, string>? HttpHeaders = null);

/// <summary>
/// TLS certificate information gathered from a host's service.
/// </summary>
public sealed record CertificateInfo(
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    bool IsExpired,
    bool ExpiresSoon,
    string KeyAlgorithm,
    string SignatureAlgorithm,
    string[] SubjectAltNames,
    string TlsVersion,
    string Thumbprint);

/// <summary>
/// A heuristic role match with confidence score and supporting evidence.
/// </summary>
public sealed record RoleMatch(
    string Role,
    int Confidence,
    string[] Evidence);

/// <summary>
/// VLAN or network segment information inferred from scan results.
/// </summary>
public sealed record VlanInfo(
    int? VlanId,
    string Name,
    string Subnet,
    string? Gateway,
    List<string> MemberIps);

/// <summary>
/// Types of changes that can occur on a host between scans.
/// </summary>
public enum HostChangeType
{
    PortAdded,
    PortRemoved,
    HostnameChanged,
    RoleChanged,
    OsChanged,
    NetBiosChanged,
    ManufacturerChanged
}

/// <summary>
/// A single structural change detected on a host.
/// </summary>
public sealed record HostChange(
    HostChangeType Type,
    string? OldValue,
    string? NewValue,
    int? Port = null);

/// <summary>
/// Difference between two scan snapshots.
/// </summary>
public sealed record ScanDiff(
    List<HostScanResult> NewHosts,
    List<HostScanResult> RemovedHosts,
    List<(HostScanResult Old, HostScanResult New, List<HostChange> Changes)> ModifiedHosts);

// ── Knowledge Base models ────────────────────────────────────────

/// <summary>
/// Timestamp-tagged wrapper enabling per-field freshness tracking
/// in the knowledge base. The <paramref name="Source"/> field identifies
/// the scan vantage point (gateway name or "local").
/// </summary>
public sealed record Observation<T>(T Value, DateTime ObservedAt, string? Source = null);

/// <summary>
/// Accumulated knowledge about a single host, merged from multiple scans.
/// Keyed by IP address. Each field carries its own observation timestamp
/// so the merge engine can apply "newest wins" per field.
/// </summary>
public sealed record KnownHost(
    string IpAddress,
    DateTime FirstSeen,
    DateTime LastSeen,
    int ScanCount,
    Observation<string>? Hostname,
    Observation<bool>? IsAlive,
    Observation<long>? PingLatencyMs,
    List<KnownService> Services,
    Observation<RoleMatch>? PrimaryRole,
    List<Observation<RoleMatch>> AllRoles,
    Observation<string>? MacAddress,
    Observation<string>? Manufacturer,
    Observation<OsFingerprint>? OsFingerprint,
    Observation<string>? NetBiosName,
    Observation<string>? NetBiosDomain,
    Observation<SnmpInfo>? SnmpInfo,
    Observation<List<string>>? MdnsServices,
    Observation<Dictionary<string, string>>? HttpHeaders,
    Observation<SsdpInfo>? SsdpInfo,
    Observation<NtlmInfo>? NtlmInfo = null,
    Observation<string>? SshHashFingerprint = null,
    Observation<int>? FaviconHash = null,
    string? Notes = null);

/// <summary>
/// A service/port observation with its own timestamp for TTL-based cache invalidation.
/// </summary>
public sealed record KnownService(
    int Port,
    bool IsOpen,
    string? ServiceName,
    string? Banner,
    string? Version,
    CertificateInfo? Certificate,
    Dictionary<string, string>? HttpHeaders,
    DateTime ObservedAt,
    string? Source = null);

/// <summary>
/// Root container for the entire knowledge base.
/// </summary>
public sealed record NetworkKnowledgeBase(
    int Version,
    DateTime LastUpdated,
    KnowledgeBaseTtlConfig TtlConfig,
    Dictionary<string, KnownHost> Hosts);

/// <summary>
/// Configurable TTL values (in hours) that control when cached data is
/// considered stale and should be re-probed.
/// </summary>
public sealed record KnowledgeBaseTtlConfig(
    int HostAliveHours = 4,
    int PortScanHours = 24,
    int BannerGrabHours = 168,
    int DnsHours = 72,
    int UdpProbeHours = 168,
    int CertificateHours = 720,
    int MacAddressHours = 720);
