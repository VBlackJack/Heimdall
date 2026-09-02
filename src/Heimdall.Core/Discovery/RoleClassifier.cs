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

using Heimdall.Core.Models;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Heuristic role classification based on open port patterns.
/// Maps common port combinations to server roles (e.g., Active Directory,
/// Web Server, Database) with confidence scoring.
/// </summary>
public static class RoleClassifier
{
    /// <summary>Cached compiled regex for extracting CN from X.500 subject strings.</summary>
    private static readonly System.Text.RegularExpressions.Regex CnRegex = new(
        @"CN=([^,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly RoleDefinition[] Definitions =
    [
        // ── Enterprise servers ─────────────────────────────────────────
        new("Active Directory", [53, 88, 389], [636, 445, 135, 464, 3268, 3269], 95),
        new("DNS Server", [53], [], 60),
        new("Web Server", [80], [443, 8080, 8443, 9090, 9443, 3000], 70),
        new("Web Server (HTTPS)", [443], [8443, 9443], 75),
        new("Web Server (HTTPS-Alt)", [8443], [443, 9443], 65),
        new("Database (MySQL)", [3306], [33060], 85),
        new("Database (PostgreSQL)", [5432], [], 85),
        new("Database (MSSQL)", [1433], [1434], 85),
        new("Database (Oracle)", [1521], [], 85),
        new("Database (MongoDB)", [27017], [27018, 27019], 85),
        new("Mail Server", [25], [587, 465, 993, 995, 143, 110], 80),
        new("LDAP Directory", [389], [636, 22], 75),
        new("Syslog Server", [6514], [22], 65),
        new("HTTP Proxy", [3128], [8080, 80, 443], 70),
        new("SSH Server", [22], [], 50),
        new("Windows Server", [DefaultPorts.Rdp, 445], [135, 139, 5985, 5986, DefaultPorts.HttpStd, DefaultPorts.HttpsStd], 80),
        new("Windows RDP", [DefaultPorts.Rdp], [445, 135, 139, 5985], 70),
        new("VNC Server", [DefaultPorts.Vnc], [DefaultPorts.VncAlt, 5902], 65),
        new("Proxy/Load Balancer", [8080], [8443, 3128, 80, 443], 55),
        // Syslog (514/udp) and DHCP (67-68/udp) are UDP-only - detected via
        // banner fingerprints or SNMP, not TCP port scan.
        new("FTP Server", [21], [990], 60),
        new("Redis", [6379], [], 75),
        new("Elasticsearch", [9200], [9300], 75),
        new("Docker/Container Host", [2375], [2376], 60),
        new("Kubernetes API", [6443], [10250], 70),

        // ── Network equipment ──────────────────────────────────────────
        new("Network Equipment (SNMP)", [161], [162, 80, 443], 60),
        new("Router/Gateway (DNS)", [53, 80], [443, 554, 445, 161, 23, 8080], 72),
        new("Router/Gateway", [53, 67, 80], [443, 8080, 161, 23], 75),
        new("Managed Switch", [161, 22], [23, 80, 443], 65),
        new("MikroTik Router", [8291], [80, 443, 22, 8728, 8729], 80),
        new("Wireless Access Point", [80], [443, 22, 161], 40),

        // ── NAS / Storage ──────────────────────────────────────────────
        new("NAS (Synology)", [5000], [5001, 80, 443, 22, 139, 445], 85),
        new("NAS (QNAP)", [8080], [443, 22, 139, 445, 6881], 70),
        new("NAS/File Server", [139, 445], [21, 22, 80, 111, 2049], 60),
        new("NFS Server", [2049], [111], 70),
        new("iSCSI Target", [3260], [], 75),

        // ── IP Camera / Surveillance ───────────────────────────────────
        new("IP Camera (RTSP)", [554], [80, 443, 8080], 80),
        new("IP Camera (ONVIF)", [80, 8899], [554, 443], 75),
        new("DVR/NVR", [554, 80], [443, 37777, 34567], 75),

        // ── Printers ───────────────────────────────────────────────────
        new("Network Printer", [9100], [631, 515, 80], 80),
        new("Print Server (IPP)", [631], [9100, 515, 80], 70),

        // ── IoT / Smart devices ────────────────────────────────────────
        new("Smart Home Hub", [8123], [80, 443, 1883], 65),
        new("MQTT Broker", [1883], [8883, 9001], 75),
        new("Apple Device", [62078], [5353, 3689], 70),
        new("Chromecast/Smart TV", [8008], [8443, 9000], 70),
        // UPnP/SSDP (1900/udp) is UDP multicast - detected via SSDP probe, not TCP port scan.

        // ── Telephony / VoIP ───────────────────────────────────────────
        new("VoIP/SIP Server", [5060], [5061, 80, 443], 75),
        new("Asterisk PBX", [5060, 5038], [8088, 8089], 85),

        // ── Virtualization ─────────────────────────────────────────────
        new("VMware ESXi", [443, 902], [80, 22], 80),
        new("Proxmox VE", [8006], [22, 3128], 80),
        new("Hyper-V Host", [DefaultPorts.Rdp, 445], [135, 139, 5985, 5986], 60),

        // ── Server Management ─────────────────────────────────────────
        new("Server Management (IPMI)", [623], [443, 80, 22], 80),

        // ── Monitoring ─────────────────────────────────────────────────
        new("Zabbix Server", [10051], [10050, 80, 443], 75),
        new("Prometheus", [9090], [9093, 9094], 65),
        new("Grafana", [3000], [80, 443], 60),
        new("Nagios/Icinga", [80, 5665], [443, 5667], 65),

        // ── CI/CD ─────────────────────────────────────────────────────
        new("CI/CD (Jenkins)", [8080], [443, 50000], 55),
        new("GitLab Instance", [80, 443], [22, 8443, 5050], 55),
        new("Container Registry", [5000], [443, 5043], 55),

        // ── UPS / Power ───────────────────────────────────────────────
        new("UPS Management", [161], [80, 443], 40),
    ];

    /// <summary>
    /// Banner-based fingerprints for device identification beyond port analysis.
    /// Applied after port-based classification to refine or override roles.
    /// </summary>
    private static readonly (string Pattern, string Role, int Confidence)[] BannerFingerprints =
    [
        // Cameras
        ("Hikvision", "IP Camera (Hikvision)", 90),
        ("Dahua", "IP Camera (Dahua)", 90),
        ("AXIS", "IP Camera (AXIS)", 90),
        ("Vivotek", "IP Camera (Vivotek)", 85),
        ("Foscam", "IP Camera (Foscam)", 85),
        ("Reolink", "IP Camera (Reolink)", 85),
        ("ONVIF", "IP Camera (ONVIF)", 80),

        // NAS
        ("Synology", "NAS (Synology DSM)", 92),
        ("QNAP", "NAS (QNAP QTS)", 92),
        ("FreeNAS", "NAS (TrueNAS/FreeNAS)", 85),
        ("TrueNAS", "NAS (TrueNAS)", 85),
        ("Netgear ReadyNAS", "NAS (Netgear)", 85),
        ("Western Digital", "NAS (WD My Cloud)", 80),

        // Routers / Network
        ("MikroTik", "Router (MikroTik)", 90),
        ("RouterOS", "Router (MikroTik RouterOS)", 90),
        ("AVM FRITZ", "Router (Fritz!Box)", 90),
        ("Ubiquiti", "Network (Ubiquiti)", 85),
        ("UniFi", "Network (UniFi)", 85),
        ("OpenWrt", "Router (OpenWrt)", 85),
        ("DD-WRT", "Router (DD-WRT)", 85),
        ("pfSense", "Firewall (pfSense)", 90),
        ("OPNsense", "Firewall (OPNsense)", 90),
        ("Fortinet", "Firewall (FortiGate)", 90),
        ("FortiOS", "Firewall (FortiGate)", 90),
        ("Cisco IOS", "Router (Cisco IOS)", 90),
        ("Cisco Adaptive", "Firewall (Cisco ASA)", 90),
        ("TP-LINK", "Router (TP-Link)", 80),
        ("NETGEAR", "Router (Netgear)", 80),
        ("Linksys", "Router (Linksys)", 80),
        ("D-Link", "Router (D-Link)", 80),
        ("Zyxel", "Network (Zyxel)", 80),
        ("Aruba", "Network (Aruba)", 85),
        ("Juniper", "Network (Juniper)", 85),
        ("Palo Alto", "Firewall (Palo Alto)", 90),

        // Printers
        ("HP LaserJet", "Printer (HP LaserJet)", 90),
        ("HP Color LaserJet", "Printer (HP Color LaserJet)", 90),
        ("Brother", "Printer (Brother)", 85),
        ("Canon", "Printer (Canon)", 80),
        ("Epson", "Printer (Epson)", 80),
        ("Xerox", "Printer (Xerox)", 85),
        ("Ricoh", "Printer (Ricoh)", 85),
        ("Lexmark", "Printer (Lexmark)", 85),
        ("Kyocera", "Printer (Kyocera)", 85),
        ("CUPS", "Print Server (CUPS)", 75),

        // Hypervisors
        ("VMware", "Hypervisor (VMware)", 85),
        ("ESXi", "Hypervisor (VMware ESXi)", 90),
        ("Proxmox", "Hypervisor (Proxmox VE)", 90),
        ("XenServer", "Hypervisor (XenServer)", 85),

        // Smart Home / IoT
        ("Home Assistant", "Smart Home (Home Assistant)", 90),
        ("Philips Hue", "IoT (Philips Hue Bridge)", 85),
        ("Sonos", "IoT (Sonos Speaker)", 80),

        // Web Servers / Reverse Proxies
        ("nginx", "Web Server (nginx)", 80),
        ("Apache", "Web Server (Apache)", 80),
        ("Microsoft-IIS", "Web Server (IIS)", 80),
        ("Microsoft-HTTPAPI", "Windows HTTP API", 70),
        ("HAProxy", "Load Balancer (HAProxy)", 85),
        ("Traefik", "Reverse Proxy (Traefik)", 80),
        ("Caddy", "Web Server (Caddy)", 75),
        ("LiteSpeed", "Web Server (LiteSpeed)", 75),
        ("Kestrel", "Web Server (.NET Kestrel)", 70),
        ("Tomcat", "App Server (Tomcat)", 80),
        ("Jetty", "App Server (Jetty)", 75),

        // Mail Servers
        ("Postfix", "Mail Server (Postfix)", 85),
        ("Dovecot", "Mail Server (Dovecot)", 85),
        ("Exchange", "Mail Server (Exchange)", 90),

        // Directory / File Servers
        ("Samba", "File Server (Samba)", 80),
        ("OpenLDAP", "LDAP Server (OpenLDAP)", 85),
        ("389 Directory", "LDAP Server (389DS)", 85),

        // Proxy Servers
        ("squid", "HTTP Proxy (Squid)", 90),
        ("Varnish", "HTTP Cache (Varnish)", 85),

        // Syslog / Log Collectors
        ("rsyslog", "Syslog Server (rsyslog)", 85),
        ("syslog-ng", "Syslog Server (syslog-ng)", 85),

        // Server Management
        ("iDRAC", "Server Management (Dell iDRAC)", 95),
        ("iLO", "Server Management (HP iLO)", 95),

        // Appliance Identification (via HTML <title>)
        ("Proxmox Virtual Environment", "Hypervisor (Proxmox VE)", 95),
        ("UniFi Network", "Network Controller (UniFi)", 90),
        ("Pi-hole", "DNS Filter (Pi-hole)", 85),

        // NTP
        ("ntpd", "NTP Server", 80),
        ("chrony", "NTP Server (Chrony)", 80),

        // IoT / Smart Home (additional)
        ("Shelly", "IoT (Shelly)", 90),
        ("Tasmota", "IoT (Tasmota)", 90),
        ("ESPHome", "IoT (ESPHome)", 85),
        ("ESPEasy", "IoT (ESP Easy)", 80),
        ("Tuya", "IoT (Tuya)", 80),

        // DevOps / CI-CD
        ("Portainer", "Container Management (Portainer)", 90),
        ("Rancher", "Container Management (Rancher)", 85),
        ("Jenkins", "CI/CD (Jenkins)", 90),
        ("GitLab", "CI/CD (GitLab)", 90),
        ("Gitea", "Git Server (Gitea)", 85),
        ("Gogs", "Git Server (Gogs)", 80),
        ("Nexus Repository", "Artifact Repository (Nexus)", 85),
        ("SonarQube", "Code Analysis (SonarQube)", 85),
        ("Grafana", "Monitoring (Grafana)", 85),

        // Management Appliances
        ("Proxmox Backup Server", "Backup (Proxmox BS)", 90),
        ("Veeam", "Backup (Veeam)", 90),
        ("NetApp", "Storage (NetApp)", 90),
        ("Zabbix", "Monitoring (Zabbix)", 85),
        ("Nagios", "Monitoring (Nagios)", 85),
        ("Icinga", "Monitoring (Icinga)", 85),
    ];

    /// <summary>
    /// TLS certificate domain fingerprints for device identification.
    /// Self-signed certs on appliances often reveal device type via CN/SAN.
    /// </summary>
    private static readonly (string DomainPattern, string Role, int Boost)[] CertificateDomainFingerprints =
    [
        // ISP routers / gateways
        ("fbxos.fr", "Router/Gateway (Freebox)", 30),
        ("freebox", "Router/Gateway (Freebox)", 30),
        ("fritz.box", "Router (Fritz!Box)", 30),
        ("bbox.fr", "Router/Gateway (Bouygues)", 30),
        ("sfr.fr", "Router/Gateway (SFR)", 30),
        ("livebox", "Router/Gateway (Livebox)", 30),

        // Network equipment
        ("tplinkrepeater", "WiFi Repeater (TP-Link)", 35),
        ("tplinkwifi", "Router (TP-Link)", 30),
        ("tplinkap", "Wireless Access Point (TP-Link)", 30),
        ("tplinkmifi", "Mobile Hotspot (TP-Link)", 25),
        ("tplinkextender", "WiFi Repeater (TP-Link)", 35),
        ("ubnt.com", "Network (Ubiquiti)", 25),
        ("unifi", "Network Controller (UniFi)", 25),
        ("mikrotik", "Router (MikroTik)", 30),
        ("netgear", "Router (Netgear)", 25),
        ("dlink", "Router (D-Link)", 25),
        ("zyxel", "Network (Zyxel)", 25),

        // NAS / storage
        ("synology", "NAS (Synology)", 25),
        ("myqnapcloud", "NAS (QNAP)", 25),

        // Generic patterns (lower boost, broader match)
        ("router", "Router/Gateway", 15),
        ("gateway", "Router/Gateway", 15),
        ("repeater", "WiFi Repeater", 20),
        ("extender", "WiFi Repeater", 20),
    ];

    /// <summary>
    /// Manufacturer-to-role mapping for devices with no open ports.
    /// Used as last-resort identification when all other signals are absent.
    /// </summary>
    private static readonly (string ManufacturerPattern, string Role, int Confidence)[] ManufacturerRoleInference =
    [
        ("Apple", "Mobile Device (Apple)", 40),
        ("Samsung", "Mobile/Smart TV (Samsung)", 35),
        ("Google Nest", "Smart Home (Google Nest)", 45),
        ("Google Chromecast", "Chromecast (Google)", 50),
        ("Google", "Smart Device (Google)", 35),
        ("Amazon", "Smart Device (Amazon)", 35),
        ("Ring", "IoT (Ring)", 45),
        ("Arlo", "IP Camera (Arlo)", 50),
        ("Securitas", "Alarm System (Verisure)", 55),
        ("Verisure", "Alarm System (Verisure)", 55),
        ("Hikvision", "IP Camera (Hikvision)", 55),
        ("Dahua", "IP Camera (Dahua)", 55),
        ("Sony", "PlayStation/Smart TV (Sony)", 30),
        ("Nintendo", "Game Console (Nintendo)", 40),
        ("Microsoft", "Windows Device", 30),
        ("Xiaomi", "Mobile Device (Xiaomi)", 35),
        ("Huawei", "Mobile Device (Huawei)", 35),
        ("OnePlus", "Mobile Device (OnePlus)", 35),
        ("Raspberry Pi", "Raspberry Pi", 45),
        ("Espressif", "IoT Device (ESP)", 40),
        ("Sonos", "Smart Speaker (Sonos)", 50),
        ("Philips Hue", "IoT (Philips Hue Bridge)", 50),
        ("LIFX", "IoT (LIFX)", 45),
        ("Nest", "Smart Home (Nest)", 45),
        ("Roku", "Media Streamer (Roku)", 50),
        ("Free (Freebox)", "Router/Gateway (Freebox)", 50),
        ("Freebox", "Router/Gateway (Freebox)", 50),
        ("Private (Randomized MAC)", "Smartphone/Tablet", 35),
        ("Sagemcom", "Router/Gateway (ISP CPE)", 40),
        ("Technicolor", "Router/Gateway (ISP CPE)", 40),
        ("AVM", "Router (Fritz!Box)", 45),
    ];

    /// <summary>
    /// Classifies a host based on its open ports, returning all matching roles
    /// sorted by descending confidence.
    /// </summary>
    public static List<RoleMatch> Classify(IReadOnlyList<int> openPorts)
    {
        var matches = new List<RoleMatch>();
        var portSet = new HashSet<int>(openPorts);

        foreach (var def in Definitions)
        {
            var requiredHits = def.RequiredPorts.Count(p => portSet.Contains(p));
            if (requiredHits == 0) continue;

            var optionalHits = def.OptionalPorts.Count(p => portSet.Contains(p));

            var requiredCoverage = (double)requiredHits / def.RequiredPorts.Length;
            var confidence = (int)(def.BaseConfidence * requiredCoverage);
            if (optionalHits > 0) confidence += optionalHits * 5;
            confidence = Math.Min(confidence, 99);

            if (requiredCoverage < 1.0 && confidence < 40) continue;

            var evidence = new List<string>();
            foreach (var p in def.RequiredPorts.Where(p => portSet.Contains(p)))
                evidence.Add($"port:{p} ({GetPortServiceName(p)})");
            foreach (var p in def.OptionalPorts.Where(p => portSet.Contains(p)))
                evidence.Add($"port:{p} ({GetPortServiceName(p)}) [optional]");

            matches.Add(new RoleMatch(def.RoleName, confidence, [.. evidence]));
        }

        return [.. matches.OrderByDescending(m => m.Confidence)];
    }

    /// <summary>
    /// Enhanced classification using both open ports AND service banners.
    /// Banner fingerprints can identify specific devices (cameras, NAS, routers, printers)
    /// that port-only analysis would classify as generic "Web Server" or "Unknown".
    /// </summary>
    public static List<RoleMatch> ClassifyWithBanners(
        IReadOnlyList<int> openPorts,
        IReadOnlyList<string?> banners)
    {
        // Start with port-based classification
        var matches = Classify(openPorts);

        // Scan all banners for device fingerprints
        foreach (var banner in banners)
        {
            if (string.IsNullOrWhiteSpace(banner)) continue;

            foreach (var (pattern, role, confidence) in BannerFingerprints)
            {
                if (banner.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    // Check if this role is already matched (avoid duplicates)
                    if (matches.Any(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    matches.Add(new RoleMatch(
                        role,
                        confidence,
                        [$"banner: \"{pattern}\" detected"]));
                }
            }
        }

        return [.. matches.OrderByDescending(m => m.Confidence)];
    }

    /// <summary>
    /// Full enriched classification using ports, banners, OS fingerprint, NetBIOS,
    /// SNMP, mDNS services, HTTP headers, TLS certificates, and SSDP device info.
    /// </summary>
    public static List<RoleMatch> ClassifyEnriched(
        IReadOnlyList<int> openPorts,
        IReadOnlyList<string?> banners,
        OsFingerprint? os,
        string? netBiosName,
        string? netBiosDomain,
        SnmpInfo? snmp,
        List<string>? mdnsServices,
        Dictionary<string, string>? httpHeaders,
        IReadOnlyList<CertificateInfo>? certificates = null,
        SsdpInfo? ssdp = null)
    {
        var matches = ClassifyWithBanners(openPorts, banners);

        // SNMP-based evidence
        if (snmp?.SysDescr is not null)
        {
            var descr = snmp.SysDescr;
            ApplySnmpBoosts(matches, descr);
        }

        // SNMP sysObjectID enterprise OID classification (Cisco, HP, etc.)
        if (snmp?.SysObjectId is not null)
        {
            ApplySnmpOidBoosts(matches, snmp.SysObjectId);
        }

        // NetBIOS domain membership boosts Windows-related roles
        if (!string.IsNullOrEmpty(netBiosDomain))
        {
            BoostMatchesContaining(matches, "Windows", 10, $"netbios-domain: {netBiosDomain}");
            BoostMatchesContaining(matches, "RDP", 10, $"netbios-domain: {netBiosDomain}");
            BoostMatchesContaining(matches, "Active Directory", 10, $"netbios-domain: {netBiosDomain}");
        }

        // mDNS service evidence
        if (mdnsServices is { Count: > 0 })
        {
            ApplyMdnsBoosts(matches, mdnsServices);
        }

        // HTTP header evidence
        if (httpHeaders is { Count: > 0 })
        {
            ApplyHttpHeaderBoosts(matches, httpHeaders);
        }

        // TLS certificate domain evidence
        if (certificates is { Count: > 0 })
        {
            ApplyCertificateBoosts(matches, certificates);
        }

        // SSDP/UPnP device type evidence
        if (ssdp is not null)
        {
            ApplySsdpBoosts(matches, ssdp);
        }

        // OS + port cross-reference
        if (os is not null)
        {
            ApplyOsBoosts(matches, os, openPorts);
        }

        // Resolve conflicting classifications (e.g., DNS server misclassified as camera)
        ApplyConflictResolution(matches, openPorts);

        return [.. matches.OrderByDescending(m => m.Confidence)];
    }

    private static void ApplySnmpBoosts(List<RoleMatch> matches, string sysDescr)
    {
        var snmpRoles = new (string Pattern, string Role, int Boost)[]
        {
            ("Cisco", "Router", 15),
            ("Cisco", "Switch", 15),
            ("Cisco", "Network", 15),
            ("HP ETHERNET", "Printer", 20),
            ("Hewlett-Packard", "Printer", 15),
            ("RICOH", "Printer", 20),
            ("KYOCERA", "Printer", 20),
            ("Brother", "Printer", 20),
            ("Canon", "Printer", 15),
            ("Xerox", "Printer", 20),
            ("Lexmark", "Printer", 20),
            ("APC", "UPS", 25),
            ("Eaton", "UPS", 25),
            ("Liebert", "UPS", 25),
            ("Juniper", "Network", 15),
            ("MikroTik", "Router", 15),
            ("Synology", "NAS", 15),
            ("QNAP", "NAS", 15),
            ("VMware", "Hypervisor", 15),
            ("Linux", "Linux", 10),
            ("Windows", "Windows", 10),
        };

        foreach (var (pattern, roleFragment, boost) in snmpRoles)
        {
            if (!sysDescr.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;

            var boosted = false;
            for (var i = 0; i < matches.Count; i++)
            {
                if (matches[i].Role.Contains(roleFragment, StringComparison.OrdinalIgnoreCase))
                {
                    matches[i] = matches[i] with
                    {
                        Confidence = Math.Min(99, matches[i].Confidence + boost),
                        Evidence = [.. matches[i].Evidence, $"snmp-sysDescr: \"{pattern}\""]
                    };
                    boosted = true;
                }
            }

            // If no existing match was boosted, add a new one for specific appliances
            if (!boosted && (pattern is "APC" or "Eaton" or "Liebert"))
            {
                matches.Add(new RoleMatch($"UPS ({pattern})", 70,
                    [$"snmp-sysDescr: \"{pattern}\""]));
            }
        }
    }

    /// <summary>
    /// Classifies devices based on SNMP sysObjectID enterprise OID prefix.
    /// Cisco uses 1.3.6.1.4.1.9.*, with sub-branches for product families.
    /// </summary>
    private static void ApplySnmpOidBoosts(List<RoleMatch> matches, string sysObjectId)
    {
        // Cisco enterprise OID: 1.3.6.1.4.1.9
        if (sysObjectId.StartsWith("1.3.6.1.4.1.9.", StringComparison.Ordinal))
        {
            var ciscoRoles = new (string OidPrefix, string Role, int Confidence)[]
            {
                ("1.3.6.1.4.1.9.1.",    "Router (Cisco)", 85),      // ciscoProducts (routers, general)
                ("1.3.6.1.4.1.9.5.",    "Switch (Cisco Catalyst)", 85), // Catalyst switches
                ("1.3.6.1.4.1.9.6.",    "Switch (Cisco)", 80),      // Other switches
                ("1.3.6.1.4.1.9.9.",    "Network (Cisco)", 70),     // MIB objects (generic)
                ("1.3.6.1.4.1.9.12.",   "Network (Cisco)", 75),     // Cisco entity physical
            };

            foreach (var (prefix, role, conf) in ciscoRoles)
            {
                if (!sysObjectId.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var existingCisco = matches.FindIndex(m =>
                    m.Role.Contains("Cisco", StringComparison.OrdinalIgnoreCase));
                if (existingCisco >= 0)
                {
                    matches[existingCisco] = matches[existingCisco] with
                    {
                        Confidence = Math.Min(99, Math.Max(matches[existingCisco].Confidence, conf)),
                        Evidence = [.. matches[existingCisco].Evidence, $"snmp-oid: {sysObjectId}"]
                    };
                }
                else
                {
                    matches.Add(new RoleMatch(role, conf, [$"snmp-oid: {sysObjectId}"]));
                }
                break;
            }
        }

        // Other enterprise OIDs
        var vendors = new (string OidPrefix, string Role, int Confidence)[]
        {
            ("1.3.6.1.4.1.2636.",  "Router (Juniper)", 85),         // Juniper
            ("1.3.6.1.4.1.14988.", "Router (MikroTik)", 85),        // MikroTik
            ("1.3.6.1.4.1.12356.", "Firewall (FortiGate)", 85),     // Fortinet
            ("1.3.6.1.4.1.25461.", "Firewall (Palo Alto)", 85),     // Palo Alto
            ("1.3.6.1.4.1.8072.",  "Linux (Net-SNMP)", 60),         // Net-SNMP (Linux)
            ("1.3.6.1.4.1.311.",   "Windows Server", 60),           // Microsoft
            ("1.3.6.1.4.1.6876.",  "Hypervisor (VMware)", 80),      // VMware
        };

        foreach (var (prefix, role, conf) in vendors)
        {
            if (!sysObjectId.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var existing = matches.FindIndex(m =>
                m.Role.Contains(role.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                matches[existing] = matches[existing] with
                {
                    Confidence = Math.Min(99, Math.Max(matches[existing].Confidence, conf)),
                    Evidence = [.. matches[existing].Evidence, $"snmp-oid: {sysObjectId}"]
                };
            }
            else
            {
                matches.Add(new RoleMatch(role, conf, [$"snmp-oid: {sysObjectId}"]));
            }
            break;
        }
    }

    private static void ApplyMdnsBoosts(List<RoleMatch> matches, List<string> services)
    {
        var mdnsRoles = new (string Service, string RoleFragment, string NewRole, int Boost)[]
        {
            ("IPP Printer", "Printer", "Network Printer (mDNS)", 15),
            ("IPP Printer (TLS)", "Printer", "Network Printer (mDNS)", 15),
            ("Network Printer", "Printer", "Network Printer (mDNS)", 15),
            ("AirPlay", "Apple", "Apple Device (AirPlay)", 20),
            ("AirPlay Audio", "Apple", "Apple Device (AirPlay)", 20),
            ("Apple Companion", "Apple", "Apple Device", 15),
            ("Chromecast", "Chromecast", "Chromecast/Smart TV", 25),
            ("HomeKit", "IoT", "IoT (HomeKit)", 20),
            ("HomeKit Accessory", "IoT", "IoT (HomeKit)", 20),
            ("MQTT", "MQTT", "MQTT Broker", 10),
            ("Spotify Connect", "IoT", "Media Player (Spotify)", 15),
            ("Sonos", "Sonos", "IoT (Sonos Speaker)", 20),
            ("Scanner", "Printer", "Scanner/Printer (mDNS)", 10),
            ("Freebox API", "Router", "Router/Gateway (Freebox)", 30),
            ("PSIA IP Camera", "Camera", "IP Camera (PSIA)", 15),
            ("Arlo Camera", "Camera", "IP Camera (Arlo)", 15),
        };

        foreach (var (service, roleFragment, newRole, boost) in mdnsRoles)
        {
            if (!services.Any(s => s.Equals(service, StringComparison.OrdinalIgnoreCase))) continue;

            var boosted = false;
            for (var i = 0; i < matches.Count; i++)
            {
                if (matches[i].Role.Contains(roleFragment, StringComparison.OrdinalIgnoreCase))
                {
                    matches[i] = matches[i] with
                    {
                        Confidence = Math.Min(99, matches[i].Confidence + boost),
                        Evidence = [.. matches[i].Evidence, $"mdns: {service}"]
                    };
                    boosted = true;
                }
            }

            if (!boosted)
            {
                matches.Add(new RoleMatch(newRole, Math.Min(80, 50 + boost),
                    [$"mdns: {service}"]));
            }
        }
    }

    private static void ApplyHttpHeaderBoosts(List<RoleMatch> matches, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue("X-Powered-By", out var poweredBy))
        {
            if (poweredBy.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase))
            {
                BoostMatchesContaining(matches, "Windows", 10, $"http: X-Powered-By={poweredBy}");
                BoostMatchesContaining(matches, "IIS", 10, $"http: X-Powered-By={poweredBy}");
            }
        }

        if (headers.TryGetValue("WWW-Authenticate", out var auth))
        {
            if (auth.Contains("NTLM", StringComparison.OrdinalIgnoreCase))
                BoostMatchesContaining(matches, "Windows", 15, "http: WWW-Authenticate=NTLM");
            if (auth.Contains("Negotiate", StringComparison.OrdinalIgnoreCase))
                BoostMatchesContaining(matches, "Active Directory", 10, "http: WWW-Authenticate=Negotiate");
        }
    }

    private static void ApplyOsBoosts(List<RoleMatch> matches, OsFingerprint os, IReadOnlyList<int> openPorts)
    {
        var portSet = new HashSet<int>(openPorts);

        if (os.OsGuess.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            // Windows OS + AD ports → boost AD confidence
            if (portSet.Contains(88) && portSet.Contains(389))
                BoostMatchesContaining(matches, "Active Directory", 10, $"os: {os.OsGuess}");

            BoostMatchesContaining(matches, "Windows", 5, $"os: {os.OsGuess}");
            BoostMatchesContaining(matches, "RDP", 5, $"os: {os.OsGuess}");
        }
        else if (os.OsGuess.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
                 os.OsGuess.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) ||
                 os.OsGuess.Contains("Debian", StringComparison.OrdinalIgnoreCase) ||
                 os.OsGuess.Contains("CentOS", StringComparison.OrdinalIgnoreCase) ||
                 os.OsGuess.Contains("Red Hat", StringComparison.OrdinalIgnoreCase))
        {
            BoostMatchesContaining(matches, "SSH", 5, $"os: {os.OsGuess}");
            BoostMatchesContaining(matches, "LDAP", 5, $"os: {os.OsGuess}");
            BoostMatchesContaining(matches, "Syslog", 5, $"os: {os.OsGuess}");
            BoostMatchesContaining(matches, "Proxy", 5, $"os: {os.OsGuess}");
        }
    }

    private static void BoostMatchesContaining(
        List<RoleMatch> matches, string roleFragment, int boost, string evidence)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].Role.Contains(roleFragment, StringComparison.OrdinalIgnoreCase))
            {
                matches[i] = matches[i] with
                {
                    Confidence = Math.Min(99, matches[i].Confidence + boost),
                    Evidence = [.. matches[i].Evidence, evidence]
                };
            }
        }
    }

    /// <summary>
    /// Boosts or adds role matches based on TLS certificate CN and SAN domains.
    /// Appliance self-signed certs often embed the device type in the domain name.
    /// </summary>
    private static void ApplyCertificateBoosts(
        List<RoleMatch> matches, IReadOnlyList<CertificateInfo> certificates)
    {
        foreach (var cert in certificates)
        {
            // Collect all identifiers from cert subject CN and SANs
            var domains = new List<string>();

            // Parse CN from X.500 subject string (e.g., "CN=foo.bar, O=Org")
            var cnMatch = CnRegex.Match(cert.Subject);
            if (cnMatch.Success)
                domains.Add(cnMatch.Groups[1].Value.Trim());

            if (cert.SubjectAltNames is { Length: > 0 })
                domains.AddRange(cert.SubjectAltNames);

            // Also check issuer O= field for vendor identification
            var issuerO = ExtractField(cert.Issuer, "O=");
            if (issuerO is not null) domains.Add(issuerO);

            // Check subject O= and OU= for appliance identification
            var subjectO = ExtractField(cert.Subject, "O=");
            if (subjectO is not null) domains.Add(subjectO);
            var subjectOu = ExtractField(cert.Subject, "OU=");
            if (subjectOu is not null) domains.Add(subjectOu);

            if (domains.Count == 0) continue;

            // Self-signed cert with ~10-year validity = appliance default cert
            var isSelfSigned = cert.Subject == cert.Issuer;
            var validityDays = (cert.NotAfter - cert.NotBefore).TotalDays;
            if (isSelfSigned && validityDays is >= 3640 and <= 3660)
            {
                // Typical appliance default cert (3650 days = 10 years)
                for (var i = 0; i < matches.Count; i++)
                {
                    if (matches[i].Role.Contains("Web Server", StringComparison.OrdinalIgnoreCase))
                    {
                        matches[i] = matches[i] with
                        {
                            Evidence = [.. matches[i].Evidence, "cert: self-signed 10yr (appliance default)"]
                        };
                    }
                }
            }

            foreach (var (pattern, role, boost) in CertificateDomainFingerprints)
            {
                if (!domains.Any(d => d.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var boosted = false;
                // Extract the role family keyword for matching (first word before parenthesis)
                var roleFamily = role.Split('(')[0].Trim().Split('/')[0].Trim();

                for (var i = 0; i < matches.Count; i++)
                {
                    if (matches[i].Role.Contains(roleFamily, StringComparison.OrdinalIgnoreCase))
                    {
                        matches[i] = matches[i] with
                        {
                            Confidence = Math.Min(99, matches[i].Confidence + boost),
                            Evidence = [.. matches[i].Evidence, $"cert-cn: \"{pattern}\""]
                        };
                        boosted = true;
                    }
                }

                if (!boosted)
                {
                    matches.Add(new RoleMatch(role, Math.Min(95, 60 + boost),
                        [$"cert-cn: \"{pattern}\""]));
                }
            }
        }
    }

    /// <summary>
    /// Boosts or adds role matches based on SSDP/UPnP device type information.
    /// </summary>
    private static void ApplySsdpBoosts(List<RoleMatch> matches, SsdpInfo ssdp)
    {
        var ssdpRoles = new (string Pattern, string RoleFragment, string NewRole, int Boost)[]
        {
            ("InternetGatewayDevice", "Router", "Router/Gateway (UPnP)", 25),
            ("MediaRenderer", "Smart TV", "Smart TV/Media Player (UPnP)", 20),
            ("MediaServer", "Media", "Media Server (UPnP)", 20),
            ("Printer", "Printer", "Network Printer (UPnP)", 15),
        };

        var deviceType = ssdp.DeviceType ?? "";
        var server = ssdp.Server ?? "";
        var searchText = $"{deviceType} {ssdp.FriendlyName} {ssdp.ModelName} {server}";

        foreach (var (pattern, roleFragment, newRole, boost) in ssdpRoles)
        {
            if (!searchText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;

            var boosted = false;
            for (var i = 0; i < matches.Count; i++)
            {
                if (matches[i].Role.Contains(roleFragment, StringComparison.OrdinalIgnoreCase))
                {
                    matches[i] = matches[i] with
                    {
                        Confidence = Math.Min(99, matches[i].Confidence + boost),
                        Evidence = [.. matches[i].Evidence, $"ssdp: {pattern}"]
                    };
                    boosted = true;
                }
            }

            if (!boosted)
            {
                matches.Add(new RoleMatch(newRole, Math.Min(85, 55 + boost),
                    [$"ssdp: {pattern}"]));
            }
        }

        // Also check SSDP manufacturer/server string against banner fingerprints
        if (!string.IsNullOrEmpty(server))
        {
            foreach (var (pattern, role, confidence) in BannerFingerprints)
            {
                if (server.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    if (!matches.Any(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add(new RoleMatch(role, Math.Min(confidence, 80),
                            [$"ssdp-server: \"{pattern}\""]));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves conflicting role classifications. For example, DNS servers
    /// are almost never IP cameras, so DNS presence suppresses camera confidence.
    /// </summary>
    private static void ApplyConflictResolution(
        List<RoleMatch> matches, IReadOnlyList<int> openPorts)
    {
        var portSet = new HashSet<int>(openPorts);

        // DNS port open → suppress camera roles (DNS servers are not cameras)
        if (portSet.Contains(53))
        {
            SuppressRoles(matches, ["Camera", "DVR", "NVR"], 25, "conflict: DNS suppresses camera");
        }

        // LDAP (389/636) present → suppress generic SSH Server (LDAP is the primary role)
        if (portSet.Contains(389) || portSet.Contains(636))
        {
            SuppressRoles(matches, ["SSH Server"], 20, "conflict: LDAP suppresses generic SSH");
        }

        // Windows Server (RDP + SMB) present → suppress generic Windows RDP
        if (portSet.Contains(DefaultPorts.Rdp) && portSet.Contains(445))
        {
            SuppressRoles(matches, ["Windows RDP"], 15, "conflict: Windows Server suppresses generic RDP");
        }

        // Active Directory present → suppress generic LDAP Directory and Windows Server
        if (portSet.Contains(88) && portSet.Contains(389) && portSet.Contains(53))
        {
            SuppressRoles(matches, ["LDAP Directory", "Windows Server", "DNS Server"],
                20, "conflict: AD suppresses partial roles");
        }

        // Syslog-TLS (6514) present → suppress generic SSH Server
        if (portSet.Contains(6514))
        {
            SuppressRoles(matches, ["SSH Server"], 15, "conflict: Syslog suppresses generic SSH");
        }

        // HTTP Proxy (3128) present → suppress generic SSH Server
        if (portSet.Contains(3128))
        {
            SuppressRoles(matches, ["SSH Server"], 15, "conflict: Proxy suppresses generic SSH");
        }
    }

    private static void SuppressRoles(
        List<RoleMatch> matches, string[] roleFragments, int penalty, string evidence)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            foreach (var fragment in roleFragments)
            {
                if (matches[i].Role.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    matches[i] = matches[i] with
                    {
                        Confidence = Math.Max(0, matches[i].Confidence - penalty),
                        Evidence = [.. matches[i].Evidence, evidence]
                    };
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Last-resort role inference based on MAC manufacturer when no open ports
    /// or other signals are available.
    /// </summary>
    public static RoleMatch? InferFromManufacturer(string? manufacturer)
    {
        if (string.IsNullOrEmpty(manufacturer)) return null;

        foreach (var (pattern, role, confidence) in ManufacturerRoleInference)
        {
            if (manufacturer.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return new RoleMatch(role, confidence,
                    [$"manufacturer: \"{manufacturer}\""]);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a human-readable service name for a well-known port number.
    /// </summary>
    public static string GetPortServiceName(int port) => port switch
    {
        21 => "FTP",
        22 => "SSH",
        25 => "SMTP",
        53 => "DNS",
        67 => "DHCP",
        68 => "DHCP",
        80 => "HTTP",
        88 => "Kerberos",
        110 => "POP3",
        135 => "RPC",
        139 => "NetBIOS",
        143 => "IMAP",
        161 => "SNMP",
        162 => "SNMP-Trap",
        389 => "LDAP",
        443 => "HTTPS",
        445 => "SMB",
        464 => "Kerberos-Change",
        465 => "SMTPS",
        514 => "Syslog",
        587 => "SMTP-Submission",
        623 => "IPMI",
        636 => "LDAPS",
        993 => "IMAPS",
        995 => "POP3S",
        990 => "FTPS",
        1433 => "MSSQL",
        1434 => "MSSQL-Browser",
        1521 => "Oracle",
        2375 => "Docker",
        2376 => "Docker-TLS",
        3128 => "Squid",
        3268 => "Global-Catalog",
        3269 => "GC-SSL",
        3306 => "MySQL",
        DefaultPorts.Rdp => "RDP",
        5432 => "PostgreSQL",
        DefaultPorts.Vnc => "VNC",
        6379 => "Redis",
        6443 => "K8s-API",
        6514 => "Syslog-TLS",
        8080 => "HTTP-Alt",
        8443 => "HTTPS-Alt",
        9090 => "Web-Console",
        9200 => "Elasticsearch",
        9300 => "ES-Transport",
        10250 => "Kubelet",
        27017 => "MongoDB",
        554 => "RTSP",
        631 => "IPP",
        515 => "LPD",
        902 => "VMware-Auth",
        1883 => "MQTT",
        1900 => "UPnP/SSDP",
        2049 => "NFS",
        3000 => "Grafana/Dev",
        3260 => "iSCSI",
        3689 => "DAAP",
        5000 => "Synology-HTTP",
        5001 => "Synology-HTTPS",
        5038 => "Asterisk-AMI",
        5060 => "SIP",
        5061 => "SIP-TLS",
        5353 => "mDNS",
        5665 => "Icinga2",
        5667 => "NSCA",
        5985 => "WinRM-HTTP",
        5986 => "WinRM-HTTPS",
        8006 => "Proxmox-Web",
        8008 => "Chromecast",
        8088 => "Asterisk-HTTP",
        8123 => "Home-Assistant",
        8291 => "MikroTik-Winbox",
        8728 => "MikroTik-API",
        8883 => "MQTT-TLS",
        8899 => "ONVIF",
        9001 => "MQTT-WS",
        9100 => "RAW-Print",
        9443 => "HTTPS-Alt",
        10050 => "Zabbix-Agent",
        10051 => "Zabbix-Server",
        27018 => "MongoDB-Shard",
        27019 => "MongoDB-Config",
        33060 => "MySQL-X",
        34567 => "DVR-Admin",
        37777 => "Dahua-DVR",
        62078 => "Apple-Lockdown",
        _ => $"Port-{port}"
    };

    /// <summary>Extracts a single field value from an X.500 DN string.</summary>
    private static string? ExtractField(string dn, string field)
    {
        var idx = dn.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + field.Length;
        var end = dn.IndexOf(',', start);
        return end > start ? dn[start..end].Trim() : dn[start..].Trim();
    }

    private sealed record RoleDefinition(
        string RoleName,
        int[] RequiredPorts,
        int[] OptionalPorts,
        int BaseConfidence);
}
