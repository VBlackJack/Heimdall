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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Orchestrates network cartography scans: ping sweep, port scan, banner grab,
/// reverse DNS, and heuristic role classification.
/// </summary>
public sealed class CartographyEngine
{
    /// <summary>Windows image name of the ARP tool, which lives in the system directory.</summary>
    internal const string WindowsArpExecutableName = "arp.exe";

    /// <summary>Unix image name of the same tool, resolved through the platform's own PATH.</summary>
    private const string UnixArpExecutableName = "arp";

    /// <summary>Dump the whole table, which is what the parsers expect.</summary>
    private const string ArpArguments = "-a";

    // Cached compiled regexes for banner parsing
    private static readonly Regex ServerHeaderRegex = new(
        @"Server:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleTagRegex = new(
        @"<title>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Pre-compiled regex cache for HTTP header extraction (one per target header).</summary>
    private static readonly Dictionary<string, Regex> HttpHeaderRegexCache;

    static CartographyEngine()
    {
        string[] targetHeaders =
        [
            "Server", "X-Powered-By", "X-Generator", "X-AspNet-Version",
            "WWW-Authenticate", "X-Frame-Options", "Strict-Transport-Security"
        ];
        HttpHeaderRegexCache = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in targetHeaders)
        {
            HttpHeaderRegexCache[name] = new Regex(
                $@"(?:^|\n){Regex.Escape(name)}:\s*(.+?)(?:\r?\n|$)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    /// <summary>Ports that typically serve TLS-encrypted traffic.</summary>
    internal static readonly HashSet<int> TlsPorts = [443, 8443, 636, 993, 995, 465, 990, 3269, 9443];

    /// <summary>Subset of TLS ports where HTTPS (HTTP-over-TLS) is expected.</summary>
    internal static readonly HashSet<int> HttpsTlsPorts = [443, 8443, 9443];

    /// <summary>Top ports for quick reconnaissance.</summary>
    public static readonly int[] QuickPorts =
        [DefaultPorts.Ssh, DefaultPorts.HttpStd, DefaultPorts.HttpsStd, DefaultPorts.Rdp, DefaultPorts.Http,
         DefaultPorts.Dns, DefaultPorts.Smtp, DefaultPorts.MySql, DefaultPorts.PostgreSql, 445, 139, 88, 389,
         DefaultPorts.Snmp, DefaultPorts.Ftp, 554, 631, DefaultPorts.Vnc, DefaultPorts.HttpsAlt, DefaultPorts.Mssql,
         9100, 27017, 6379];

    /// <summary>~70 ports covering the most common enterprise services.</summary>
    public static readonly int[] StandardPorts =
        [DefaultPorts.Ftp, DefaultPorts.Ssh, DefaultPorts.Telnet, DefaultPorts.Smtp, DefaultPorts.Dns, 67,
         DefaultPorts.HttpStd, 88, 110, 111, 135, 139, 143, DefaultPorts.Snmp, 162, 389, DefaultPorts.HttpsStd,
         445, 464, DefaultPorts.Smtps, 514, 554, DefaultPorts.SmtpSubmission, 623, 631, DefaultPorts.Ldaps, 902,
         DefaultPorts.Imaps, DefaultPorts.Pop3s, DefaultPorts.Mssql, 1434, DefaultPorts.OracleDb, 1883, 1900,
         2049, 2375, 2376, 3000, 3128, 3260, 3268, DefaultPorts.MySql, DefaultPorts.Rdp, 5000, 5038, 5060,
         DefaultPorts.PostgreSql, 5665, DefaultPorts.Vnc, DefaultPorts.VncAlt, 5985, 5986, DefaultPorts.Redis,
         6443, 6514, 8006, 8008, DefaultPorts.Http, 8123, 8291, DefaultPorts.HttpsAlt, 8899, DefaultPorts.Prometheus,
         9100, 9200, 9300, 9443,
         10050, 10051, 10250, 27017, 33060, 62078];

    /// <summary>Fires during the ICMP ping sweep phase.</summary>
    public event Action<int, int>? HostDiscoveryProgress;

    /// <summary>Fires during port scanning of an individual host.</summary>
    public event Action<string, int, int>? PortScanProgress;

    /// <summary>Fires when a single host has been fully scanned.</summary>
    public event Action<HostScanResult>? HostCompleted;

    /// <summary>Fires during UDP enrichment phases (NetBIOS, SNMP).</summary>
    public event Action<string, string>? EnrichmentProgress;

    /// <summary>Fires when a host is served from the knowledge base cache.</summary>
    public event Action<string, string>? CacheHitProgress;

    /// <summary>
    /// Runs a full cartography scan against the specified profile.
    /// When <paramref name="knowledgeBase"/> is provided, fresh cached data
    /// is reused and redundant probes are skipped based on TTL configuration.
    /// </summary>
    public async Task<NetworkScanSnapshot> ScanAsync(
        ScanProfile profile,
        NetworkKnowledgeBase? knowledgeBase = null,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var hosts = new ConcurrentBag<HostScanResult>();

        var ipList = ParseCidr(profile.Subnet);
        ShuffleInPlace(ipList); // Randomize probe order to reduce IDS triggering

        // Pre-scan ARP table: seed alive hosts with IPs already known to the OS.
        // This catches hosts that respond to broadcast but block ICMP ping.
        var arpTable = GetArpTable();
        var subnetIpSet = new HashSet<string>(ipList, StringComparer.OrdinalIgnoreCase);
        var arpSeedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in arpTable.Keys)
        {
            if (subnetIpSet.Contains(ip))
                arpSeedIps.Add(ip);
        }

        Dictionary<string, (long LatencyMs, int Ttl)> pingResults;
        List<string> aliveHosts;
        if (profile.SkipPing)
        {
            aliveHosts = ipList;
            pingResults = [];
        }
        else
        {
            // KB cache: hosts known alive within TTL can skip ping
            var skipPingIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (knowledgeBase is not null)
            {
                foreach (var ip in ipList)
                {
                    var cached = KnowledgeBaseManager.Lookup(knowledgeBase, ip);
                    if (cached is not null &&
                        KnowledgeBaseManager.IsAliveFresh(cached, knowledgeBase.TtlConfig) &&
                        cached.IsAlive?.Value == true)
                    {
                        skipPingIps.Add(ip);
                    }
                }
            }

            var toPing = ipList.Where(ip => !skipPingIps.Contains(ip)).ToList();
            pingResults = toPing.Count > 0
                ? await PingSweepAsync(toPing, profile, ct).ConfigureAwait(false)
                : [];

            // Combine pinged results with KB-cached alive hosts
            aliveHosts = [.. pingResults.Keys];
            var aliveSet = new HashSet<string>(aliveHosts, StringComparer.OrdinalIgnoreCase);
            foreach (var ip in skipPingIps)
            {
                if (aliveSet.Add(ip))
                {
                    aliveHosts.Add(ip);
                    var cached = KnowledgeBaseManager.Lookup(knowledgeBase!, ip)!;
                    pingResults[ip] = (cached.PingLatencyMs?.Value ?? 0, 0);
                    CacheHitProgress?.Invoke(ip, "ping");
                }
            }

            // Seed ARP-known hosts that didn't respond to ping (ICMP blocked
            // but the OS saw them via broadcast/prior communication)
            foreach (var ip in arpSeedIps)
            {
                if (aliveSet.Add(ip))
                {
                    aliveHosts.Add(ip);
                    pingResults[ip] = (0, 0);
                }
            }

            // Additional discovery: reverse DNS + NetBIOS broadcast + TCP probes
            // Run in parallel to minimize wall-clock time
            var undiscovered = ipList.Where(ip => !aliveSet.Contains(ip)).ToList();
            if (undiscovered.Count > 0)
            {
                var extraHosts = await MultiProbeDiscoveryAsync(
                    undiscovered, profile, ct).ConfigureAwait(false);
                foreach (var ip in extraHosts)
                {
                    if (aliveSet.Add(ip))
                    {
                        aliveHosts.Add(ip);
                        pingResults[ip] = (0, 0);
                    }
                }
            }
        }

        var ports = profile.CustomPorts ?? profile.Depth switch
        {
            ScanDepth.Quick => QuickPorts,
            ScanDepth.Standard => StandardPorts,
            _ => StandardPorts
        };

        // Detect OS default gateway IPs for automatic router classification
        var gatewayIps = GetDefaultGatewayIps();

        // mDNS + SSDP discovery in parallel (multicast queries for all local devices)
        var mdnsResults = new Dictionary<string, List<string>>();
        var ssdpResults = new Dictionary<string, SsdpInfo>();

        var mdnsTask = Task.Run(async () =>
        {
            try
            {
                return await UdpProbeEngine.QueryMdnsServicesAsync(aliveHosts, 2000, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Dictionary<string, List<string>>(); }
        }, ct);

        var ssdpTask = Task.Run(async () =>
        {
            try
            {
                return await UdpProbeEngine.QuerySsdpAsync(aliveHosts, 2500, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Dictionary<string, SsdpInfo>(); }
        }, ct);

        await Task.WhenAll(mdnsTask, ssdpTask).ConfigureAwait(false);
        mdnsResults = mdnsTask.Result;
        ssdpResults = ssdpTask.Result;

        using var semaphore = new SemaphoreSlim(profile.MaxConcurrency);
        var ttlConfig = knowledgeBase?.TtlConfig;
        var tasks = aliveHosts.Select(async ip =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // KB cache: if all probe data is fresh, reuse cached result
                if (knowledgeBase is not null && ttlConfig is not null)
                {
                    var cached = KnowledgeBaseManager.Lookup(knowledgeBase, ip);
                    if (cached is not null &&
                        KnowledgeBaseManager.IsAliveFresh(cached, ttlConfig) &&
                        KnowledgeBaseManager.ArePortsFresh(cached, ttlConfig) &&
                        KnowledgeBaseManager.AreUdpProbesFresh(cached, ttlConfig))
                    {
                        var cachedResult = KnowledgeBaseManager.ToScanResult(cached);
                        hosts.Add(cachedResult);
                        CacheHitProgress?.Invoke(ip, "all");
                        HostCompleted?.Invoke(cachedResult);
                        return;
                    }
                }

                var (latency, ttl) = pingResults.TryGetValue(ip, out var p) ? p : (0L, 0);
                var mdnsServices = mdnsResults.TryGetValue(ip, out var ms) ? ms : null;
                var ssdpInfo = ssdpResults.TryGetValue(ip, out var ss) ? ss : null;
                var result = await ScanHostAsync(ip, ports, profile, arpTable,
                    gatewayIps, ttl, latency, mdnsServices, ssdpInfo, ct).ConfigureAwait(false);

                // Backfill null fields from KB (OS/hostname may exist from prior scans)
                if (knowledgeBase is not null)
                {
                    var prior = KnowledgeBaseManager.Lookup(knowledgeBase, ip);
                    if (prior is not null)
                    {
                        if (result.OsFingerprint is null && prior.OsFingerprint is not null)
                            result = result with { OsFingerprint = prior.OsFingerprint.Value };
                        if (result.Hostname is null && prior.Hostname is not null)
                            result = result with { Hostname = prior.Hostname.Value };
                    }
                }

                if (result.HasMeaningfulData)
                {
                    hosts.Add(result);
                    HostCompleted?.Invoke(result);
                }
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Refresh ARP table after scan (ping+TCP connections populated it)
        var freshArp = GetArpTable();
        var hostList = hosts.ToList();
        for (var i = 0; i < hostList.Count; i++)
        {
            var h = hostList[i];

            // Enrich MAC/Manufacturer from fresh ARP
            if (h.MacAddress is null && freshArp.TryGetValue(h.IpAddress, out var mac))
            {
                var mfr = OuiDatabase.LookupManufacturer(mac);
                h = h with { MacAddress = mac, Manufacturer = mfr };
            }
            // Re-resolve manufacturer if MAC exists but manufacturer was not found previously
            else if (h.MacAddress is not null && h.Manufacturer is null)
            {
                var mfr = OuiDatabase.LookupManufacturer(h.MacAddress);
                if (mfr is not null)
                    h = h with { Manufacturer = mfr };
            }

            // Backfill null fields from KB (OS, hostname, etc. may exist from prior scans)
            if (knowledgeBase is not null)
            {
                var cached = KnowledgeBaseManager.Lookup(knowledgeBase, h.IpAddress);
                if (cached is not null)
                {
                    if (h.OsFingerprint is null && cached.OsFingerprint is not null)
                        h = h with { OsFingerprint = cached.OsFingerprint.Value };
                    if (h.Hostname is null && cached.Hostname is not null)
                        h = h with { Hostname = cached.Hostname.Value };
                    if (h.MacAddress is null && cached.MacAddress is not null)
                    {
                        var mfr = cached.Manufacturer?.Value ?? OuiDatabase.LookupManufacturer(cached.MacAddress.Value);
                        h = h with { MacAddress = cached.MacAddress.Value, Manufacturer = mfr };
                    }
                }
            }

            hostList[i] = h;
        }

        var orderedHosts = hostList.OrderBy(h => IpToLong(h.IpAddress)).ToList();

        // Passive VLAN detection from discovered host IPs using scan CIDR
        var detectedVlans = VlanDetector.InferFromHosts(orderedHosts, profile.Subnet);

        return new NetworkScanSnapshot(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            profile,
            null,
            DateTime.UtcNow - startTime,
            orderedHosts,
            detectedVlans);
    }

    private async Task<Dictionary<string, (long LatencyMs, int Ttl)>> PingSweepAsync(
        List<string> ips, ScanProfile profile, CancellationToken ct)
    {
        var alive = new ConcurrentDictionary<string, (long LatencyMs, int Ttl)>();
        var completed = 0;
        using var semaphore = new SemaphoreSlim(Math.Min(64, profile.MaxConcurrency));

        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, Math.Min(profile.TimeoutMs, 1000))
                    .ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    var ttl = reply.Options?.Ttl ?? 0;
                    var latency = reply.RoundtripTime;
                    alive[ip] = (latency, ttl);
                }
                Interlocked.Increment(ref completed);
                HostDiscoveryProgress?.Invoke(completed, ips.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Interlocked.Increment(ref completed);
                HostDiscoveryProgress?.Invoke(completed, ips.Count);
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<string, (long LatencyMs, int Ttl)>(alive);
    }

    /// <summary>
    /// Secondary discovery for IPs that didn't respond to ICMP ping.
    /// Runs three probes in parallel: reverse DNS, NetBIOS Name Service, and
    /// TCP connect on a handful of high-value ports (SSH, HTTP, HTTPS, SMB, RDP).
    /// Returns the set of IPs where at least one probe succeeded.
    /// </summary>
    private static readonly int[] DiscoveryPorts =
        [DefaultPorts.Ssh, DefaultPorts.HttpStd, DefaultPorts.HttpsStd, 445, DefaultPorts.Rdp];

    private async Task<HashSet<string>> MultiProbeDiscoveryAsync(
        List<string> ips, ScanProfile profile, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<string, byte>();
        using var semaphore = new SemaphoreSlim(Math.Min(64, profile.MaxConcurrency));

        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Run all 3 probes in parallel per IP
                var dnsTask = ReverseDnsProbeAsync(ip, ct);
                var nbnsTask = NetBiosProbeAsync(ip, ct);
                var tcpTask = TcpDiscoveryProbeAsync(ip, profile.TimeoutMs, ct);

                await Task.WhenAll(dnsTask, nbnsTask, tcpTask).ConfigureAwait(false);

                if (dnsTask.Result || nbnsTask.Result || tcpTask.Result)
                {
                    found[ip] = 0;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* probe failed for this IP - skip */ }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. found.Keys];
    }

    private static async Task<bool> ReverseDnsProbeAsync(string ip, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip, ct).ConfigureAwait(false);
            // A real hostname (not the IP echoed back) means the host exists in DNS
            return entry.HostName is not null
                && !entry.HostName.Equals(ip, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task<bool> NetBiosProbeAsync(string ip, CancellationToken ct)
    {
        try
        {
            var (name, _, _) = await UdpProbeEngine.QueryNetBiosAsync(ip, 1500, ct)
                .ConfigureAwait(false);
            return name is not null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task<bool> TcpDiscoveryProbeAsync(
        string ip, int timeoutMs, CancellationToken ct)
    {
        // Quick TCP connect on a few key ports - any open port proves the host exists
        using var cts = new CancellationTokenSource(Math.Min(timeoutMs, 1500));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        var portTasks = DiscoveryPorts.Select(async port =>
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(ip, port, linked.Token).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        });

        var results = await Task.WhenAll(portTasks).ConfigureAwait(false);
        return results.Any(r => r);
    }

    private async Task<HostScanResult> ScanHostAsync(
        string ip, int[] ports, ScanProfile profile,
        Dictionary<string, string> arpTable, HashSet<string> gatewayIps,
        int ttl, long latencyMs,
        List<string>? mdnsServices, SsdpInfo? ssdpInfo, CancellationToken ct)
    {
        var services = new ConcurrentBag<ServiceResult>();
        var portCompleted = 0;

        using var hostSemaphore = new SemaphoreSlim(Math.Min(20, profile.MaxConcurrency));
        var portTasks = ports.Select(async port =>
        {
            await hostSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await ProbePortAsync(ip, port, profile.TimeoutMs, ct)
                    .ConfigureAwait(false);
                if (result.IsOpen)
                {
                    services.Add(result);
                }
                Interlocked.Increment(ref portCompleted);
                PortScanProgress?.Invoke(ip, portCompleted, ports.Length);
            }
            finally { hostSemaphore.Release(); }
        });

        await Task.WhenAll(portTasks).ConfigureAwait(false);

        // UDP enrichment: NetBIOS + SNMP in parallel
        EnrichmentProgress?.Invoke(ip, "NetBIOS/SNMP");
        var netBiosTask = UdpProbeEngine.QueryNetBiosAsync(ip, 1500, ct);
        var snmpTask = UdpProbeEngine.QuerySnmpAsync(ip, 2000, ct);
        await Task.WhenAll(netBiosTask, snmpTask).ConfigureAwait(false);

        var (nbName, nbDomain, nbMac) = await netBiosTask.ConfigureAwait(false);
        var snmpInfo = await snmpTask.ConfigureAwait(false);

        string? hostname = null;
        if (profile.ReverseDns)
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip, ct).ConfigureAwait(false);
                hostname = entry.HostName;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* DNS reverse failed */ }
        }

        var openServices = services.Where(s => s.IsOpen).ToList();
        var openPorts = openServices.Select(s => s.Port).ToList();
        var openPortSet = new HashSet<int>(openPorts);
        var banners = openServices.Select(s => s.Banner).ToList();

        // Advanced probes in parallel: NTLM+SMB (445), SSH HASSH (22), Favicon+HTTP (80/443)
        EnrichmentProgress?.Invoke(ip, "NTLM/SSH/HTTP");
        var probeTmo = Math.Min(profile.TimeoutMs, 3000);

        var smbTask = openPortSet.Contains(445)
            ? NtlmProbe.ProbeWithSmbInfoAsync(ip, probeTmo, ct)
            : Task.FromResult<(NtlmInfo?, SmbNegotiateInfo?)>((null, null));

        var hasshTask = openPortSet.Contains(22)
            ? SshFingerprinter.ComputeHashAsync(ip, 22, probeTmo, ct)
            : Task.FromResult<string?>(null);

        // HTTP port selection (prefer TLS)
        var httpPort = openPortSet.Contains(443) ? 443
            : openPortSet.Contains(80) ? 80
            : openPortSet.Contains(8443) ? 8443
            : openPortSet.Contains(8080) ? 8080
            : openPortSet.Contains(9090) ? 9090
            : 0;
        var httpIsTls = httpPort > 0 && TlsPorts.Contains(httpPort);

        var faviconTask = httpPort > 0
            ? FaviconHasher.HashAsync(ip, httpPort, httpIsTls, probeTmo, ct)
            : Task.FromResult<int?>(null);

        var httpProbeTask = httpPort > 0
            ? HttpFingerprinter.ProbeProductUrlsAsync(ip, httpPort, httpIsTls, probeTmo, ct)
            : Task.FromResult<(string?, string?)>((null, null));

        await Task.WhenAll(smbTask, hasshTask, faviconTask, httpProbeTask).ConfigureAwait(false);
        var (ntlmInfo, smbInfo) = smbTask.Result;
        var sshHash = hasshTask.Result;
        var faviconHash = faviconTask.Result;
        var (httpProduct, httpProductUrl) = httpProbeTask.Result;

        // HTTP framework detection from banner cookies/errors
        string? httpFramework = null;
        foreach (var svc in openServices)
        {
            if (svc.Banner is null) continue;
            httpFramework = HttpFingerprinter.DetectFrameworkFromCookies(svc.Banner)
                ?? HttpFingerprinter.DetectFrameworkFromErrorPage(svc.Banner);
            if (httpFramework is not null) break;
        }

        var httpFingerprint = (httpProduct ?? httpFramework) is not null
            ? new HttpFingerprint(httpFramework, httpProductUrl, httpProduct)
            : null;

        // Merge NTLM data into NetBIOS name/domain (NTLM is more reliable)
        if (ntlmInfo is not null)
        {
            nbName ??= ntlmInfo.NetBiosComputerName;
            nbDomain ??= ntlmInfo.NetBiosDomainName;
            hostname ??= ntlmInfo.DnsComputerName;
        }

        // OS fingerprinting from TTL, banners, open ports, SNMP, and NTLM
        var ttlOs = OsFingerprinter.GuessFromTtl(ttl);
        var bannerOs = OsFingerprinter.GuessFromBanners(openServices);
        var portOs = OsFingerprinter.GuessFromPorts(openPorts);
        var snmpOs = OsFingerprinter.GuessFromSnmp(snmpInfo?.SysDescr);
        var ntlmOs = ntlmInfo?.OsBuild is not null
            ? OsFingerprinter.GuessFromNtlm(ntlmInfo.OsBuild)
            : null;
        var osFingerprint = OsFingerprinter.MergeAll(ttlOs, bannerOs, portOs, snmpOs, ntlmOs);

        // Aggregate HTTP headers from all services
        var allHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in openServices)
        {
            if (svc.HttpHeaders is null) continue;
            foreach (var (key, value) in svc.HttpHeaders)
            {
                allHttpHeaders.TryAdd(key, value);
            }
        }

        // Collect all TLS certificates for classification
        var allCertificates = openServices
            .Where(s => s.Certificate is not null)
            .Select(s => s.Certificate!)
            .ToList();

        // Enhanced role classification with all evidence sources
        var roles = RoleClassifier.ClassifyEnriched(
            openPorts, banners, osFingerprint, nbName, nbDomain,
            snmpInfo, mdnsServices, allHttpHeaders.Count > 0 ? allHttpHeaders : null,
            allCertificates.Count > 0 ? allCertificates : null,
            ssdpInfo);
        var primaryRole = roles.Count > 0 ? roles[0] : null;

        // Default gateway auto-detection: boost or add Router/Gateway role
        if (gatewayIps.Contains(ip))
        {
            var routerIdx = roles.FindIndex(r =>
                r.Role.Contains("Router", StringComparison.OrdinalIgnoreCase) ||
                r.Role.Contains("Gateway", StringComparison.OrdinalIgnoreCase));
            if (routerIdx >= 0)
            {
                roles[routerIdx] = roles[routerIdx] with
                {
                    Confidence = Math.Min(99, Math.Max(roles[routerIdx].Confidence, 95)),
                    Evidence = [.. roles[routerIdx].Evidence, "default-gateway"]
                };
            }
            else
            {
                roles.Insert(0, new RoleMatch("Router/Gateway", 95, ["default-gateway"]));
            }
            // Re-sort after gateway boost
            roles = [.. roles.OrderByDescending(r => r.Confidence)];
            primaryRole = roles[0];
        }

        // Enrich with MAC address (prefer ARP, fallback to NetBIOS MAC)
        var mac = arpTable.TryGetValue(ip, out var m) ? m : nbMac;
        var manufacturer = mac is not null ? OuiDatabase.LookupManufacturer(mac) : null;

        // SNMP sysObjectID → IANA PEN vendor decode (fallback if OUI didn't identify)
        if (manufacturer is null && snmpInfo?.SysObjectId is not null)
        {
            manufacturer = IanaPenDatabase.LookupVendor(snmpInfo.SysObjectId);
        }

        // MAC-based role inference for devices with no ports and no role
        if (primaryRole is null && manufacturer is not null)
        {
            var inferred = RoleClassifier.InferFromManufacturer(manufacturer);
            if (inferred is not null)
            {
                roles = [inferred];
                primaryRole = inferred;
            }
        }

        return new HostScanResult(
            ip, hostname, true, latencyMs, [.. services], primaryRole, roles,
            mac, manufacturer,
            OsFingerprint: osFingerprint,
            NetBiosName: nbName,
            NetBiosDomain: nbDomain,
            SnmpInfo: snmpInfo,
            MdnsServices: mdnsServices,
            HttpHeaders: allHttpHeaders.Count > 0 ? allHttpHeaders : null,
            SsdpInfo: ssdpInfo,
            NtlmInfo: ntlmInfo,
            SshHashFingerprint: sshHash,
            FaviconHash: faviconHash,
            SmbInfo: smbInfo,
            HttpFingerprint: httpFingerprint);
    }

    private static async Task<ServiceResult> ProbePortAsync(
        string host, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            sw.Stop();

            var probeStrategy = GetProbeStrategy(port);
            string? banner = null;
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 1000;
                var buf = new byte[2048];

                if (stream.DataAvailable || await WaitForDataAsync(stream, 800, ct).ConfigureAwait(false))
                {
                    var read = await stream.ReadAsync(buf, linked.Token).ConfigureAwait(false);
                    if (read > 0)
                        banner = Encoding.ASCII.GetString(buf, 0, read).Trim();
                }

                if (banner is null && probeStrategy.PlaintextHttp)
                {
                    var probe = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: " + host + "\r\n\r\n");
                    await stream.WriteAsync(probe, linked.Token).ConfigureAwait(false);
                    await stream.FlushAsync(linked.Token).ConfigureAwait(false);

                    if (await WaitForDataAsync(stream, 1000, ct).ConfigureAwait(false))
                    {
                        var read = await stream.ReadAsync(buf, linked.Token).ConfigureAwait(false);
                        if (read > 0)
                            banner = Encoding.ASCII.GetString(buf, 0, read).Trim();
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-port timeout expired during banner grab - port is still open
            }
            catch (OperationCanceledException) { throw; }
            catch { /* banner grab failed, port is still open */ }

            CertificateInfo? certInfo = null;
            if (probeStrategy.TlsInspection)
            {
                var (cert, tlsBanner) = await InspectTlsWithHttpAsync(
                    host, port, probeStrategy.HttpOverTls, ct).ConfigureAwait(false);
                certInfo = cert;
                if (tlsBanner is not null)
                    banner = tlsBanner;
            }

            var (serviceName, version) = ParseBanner(banner, port);
            var httpHeaders = ExtractHttpHeaders(banner);

            return new ServiceResult(port, true, serviceName, banner, version,
                sw.ElapsedMilliseconds, certInfo, httpHeaders);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-port timeout - not a scan cancellation. Treat as closed port.
            return new ServiceResult(port, false, null, null, null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new ServiceResult(port, false, null, null, null, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Performs TLS handshake for certificate inspection. When <paramref name="probeHttp"/>
    /// is true (HTTPS ports only), also sends GET / over the encrypted stream to capture
    /// the HTTP banner and headers. Non-HTTP TLS services (SMTPS, LDAPS, IMAPS, etc.)
    /// are not probed with HTTP to avoid protocol mismatch.
    /// </summary>
    private static async Task<(CertificateInfo? Cert, string? HttpBanner)> InspectTlsWithHttpAsync(
        string host, int port, bool probeHttp, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(5000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

            // Accept all certificates: this is a network scanner inspecting cert metadata,
            // not a client trusting the connection. Validation is intentionally disabled.
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host
            }, linked.Token).ConfigureAwait(false);

            // Extract certificate
            var certInfo = ExtractCertInfo(ssl);

            // Only send HTTP GET on HTTPS-likely ports (443, 8443, 9443)
            string? httpBanner = null;
            if (probeHttp)
            {
                try
                {
                    var probe = Encoding.ASCII.GetBytes(
                        $"GET / HTTP/1.0\r\nHost: {host}\r\nConnection: close\r\n\r\n");
                    await ssl.WriteAsync(probe, linked.Token).ConfigureAwait(false);
                    await ssl.FlushAsync(linked.Token).ConfigureAwait(false);

                    var buf = new byte[4096];
                    var read = await ssl.ReadAsync(buf, linked.Token).ConfigureAwait(false);
                    if (read > 0)
                        httpBanner = Encoding.ASCII.GetString(buf, 0, read).Trim();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* per-port TLS timeout */ }
                catch (OperationCanceledException) { throw; }
                catch { /* HTTP probe over TLS failed, cert is still valid */ }
            }

            return (certInfo, httpBanner);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logging.FileLogger.Log("DEBUG",
                $"TLS inspection failed for {host}:{port}: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    private static CertificateInfo? ExtractCertInfo(SslStream ssl)
    {
        var remoteCert = ssl.RemoteCertificate;
        if (remoteCert is null) return null;

        using var x509 = new X509Certificate2(remoteCert);

        var sans = new List<string>();
        foreach (var ext in x509.Extensions)
        {
            if (ext.Oid?.Value == "2.5.29.17")
            {
                var sanStr = ext.Format(false);
                foreach (var part in sanStr.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.StartsWith("DNS Name=", StringComparison.OrdinalIgnoreCase))
                        sans.Add(trimmed["DNS Name=".Length..]);
                    else if (trimmed.StartsWith("IP Address=", StringComparison.OrdinalIgnoreCase))
                        sans.Add(trimmed["IP Address=".Length..]);
                }
            }
        }

        var keyAlg = x509.PublicKey.Oid.FriendlyName ?? "Unknown";
        var keySize = 0;
        try
        {
            using var rsa = x509.PublicKey.GetRSAPublicKey();
            using var ecdsa = x509.PublicKey.GetECDsaPublicKey();
            keySize = rsa?.KeySize ?? ecdsa?.KeySize ?? 0;
        }
        catch { /* key type may not support size extraction */ }

        return new CertificateInfo(
            x509.Subject,
            x509.Issuer,
            x509.NotBefore,
            x509.NotAfter,
            x509.NotAfter < DateTime.UtcNow,
            x509.NotAfter < DateTime.UtcNow.AddDays(30) && x509.NotAfter >= DateTime.UtcNow,
            keySize > 0 ? $"{keyAlg} {keySize}" : keyAlg,
            x509.SignatureAlgorithm.FriendlyName ?? "",
            [.. sans],
            ssl.SslProtocol.ToString(),
            x509.GetCertHashString(HashAlgorithmName.SHA256));
    }

    private static (string? service, string? version) ParseBanner(string? banner, int port)
    {
        if (string.IsNullOrWhiteSpace(banner))
        {
            var fallback = RoleClassifier.GetPortServiceName(port);
            return (fallback != $"Port-{port}" ? fallback : null, null);
        }

        if (banner.StartsWith("SSH-", StringComparison.Ordinal))
        {
            var parts = banner.Split('-', 3);
            return ("SSH", parts.Length >= 3 ? parts[2].Split(' ')[0] : null);
        }

        if (banner.StartsWith("220", StringComparison.Ordinal))
            return ("FTP", banner.Length > 4 ? banner[4..].Trim() : null);

        if (banner.Contains("SMTP", StringComparison.OrdinalIgnoreCase) ||
            banner.Contains("Postfix", StringComparison.OrdinalIgnoreCase) ||
            banner.Contains("Sendmail", StringComparison.OrdinalIgnoreCase))
            return ("SMTP", banner);

        if (banner.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            var serverMatch = ServerHeaderRegex.Match(banner);
            var serverVersion = serverMatch.Success ? serverMatch.Groups[1].Value.Trim() : null;

            // Extract <title> for device identification (login pages often reveal appliance type)
            var titleMatch = TitleTagRegex.Match(banner);
            if (titleMatch.Success)
            {
                var title = titleMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(title))
                {
                    return ("HTTP", serverVersion is not null ? $"{serverVersion} [{title}]" : title);
                }
            }

            return ("HTTP", serverVersion);
        }

        if (banner.Contains("mysql", StringComparison.OrdinalIgnoreCase) ||
            (port == 3306 && banner.Length > 5))
            return ("MySQL", banner);

        if (banner.StartsWith('+') || banner.StartsWith("-ERR", StringComparison.Ordinal))
            return ("Redis", banner);

        if (banner.StartsWith("RFB ", StringComparison.Ordinal))
            return ("VNC", banner[4..]);

        return (null, banner);
    }

    /// <summary>
    /// Extracts security-relevant HTTP headers from an HTTP banner response.
    /// </summary>
    private static Dictionary<string, string>? ExtractHttpHeaders(string? banner)
    {
        if (string.IsNullOrEmpty(banner) ||
            !banner.StartsWith("HTTP/", StringComparison.Ordinal))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (headerName, regex) in HttpHeaderRegexCache)
        {
            var match = regex.Match(banner);
            if (match.Success)
                headers[headerName] = match.Groups[1].Value.Trim();
        }

        return headers.Count > 0 ? headers : null;
    }

    private static bool IsLikelyHttpPort(int port) =>
        port is 80 or 443 or 8080 or 8443 or 9090 or 9443 or 3000 or 5000 or 8000 or 8006 or 8008
            or 8123 or 8888;

    /// <summary>
    /// Determines the HTTP probe strategy for a given port.
    /// Returns (shouldPlaintextProbe, shouldTlsProbe, shouldHttpOverTls).
    /// This encodes the exact decision path used by <see cref="ProbePortAsync"/>.
    /// </summary>
    internal static (bool PlaintextHttp, bool TlsInspection, bool HttpOverTls) GetProbeStrategy(int port)
    {
        var isTls = TlsPorts.Contains(port);
        var isHttpLikely = IsLikelyHttpPort(port);
        return (
            PlaintextHttp: isHttpLikely && !isTls,
            TlsInspection: isTls,
            HttpOverTls: isTls && HttpsTlsPorts.Contains(port)
        );
    }

    private static async Task<bool> WaitForDataAsync(
        NetworkStream stream, int maxWaitMs, CancellationToken ct)
    {
        var waited = 0;
        while (waited < maxWaitMs && !ct.IsCancellationRequested)
        {
            if (stream.DataAvailable) return true;
            await Task.Delay(50, ct).ConfigureAwait(false);
            waited += 50;
        }
        return stream.DataAvailable;
    }

    /// <summary>
    /// Generates the list of host IPs from a CIDR notation string.
    /// Safety limit: refuses to scan larger than /16.
    /// </summary>
    public static List<string> ParseCidr(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var ip) ||
            !int.TryParse(parts[1], out var prefix))
            return [];
        if (prefix < 16 || prefix > 30) return [];

        var ipBytes = ip.GetAddressBytes();
        var ipUint = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ipUint & mask;
        var broadcast = network | ~mask;

        var result = new List<string>();
        for (var addr = network + 1; addr < broadcast; addr++)
        {
            result.Add($"{(addr >> 24) & 0xFF}.{(addr >> 16) & 0xFF}.{(addr >> 8) & 0xFF}.{addr & 0xFF}");
        }
        return result;
    }

    /// <summary>Fisher-Yates in-place shuffle for probe order randomization.</summary>
    private static void ShuffleInPlace<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public static long IpToLong(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return 0;
        if (!long.TryParse(parts[0], out var a) || !long.TryParse(parts[1], out var b) ||
            !long.TryParse(parts[2], out var c) || !long.TryParse(parts[3], out var d))
            return 0;
        return a << 24 | b << 16 | c << 8 | d;
    }

    /// <summary>
    /// Builds CSV content from a scan snapshot.
    /// </summary>
    public static string BuildCsvExport(
        NetworkScanSnapshot snapshot,
        Func<string, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var builder = new StringBuilder();
        builder.AppendLine(L(localize, "ToolNetMapExportHeader"));

        foreach (var host in snapshot.Hosts.Where(ShouldExportHost))
        {
            var openPorts = host.Services
                .Where(service => service.IsOpen)
                .Select(service => service.Port)
                .OrderBy(port => port)
                .ToList();
            var services = host.Services
                .Where(service => service.IsOpen && service.ServiceName is not null)
                .Select(service => service.ServiceName!)
                .Distinct()
                .OrderBy(name => name);

            var certificate = host.Services.FirstOrDefault(service => service.Certificate is not null)?.Certificate;
            var certStatus = certificate is null
                ? string.Empty
                : certificate.IsExpired
                    ? L(localize, "ToolNetMapCertExpired")
                    : certificate.ExpiresSoon
                        ? L(localize, "ToolNetMapCertExpiring")
                        : L(localize, "ToolNetMapCertValid");
            var tlsStatus = certificate is null
                ? string.Empty
                : certificate.IsExpired
                    ? $"{L(localize, "ToolNetMapCertExpired")}!"
                    : certificate.ExpiresSoon
                        ? $"{certificate.TlsVersion} ({L(localize, "ToolNetMapCertExpiring")} {certificate.NotAfter:yyyy-MM-dd})"
                        : $"{certificate.TlsVersion} ({L(localize, "ToolNetMapCertValid")} {certificate.NotAfter:yyyy-MM-dd})";

            var confidence = host.PrimaryRole is not null
                ? $"{host.PrimaryRole.Confidence}%"
                : host.Manufacturer is not null ? "MAC" : "\u2014";
            var role = host.PrimaryRole?.Role
                ?? (host.Manufacturer is not null ? host.Manufacturer : "\u2014");
            var mdnsServices = host.MdnsServices is { Count: > 0 }
                ? string.Join("; ", host.MdnsServices)
                : string.Empty;
            var vlan = snapshot.DetectedVlans?
                .FirstOrDefault(item => item.MemberIps.Contains(host.IpAddress))
                is { } detectedVlan
                    ? $"VLAN {detectedVlan.VlanId} ({detectedVlan.Subnet})"
                    : string.Empty;
            var ssdpSummary = host.SsdpInfo is not null
                ? FormatSsdpSummary(host.SsdpInfo)
                : string.Empty;

            builder.AppendLine(string.Join(",",
                EscapeCsv(host.IpAddress),
                EscapeCsv(host.Hostname ?? string.Empty),
                EscapeCsv(host.OsFingerprint?.OsGuess ?? string.Empty),
                EscapeCsv(string.Join(", ", openPorts)),
                EscapeCsv(string.Join(", ", services)),
                EscapeCsv(tlsStatus),
                EscapeCsv(certificate?.Subject ?? string.Empty),
                EscapeCsv(certificate?.NotAfter.ToString("yyyy-MM-dd") ?? string.Empty),
                EscapeCsv(certificate?.KeyAlgorithm ?? string.Empty),
                EscapeCsv(certStatus),
                EscapeCsv(role),
                EscapeCsv(confidence),
                EscapeCsv(host.NetBiosName ?? string.Empty),
                EscapeCsv(host.NetBiosDomain ?? string.Empty),
                EscapeCsv(host.SnmpInfo?.SysName ?? string.Empty),
                EscapeCsv(host.SnmpInfo?.SysDescr ?? string.Empty),
                EscapeCsv(host.SnmpInfo?.SysLocation ?? string.Empty),
                EscapeCsv(host.SnmpInfo?.SysObjectId ?? string.Empty),
                EscapeCsv(mdnsServices),
                EscapeCsv(host.Manufacturer ?? string.Empty),
                EscapeCsv(vlan),
                EscapeCsv(ssdpSummary),
                EscapeCsv(host.NtlmInfo?.DnsComputerName ?? string.Empty),
                EscapeCsv(host.NtlmInfo?.DnsDomainName ?? string.Empty),
                EscapeCsv(host.NtlmInfo?.OsBuild ?? string.Empty),
                EscapeCsv(host.SshHashFingerprint ?? string.Empty),
                EscapeCsv(host.FaviconHash?.ToString() ?? string.Empty)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats SSDP/UPnP discovery info into a single summary string.
    /// </summary>
    public static string FormatSsdpSummary(SsdpInfo ssdp)
    {
        ArgumentNullException.ThrowIfNull(ssdp);

        var parts = new List<string>();
        if (ssdp.FriendlyName is not null) parts.Add(ssdp.FriendlyName);
        else if (ssdp.ModelName is not null) parts.Add(ssdp.ModelName);
        else if (ssdp.DeviceType is not null) parts.Add(ssdp.DeviceType);
        if (ssdp.Manufacturer is not null) parts.Add(ssdp.Manufacturer);
        if (ssdp.Server is not null) parts.Add(ssdp.Server);
        return parts.Count > 0 ? string.Join(" | ", parts) : string.Empty;
    }

    /// <summary>
    /// Detects the OS default gateway IP addresses from all active network interfaces.
    /// Used to auto-classify the gateway host as Router/Gateway with high confidence.
    /// </summary>
    private static HashSet<string> GetDefaultGatewayIps()
    {
        var gateways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                var props = iface.GetIPProperties();
                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        gateways.Add(gw.Address.ToString());
                }
            }
        }
        catch { /* gateway detection unavailable */ }
        return gateways;
    }

    /// <summary>
    /// Builds the start info for the Windows ARP tool.
    /// </summary>
    internal static ProcessStartInfo CreateWindowsArpStartInfo() =>
        CreateArpStartInfo(
            SystemExecutablePath.InSystemDirectory(WindowsArpExecutableName),
            SystemExecutablePath.SystemDirectory);

    /// <summary>
    /// Builds the start info for the Unix ARP tool, resolved through the platform's own PATH.
    /// </summary>
    private static ProcessStartInfo CreateUnixArpStartInfo() =>
        CreateArpStartInfo(UnixArpExecutableName, string.Empty);

    private static ProcessStartInfo CreateArpStartInfo(string fileName, string workingDirectory) =>
        new()
        {
            FileName = fileName,
            Arguments = ArpArguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

    /// <summary>
    /// Retrieves the local ARP table by running "arp -a" (Windows/macOS) or reading /proc/net/arp (Linux).
    /// Returns a dictionary mapping IP addresses to MAC addresses.
    /// </summary>
    private static Dictionary<string, string> GetArpTable()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                using var proc = Process.Start(CreateWindowsArpStartInfo());
                if (proc is null) return result;

                var output = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { /* already exited */ } }

                // Parse lines like: "  10.0.0.1             aa-bb-cc-dd-ee-ff     dynamic"
                // Skip "Interface:" header lines (contain "---" which is not a valid MAC)
                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var ip = parts[0];
                        var mac = parts[1];
                        // Validate MAC format: exactly 5 dashes (aa-bb-cc-dd-ee-ff)
                        if (IPAddress.TryParse(ip, out _) &&
                            mac.Length == 17 && mac.Count(c => c == '-') == 5)
                        {
                            result[ip] = mac;
                        }
                    }
                }
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                if (System.IO.File.Exists("/proc/net/arp"))
                {
                    var lines = System.IO.File.ReadAllLines("/proc/net/arp");
                    foreach (var line in lines.Skip(1)) // Skip header
                    {
                        var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            var ip = parts[0];
                            var mac = parts[3];
                            if (mac != "00:00:00:00:00:00" && IPAddress.TryParse(ip, out _))
                            {
                                result[ip] = mac.Replace(':', '-');
                            }
                        }
                    }
                }
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                using var proc = Process.Start(CreateUnixArpStartInfo());
                if (proc is null) return result;
                var output = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { /* already exited */ } }

                // Parse lines like: "? (192.168.1.1) at 00:11:22:33:44:55 on en0 ifscope [ether]"
                foreach (var line in output.Split('\n'))
                {
                    var match = Regex.Match(line, @"\((.*?)\)\s+at\s+([a-fA-F0-9:]+)");
                    if (match.Success)
                    {
                        result[match.Groups[1].Value] = match.Groups[2].Value.Replace(':', '-');
                    }
                }
            }
        }
        catch { /* ARP table unavailable */ }
        return result;
    }

    private static bool ShouldExportHost(HostScanResult host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.Services.Any(service => service.IsOpen)
            || host.PrimaryRole is not null
            || !string.IsNullOrWhiteSpace(host.Hostname)
            || !string.IsNullOrWhiteSpace(host.Manufacturer);
    }

    private static string EscapeCsv(string value)
    {
        var sanitized = InputValidator.SanitizeCsvCell(value)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{sanitized}\"";
    }

    private static string L(Func<string, string>? localize, string key)
        => localize?.Invoke(key) ?? key;
}
