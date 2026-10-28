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

using System.Net;
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Logging;
using Heimdall.Core.Network;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Raw UDP probes for NetBIOS, SNMP, and mDNS device discovery.
/// All packets are constructed manually without external libraries.
/// </summary>
public static class UdpProbeEngine
{
    private const int NetBiosPort = 137;
    private const int SnmpPort = 161;
    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");

    private const string DefaultSnmpCommunity = "public";

    /// <summary>
    /// Human-readable names for well-known mDNS service types.
    /// </summary>
    private static readonly Dictionary<string, string> MdnsServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_airplay._tcp"] = "AirPlay",
        ["_raop._tcp"] = "AirPlay Audio",
        ["_ipp._tcp"] = "IPP Printer",
        ["_ipps._tcp"] = "IPP Printer (TLS)",
        ["_http._tcp"] = "HTTP",
        ["_https._tcp"] = "HTTPS",
        ["_smb._tcp"] = "SMB File Sharing",
        ["_afpovertcp._tcp"] = "AFP (Apple Filing)",
        ["_googlecast._tcp"] = "Chromecast",
        ["_homekit._tcp"] = "HomeKit",
        ["_hap._tcp"] = "HomeKit Accessory",
        ["_companion-link._tcp"] = "Apple Companion",
        ["_ssh._tcp"] = "SSH",
        ["_sftp-ssh._tcp"] = "SFTP",
        ["_printer._tcp"] = "Printer",
        ["_pdl-datastream._tcp"] = "Network Printer",
        ["_scanner._tcp"] = "Scanner",
        ["_mqtt._tcp"] = "MQTT",
        ["_spotify-connect._tcp"] = "Spotify Connect",
        ["_sonos._tcp"] = "Sonos",
        ["_daap._tcp"] = "iTunes Sharing",
        ["_sleep-proxy._udp"] = "Sleep Proxy",
        ["_nfs._tcp"] = "NFS",
        ["_ftp._tcp"] = "FTP",
        ["_rdp._tcp"] = "RDP",
        ["_device-info._tcp"] = "Device Info",
        ["_fbx-api._tcp"] = "Freebox API",
        ["_psia._tcp"] = "PSIA IP Camera",
        ["_cgi._tcp"] = "CGI Service",
        ["_arlo-video._tcp"] = "Arlo Camera",
        ["_axis-video._tcp"] = "AXIS Camera",
        ["_nvr._tcp"] = "Network Video Recorder",
        ["_coap._udp"] = "IoT CoAP",
        ["_hue._tcp"] = "Philips Hue",
        ["_matter._tcp"] = "Matter Smart Home",
        ["_thread._udp"] = "Thread Network",
    };

    // ── NetBIOS NBSTAT ───────────────────────────────────────────────

    /// <summary>
    /// Sends a NetBIOS Name Query (NBSTAT) to UDP 137 and parses the response
    /// to extract the computer name, workgroup/domain, and MAC address.
    /// </summary>
    public static async Task<(string? Name, string? Domain, string? Mac)> QueryNetBiosAsync(
        string ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;

            var packet = BuildNetBiosNbstatQuery();
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), NetBiosPort);
            await udp.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return ParseNetBiosResponse(result.Buffer);
        }
        catch (OperationCanceledException) { return (null, null, null); }
        catch (Exception ex)
        {
            FileLogger.Log("DEBUG", $"NetBIOS query to {ip} failed: {ex.Message}");
            return (null, null, null);
        }
    }

    internal static byte[] BuildNetBiosNbstatQuery()
    {
        // NetBIOS Name Service packet for NBSTAT query (wildcard name *)
        var packet = new byte[50];
        var txId = (ushort)Random.Shared.Next(0, 0xFFFF);

        // Transaction ID
        packet[0] = (byte)(txId >> 8);
        packet[1] = (byte)(txId & 0xFF);
        // Flags: 0x0000 (standard query)
        // Questions: 1
        packet[4] = 0x00;
        packet[5] = 0x01;
        // Answers, Authority, Additional: 0

        // Encoded NetBIOS name for wildcard "*" query
        // Length byte: 32
        packet[12] = 0x20;
        // "*" encoded: first char is '*' (0x2A), rest padded with spaces (0x20)
        // Each byte is split into two nibble characters offset by 'A'
        var name = new byte[16];
        name[0] = 0x2A; // '*'
        for (var i = 1; i < 16; i++) name[i] = 0x20; // space padding

        for (var i = 0; i < 16; i++)
        {
            packet[13 + i * 2] = (byte)('A' + ((name[i] >> 4) & 0x0F));
            packet[14 + i * 2] = (byte)('A' + (name[i] & 0x0F));
        }

        // Null terminator after name
        packet[45] = 0x00;
        // Type: NBSTAT (0x0021)
        packet[46] = 0x00;
        packet[47] = 0x21;
        // Class: IN (0x0001)
        packet[48] = 0x00;
        packet[49] = 0x01;

        return packet;
    }

    internal static (string? Name, string? Domain, string? Mac) ParseNetBiosResponse(byte[] data)
    {
        // Minimum: 12 (header) + 2 (name ptr) + 10 (RR header) + 1 (count) = 25
        if (data.Length < 25) return (null, null, null);

        // Header: TxID(2) + Flags(2) + QDCount(2) + ANCount(2) + NSCount(2) + ARCount(2) = 12 bytes
        var qdCount = Math.Min((data[4] << 8) | data[5], 4); // cap to prevent malicious loops
        var offset = 12;

        // Skip question section (echoed query): name + QTYPE(2) + QCLASS(2)
        for (var q = 0; q < qdCount && offset < data.Length; q++)
        {
            offset = SkipNetBiosName(data, offset);
            if (offset + 4 > data.Length) return (null, null, null);
            offset += 4; // QTYPE + QCLASS
        }

        if (offset >= data.Length) return (null, null, null);

        // Answer section: name + TYPE(2) + CLASS(2) + TTL(4) + RDLENGTH(2) + RDATA
        offset = SkipNetBiosName(data, offset); // answer name (often a compression pointer)
        if (offset + 10 > data.Length) return (null, null, null);
        offset += 10; // TYPE(2) + CLASS(2) + TTL(4) + RDLENGTH(2)

        if (offset >= data.Length) return (null, null, null);

        // RDATA starts here: count(1) + name entries (18 bytes each) + MAC(6)
        var nameCount = data[offset];
        offset++;

        string? computerName = null;
        string? domain = null;

        for (var i = 0; i < nameCount && offset + 18 <= data.Length; i++)
        {
            var entryName = ReplyText.Clean(Encoding.ASCII.GetString(data, offset, 15));
            var suffix = data[offset + 15];
            var flags = (ushort)((data[offset + 16] << 8) | data[offset + 17]);
            var isGroup = (flags & 0x8000) != 0;

            if (suffix == 0x00 && !isGroup && computerName is null)
                computerName = entryName;
            else if (suffix == 0x00 && isGroup && domain is null)
                domain = entryName;

            offset += 18;
        }

        // MAC address is the last 6 bytes of the name table
        string? mac = null;
        if (offset + 6 <= data.Length)
        {
            mac = string.Join("-",
                data.Skip(offset).Take(6).Select(b => b.ToString("X2")));
            // Ignore if all zeros or all FFs (invalid)
            if (mac is "00-00-00-00-00-00" or "FF-FF-FF-FF-FF-FF")
                mac = null;
        }

        return (computerName, domain, mac);
    }

    /// <summary>
    /// Advances past a NetBIOS-encoded name (label-length or compression pointer).
    /// </summary>
    private static int SkipNetBiosName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            var len = data[offset];
            if (len == 0x00) return offset + 1;       // null terminator
            if ((len & 0xC0) == 0xC0) return offset + 2; // compression pointer (2 bytes)
            offset += len + 1;                          // label: length byte + label bytes
        }
        return offset;
    }

    // ── SNMPv2c GET ──────────────────────────────────────────────────

    /// <summary>
    /// Sends an SNMPv2c GET request for sysDescr, sysName, and sysLocation
    /// and returns the parsed values.
    /// </summary>
    public static async Task<SnmpInfo?> QuerySnmpAsync(
        string ip, int timeoutMs, CancellationToken ct)
    {
        return await QuerySnmpAsync(ip, DefaultSnmpCommunity, timeoutMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an SNMPv2c GET request with a specific community string.
    /// </summary>
    public static async Task<SnmpInfo?> QuerySnmpAsync(
        string ip, string community, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;

            var packet = BuildSnmpGetRequest(community);
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), SnmpPort);
            await udp.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return ParseSnmpResponse(result.Buffer);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            FileLogger.Log("DEBUG", $"SNMP query to {ip} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>sysServices is a seven-bit OSI layer mask (RFC 1213).</summary>
    private const int MaxSysServices = 127;

    // OIDs for system group queries
    private static readonly int[][] SnmpOids =
    [
        [1, 3, 6, 1, 2, 1, 1, 1, 0], // sysDescr (0x04 OCTET STRING)
        [1, 3, 6, 1, 2, 1, 1, 5, 0], // sysName (0x04 OCTET STRING)
        [1, 3, 6, 1, 2, 1, 1, 6, 0], // sysLocation (0x04 OCTET STRING)
        [1, 3, 6, 1, 2, 1, 1, 2, 0], // sysObjectID (0x06 OID)
        [1, 3, 6, 1, 2, 1, 1, 3, 0], // sysUpTime (0x43 TimeTicks, centiseconds)
        [1, 3, 6, 1, 2, 1, 1, 7, 0], // sysServices (0x02 INTEGER, OSI layer bitmask)
    ];

    internal static byte[] BuildSnmpGetRequest(string community)
    {
        var requestId = Random.Shared.Next(1, 0x7FFFFFFF);

        // Build varbind list: 3 OIDs with NULL values
        var varbinds = new List<byte>();
        foreach (var oid in SnmpOids)
        {
            var oidBytes = Asn1EncodeOid(oid);
            var nullVal = new byte[] { 0x05, 0x00 }; // ASN.1 NULL
            var varbind = Asn1Wrap(0x30, [.. oidBytes, .. nullVal]); // SEQUENCE
            varbinds.AddRange(varbind);
        }
        var varbindList = Asn1Wrap(0x30, [.. varbinds]); // SEQUENCE of varbinds

        // Build PDU: GET-REQUEST (0xA0)
        var reqIdBytes = Asn1EncodeInteger(requestId);
        var errorStatus = Asn1EncodeInteger(0);
        var errorIndex = Asn1EncodeInteger(0);
        var pdu = Asn1Wrap(0xA0, [.. reqIdBytes, .. errorStatus, .. errorIndex, .. varbindList]);

        // Build message: version(1=SNMPv2c) + community + PDU
        var version = Asn1EncodeInteger(1);
        var communityBytes = Asn1EncodeOctetString(community);
        var message = Asn1Wrap(0x30, [.. version, .. communityBytes, .. pdu]);

        return message;
    }

    internal static SnmpInfo? ParseSnmpResponse(byte[] data)
    {
        try
        {
            var (_, messageContent) = Asn1ReadTlv(data, 0);
            if (messageContent is null) return null;

            // Parse: version, community, then PDU
            var offset = 0;

            // Skip version
            var (vLen, _) = Asn1ReadTlv(messageContent, offset);
            offset += vLen;

            // Skip community
            var (cLen, _2) = Asn1ReadTlv(messageContent, offset);
            offset += cLen;

            // PDU (GetResponse = 0xA2)
            var (_, pduContent) = Asn1ReadTlv(messageContent, offset);
            if (pduContent is null) return null;

            // Skip requestId, errorStatus, errorIndex
            var pduOffset = 0;
            for (var i = 0; i < 3; i++)
            {
                var (skip, _3) = Asn1ReadTlv(pduContent, pduOffset);
                pduOffset += skip;
            }

            // Varbind list (SEQUENCE of SEQUENCE)
            var (_, varbindListContent) = Asn1ReadTlv(pduContent, pduOffset);
            if (varbindListContent is null) return null;

            // Extract values from varbinds (6 OIDs)
            var stringValues = new string?[3]; // sysDescr, sysName, sysLocation
            string? sysObjectId = null;
            long? sysUpTime = null;
            int? sysServices = null;

            var vbOffset = 0;
            var idx = 0;
            while (vbOffset < varbindListContent.Length && idx < 6)
            {
                var (vbLen, vbContent) = Asn1ReadTlv(varbindListContent, vbOffset);
                if (vbContent is not null)
                {
                    // Skip OID, read value
                    var (oidLen, _4) = Asn1ReadTlv(vbContent, 0);
                    if (oidLen < vbContent.Length)
                    {
                        var valueTag = vbContent[oidLen];
                        var (_, valContent) = Asn1ReadTlv(vbContent, oidLen);
                        if (valContent is not null)
                        {
                            switch (idx)
                            {
                                case < 3 when valueTag == 0x04: // OCTET STRING
                                    stringValues[idx] = Encoding.UTF8.GetString(valContent).Trim();
                                    break;
                                case 3 when valueTag == 0x06: // OID (sysObjectID)
                                    sysObjectId = Asn1DecodeOid(valContent);
                                    break;
                                case 4 when valueTag == 0x43: // TimeTicks (sysUpTime)
                                    sysUpTime = TryDecodeUnsignedInt(valContent, uint.MaxValue) / 100; // centiseconds → seconds
                                    break;
                                case 5 when valueTag == 0x02: // INTEGER (sysServices)
                                    sysServices = (int?)TryDecodeUnsignedInt(valContent, MaxSysServices);
                                    break;
                            }
                        }
                    }
                }
                vbOffset += vbLen;
                idx++;
            }

            if (stringValues[0] is null && stringValues[1] is null && stringValues[2] is null &&
                sysObjectId is null)
                return null;

            return new SnmpInfo(stringValues[0], stringValues[1], stringValues[2],
                sysObjectId, sysUpTime, sysServices);
        }
        catch
        {
            return null;
        }
    }

    // ── mDNS Service Discovery ───────────────────────────────────────

    /// <summary>
    /// Sends an mDNS query for service discovery and collects responses,
    /// returning discovered services keyed by responding IP address.
    /// </summary>
    public static async Task<Dictionary<string, List<string>>> QueryMdnsServicesAsync(
        IReadOnlyList<string> targetIps, int timeoutMs, CancellationToken ct)
    {
        var results = new Dictionary<string, List<string>>();
        var targetSet = new HashSet<string>(targetIps);

        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.JoinMulticastGroup(MdnsMulticast);

            var query = BuildMdnsServiceQuery();
            var mdnsEndpoint = new IPEndPoint(MdnsMulticast, MdnsPort);
            await udp.SendAsync(query, query.Length, mdnsEndpoint).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var senderIp = result.RemoteEndPoint.Address.ToString();

                    if (!targetSet.Contains(senderIp)) continue;

                    var services = ParseMdnsResponse(result.Buffer);
                    if (services.Count > 0)
                    {
                        if (!results.ContainsKey(senderIp))
                            results[senderIp] = [];
                        foreach (var svc in services)
                        {
                            if (!results[senderIp].Contains(svc))
                                results[senderIp].Add(svc);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore malformed responses */ }
            }

            udp.DropMulticastGroup(MdnsMulticast);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            FileLogger.Log("DEBUG", $"mDNS discovery failed: {ex.Message}");
        }

        return results;
    }

    internal static byte[] BuildMdnsServiceQuery()
    {
        var packet = new List<byte>();

        // DNS header
        packet.AddRange(new byte[] { 0x00, 0x00 }); // Transaction ID
        packet.AddRange(new byte[] { 0x00, 0x00 }); // Flags (standard query)
        packet.AddRange(new byte[] { 0x00, 0x01 }); // Questions: 1
        packet.AddRange(new byte[] { 0x00, 0x00 }); // Answers: 0
        packet.AddRange(new byte[] { 0x00, 0x00 }); // Authority: 0
        packet.AddRange(new byte[] { 0x00, 0x00 }); // Additional: 0

        // Question: _services._dns-sd._udp.local
        var labels = new[] { "_services", "_dns-sd", "_udp", "local" };
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.Add(0x00); // Root label

        packet.AddRange(new byte[] { 0x00, 0x0C }); // Type: PTR (12)
        packet.AddRange(new byte[] { 0x00, 0x01 }); // Class: IN (1)

        return [.. packet];
    }

    internal static List<string> ParseMdnsResponse(byte[] data)
    {
        var services = new List<string>();
        if (data.Length < 12) return services;

        var answerCount = (data[6] << 8) | data[7];
        var additionalCount = (data[10] << 8) | data[11];
        var totalRecords = answerCount + ((data[8] << 8) | data[9]) + additionalCount;

        // Skip header (12 bytes) and questions
        var questionCount = (data[4] << 8) | data[5];
        var offset = 12;
        for (var i = 0; i < questionCount && offset < data.Length; i++)
        {
            offset = SkipDnsName(data, offset);
            offset += 4; // Type + Class
        }

        // Parse answer/authority/additional records
        for (var i = 0; i < totalRecords && offset < data.Length; i++)
        {
            var nameStart = offset;
            offset = SkipDnsName(data, offset);
            if (offset + 10 > data.Length) break;

            var rrType = (data[offset] << 8) | data[offset + 1];
            offset += 8; // Type(2) + Class(2) + TTL(4)
            var rdLength = (data[offset] << 8) | data[offset + 1];
            offset += 2;

            if (rrType == 12 && rdLength > 0 && offset + rdLength <= data.Length) // PTR
            {
                var name = ReadDnsName(data, offset);
                if (!string.IsNullOrEmpty(name))
                {
                    // Remove .local suffix and convert to human-readable
                    var svcType = name.Replace(".local", "").TrimEnd('.');
                    var display = MdnsServiceNames.TryGetValue(svcType, out var friendly)
                        ? friendly
                        : svcType;
                    if (!services.Contains(display))
                        services.Add(display);
                }
            }

            offset += rdLength;
        }

        return services;
    }

    private static string ReadDnsName(byte[] data, int offset)
    {
        var parts = new List<string>();
        var maxJumps = 10;

        while (offset < data.Length && maxJumps-- > 0)
        {
            var len = data[offset];
            if (len == 0) break;

            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer
                if (offset + 1 >= data.Length) break;
                offset = ((len & 0x3F) << 8) | data[offset + 1];
                continue;
            }

            offset++;
            if (offset + len > data.Length) break;
            parts.Add(Encoding.ASCII.GetString(data, offset, len));
            offset += len;
        }

        return string.Join(".", parts);
    }

    private static int SkipDnsName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            var len = data[offset];
            if (len == 0) return offset + 1;
            if ((len & 0xC0) == 0xC0) return offset + 2; // Pointer
            offset += len + 1;
        }
        return offset;
    }

    // ── SSDP/UPnP Discovery ────────────────────────────────────────

    private static readonly IPAddress SsdpMulticast = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;

    /// <summary>
    /// Sends an SSDP M-SEARCH multicast query and collects UPnP device responses,
    /// returning discovered device info keyed by responding IP address.
    /// </summary>
    public static async Task<Dictionary<string, SsdpInfo>> QuerySsdpAsync(
        IReadOnlyList<string> targetIps, int timeoutMs, CancellationToken ct)
    {
        var results = new Dictionary<string, SsdpInfo>();
        var targetSet = new HashSet<string>(targetIps);

        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = BuildSsdpMSearchQuery();
            var ssdpEndpoint = new IPEndPoint(SsdpMulticast, SsdpPort);
            await udp.SendAsync(query, query.Length, ssdpEndpoint).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var senderIp = result.RemoteEndPoint.Address.ToString();

                    if (!targetSet.Contains(senderIp)) continue;

                    var responseText = Encoding.ASCII.GetString(result.Buffer);
                    var info = ParseSsdpResponse(responseText);
                    if (info is not null && !results.ContainsKey(senderIp))
                    {
                        results[senderIp] = info;
                        // Attempt rootDesc.xml fetch if LOCATION present
                        var locMatch = System.Text.RegularExpressions.Regex.Match(
                            responseText, @"LOCATION:\s*(\S+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (locMatch.Success)
                        {
                            try
                            {
                                var enriched = await FetchRootDescAsync(
                                    locMatch.Groups[1].Value, info, 3000, cts.Token)
                                    .ConfigureAwait(false);
                                if (enriched is not null)
                                    results[senderIp] = enriched;
                            }
                            catch { /* rootDesc fetch failed */ }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore malformed responses */ }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            FileLogger.Log("DEBUG", $"SSDP discovery failed: {ex.Message}");
        }

        return results;
    }

    internal static byte[] BuildSsdpMSearchQuery()
    {
        var query = "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    "ST: ssdp:all\r\n" +
                    "\r\n";
        return Encoding.ASCII.GetBytes(query);
    }

    internal static SsdpInfo? ParseSsdpResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        string? deviceType = null;
        string? server = null;
        string? usn = null;
        string? location = null;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
            {
                location = trimmed[9..].Trim();
            }
            else if (trimmed.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))
            {
                var st = trimmed[3..].Trim();
                // Extract device type from URN (e.g., urn:schemas-upnp-org:device:InternetGatewayDevice:1)
                var urnParts = st.Split(':');
                if (urnParts.Length >= 4 && st.Contains("device", StringComparison.OrdinalIgnoreCase))
                    deviceType = urnParts[^2]; // device type is second-to-last
                else
                    deviceType ??= st;
            }
            else if (trimmed.StartsWith("SERVER:", StringComparison.OrdinalIgnoreCase))
            {
                server = trimmed[7..].Trim();
            }
            else if (trimmed.StartsWith("USN:", StringComparison.OrdinalIgnoreCase))
            {
                usn = trimmed[4..].Trim();
            }
        }

        if (deviceType is null && server is null) return null;

        // Try to extract friendly name from USN
        string? friendlyName = null;
        string? manufacturer = null;
        string? modelName = null;

        // SERVER header often contains OS and device info (e.g., "Linux/3.4, UPnP/1.0, Portable SDK/1.8.6")
        if (server is not null)
        {
            var serverParts = server.Split(',');
            if (serverParts.Length > 0)
                manufacturer = serverParts[0].Trim();
        }

        return new SsdpInfo(deviceType, friendlyName, manufacturer, modelName, server);
    }

    /// <summary>
    /// Fetches a UPnP rootDesc.xml from the SSDP LOCATION URL and enriches
    /// the SsdpInfo with friendlyName, manufacturer, modelName, modelNumber,
    /// serialNumber, and presentationURL.
    /// </summary>
    public static async Task<SsdpInfo?> FetchRootDescAsync(
        string locationUrl, SsdpInfo? existing, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var uri = new Uri(locationUrl);
            using var cts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            await client.ConnectAsync(uri.Host, uri.Port, linked.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            var request = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Host}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), linked.Token)
                .ConfigureAwait(false);

            using var ms = new MemoryStream();
            var buf = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(buf, linked.Token).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > 65536) break;
            }

            var response = Encoding.UTF8.GetString(ms.ToArray());
            // Find XML body after headers
            var bodyStart = response.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
            if (bodyStart < 0) bodyStart = response.IndexOf("<root", StringComparison.OrdinalIgnoreCase);
            if (bodyStart < 0) return existing;

            var xml = response[bodyStart..];
            return ParseRootDesc(xml, existing);
        }
        catch
        {
            return existing;
        }
    }

    private static SsdpInfo? ParseRootDesc(string xml, SsdpInfo? existing)
    {
        // Simple regex-based XML parsing (avoids System.Xml dependency overhead)
        var friendlyName = ExtractXmlTag(xml, "friendlyName");
        var manufacturer = ExtractXmlTag(xml, "manufacturer");
        var modelName = ExtractXmlTag(xml, "modelName");
        var modelNumber = ExtractXmlTag(xml, "modelNumber");
        var serialNumber = ExtractXmlTag(xml, "serialNumber");
        var presentationUrl = ExtractXmlTag(xml, "presentationURL");
        var deviceType = ExtractXmlTag(xml, "deviceType");

        return new SsdpInfo(
            existing?.DeviceType ?? deviceType,
            friendlyName ?? existing?.FriendlyName,
            manufacturer ?? existing?.Manufacturer,
            modelName ?? existing?.ModelName,
            existing?.Server,
            modelNumber,
            serialNumber,
            presentationUrl);
    }

    private static string? ExtractXmlTag(string xml, string tagName)
    {
        var pattern = $"<{tagName}>(.*?)</{tagName}>";
        var match = System.Text.RegularExpressions.Regex.Match(xml, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ── ASN.1/BER helpers (for SNMP) ─────────────────────────────────

    private static byte[] Asn1Wrap(byte tag, byte[] content)
    {
        var length = Asn1EncodeLength(content.Length);
        var result = new byte[1 + length.Length + content.Length];
        result[0] = tag;
        Array.Copy(length, 0, result, 1, length.Length);
        Array.Copy(content, 0, result, 1 + length.Length, content.Length);
        return result;
    }

    private static byte[] Asn1EncodeLength(int length)
    {
        if (length < 0x80)
            return [(byte)length];
        if (length <= 0xFF)
            return [0x81, (byte)length];
        return [0x82, (byte)(length >> 8), (byte)(length & 0xFF)];
    }

    internal static byte[] Asn1EncodeOid(int[] oid)
    {
        var bytes = new List<byte>();
        // First two components encoded as 40*first + second
        if (oid.Length >= 2)
            bytes.Add((byte)(40 * oid[0] + oid[1]));

        for (var i = 2; i < oid.Length; i++)
        {
            var val = oid[i];
            if (val < 0x80)
            {
                bytes.Add((byte)val);
            }
            else
            {
                // Multi-byte encoding
                var encoded = new Stack<byte>();
                encoded.Push((byte)(val & 0x7F));
                val >>= 7;
                while (val > 0)
                {
                    encoded.Push((byte)(0x80 | (val & 0x7F)));
                    val >>= 7;
                }
                while (encoded.Count > 0) bytes.Add(encoded.Pop());
            }
        }

        return Asn1Wrap(0x06, [.. bytes]);
    }

    private static byte[] Asn1EncodeInteger(int value)
    {
        var bytes = new List<byte>();
        if (value == 0)
        {
            bytes.Add(0);
        }
        else
        {
            var temp = value;
            var parts = new Stack<byte>();
            while (temp > 0)
            {
                parts.Push((byte)(temp & 0xFF));
                temp >>= 8;
            }
            // Add leading zero if high bit set (to keep positive)
            if ((parts.Peek() & 0x80) != 0)
                parts.Push(0);
            while (parts.Count > 0) bytes.Add(parts.Pop());
        }

        return Asn1Wrap(0x02, [.. bytes]);
    }

    private static byte[] Asn1EncodeOctetString(string value)
    {
        return Asn1Wrap(0x04, Encoding.ASCII.GetBytes(value));
    }

    /// <summary>
    /// Reads a TLV (Tag-Length-Value) structure from an ASN.1 buffer.
    /// Returns the total consumed length and the value bytes.
    /// </summary>
    private static (int TotalLength, byte[]? Value) Asn1ReadTlv(byte[] data, int offset) =>
        SnmpCodec.Asn1ReadTlv(data, offset);

    /// <summary>
    /// Decodes a BER-encoded OID value (content bytes only, without tag/length).
    /// Returns dotted-decimal string like "1.3.6.1.4.1.9.1.1745".
    /// </summary>
    private static string? Asn1DecodeOid(byte[] content)
    {
        if (content.Length < 1) return null;
        var components = new List<int>
        {
            content[0] / 40,
            content[0] % 40
        };

        var val = 0;
        for (var i = 1; i < content.Length; i++)
        {
            val = (val << 7) | (content[i] & 0x7F);
            if ((content[i] & 0x80) == 0)
            {
                components.Add(val);
                val = 0;
            }
        }
        return string.Join('.', components);
    }

    /// <summary>
    /// Decodes a BER-encoded unsigned integer (TimeTicks, Counter, Gauge, INTEGER), or null when
    /// it would wrap around or is larger than <paramref name="max"/>.
    /// </summary>
    private static long? TryDecodeUnsignedInt(byte[] content, long max)
    {
        long result = 0;
        foreach (var b in content)
        {
            if (result > (long.MaxValue >> 8))
            {
                return null;
            }

            result = (result << 8) | b;
        }

        return result <= max ? result : null;
    }
}
