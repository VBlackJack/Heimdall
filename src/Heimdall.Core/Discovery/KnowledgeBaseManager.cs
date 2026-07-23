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
using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Manages the persistent network knowledge base: an accumulated,
/// per-field-timestamped store of all discovered hosts across scans.
/// Supports merge from scan snapshots, TTL-based staleness queries,
/// and automatic purge of old entries.
/// </summary>
public static class KnowledgeBaseManager
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int CurrentVersion = 1;

    internal static string GetKbPath() =>
        Path.Combine(ApplicationDataPathResolver.Resolve(), "network-kb.json");

    /// <summary>
    /// Loads the knowledge base from disk. Returns an empty KB if the file
    /// does not exist or is corrupt.
    /// </summary>
    public static async Task<NetworkKnowledgeBase> LoadAsync()
    {
        var path = GetKbPath();
        if (!File.Exists(path)) return CreateEmpty();

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var kb = JsonSerializer.Deserialize<NetworkKnowledgeBase>(json, DeserializeOptions);
            return kb ?? CreateEmpty();
        }
        catch
        {
            Logging.FileLogger.Warn("Knowledge base file is corrupt, starting fresh.");
            return CreateEmpty();
        }
    }

    /// <summary>
    /// Persists the knowledge base to disk using atomic temp-then-rename.
    /// </summary>
    public static async Task SaveAsync(NetworkKnowledgeBase kb)
    {
        var path = GetKbPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var updated = kb with { LastUpdated = DateTime.UtcNow };
        var json = JsonSerializer.Serialize(updated, SerializeOptions);

        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
            Logging.FileLogger.Log("DEBUG",
                $"Knowledge base saved: {kb.Hosts.Count} hosts, {json.Length} bytes → {path}");
        }
        catch (Exception ex)
        {
            Logging.FileLogger.Warn($"Knowledge base save failed: {ex.Message} (path={path})");
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Creates an empty knowledge base with default TTL configuration.
    /// </summary>
    public static NetworkKnowledgeBase CreateEmpty() => new(
        CurrentVersion,
        DateTime.UtcNow,
        new KnowledgeBaseTtlConfig(),
        new Dictionary<string, KnownHost>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Merges an entire scan snapshot into the knowledge base, updating or
    /// creating entries for each discovered host.
    /// </summary>
    public static NetworkKnowledgeBase MergeSnapshot(
        NetworkKnowledgeBase kb, NetworkScanSnapshot snapshot)
    {
        var hosts = new Dictionary<string, KnownHost>(
            kb.Hosts, StringComparer.OrdinalIgnoreCase);
        var scanTime = snapshot.Timestamp;
        var source = snapshot.GatewayName ?? "local";

        foreach (var scanned in snapshot.Hosts)
        {
            hosts.TryGetValue(scanned.IpAddress, out var existing);
            hosts[scanned.IpAddress] = MergeHost(existing, scanned, scanTime, source);
        }

        return kb with { Hosts = hosts };
    }

    /// <summary>
    /// Merges a single scanned host result into an existing (or null) known host,
    /// applying per-field "newest wins" logic.
    /// </summary>
    public static KnownHost MergeHost(
        KnownHost? existing, HostScanResult scanned,
        DateTime scanTime, string? source)
    {
        if (existing is null)
        {
            return FromScanResult(scanned, scanTime, source);
        }

        return existing with
        {
            LastSeen = scanTime > existing.LastSeen ? scanTime : existing.LastSeen,
            FirstSeen = scanTime < existing.FirstSeen ? scanTime : existing.FirstSeen,
            ScanCount = existing.ScanCount + 1,

            Hostname = MergeObservation(existing.Hostname, scanned.Hostname, scanTime, source),
            IsAlive = new Observation<bool>(scanned.IsAlive, scanTime, source),
            PingLatencyMs = new Observation<long>(scanned.PingLatencyMs, scanTime, source),

            Services = MergeServices(existing.Services, scanned.Services, scanTime, source),

            PrimaryRole = scanned.PrimaryRole is not null
                ? new Observation<RoleMatch>(scanned.PrimaryRole, scanTime, source)
                : existing.PrimaryRole,
            AllRoles = scanned.AllRoles.Count > 0
                ? scanned.AllRoles.Select(r => new Observation<RoleMatch>(r, scanTime, source)).ToList()
                : existing.AllRoles,

            MacAddress = MergeObservation(existing.MacAddress, scanned.MacAddress, scanTime, source),
            Manufacturer = MergeObservation(existing.Manufacturer, scanned.Manufacturer, scanTime, source),

            OsFingerprint = MergeOsFingerprint(
                existing.OsFingerprint, scanned.OsFingerprint, scanTime, source),

            NetBiosName = MergeObservation(existing.NetBiosName, scanned.NetBiosName, scanTime, source),
            NetBiosDomain = MergeObservation(existing.NetBiosDomain, scanned.NetBiosDomain, scanTime, source),
            SnmpInfo = MergeObservation(existing.SnmpInfo, scanned.SnmpInfo, scanTime, source),
            MdnsServices = MergeObservation(existing.MdnsServices, scanned.MdnsServices, scanTime, source),
            HttpHeaders = MergeObservation(existing.HttpHeaders, scanned.HttpHeaders, scanTime, source),
            SsdpInfo = MergeObservation(existing.SsdpInfo, scanned.SsdpInfo, scanTime, source),
            NtlmInfo = MergeObservation(existing.NtlmInfo, scanned.NtlmInfo, scanTime, source),
            SshHashFingerprint = MergeObservation(existing.SshHashFingerprint, scanned.SshHashFingerprint, scanTime, source),
            FaviconHash = scanned.FaviconHash is not null
                ? new Observation<int>(scanned.FaviconHash.Value, scanTime, source)
                : existing.FaviconHash,
        };
    }

    /// <summary>
    /// Checks whether a field observation is still fresh given a TTL in hours.
    /// </summary>
    public static bool IsFresh(DateTime observedAt, int ttlHours) =>
        (DateTime.UtcNow - observedAt).TotalHours < ttlHours;

    /// <summary>
    /// Checks whether a host's port scan data is fresh enough to skip re-scanning.
    /// </summary>
    public static bool ArePortsFresh(KnownHost host, KnowledgeBaseTtlConfig ttl) =>
        host.Services.Count > 0 &&
        host.Services.All(s => IsFresh(s.ObservedAt, ttl.PortScanHours));

    /// <summary>
    /// Checks whether a host's alive status is fresh enough to skip ping.
    /// </summary>
    public static bool IsAliveFresh(KnownHost host, KnowledgeBaseTtlConfig ttl) =>
        host.IsAlive is not null && IsFresh(host.IsAlive.ObservedAt, ttl.HostAliveHours);

    /// <summary>
    /// Checks whether UDP probe data (NetBIOS, SNMP) is fresh.
    /// Returns false if probes were never run (null observations) so the
    /// host gets re-probed on next scan rather than served from cache.
    /// Also returns false if the last probe timestamp exceeds the TTL.
    /// </summary>
    public static bool AreUdpProbesFresh(KnownHost host, KnowledgeBaseTtlConfig ttl)
    {
        // If we have at least one UDP observation with a timestamp, check freshness.
        // If we have NO observations at all, consider stale so the host gets probed.
        var hasAnyUdpData = host.NetBiosName is not null || host.SnmpInfo is not null;
        if (!hasAnyUdpData)
        {
            // Check if the host was scanned recently (use LastSeen as proxy for
            // "UDP probes were attempted but returned null"). If the host was seen
            // within the UDP TTL window, the null result is still valid — the host
            // simply doesn't respond to UDP probes and re-probing won't help.
            return IsFresh(host.LastSeen, ttl.UdpProbeHours);
        }

        return (host.NetBiosName is null || IsFresh(host.NetBiosName.ObservedAt, ttl.UdpProbeHours)) &&
               (host.SnmpInfo is null || IsFresh(host.SnmpInfo.ObservedAt, ttl.UdpProbeHours));
    }

    /// <summary>
    /// Checks whether DNS data is fresh.
    /// </summary>
    public static bool IsDnsFresh(KnownHost host, KnowledgeBaseTtlConfig ttl) =>
        host.Hostname is not null && IsFresh(host.Hostname.ObservedAt, ttl.DnsHours);

    /// <summary>
    /// Looks up a host by IP address.
    /// </summary>
    public static KnownHost? Lookup(NetworkKnowledgeBase kb, string ip) =>
        kb.Hosts.TryGetValue(ip, out var host) ? host : null;

    /// <summary>
    /// Returns the number of known hosts.
    /// </summary>
    public static int HostCount(NetworkKnowledgeBase kb) => kb.Hosts.Count;

    /// <summary>
    /// Removes hosts that have not been seen within <paramref name="maxAgeHours"/>.
    /// </summary>
    public static NetworkKnowledgeBase PurgeStaleHosts(
        NetworkKnowledgeBase kb, int maxAgeHours)
    {
        var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
        var filtered = new Dictionary<string, KnownHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, host) in kb.Hosts)
        {
            if (host.LastSeen >= cutoff)
                filtered[ip] = host;
        }
        return kb with { Hosts = filtered };
    }

    /// <summary>
    /// Clears all hosts from the knowledge base.
    /// </summary>
    public static NetworkKnowledgeBase Clear(NetworkKnowledgeBase kb) =>
        kb with { Hosts = new Dictionary<string, KnownHost>(StringComparer.OrdinalIgnoreCase) };

    /// <summary>
    /// Converts cached <see cref="KnownHost"/> data back to a
    /// <see cref="HostScanResult"/> for use in scan results when cache is hit.
    /// </summary>
    public static HostScanResult ToScanResult(KnownHost host) => new(
        host.IpAddress,
        host.Hostname?.Value,
        host.IsAlive?.Value ?? true,
        host.PingLatencyMs?.Value ?? 0,
        host.Services.Select(s => new ServiceResult(
            s.Port, s.IsOpen, s.ServiceName, s.Banner, s.Version, 0,
            s.Certificate, s.HttpHeaders)).ToList(),
        host.PrimaryRole?.Value,
        host.AllRoles.Select(r => r.Value).ToList(),
        host.MacAddress?.Value,
        host.Manufacturer?.Value,
        host.OsFingerprint?.Value,
        host.NetBiosName?.Value,
        host.NetBiosDomain?.Value,
        host.SnmpInfo?.Value,
        host.MdnsServices?.Value,
        host.HttpHeaders?.Value,
        host.SsdpInfo?.Value,
        host.NtlmInfo?.Value,
        host.SshHashFingerprint?.Value,
        host.FaviconHash?.Value);

    // ── Private helpers ──────────────────────────────────────────────

    private static KnownHost FromScanResult(
        HostScanResult r, DateTime scanTime, string? source) => new(
        IpAddress: r.IpAddress,
        FirstSeen: scanTime,
        LastSeen: scanTime,
        ScanCount: 1,
        Hostname: r.Hostname is not null ? new(r.Hostname, scanTime, source) : null,
        IsAlive: new(r.IsAlive, scanTime, source),
        PingLatencyMs: new(r.PingLatencyMs, scanTime, source),
        Services: r.Services.Select(s => new KnownService(
            s.Port, s.IsOpen, s.ServiceName, s.Banner, s.Version,
            s.Certificate, s.HttpHeaders, scanTime, source)).ToList(),
        PrimaryRole: r.PrimaryRole is not null ? new(r.PrimaryRole, scanTime, source) : null,
        AllRoles: r.AllRoles.Select(role => new Observation<RoleMatch>(role, scanTime, source)).ToList(),
        MacAddress: r.MacAddress is not null ? new(r.MacAddress, scanTime, source) : null,
        Manufacturer: r.Manufacturer is not null ? new(r.Manufacturer, scanTime, source) : null,
        OsFingerprint: r.OsFingerprint is not null ? new(r.OsFingerprint, scanTime, source) : null,
        NetBiosName: r.NetBiosName is not null ? new(r.NetBiosName, scanTime, source) : null,
        NetBiosDomain: r.NetBiosDomain is not null ? new(r.NetBiosDomain, scanTime, source) : null,
        SnmpInfo: r.SnmpInfo is not null ? new(r.SnmpInfo, scanTime, source) : null,
        MdnsServices: r.MdnsServices is not null ? new(r.MdnsServices, scanTime, source) : null,
        HttpHeaders: r.HttpHeaders is not null ? new(r.HttpHeaders, scanTime, source) : null,
        SsdpInfo: r.SsdpInfo is not null ? new(r.SsdpInfo, scanTime, source) : null,
        NtlmInfo: r.NtlmInfo is not null ? new(r.NtlmInfo, scanTime, source) : null,
        SshHashFingerprint: r.SshHashFingerprint is not null ? new(r.SshHashFingerprint, scanTime, source) : null,
        FaviconHash: r.FaviconHash is not null ? new(r.FaviconHash.Value, scanTime, source) : null);

    /// <summary>
    /// Newest-non-null-wins merge for nullable string observations.
    /// </summary>
    private static Observation<string>? MergeObservation(
        Observation<string>? existing, string? newValue,
        DateTime scanTime, string? source)
    {
        if (newValue is not null)
            return new Observation<string>(newValue, scanTime, source);
        return existing;
    }

    /// <summary>
    /// Newest-non-null-wins merge for generic reference-type observations.
    /// </summary>
    private static Observation<T>? MergeObservation<T>(
        Observation<T>? existing, T? newValue,
        DateTime scanTime, string? source) where T : class
    {
        if (newValue is not null)
            return new Observation<T>(newValue, scanTime, source);
        return existing;
    }

    /// <summary>
    /// OS fingerprint merge: higher confidence wins; on tie, newest wins.
    /// </summary>
    private static Observation<OsFingerprint>? MergeOsFingerprint(
        Observation<OsFingerprint>? existing, OsFingerprint? newOs,
        DateTime scanTime, string? source)
    {
        if (newOs is null) return existing;
        if (existing is null) return new(newOs, scanTime, source);

        if (newOs.Confidence > existing.Value.Confidence ||
            (newOs.Confidence == existing.Value.Confidence && scanTime >= existing.ObservedAt))
        {
            return new(newOs, scanTime, source);
        }
        return existing;
    }

    /// <summary>
    /// Merges service lists: per-port update with new data, preserving old
    /// ports not present in the new scan (they retain their old timestamps).
    /// </summary>
    private static List<KnownService> MergeServices(
        List<KnownService> existing, List<ServiceResult> scanned,
        DateTime scanTime, string? source)
    {
        var byPort = existing.ToDictionary(s => s.Port);

        foreach (var svc in scanned)
        {
            byPort[svc.Port] = new KnownService(
                svc.Port, svc.IsOpen, svc.ServiceName, svc.Banner, svc.Version,
                svc.Certificate, svc.HttpHeaders, scanTime, source);
        }

        return [.. byPort.Values.OrderBy(s => s.Port)];
    }
}
