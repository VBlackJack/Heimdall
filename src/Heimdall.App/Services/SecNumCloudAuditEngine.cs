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

using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Discovery;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Orchestrates a SecNumCloud-aligned security audit across four chapters
/// (Network, Cryptography, Access Control, Operations) with 15 individual checks.
/// Uses CartographyEngine for host discovery and probes TLS, SSH, SMB, SNMP,
/// HTTP headers, DNS records, and default credentials.
/// </summary>
public sealed class SecNumCloudAuditEngine
{
    /// <summary>Executable that resolves DNS records locally.</summary>
    internal const string NslookupExecutableName = "nslookup.exe";

    // ── Events ───────────────────────────────────────────────────────

    /// <summary>Fires per-host within each audit phase (phaseName, completed, total).</summary>
    public event Action<string, int, int>? PhaseProgress;

    /// <summary>Fires when the engine transitions between major phases.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fires after each individual check completes.</summary>
    public event Action<AuditCheck>? CheckCompleted;

    // ── Localization ────────────────────────────────────────────────

    private readonly Func<string, string> _l;

    /// <summary>
    /// Initializes a new instance with an optional localization delegate.
    /// When omitted, keys are returned as-is (passthrough).
    /// </summary>
    public SecNumCloudAuditEngine(Func<string, string>? localize = null)
    {
        _l = localize ?? (key => key);
    }

    // ── Constants ────────────────────────────────────────────────────

    private const int DefaultTimeoutMs = 5_000;
    private const int SshTimeoutMs = 8_000;
    private const int DnsTimeoutMs = 10_000;
    private const int MaxConcurrency = 20;

    /// <summary>Ports considered standard/expected in a secure enterprise environment.</summary>
    private static readonly HashSet<int> StandardPorts =
        [DefaultPorts.Ssh, DefaultPorts.HttpStd, DefaultPorts.HttpsStd, DefaultPorts.Rdp];

    /// <summary>Ports that typically serve TLS-encrypted traffic.</summary>
    private static readonly HashSet<int> TlsPorts = [443, 8443, 636, 993, 995, 465, 990, 3269, 9443];

    /// <summary>Ports that typically serve HTTP/HTTPS traffic.</summary>
    private static readonly HashSet<int> HttpPorts = [80, 443, 8080, 8443, 9443];

    /// <summary>Well-known SNMP community strings to test.</summary>
    private static readonly string[] DefaultSnmpCommunities = ["public", "private", "community"];

    /// <summary>Default SSH credential pairs to test.</summary>
    private static readonly (string User, string Pass)[] DefaultSshCredentials =
    [
        ("root", "root"),
        ("admin", "admin"),
        ("admin", "password"),
        ("root", "toor"),
        ("pi", "raspberry"),
    ];

    /// <summary>HTTP security headers required by SecNumCloud hardening guidelines.</summary>
    private static readonly string[] RequiredSecurityHeaders =
    [
        "Strict-Transport-Security",
        "Content-Security-Policy",
        "X-Frame-Options",
        "X-Content-Type-Options",
    ];

    // ── TLS protocol definitions (requires obsolete enum values) ─────

#pragma warning disable CA5397, CS0618, SYSLIB0039 // Obsolete TLS/SSL versions - needed to detect insecure configs
    private static readonly (string Name, SslProtocols Protocol, bool IsWeak)[] TlsProtocols =
    [
        ("SSL 3.0",  SslProtocols.Ssl3,  true),
        ("TLS 1.0",  SslProtocols.Tls,   true),
        ("TLS 1.1",  SslProtocols.Tls11, true),
        ("TLS 1.2",  SslProtocols.Tls12, false),
        ("TLS 1.3",  SslProtocols.Tls13, false),
    ];
#pragma warning restore CA5397, CS0618, SYSLIB0039

    /// <summary>Pre-compiled regex to extract CN from certificate subject.</summary>
    private static readonly Regex CnRegex = new(@"CN=([^,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Weak cipher suite names (substring matching against negotiated cipher) ──

    private static readonly string[] WeakCipherPatterns =
    [
        "RC4", "3DES", "DES_CBC", "NULL", "EXPORT", "anon",
    ];

    // ── Embedded CVE database (simplified; matches banner versions) ──

    private sealed record CveEntry(
        string Id,
        double CvssScore,
        string Severity,
        string Summary,
        string AffectedVersions,
        Func<string, bool> IsAffected);

    private static readonly Dictionary<string, List<CveEntry>> CveDatabase =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenSSH"] =
        [
            new("CVE-2024-6387", 8.1, "High",
                "regreSSHion: Race condition in signal handler allows unauthenticated RCE",
                "8.5p1 - 9.7p1", v => VersionInRange(v, "8.5", "9.7")),
            new("CVE-2023-38408", 9.8, "Critical",
                "PKCS#11 provider: RCE via forwarded ssh-agent",
                "< 9.3p2", v => VersionBelow(v, "9.3.2")),
            new("CVE-2023-48795", 5.9, "Medium",
                "Terrapin: Prefix truncation attack on SSH BPP (strict kex bypass)",
                "< 9.6", v => VersionBelow(v, "9.6")),
        ],
            ["Apache"] =
        [
            new("CVE-2021-41773", 7.5, "High",
                "Path traversal and file disclosure in Apache HTTP Server 2.4.49",
                "2.4.49", v => VersionEquals(v, "2.4.49")),
            new("CVE-2023-25690", 9.8, "Critical",
                "HTTP request smuggling via mod_proxy with RewriteRule",
                "< 2.4.56", v => VersionBelow(v, "2.4.56")),
        ],
            ["nginx"] =
        [
            new("CVE-2021-23017", 7.7, "High",
                "DNS resolver off-by-one heap write allows RCE",
                "0.6.18 - 1.20.0", v => VersionInRange(v, "0.6.18", "1.20.0")),
            new("CVE-2022-41741", 7.8, "High",
                "Memory corruption via crafted mp4 file (ngx_http_mp4_module)",
                "< 1.23.2", v => VersionBelow(v, "1.23.2")),
        ],
            ["MySQL"] =
        [
            new("CVE-2023-21980", 7.1, "High",
                "Client: buffer overflow in C API",
                "< 8.0.33", v => VersionBelow(v, "8.0.33")),
        ],
            ["PostgreSQL"] =
        [
            new("CVE-2024-10979", 8.8, "High",
                "Arbitrary code execution via environment variable manipulation in PL/Perl",
                "< 17.1 / < 16.5 / < 15.9", v => VersionBelow(v, "17.1")),
            new("CVE-2023-5869", 8.8, "High",
                "Buffer overflow in integer overflow in array modification",
                "< 16.1", v => VersionBelow(v, "16.1")),
        ],
            ["Microsoft IIS"] =
        [
            new("CVE-2022-21907", 9.8, "Critical",
                "HTTP Protocol Stack (http.sys) RCE in trailer support",
                "IIS 10.0 (Win Server 2022)", v => v.StartsWith("10.", StringComparison.Ordinal) || v == "10"),
        ],
        };

    /// <summary>Banner regex patterns mapping raw banners to product names.</summary>
    private static readonly (Regex Pattern, string Software)[] BannerPatterns =
    [
        (new(@"OpenSSH[_/ ]?(\d[\d.p]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "OpenSSH"),
        (new(@"Apache(?:/| HTTPD |httpd[ /])(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Apache"),
        (new(@"nginx/(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "nginx"),
        (new(@"Microsoft-IIS/(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Microsoft IIS"),
        (new(@"MySQL(?:.*?)?(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MySQL"),
        (new(@"PostgreSQL[/ ](\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "PostgreSQL"),
    ];

    // ═══════════════════════════════════════════════════════════════════
    //  Main entry point
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs the full SecNumCloud audit and returns the completed report.
    /// Each chapter and check is independent - a single failure never aborts
    /// the entire audit.
    /// </summary>
    public async Task<AuditReport> RunAuditAsync(
        AuditScope scope,
        AuditOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        if ((scope.Targets is null || scope.Targets.Count == 0) && string.IsNullOrWhiteSpace(scope.Subnet))
            throw new ArgumentException("Audit scope must have at least one target or a subnet.", nameof(scope));

        var report = new AuditReport { StartTime = DateTime.UtcNow, Scope = scope };

        // Phase 1: Discovery
        StatusChanged?.Invoke(_l("AuditPhaseDiscovery"));
        var (hosts, snapshot) = await DiscoverHostsAsync(scope, options.Depth, ct)
            .ConfigureAwait(false);
        report.NetworkSnapshot = snapshot;

        // Phase 2: Network Security
        if (options.CheckNetwork)
        {
            StatusChanged?.Invoke(_l("AuditPhaseNetwork"));
            var chapter = BuildNetworkChapter();
            await RunNetworkChecksAsync(chapter, hosts, snapshot, ct).ConfigureAwait(false);
            report.Chapters.Add(chapter);
        }

        // Phase 3: Cryptography
        if (options.CheckCrypto)
        {
            StatusChanged?.Invoke(_l("AuditPhaseCrypto"));
            var chapter = BuildCryptoChapter();
            await RunCryptoChecksAsync(chapter, hosts, snapshot, ct).ConfigureAwait(false);
            report.Chapters.Add(chapter);
        }

        // Phase 4: Access Control
        if (options.CheckAccess)
        {
            StatusChanged?.Invoke(_l("AuditPhaseAccess"));
            var chapter = BuildAccessChapter();
            await RunAccessChecksAsync(chapter, hosts, snapshot, ct).ConfigureAwait(false);
            report.Chapters.Add(chapter);
        }

        // Phase 5: Operations Security
        if (options.CheckOperations)
        {
            StatusChanged?.Invoke(_l("AuditPhaseOperations"));
            var chapter = BuildOperationsChapter();
            await RunOperationsChecksAsync(chapter, hosts, scope, snapshot, ct).ConfigureAwait(false);
            report.Chapters.Add(chapter);
        }

        report.EndTime = DateTime.UtcNow;
        StatusChanged?.Invoke(_l("AuditPhaseComplete"));
        return report;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 1: Discovery
    // ═══════════════════════════════════════════════════════════════════

    private async Task<(List<HostScanResult> Hosts, NetworkScanSnapshot? Snapshot)> DiscoverHostsAsync(
        AuditScope scope, AuditDepth depth, CancellationToken ct)
    {
        // If a subnet is provided, use the CartographyEngine for full discovery
        if (!string.IsNullOrWhiteSpace(scope.Subnet))
        {
            var scanDepth = depth switch
            {
                AuditDepth.Quick => ScanDepth.Quick,
                AuditDepth.Deep => ScanDepth.Deep,
                _ => ScanDepth.Standard,
            };

            var profile = new ScanProfile(
                Subnet: scope.Subnet,
                Depth: scanDepth,
                CustomPorts: null,
                MaxConcurrency: MaxConcurrency,
                TimeoutMs: DefaultTimeoutMs,
                SkipPing: false,
                ReverseDns: true);

            var engine = new CartographyEngine();
            engine.HostDiscoveryProgress += (done, total) =>
                PhaseProgress?.Invoke(_l("AuditPhaseNameDiscovery"), done, total);
            engine.HostCompleted += host =>
                PhaseProgress?.Invoke(_l("AuditPhaseNameScanning"), 0, 0);

            var snapshot = await engine.ScanAsync(profile, ct: ct).ConfigureAwait(false);
            return (snapshot.Hosts, snapshot);
        }

        // Otherwise probe each target individually with standard ports
        var hosts = new List<HostScanResult>();
        var hostsLock = new object();
        var targets = scope.Targets;
        var completed = 0;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct },
            async (target, token) =>
            {
                var services = await ProbeHostPortsAsync(target, token).ConfigureAwait(false);
                var host = new HostScanResult(
                    IpAddress: target,
                    Hostname: null,
                    IsAlive: services.Count > 0,
                    PingLatencyMs: 0,
                    Services: services,
                    PrimaryRole: null,
                    AllRoles: []);

                lock (hostsLock)
                {
                    hosts.Add(host);
                }

                var count = Interlocked.Increment(ref completed);
                PhaseProgress?.Invoke(_l("AuditPhaseNameDiscovery"), count, targets.Count);
            }).ConfigureAwait(false);

        return (hosts, null);
    }

    /// <summary>
    /// Probes a single host against common ports when no subnet scan is used.
    /// </summary>
    private static async Task<List<ServiceResult>> ProbeHostPortsAsync(
        string host, CancellationToken ct)
    {
        var services = new List<ServiceResult>();
        var servicesLock = new object();
        var ports = CartographyEngine.StandardPorts;

        await Parallel.ForEachAsync(
            ports,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct },
            async (port, token) =>
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var cts = new CancellationTokenSource(DefaultTimeoutMs);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
                    await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

                    // Grab banner (best effort)
                    string? banner = null;
                    try
                    {
                        tcp.ReceiveTimeout = 2_000;
                        var buf = new byte[512];
                        var stream = tcp.GetStream();
                        stream.ReadTimeout = 2_000;
                        var read = await stream.ReadAsync(buf.AsMemory(), linked.Token)
                            .ConfigureAwait(false);
                        if (read > 0)
                            banner = Encoding.ASCII.GetString(buf, 0, read).Trim();
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* per-port timeout during banner grab */ }
                    catch (OperationCanceledException) { throw; }
                    catch { /* banner grab is best-effort */ }

                    var result = new ServiceResult(
                        Port: port,
                        IsOpen: true,
                        ServiceName: GuessServiceName(port),
                        Banner: banner,
                        Version: null,
                        ResponseTimeMs: 0);

                    lock (servicesLock)
                    {
                        services.Add(result);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Per-port timeout - not a scan cancellation. Skip this port.
                }
                catch (OperationCanceledException) { throw; }
                catch { /* port closed or unreachable */ }
            }).ConfigureAwait(false);

        return services;
    }

    private static string? GuessServiceName(int port) => port switch
    {
        21 => "ftp",
        22 => "ssh",
        23 => "telnet",
        25 => "smtp",
        53 => "dns",
        80 => "http",
        110 => "pop3",
        143 => "imap",
        161 => "snmp",
        443 => "https",
        445 => "smb",
        993 => "imaps",
        995 => "pop3s",
        1433 => "mssql",
        3306 => "mysql",
        DefaultPorts.Rdp => "rdp",
        5432 => "postgresql",
        DefaultPorts.Vnc => "vnc",
        8080 => "http-proxy",
        8443 => "https-alt",
        _ => null,
    };

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 2: Network Security (Chapter NET - 4 checks)
    // ═══════════════════════════════════════════════════════════════════

    private AuditChapter BuildNetworkChapter() => new()
    {
        Id = "NET",
        Name = _l("AuditChapterNetwork"),
        SecNumCloudRef = "SecNumCloud 3.2 - 9.1 Network Architecture",
        Checks =
        [
            new() { Id = "NET-01", Name = _l("AuditCheckNet01Name"), SecNumCloudClause = "9.1.2" },
            new() { Id = "NET-02", Name = _l("AuditCheckNet02Name"), SecNumCloudClause = "9.1.3" },
            new() { Id = "NET-03", Name = _l("AuditCheckNet03Name"), SecNumCloudClause = "9.1.4" },
            new() { Id = "NET-04", Name = _l("AuditCheckNet04Name"), SecNumCloudClause = "9.1.1" },
        ],
    };

    private async Task RunNetworkChecksAsync(
        AuditChapter chapter,
        List<HostScanResult> hosts,
        NetworkScanSnapshot? snapshot,
        CancellationToken ct)
    {
        var completed = 0;
        var total = chapter.Checks.Count;

        // NET-01: Open ports inventory
        var net01 = chapter.Checks[0];
        try
        {
            var totalOpen = 0;
            foreach (var host in hosts)
            {
                var openPorts = host.Services.Where(s => s.IsOpen).ToList();
                totalOpen += openPorts.Count;
                if (openPorts.Count > 0)
                {
                    net01.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceOpenPorts"),
                            openPorts.Count,
                            string.Join(", ", openPorts.Select(p => $"{p.Port}/{p.ServiceName ?? _l("AuditServiceUnknown")}"))),
                    });
                }
            }
            net01.Status = AuditStatus.Pass;
            net01.Summary = string.Format(_l("AuditCheckNet01Summary"), totalOpen, hosts.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            net01.Status = AuditStatus.Error;
            net01.Summary = string.Format(_l("AuditCheckNet01Error"), ex.Message);
        }
        CheckCompleted?.Invoke(net01);
        PhaseProgress?.Invoke(_l("AuditPhaseNameNetwork"), ++completed, total);

        // NET-02: Unnecessary services
        var net02 = chapter.Checks[1];
        try
        {
            var flaggedCount = 0;
            foreach (var host in hosts)
            {
                var nonStandard = host.Services
                    .Where(s => s.IsOpen && !StandardPorts.Contains(s.Port))
                    .ToList();

                if (nonStandard.Count > 0)
                {
                    flaggedCount += nonStandard.Count;
                    net02.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceNonStdPort"),
                            string.Join(", ", nonStandard.Select(p =>
                                $"{p.Port}/{p.ServiceName ?? _l("AuditServiceUnknown")}"))),
                    });
                }
            }

            if (flaggedCount == 0)
            {
                net02.Status = AuditStatus.Pass;
                net02.Summary = _l("AuditCheckNet02SummaryPass");
            }
            else
            {
                net02.Status = AuditStatus.Warning;
                net02.Summary = string.Format(_l("AuditCheckNet02SummaryWarn"), flaggedCount);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            net02.Status = AuditStatus.Error;
            net02.Summary = string.Format(_l("AuditCheckNet02Error"), ex.Message);
        }
        CheckCompleted?.Invoke(net02);
        PhaseProgress?.Invoke(_l("AuditPhaseNameNetwork"), ++completed, total);

        // NET-03: HTTP security headers
        var net03 = chapter.Checks[2];
        try
        {
            await CheckHttpSecurityHeadersAsync(net03, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            net03.Status = AuditStatus.Error;
            net03.Summary = string.Format(_l("AuditCheckNet03Error"), ex.Message);
        }
        CheckCompleted?.Invoke(net03);
        PhaseProgress?.Invoke(_l("AuditPhaseNameNetwork"), ++completed, total);

        // NET-04: Network segmentation (informational)
        var net04 = chapter.Checks[3];
        try
        {
            var subnets = new HashSet<string>();
            foreach (var host in hosts)
            {
                var parts = host.IpAddress.Split('.');
                if (parts.Length == 4)
                    subnets.Add($"{parts[0]}.{parts[1]}.{parts[2]}.0/24");
            }

            if (snapshot?.DetectedVlans is { Count: > 0 } vlans)
            {
                foreach (var vlan in vlans)
                {
                    net04.Evidence.Add(new AuditEvidence
                    {
                        Host = vlan.Subnet,
                        Detail = string.Format(_l("AuditEvidenceVlan"),
                            vlan.VlanId?.ToString() ?? "N/A", vlan.Name, vlan.MemberIps.Count),
                    });
                }
            }

            net04.Status = subnets.Count > 1 ? AuditStatus.Pass : AuditStatus.Warning;
            net04.Summary = snapshot?.DetectedVlans is { Count: > 0 }
                ? string.Format(_l("AuditCheckNet04SummaryVlan"), subnets.Count, snapshot.DetectedVlans.Count)
                : string.Format(_l("AuditCheckNet04SummaryNoVlan"), subnets.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            net04.Status = AuditStatus.Error;
            net04.Summary = string.Format(_l("AuditCheckNet04Error"), ex.Message);
        }
        CheckCompleted?.Invoke(net04);
        PhaseProgress?.Invoke(_l("AuditPhaseNameNetwork"), ++completed, total);
    }

    /// <summary>
    /// For each HTTP/HTTPS port across all hosts, fetch headers via raw TCP
    /// and verify the presence of required security headers.
    /// </summary>
    private async Task CheckHttpSecurityHeadersAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var missingCount = 0;
        var checkedCount = 0;

        foreach (var host in hosts)
        {
            var webServices = host.Services
                .Where(s => s.IsOpen && HttpPorts.Contains(s.Port))
                .ToList();

            foreach (var svc in webServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;
                var useTls = TlsPorts.Contains(svc.Port);

                try
                {
                    var headers = await FetchHttpHeadersAsync(
                        host.IpAddress, svc.Port, useTls, ct).ConfigureAwait(false);

                    var missing = RequiredSecurityHeaders
                        .Where(h => !headers.ContainsKey(h))
                        .ToList();

                    if (missing.Count > 0)
                    {
                        missingCount++;
                        check.Evidence.Add(new AuditEvidence
                        {
                            Host = host.IpAddress,
                            Detail = string.Format(_l("AuditEvidenceMissingHeaders"),
                                svc.Port, string.Join(", ", missing)),
                            RawData = string.Join("\n",
                                headers.Select(kv => $"{kv.Key}: {kv.Value}")),
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* host/port unreachable during header fetch */ }
            }
        }

        if (checkedCount == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckNet03SummarySkip");
        }
        else if (missingCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckNet03SummaryPass"), checkedCount);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckNet03SummaryFail"), missingCount, checkedCount);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 3: Cryptography (Chapter CRY - 4 checks)
    // ═══════════════════════════════════════════════════════════════════

    private AuditChapter BuildCryptoChapter() => new()
    {
        Id = "CRY",
        Name = _l("AuditChapterCrypto"),
        SecNumCloudRef = "SecNumCloud 3.2 - 10.1 Cryptographic Controls",
        Checks =
        [
            new() { Id = "CRY-01", Name = _l("AuditCheckCry01Name"), SecNumCloudClause = "10.1.1" },
            new() { Id = "CRY-02", Name = _l("AuditCheckCry02Name"), SecNumCloudClause = "10.1.2" },
            new() { Id = "CRY-03", Name = _l("AuditCheckCry03Name"), SecNumCloudClause = "10.1.3" },
            new() { Id = "CRY-04", Name = _l("AuditCheckCry04Name"), SecNumCloudClause = "10.1.4" },
        ],
    };

    private async Task RunCryptoChecksAsync(
        AuditChapter chapter,
        List<HostScanResult> hosts,
        NetworkScanSnapshot? snapshot,
        CancellationToken ct)
    {
        var completed = 0;
        var total = chapter.Checks.Count;

        // CRY-01: TLS protocol versions
        var cry01 = chapter.Checks[0];
        try
        {
            await CheckTlsProtocolVersionsAsync(cry01, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cry01.Status = AuditStatus.Error;
            cry01.Summary = string.Format(_l("AuditCheckCry01Error"), ex.Message);
        }
        CheckCompleted?.Invoke(cry01);
        PhaseProgress?.Invoke(_l("AuditPhaseNameCrypto"), ++completed, total);

        // CRY-02: Weak cipher suites
        var cry02 = chapter.Checks[1];
        try
        {
            await CheckWeakCiphersAsync(cry02, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cry02.Status = AuditStatus.Error;
            cry02.Summary = string.Format(_l("AuditCheckCry02Error"), ex.Message);
        }
        CheckCompleted?.Invoke(cry02);
        PhaseProgress?.Invoke(_l("AuditPhaseNameCrypto"), ++completed, total);

        // CRY-03: Certificate validity
        var cry03 = chapter.Checks[2];
        try
        {
            CheckCertificateValidity(cry03, hosts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cry03.Status = AuditStatus.Error;
            cry03.Summary = string.Format(_l("AuditCheckCry03Error"), ex.Message);
        }
        CheckCompleted?.Invoke(cry03);
        PhaseProgress?.Invoke(_l("AuditPhaseNameCrypto"), ++completed, total);

        // CRY-04: SSH key strength
        var cry04 = chapter.Checks[3];
        try
        {
            await CheckSshKeyStrengthAsync(cry04, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cry04.Status = AuditStatus.Error;
            cry04.Summary = string.Format(_l("AuditCheckCry04Error"), ex.Message);
        }
        CheckCompleted?.Invoke(cry04);
        PhaseProgress?.Invoke(_l("AuditPhaseNameCrypto"), ++completed, total);
    }

    /// <summary>
    /// Tests each TLS port for support of deprecated protocol versions (SSL3, TLS1.0, TLS1.1).
    /// </summary>
    private async Task CheckTlsProtocolVersionsAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var weakFound = 0;
        var checkedEndpoints = 0;

        foreach (var host in hosts)
        {
            var tlsServices = host.Services
                .Where(s => s.IsOpen && TlsPorts.Contains(s.Port))
                .ToList();

            foreach (var svc in tlsServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedEndpoints++;
                var weakProtocols = new List<string>();

                foreach (var (name, protocol, isWeak) in TlsProtocols)
                {
                    var supported = await TestTlsProtocolAsync(
                        host.IpAddress, svc.Port, protocol, ct).ConfigureAwait(false);

                    if (supported && isWeak)
                        weakProtocols.Add(name);
                }

                if (weakProtocols.Count > 0)
                {
                    weakFound++;
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceDeprecatedProto"),
                            svc.Port, string.Join(", ", weakProtocols)),
                    });
                }
            }
        }

        if (checkedEndpoints == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckCry01SummarySkip");
        }
        else if (weakFound == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckCry01SummaryPass"), checkedEndpoints);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckCry01SummaryFail"), weakFound, checkedEndpoints);
        }
    }

    /// <summary>
    /// Tests each TLS 1.2 port for negotiation of known weak cipher suites by inspecting
    /// the negotiated cipher name after a successful handshake.
    /// </summary>
    private async Task CheckWeakCiphersAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var weakFound = 0;
        var checkedEndpoints = 0;

        foreach (var host in hosts)
        {
            var tlsServices = host.Services
                .Where(s => s.IsOpen && TlsPorts.Contains(s.Port))
                .ToList();

            foreach (var svc in tlsServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedEndpoints++;

                try
                {
                    var cipherName = await GetNegotiatedCipherAsync(
                        host.IpAddress, svc.Port, ct).ConfigureAwait(false);

                    if (cipherName is not null &&
                        WeakCipherPatterns.Any(p =>
                            cipherName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        weakFound++;
                        check.Evidence.Add(new AuditEvidence
                        {
                            Host = host.IpAddress,
                            Detail = string.Format(_l("AuditEvidenceWeakCipher"), svc.Port, cipherName),
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* cipher check failed for this endpoint */ }
            }
        }

        if (checkedEndpoints == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckCry02SummarySkip");
        }
        else if (weakFound == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckCry02SummaryPass"), checkedEndpoints);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckCry02SummaryFail"), weakFound, checkedEndpoints);
        }
    }

    /// <summary>
    /// Validates certificate expiry, chain trust, and hostname match using data
    /// from the discovery snapshot.
    /// </summary>
    private void CheckCertificateValidity(AuditCheck check, List<HostScanResult> hosts)
    {
        var expiredCount = 0;
        var expiringSoonCount = 0;
        var checkedCount = 0;

        foreach (var host in hosts)
        {
            foreach (var svc in host.Services.Where(s => s.Certificate is not null))
            {
                checkedCount++;
                var cert = svc.Certificate!;

                if (cert.IsExpired)
                {
                    expiredCount++;
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceCertExpired"),
                            svc.Port, cert.NotAfter.ToString("yyyy-MM-dd"), cert.Subject),
                    });
                }
                else if (cert.ExpiresSoon)
                {
                    expiringSoonCount++;
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceCertExpiringSoon"),
                            svc.Port, cert.NotAfter.ToString("yyyy-MM-dd"), cert.Subject),
                    });
                }

                // Check weak signature algorithms
                if (cert.SignatureAlgorithm.Contains("SHA1", StringComparison.OrdinalIgnoreCase) ||
                    cert.SignatureAlgorithm.Contains("MD5", StringComparison.OrdinalIgnoreCase))
                {
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceWeakSigAlgo"), svc.Port, cert.SignatureAlgorithm),
                    });
                }
            }
        }

        if (checkedCount == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckCry03SummarySkip");
        }
        else if (expiredCount > 0)
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckCry03SummaryFail"),
                expiredCount, expiringSoonCount, checkedCount);
        }
        else if (expiringSoonCount > 0)
        {
            check.Status = AuditStatus.Warning;
            check.Summary = string.Format(_l("AuditCheckCry03SummaryWarn"), expiringSoonCount, checkedCount);
        }
        else
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckCry03SummaryPass"), checkedCount);
        }
    }

    /// <summary>
    /// Uses the SSH fingerprinter to check SSH key algorithms on discovered SSH ports.
    /// Flags hosts offering weak algorithms (DSA, RSA < 2048 implied by short hashes).
    /// </summary>
    private async Task CheckSshKeyStrengthAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var checkedCount = 0;
        var weakCount = 0;

        foreach (var host in hosts)
        {
            var sshServices = host.Services
                .Where(s => s.IsOpen && s.Port == 22)
                .ToList();

            foreach (var svc in sshServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;

                // Extract algorithm info from banner
                var banner = svc.Banner ?? "";
                var hasWeakAlgo = false;

                // Check for DSA in banner (known weak)
                if (banner.Contains("dsa", StringComparison.OrdinalIgnoreCase) ||
                    banner.Contains("ssh-dss", StringComparison.OrdinalIgnoreCase))
                {
                    hasWeakAlgo = true;
                }

                // Also compute HASSH fingerprint for verification
                var hassh = await SshFingerprinter.ComputeHashAsync(
                    host.IpAddress, svc.Port, SshTimeoutMs, ct).ConfigureAwait(false);

                if (hasWeakAlgo)
                {
                    weakCount++;
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceWeakSshKey"), svc.Port),
                        RawData = string.Format(_l("AuditRawBanner"), banner) +
                                  (hassh is not null ? "\n" + string.Format(_l("AuditRawHashh"), hassh) : ""),
                    });
                }
                else if (hassh is not null)
                {
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceSshFingerprint"), svc.Port, hassh),
                        RawData = string.Format(_l("AuditRawBanner"), banner),
                    });
                }
            }
        }

        if (checkedCount == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckCry04SummarySkip");
        }
        else if (weakCount > 0)
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckCry04SummaryFail"), weakCount, checkedCount);
        }
        else
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckCry04SummaryPass"), checkedCount);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 4: Access Control (Chapter ACC - 3 checks)
    // ═══════════════════════════════════════════════════════════════════

    private AuditChapter BuildAccessChapter() => new()
    {
        Id = "ACC",
        Name = _l("AuditChapterAccess"),
        SecNumCloudRef = "SecNumCloud 3.2 - 9.2 Access Management",
        Checks =
        [
            new() { Id = "ACC-01", Name = _l("AuditCheckAcc01Name"), SecNumCloudClause = "9.2.3" },
            new() { Id = "ACC-02", Name = _l("AuditCheckAcc02Name"), SecNumCloudClause = "9.2.4" },
            new() { Id = "ACC-03", Name = _l("AuditCheckAcc03Name"), SecNumCloudClause = "9.2.5" },
        ],
    };

    private async Task RunAccessChecksAsync(
        AuditChapter chapter,
        List<HostScanResult> hosts,
        NetworkScanSnapshot? snapshot,
        CancellationToken ct)
    {
        var completed = 0;
        var total = chapter.Checks.Count;

        // ACC-01: Default credentials
        var acc01 = chapter.Checks[0];
        try
        {
            await CheckDefaultCredentialsAsync(acc01, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            acc01.Status = AuditStatus.Error;
            acc01.Summary = string.Format(_l("AuditCheckAcc01Error"), ex.Message);
        }
        CheckCompleted?.Invoke(acc01);
        PhaseProgress?.Invoke(_l("AuditPhaseNameAccess"), ++completed, total);

        // ACC-02: SMB signing
        var acc02 = chapter.Checks[1];
        try
        {
            await CheckSmbSigningAsync(acc02, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            acc02.Status = AuditStatus.Error;
            acc02.Summary = string.Format(_l("AuditCheckAcc02Error"), ex.Message);
        }
        CheckCompleted?.Invoke(acc02);
        PhaseProgress?.Invoke(_l("AuditPhaseNameAccess"), ++completed, total);

        // ACC-03: SNMP community strings
        var acc03 = chapter.Checks[2];
        try
        {
            await CheckSnmpCommunityAsync(acc03, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            acc03.Status = AuditStatus.Error;
            acc03.Summary = string.Format(_l("AuditCheckAcc03Error"), ex.Message);
        }
        CheckCompleted?.Invoke(acc03);
        PhaseProgress?.Invoke(_l("AuditPhaseNameAccess"), ++completed, total);
    }

    /// <summary>
    /// Tests SSH services for common default username/password combinations
    /// and SNMP for default community strings.
    /// </summary>
    private async Task CheckDefaultCredentialsAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var vulnerableCount = 0;
        var checkedCount = 0;

        foreach (var host in hosts)
        {
            // SSH default credential check
            var sshServices = host.Services.Where(s => s.IsOpen && s.Port == 22).ToList();
            foreach (var svc in sshServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;

                foreach (var (user, pass) in DefaultSshCredentials)
                {
                    ct.ThrowIfCancellationRequested();
                    var success = await TestSshCredentialAsync(
                        host.IpAddress, svc.Port, user, pass, ct).ConfigureAwait(false);

                    if (success)
                    {
                        vulnerableCount++;
                        check.Evidence.Add(new AuditEvidence
                        {
                            Host = host.IpAddress,
                            Detail = string.Format(_l("AuditEvidenceSshDefaultCred"), svc.Port, user),
                        });
                        break; // One match is enough per host
                    }
                }
            }

            // HTTP Basic Auth check on web services
            var webServices = host.Services
                .Where(s => s.IsOpen && HttpPorts.Contains(s.Port))
                .ToList();

            foreach (var svc in webServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;

                var useTls = TlsPorts.Contains(svc.Port);
                var success = await TestHttpBasicAuthAsync(
                    host.IpAddress, svc.Port, useTls, "admin", "admin", ct)
                    .ConfigureAwait(false);

                if (success)
                {
                    vulnerableCount++;
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceHttpDefaultCred"), svc.Port),
                    });
                }
            }
        }

        if (checkedCount == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckAcc01SummarySkip");
        }
        else if (vulnerableCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckAcc01SummaryPass"), checkedCount);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckAcc01SummaryFail"), vulnerableCount);
        }
    }

    /// <summary>
    /// Probes SMB on port 445 via NtlmProbe to check whether signing is required.
    /// </summary>
    private async Task CheckSmbSigningAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var notRequiredCount = 0;
        var checkedCount = 0;

        foreach (var host in hosts)
        {
            var smbServices = host.Services.Where(s => s.IsOpen && s.Port == 445).ToList();
            foreach (var svc in smbServices)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;

                // Prefer SmbInfo from snapshot if available
                var smbInfo = host.SmbInfo;

                if (smbInfo is null)
                {
                    // Probe directly if not in snapshot
                    var (_, smb) = await NtlmProbe.ProbeWithSmbInfoAsync(
                        host.IpAddress, DefaultTimeoutMs, ct).ConfigureAwait(false);
                    smbInfo = smb;
                }

                if (smbInfo is null)
                {
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = _l("AuditEvidenceSmbNoInfo"),
                    });
                    continue;
                }

                if (!smbInfo.SigningRequired)
                {
                    notRequiredCount++;
                    check.Evidence.Add(new AuditEvidence
                    {
                        Host = host.IpAddress,
                        Detail = string.Format(_l("AuditEvidenceSmbNoSigning"),
                            smbInfo.DialectRevision, smbInfo.SigningEnabled),
                    });
                }
            }
        }

        if (checkedCount == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckAcc02SummarySkip");
        }
        else if (notRequiredCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckAcc02SummaryPass"), checkedCount);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckAcc02SummaryFail"), notRequiredCount, checkedCount);
        }
    }

    /// <summary>
    /// Tests SNMP services for default community strings (public, private, community).
    /// </summary>
    private async Task CheckSnmpCommunityAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var vulnerableCount = 0;
        var checkedCount = 0;

        foreach (var host in hosts)
        {
            var snmpServices = host.Services.Where(s => s.IsOpen && s.Port == 161).ToList();
            // Also check hosts that may have responded to SNMP during discovery
            var hasSnmpPort = snmpServices.Count > 0;
            var hasSnmpInfo = host.SnmpInfo is not null;

            if (!hasSnmpPort && !hasSnmpInfo) continue;

            ct.ThrowIfCancellationRequested();
            checkedCount++;
            var foundCommunities = new List<string>();

            foreach (var community in DefaultSnmpCommunities)
            {
                ct.ThrowIfCancellationRequested();

                var result = await UdpProbeEngine.QuerySnmpAsync(
                    host.IpAddress, community, DefaultTimeoutMs, ct).ConfigureAwait(false);

                if (result is not null)
                {
                    foundCommunities.Add(community);
                }
            }

            if (foundCommunities.Count > 0)
            {
                vulnerableCount++;
                check.Evidence.Add(new AuditEvidence
                {
                    Host = host.IpAddress,
                    Detail = string.Format(_l("AuditEvidenceSnmpDefault"),
                        string.Join(", ", foundCommunities.Select(c => $"\"{c}\""))),
                    RawData = host.SnmpInfo is not null
                        ? $"SysDescr: {host.SnmpInfo.SysDescr}, SysName: {host.SnmpInfo.SysName}"
                        : "",
                });
            }
        }

        if (checkedCount == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckAcc03SummarySkip");
        }
        else if (vulnerableCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckAcc03SummaryPass"), checkedCount);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckAcc03SummaryFail"), vulnerableCount, checkedCount);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 5: Operations Security (Chapter OPS - 4 checks)
    // ═══════════════════════════════════════════════════════════════════

    private AuditChapter BuildOperationsChapter() => new()
    {
        Id = "OPS",
        Name = _l("AuditChapterOperations"),
        SecNumCloudRef = "SecNumCloud 3.2 - 12.1 Operational Procedures",
        Checks =
        [
            new() { Id = "OPS-01", Name = _l("AuditCheckOps01Name"), SecNumCloudClause = "12.1.1" },
            new() { Id = "OPS-02", Name = _l("AuditCheckOps02Name"), SecNumCloudClause = "12.1.2" },
            new() { Id = "OPS-03", Name = _l("AuditCheckOps03Name"), SecNumCloudClause = "12.6.1" },
            new() { Id = "OPS-04", Name = _l("AuditCheckOps04Name"), SecNumCloudClause = "12.1.3" },
        ],
    };

    private async Task RunOperationsChecksAsync(
        AuditChapter chapter,
        List<HostScanResult> hosts,
        AuditScope scope,
        NetworkScanSnapshot? snapshot,
        CancellationToken ct)
    {
        var completed = 0;
        var total = chapter.Checks.Count;

        // OPS-01: SPF/DKIM/DMARC
        var ops01 = chapter.Checks[0];
        try
        {
            await CheckEmailAuthRecordsAsync(ops01, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ops01.Status = AuditStatus.Error;
            ops01.Summary = string.Format(_l("AuditCheckOps01Error"), ex.Message);
        }
        CheckCompleted?.Invoke(ops01);
        PhaseProgress?.Invoke(_l("AuditPhaseNameOperations"), ++completed, total);

        // OPS-02: CAA records
        var ops02 = chapter.Checks[1];
        try
        {
            await CheckCaaRecordsAsync(ops02, hosts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ops02.Status = AuditStatus.Error;
            ops02.Summary = string.Format(_l("AuditCheckOps02Error"), ex.Message);
        }
        CheckCompleted?.Invoke(ops02);
        PhaseProgress?.Invoke(_l("AuditPhaseNameOperations"), ++completed, total);

        // OPS-03: Known CVEs
        var ops03 = chapter.Checks[2];
        try
        {
            CheckKnownCves(ops03, hosts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ops03.Status = AuditStatus.Error;
            ops03.Summary = string.Format(_l("AuditCheckOps03Error"), ex.Message);
        }
        CheckCompleted?.Invoke(ops03);
        PhaseProgress?.Invoke(_l("AuditPhaseNameOperations"), ++completed, total);

        // OPS-04: Service inventory
        var ops04 = chapter.Checks[3];
        try
        {
            BuildServiceInventory(ops04, hosts, snapshot);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ops04.Status = AuditStatus.Error;
            ops04.Summary = string.Format(_l("AuditCheckOps04Error"), ex.Message);
        }
        CheckCompleted?.Invoke(ops04);
        PhaseProgress?.Invoke(_l("AuditPhaseNameOperations"), ++completed, total);
    }

    /// <summary>
    /// Extracts domains from certificate SANs and checks SPF, DKIM, and DMARC
    /// DNS TXT records via nslookup.
    /// </summary>
    private async Task CheckEmailAuthRecordsAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var domains = ExtractDomainsFromCertificates(hosts);

        if (domains.Count == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckOps01SummarySkip");
            return;
        }

        var missingCount = 0;

        foreach (var domain in domains)
        {
            ct.ThrowIfCancellationRequested();
            var missing = new List<string>();

            // SPF check
            var spf = await QueryDnsTxtAsync(domain, ct).ConfigureAwait(false);
            if (!spf.Contains("v=spf1", StringComparison.OrdinalIgnoreCase))
                missing.Add("SPF");

            // DMARC check
            var dmarc = await QueryDnsTxtAsync($"_dmarc.{domain}", ct).ConfigureAwait(false);
            if (!dmarc.Contains("v=DMARC1", StringComparison.OrdinalIgnoreCase))
                missing.Add("DMARC");

            // DKIM check (try default selector)
            var dkim = await QueryDnsTxtAsync($"default._domainkey.{domain}", ct).ConfigureAwait(false);
            if (!dkim.Contains("v=DKIM1", StringComparison.OrdinalIgnoreCase))
                missing.Add("DKIM (default selector)");

            if (missing.Count > 0)
            {
                missingCount++;
                check.Evidence.Add(new AuditEvidence
                {
                    Host = domain,
                    Detail = string.Format(_l("AuditEvidenceMissingEmailAuth"), string.Join(", ", missing)),
                });
            }
        }

        if (missingCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckOps01SummaryPass"), domains.Count);
        }
        else
        {
            check.Status = AuditStatus.Warning;
            check.Summary = string.Format(_l("AuditCheckOps01SummaryWarn"), missingCount, domains.Count);
        }
    }

    /// <summary>
    /// Checks for CAA (Certificate Authority Authorization) DNS records on discovered domains.
    /// </summary>
    private async Task CheckCaaRecordsAsync(
        AuditCheck check, List<HostScanResult> hosts, CancellationToken ct)
    {
        var domains = ExtractDomainsFromCertificates(hosts);

        if (domains.Count == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckOps02SummarySkip");
            return;
        }

        var missingCount = 0;

        foreach (var domain in domains)
        {
            ct.ThrowIfCancellationRequested();

            var caa = await QueryDnsCaaAsync(domain, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(caa) || !caa.Contains("issue", StringComparison.OrdinalIgnoreCase))
            {
                missingCount++;
                check.Evidence.Add(new AuditEvidence
                {
                    Host = domain,
                    Detail = _l("AuditEvidenceNoCaa"),
                });
            }
        }

        if (missingCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckOps02SummaryPass"), domains.Count);
        }
        else
        {
            check.Status = AuditStatus.Warning;
            check.Summary = string.Format(_l("AuditCheckOps02SummaryWarn"), missingCount, domains.Count);
        }
    }

    /// <summary>
    /// Matches service banners against the embedded CVE database.
    /// </summary>
    private void CheckKnownCves(AuditCheck check, List<HostScanResult> hosts)
    {
        var cveCount = 0;
        var checkedServices = 0;

        foreach (var host in hosts)
        {
            foreach (var svc in host.Services.Where(s => s.IsOpen && !string.IsNullOrEmpty(s.Banner)))
            {
                checkedServices++;
                var parsed = ParseBanner(svc.Banner!);
                if (parsed is null) continue;

                var (software, version) = parsed.Value;
                if (!CveDatabase.TryGetValue(software, out var entries)) continue;
                if (string.IsNullOrEmpty(version)) continue;

                var matching = entries.Where(e => e.IsAffected(version)).ToList();
                if (matching.Count > 0)
                {
                    cveCount += matching.Count;
                    foreach (var cve in matching)
                    {
                        check.Evidence.Add(new AuditEvidence
                        {
                            Host = host.IpAddress,
                            Detail = string.Format(_l("AuditEvidenceCveMatch"),
                                svc.Port, software, version,
                                cve.Id, cve.CvssScore.ToString("F1"), cve.Severity, cve.Summary),
                            RawData = string.Format(_l("AuditRawAffectedVersions"), cve.AffectedVersions),
                        });
                    }
                }
            }
        }

        if (checkedServices == 0)
        {
            check.Status = AuditStatus.Skipped;
            check.Summary = _l("AuditCheckOps03SummarySkip");
        }
        else if (cveCount == 0)
        {
            check.Status = AuditStatus.Pass;
            check.Summary = string.Format(_l("AuditCheckOps03SummaryPass"), checkedServices);
        }
        else
        {
            check.Status = AuditStatus.Fail;
            check.Summary = string.Format(_l("AuditCheckOps03SummaryFail"), cveCount);
        }
    }

    /// <summary>
    /// Aggregates all discovered services, versions, and OS fingerprints into
    /// an informational inventory.
    /// </summary>
    private void BuildServiceInventory(
        AuditCheck check, List<HostScanResult> hosts, NetworkScanSnapshot? snapshot)
    {
        var serviceMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var osMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in hosts)
        {
            // Collect services
            foreach (var svc in host.Services.Where(s => s.IsOpen))
            {
                var key = svc.ServiceName ?? $"port-{svc.Port}";
                if (!string.IsNullOrEmpty(svc.Version))
                    key = $"{key}/{svc.Version}";

                serviceMap[key] = serviceMap.GetValueOrDefault(key) + 1;
            }

            // Collect OS fingerprints
            if (host.OsFingerprint is not null)
            {
                var os = host.OsFingerprint.OsGuess;
                osMap[os] = osMap.GetValueOrDefault(os) + 1;
            }
        }

        // Build evidence entries for top services
        foreach (var (service, count) in serviceMap.OrderByDescending(kv => kv.Value))
        {
            check.Evidence.Add(new AuditEvidence
            {
                Host = "aggregate",
                Detail = string.Format(_l("AuditEvidenceServiceCount"), service, count),
            });
        }

        // Add OS fingerprint summary
        foreach (var (os, count) in osMap.OrderByDescending(kv => kv.Value))
        {
            check.Evidence.Add(new AuditEvidence
            {
                Host = "aggregate",
                Detail = string.Format(_l("AuditEvidenceOsCount"), os, count),
            });
        }

        check.Status = AuditStatus.Pass;
        check.Summary = osMap.Count > 0
            ? string.Format(_l("AuditCheckOps04SummaryOs"), serviceMap.Count, hosts.Count, osMap.Count)
            : string.Format(_l("AuditCheckOps04Summary"), serviceMap.Count, hosts.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Low-level probe helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests whether a specific TLS/SSL protocol version is accepted by the remote host.
    /// </summary>
    private static async Task<bool> TestTlsProtocolAsync(
        string host, int port, SslProtocols protocol, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

#pragma warning disable CA5397, CS0618, SYSLIB0039
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = protocol,
            }, linked.Token).ConfigureAwait(false);
#pragma warning restore CA5397, CS0618, SYSLIB0039
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Connects with TLS 1.2 and returns the name of the negotiated cipher suite,
    /// or null if the handshake fails.
    /// </summary>
    private static async Task<string?> GetNegotiatedCipherAsync(
        string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12,
            }, linked.Token).ConfigureAwait(false);

            return ssl.NegotiatedCipherSuite.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Sends a HEAD request via raw TCP (with optional TLS) and parses response headers.
    /// </summary>
    private static async Task<Dictionary<string, string>> FetchHttpHeadersAsync(
        string host, int port, bool useTls, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Prevent CRLF injection in HTTP Host header
        host = host.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

        using var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(DefaultTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

        Stream stream = tcp.GetStream();
        SslStream? sslStream = null;

        if (useTls)
        {
            sslStream = new SslStream(stream, leaveInnerStreamOpen: true, userCertificateValidationCallback: (_, _, _, _) => true);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
            }, linked.Token).ConfigureAwait(false);
            stream = sslStream;
        }

        try
        {
            var request = $"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), linked.Token)
                .ConfigureAwait(false);

            var buf = new byte[4096];
            var totalRead = 0;
            var sb = new StringBuilder();

            // Read until end of headers or buffer full
            while (totalRead < buf.Length)
            {
                var read = await stream.ReadAsync(buf.AsMemory(totalRead), linked.Token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;

                sb.Append(Encoding.ASCII.GetString(buf, totalRead - read, read));
                if (sb.ToString().Contains("\r\n\r\n")) break;
            }

            // Parse header lines
            var raw = sb.ToString();
            var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) headerEnd = raw.Length;
            var headerBlock = raw[..headerEnd];

            foreach (var line in headerBlock.Split("\r\n"))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var name = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    headers[name] = value;
                }
            }

            return headers;
        }
        finally
        {
            if (sslStream is not null)
                await sslStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tests SSH password authentication via SSH.NET (Renci).
    /// </summary>
    private static async Task<bool> TestSshCredentialAsync(
        string host, int port, string user, string pass, CancellationToken ct)
    {
        try
        {
            var connInfo = new Renci.SshNet.PasswordConnectionInfo(host, port, user, pass)
            {
                Timeout = TimeSpan.FromMilliseconds(SshTimeoutMs),
            };

            using var client = new Renci.SshNet.SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct).ConfigureAwait(false);
            client.Disconnect();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Tests HTTP Basic Auth by sending an Authorization header and checking for 200/302.
    /// </summary>
    private static async Task<bool> TestHttpBasicAuthAsync(
        string host, int port, bool useTls, string user, string pass, CancellationToken ct)
    {
        // Prevent CRLF injection in HTTP Host header
        host = host.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

            Stream stream = tcp.GetStream();
            SslStream? sslStream = null;

            if (useTls)
            {
                sslStream = new SslStream(stream, leaveInnerStreamOpen: true, userCertificateValidationCallback: (_, _, _, _) => true);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                }, linked.Token).ConfigureAwait(false);
                stream = sslStream;
            }

            try
            {
                var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}"));
                var request = $"GET / HTTP/1.1\r\nHost: {host}\r\n" +
                              $"Authorization: Basic {credentials}\r\n" +
                              "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), linked.Token)
                    .ConfigureAwait(false);

                var buf = new byte[1024];
                var read = await stream.ReadAsync(buf, linked.Token).ConfigureAwait(false);
                if (read == 0) return false;

                var response = Encoding.ASCII.GetString(buf, 0, Math.Min(read, 64));
                // 200 OK or 302 redirect (logged in) vs 401/403 (denied)
                return response.Contains(" 200 ") || response.Contains(" 302 ");
            }
            finally
            {
                if (sslStream is not null)
                    await sslStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Queries DNS TXT records via nslookup process.
    /// </summary>
    private static async Task<string> QueryDnsTxtAsync(string domain, CancellationToken ct)
    {
        return await RunDnsQueryAsync("TXT", domain, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries DNS CAA records via nslookup process.
    /// </summary>
    private static async Task<string> QueryDnsCaaAsync(string domain, CancellationToken ct)
    {
        return await RunDnsQueryAsync("CAA", domain, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the start info for the DNS record query.
    /// </summary>
    internal static ProcessStartInfo CreateNslookupStartInfo(string recordType, string domain)
    {
        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.InSystemDirectory(NslookupExecutableName),
            Arguments = $"-type={recordType} {domain}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = SystemExecutablePath.SystemDirectory,
        };
    }

    /// <summary>
    /// Executes nslookup with the specified record type and returns the raw output.
    /// </summary>
    private static async Task<string> RunDnsQueryAsync(
        string recordType, string domain, CancellationToken ct)
    {
        if (!InputValidator.ValidateDomain(domain))
            return string.Empty;

        try
        {
            using var process = new Process { StartInfo = CreateNslookupStartInfo(recordType, domain) };
            process.Start();

            using var cts = new CancellationTokenSource(DnsTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(linked.Token)
                    .ConfigureAwait(false);

                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return output;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return string.Empty;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Domain extraction from certificates
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts unique domain names from TLS certificates found during scanning.
    /// Filters out wildcard prefixes and IP addresses.
    /// </summary>
    private static HashSet<string> ExtractDomainsFromCertificates(List<HostScanResult> hosts)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in hosts)
        {
            foreach (var svc in host.Services.Where(s => s.Certificate is not null))
            {
                var cert = svc.Certificate!;

                // Extract from Subject CN
                var cn = ExtractCnFromSubject(cert.Subject);
                if (cn is not null)
                    TryAddDomain(domains, cn);

                // Extract from SANs
                foreach (var san in cert.SubjectAltNames)
                {
                    TryAddDomain(domains, san);
                }
            }
        }

        return domains;
    }

    private static string? ExtractCnFromSubject(string subject)
    {
        var match = CnRegex.Match(subject);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static void TryAddDomain(HashSet<string> domains, string value)
    {
        // Strip wildcard prefix
        var domain = value.TrimStart('*', '.');

        // Skip IP addresses and localhost
        if (string.IsNullOrWhiteSpace(domain)) return;
        if (domain.All(c => char.IsDigit(c) || c == '.')) return;
        if (domain.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return;

        // Must contain at least one dot (proper domain)
        if (domain.Contains('.'))
            domains.Add(domain);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Banner parsing & CVE version comparison
    // ═══════════════════════════════════════════════════════════════════

    private static (string Software, string Version)? ParseBanner(string input)
    {
        foreach (var (pattern, software) in BannerPatterns)
        {
            var match = pattern.Match(input);
            if (match.Success)
            {
                var version = match.Groups.Count > 1 && match.Groups[1].Success
                    ? NormalizeVersion(match.Groups[1].Value)
                    : "";
                return (software, version);
            }
        }
        return null;
    }

    /// <summary>
    /// Strips non-numeric suffixes from version strings: "8.9p1" becomes "8.9".
    /// </summary>
    private static string NormalizeVersion(string raw)
    {
        var sb = new StringBuilder();
        var lastWasDot = false;

        foreach (var c in raw)
        {
            if (char.IsDigit(c))
            {
                sb.Append(c);
                lastWasDot = false;
            }
            else if (c == '.' && !lastWasDot && sb.Length > 0)
            {
                sb.Append('.');
                lastWasDot = true;
            }
            else
            {
                break;
            }
        }

        return sb.ToString().TrimEnd('.');
    }

    private static bool VersionBelow(string version, string threshold)
    {
        var v = ParseVersion(version);
        var t = ParseVersion(threshold);
        return v.Length > 0 && t.Length > 0 && CompareVersions(v, t) < 0;
    }

    private static bool VersionInRange(string version, string from, string to)
    {
        var v = ParseVersion(version);
        var f = ParseVersion(from);
        var t = ParseVersion(to);
        return v.Length > 0 && f.Length > 0 && t.Length > 0
            && CompareVersions(v, f) >= 0 && CompareVersions(v, t) <= 0;
    }

    private static bool VersionEquals(string version, string target)
    {
        var v = ParseVersion(version);
        var t = ParseVersion(target);
        return v.Length > 0 && t.Length > 0 && CompareVersions(v, t) == 0;
    }

    private static int[] ParseVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];

        var normalized = NormalizeVersion(v);
        if (string.IsNullOrEmpty(normalized)) return [];

        var parts = normalized.Split('.');
        var result = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            if (int.TryParse(part, out var n))
                result.Add(n);
        }

        return [.. result];
    }

    private static int CompareVersions(int[] a, int[] b)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var va = i < a.Length ? a[i] : 0;
            var vb = i < b.Length ? b[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
