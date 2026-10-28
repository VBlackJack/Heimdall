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
using Heimdall.Core.Security;

namespace Heimdall.Core.Network;

/// <summary>
/// Pure SNMPv2c codec: ASN.1/BER encoding/decoding, SNMP packet construction,
/// OID resolution, and snmpwalk output parsing.
/// </summary>
public static class SnmpCodec
{
    public const int DefaultPort = 161;
    public const int DefaultTimeoutMs = 3000;
    public const int MaxWalkResults = 10000;

    /// <summary>
    /// Well-known OID-to-name mappings for common MIB-2 objects.
    /// </summary>
    public static readonly Dictionary<string, string> WellKnownOids = new(StringComparer.Ordinal)
    {
        ["1.3.6.1.2.1.1.1"] = "sysDescr",
        ["1.3.6.1.2.1.1.1.0"] = "sysDescr.0",
        ["1.3.6.1.2.1.1.2"] = "sysObjectID",
        ["1.3.6.1.2.1.1.2.0"] = "sysObjectID.0",
        ["1.3.6.1.2.1.1.3"] = "sysUpTime",
        ["1.3.6.1.2.1.1.3.0"] = "sysUpTime.0",
        ["1.3.6.1.2.1.1.4"] = "sysContact",
        ["1.3.6.1.2.1.1.4.0"] = "sysContact.0",
        ["1.3.6.1.2.1.1.5"] = "sysName",
        ["1.3.6.1.2.1.1.5.0"] = "sysName.0",
        ["1.3.6.1.2.1.1.6"] = "sysLocation",
        ["1.3.6.1.2.1.1.6.0"] = "sysLocation.0",
        ["1.3.6.1.2.1.1.7"] = "sysServices",
        ["1.3.6.1.2.1.1.7.0"] = "sysServices.0",
        ["1.3.6.1.2.1.2.1"] = "ifNumber",
        ["1.3.6.1.2.1.2.1.0"] = "ifNumber.0",
        ["1.3.6.1.2.1.2.2"] = "ifTable",
        ["1.3.6.1.2.1.2.2.1.1"] = "ifIndex",
        ["1.3.6.1.2.1.2.2.1.2"] = "ifDescr",
        ["1.3.6.1.2.1.2.2.1.3"] = "ifType",
        ["1.3.6.1.2.1.2.2.1.4"] = "ifMtu",
        ["1.3.6.1.2.1.2.2.1.5"] = "ifSpeed",
        ["1.3.6.1.2.1.2.2.1.6"] = "ifPhysAddress",
        ["1.3.6.1.2.1.2.2.1.7"] = "ifAdminStatus",
        ["1.3.6.1.2.1.2.2.1.8"] = "ifOperStatus",
        ["1.3.6.1.2.1.2.2.1.10"] = "ifInOctets",
        ["1.3.6.1.2.1.2.2.1.16"] = "ifOutOctets",
        ["1.3.6.1.2.1.4.1"] = "ipForwarding",
        ["1.3.6.1.2.1.4.2"] = "ipDefaultTTL",
        ["1.3.6.1.2.1.4.20"] = "ipAddrTable",
        ["1.3.6.1.2.1.4.20.1.1"] = "ipAdEntAddr",
        ["1.3.6.1.2.1.4.20.1.2"] = "ipAdEntIfIndex",
        ["1.3.6.1.2.1.4.20.1.3"] = "ipAdEntNetMask",
        ["1.3.6.1.2.1.4.21"] = "ipRouteTable",
        ["1.3.6.1.2.1.6.1"] = "tcpRtoAlgorithm",
        ["1.3.6.1.2.1.6.5"] = "tcpActiveOpens",
        ["1.3.6.1.2.1.6.6"] = "tcpPassiveOpens",
        ["1.3.6.1.2.1.6.9"] = "tcpCurrEstab",
        ["1.3.6.1.2.1.6.10"] = "tcpInSegs",
        ["1.3.6.1.2.1.6.11"] = "tcpOutSegs",
        ["1.3.6.1.2.1.6.13"] = "tcpConnTable",
        ["1.3.6.1.2.1.7.1"] = "udpInDatagrams",
        ["1.3.6.1.2.1.7.2"] = "udpNoPorts",
        ["1.3.6.1.2.1.7.3"] = "udpInErrors",
        ["1.3.6.1.2.1.7.4"] = "udpOutDatagrams",
    };

    public static string ResolveOidName(string oid)
    {
        if (WellKnownOids.TryGetValue(oid, out var name))
        {
            return name;
        }

        var dotIndex = oid.LastIndexOf('.');
        while (dotIndex > 0)
        {
            var prefix = oid[..dotIndex];
            if (WellKnownOids.TryGetValue(prefix, out var baseName))
            {
                return $"{baseName}.{oid[(dotIndex + 1)..]}";
            }

            dotIndex = prefix.LastIndexOf('.');
        }

        return string.Empty;
    }

    public static SnmpEntry? ParseSnmpWalkLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separatorIndex = line.IndexOf(" = ", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return null;
        }

        var oidPart = line[..separatorIndex].Trim();
        var valuePart = line[(separatorIndex + 3)..].Trim();

        oidPart = oidPart.Replace("iso.", "1.", StringComparison.OrdinalIgnoreCase);
        if (oidPart.StartsWith("iso", StringComparison.OrdinalIgnoreCase))
        {
            oidPart = "1" + oidPart[3..];
        }

        var mibSeparatorIndex = oidPart.IndexOf("::", StringComparison.Ordinal);
        var displayName = mibSeparatorIndex >= 0
            ? oidPart[(mibSeparatorIndex + 2)..]
            : ResolveOidName(oidPart);

        var valueSeparatorIndex = valuePart.IndexOf(": ", StringComparison.Ordinal);
        var type = valueSeparatorIndex >= 0 ? valuePart[..valueSeparatorIndex].Trim() : valuePart;
        var value = valueSeparatorIndex >= 0 ? valuePart[(valueSeparatorIndex + 2)..].Trim() : string.Empty;

        return new SnmpEntry
        {
            Oid = oidPart,
            Name = displayName,
            Type = type,
            Value = value,
        };
    }

    public static int[]? ParseOidString(string oid)
    {
        var parts = oid.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var components = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out components[i]))
            {
                return null;
            }
        }

        return components;
    }

    public static byte[] BuildSnmpGetNextRequest(string community, int[] oid)
    {
        var requestId = Random.Shared.Next(1, 0x7FFFFFFF);
        var oidBytes = Asn1EncodeOid(oid);
        var nullValue = new byte[] { 0x05, 0x00 };
        var varBind = Asn1Wrap(0x30, [.. oidBytes, .. nullValue]);
        var varBindList = Asn1Wrap(0x30, varBind);

        var requestIdBytes = Asn1EncodeInteger(requestId);
        var errorStatus = Asn1EncodeInteger(0);
        var errorIndex = Asn1EncodeInteger(0);
        var pdu = Asn1Wrap(0xA1, [.. requestIdBytes, .. errorStatus, .. errorIndex, .. varBindList]);

        var version = Asn1EncodeInteger(1);
        var communityBytes = Asn1EncodeOctetString(community);
        return Asn1Wrap(0x30, [.. version, .. communityBytes, .. pdu]);
    }

    public static (string? Oid, string Type, string Value) ParseSnmpGetNextResponse(byte[] data)
    {
        try
        {
            var (_, messageContent) = Asn1ReadTlv(data, 0);
            if (messageContent is null)
            {
                return (null, string.Empty, string.Empty);
            }

            var offset = 0;
            var (versionLength, _) = Asn1ReadTlv(messageContent, offset);
            offset += versionLength;

            var (communityLength, _) = Asn1ReadTlv(messageContent, offset);
            offset += communityLength;

            var (_, pduContent) = Asn1ReadTlv(messageContent, offset);
            if (pduContent is null)
            {
                return (null, string.Empty, string.Empty);
            }

            var pduOffset = 0;
            for (var i = 0; i < 3; i++)
            {
                var (skipLength, _) = Asn1ReadTlv(pduContent, pduOffset);
                pduOffset += skipLength;
            }

            var (_, varBindListContent) = Asn1ReadTlv(pduContent, pduOffset);
            if (varBindListContent is null)
            {
                return (null, string.Empty, string.Empty);
            }

            var (_, varBindContent) = Asn1ReadTlv(varBindListContent, 0);
            if (varBindContent is null)
            {
                return (null, string.Empty, string.Empty);
            }

            var (oidLength, oidContent) = Asn1ReadTlv(varBindContent, 0);
            if (oidContent is null || varBindContent[0] != 0x06)
            {
                return (null, string.Empty, string.Empty);
            }

            var oid = Asn1DecodeOid(oidContent);
            if (oidLength >= varBindContent.Length)
            {
                return (oid, string.Empty, string.Empty);
            }

            var valueTag = varBindContent[oidLength];
            var (_, valueContent) = Asn1ReadTlv(varBindContent, oidLength);
            var (type, value) = DecodeSnmpValue(valueTag, valueContent);
            return (oid, type, value);
        }
        catch
        {
            return (null, string.Empty, string.Empty);
        }
    }

    public static (string Type, string Value) DecodeSnmpValue(byte tag, byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            return (GetTagName(tag), string.Empty);
        }

        return tag switch
        {
            0x02 => ("INTEGER", Asn1DecodeSignedInt(content).ToString()),
            0x04 => ("STRING", DecodeOctetString(content)),
            0x05 => ("NULL", string.Empty),
            0x06 => ("OID", Asn1DecodeOid(content) ?? string.Empty),
            0x40 => ("IpAddress", DecodeIpAddress(content)),
            0x41 => ("Counter32", Asn1DecodeUnsignedInt(content).ToString()),
            0x42 => ("Gauge32", Asn1DecodeUnsignedInt(content).ToString()),
            0x43 => ("TimeTicks", FormatTimeTicks(Asn1DecodeUnsignedInt(content))),
            0x44 => ("Opaque", Convert.ToHexString(content)),
            0x46 => ("Counter64", Asn1DecodeUnsignedInt(content).ToString()),
            0x80 => ("noSuchObject", string.Empty),
            0x81 => ("noSuchInstance", string.Empty),
            0x82 => ("endOfMibView", string.Empty),
            _ => (GetTagName(tag), Convert.ToHexString(content)),
        };
    }

    public static string GetTagName(byte tag)
    {
        return tag switch
        {
            0x02 => "INTEGER",
            0x04 => "STRING",
            0x05 => "NULL",
            0x06 => "OID",
            0x40 => "IpAddress",
            0x41 => "Counter32",
            0x42 => "Gauge32",
            0x43 => "TimeTicks",
            0x44 => "Opaque",
            0x46 => "Counter64",
            0x80 => "noSuchObject",
            0x81 => "noSuchInstance",
            0x82 => "endOfMibView",
            _ => $"0x{tag:X2}",
        };
    }

    public static string DecodeOctetString(byte[] content)
    {
        var printable = true;
        foreach (var value in content)
        {
            if (value < 0x20 && value is not (0x09 or 0x0A or 0x0D))
            {
                printable = false;
                break;
            }
        }

        return printable
            ? Encoding.UTF8.GetString(content).Trim()
            : string.Join("-", content.Select(b => b.ToString("X2")));
    }

    public static string DecodeIpAddress(byte[] content)
    {
        return content.Length == 4
            ? $"{content[0]}.{content[1]}.{content[2]}.{content[3]}"
            : Convert.ToHexString(content);
    }

    public static string FormatTimeTicks(long centiseconds)
    {
        var span = TimeSpan.FromMilliseconds(centiseconds * 10);
        return $"{(int)span.TotalDays}d {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2} ({centiseconds / 100}s)";
    }

    public static byte[] Asn1Wrap(byte tag, byte[] content)
    {
        var length = Asn1EncodeLength(content.Length);
        var result = new byte[1 + length.Length + content.Length];
        result[0] = tag;
        Array.Copy(length, 0, result, 1, length.Length);
        Array.Copy(content, 0, result, 1 + length.Length, content.Length);
        return result;
    }

    public static byte[] Asn1EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return [(byte)length];
        }

        if (length <= 0xFF)
        {
            return [0x81, (byte)length];
        }

        return [0x82, (byte)(length >> 8), (byte)(length & 0xFF)];
    }

    public static byte[] Asn1EncodeOid(int[] oid)
    {
        var bytes = new List<byte>();
        if (oid.Length >= 2)
        {
            bytes.Add((byte)(40 * oid[0] + oid[1]));
        }

        for (var i = 2; i < oid.Length; i++)
        {
            var value = oid[i];
            if (value < 0x80)
            {
                bytes.Add((byte)value);
                continue;
            }

            var encoded = new Stack<byte>();
            encoded.Push((byte)(value & 0x7F));
            value >>= 7;
            while (value > 0)
            {
                encoded.Push((byte)(0x80 | (value & 0x7F)));
                value >>= 7;
            }

            while (encoded.Count > 0)
            {
                bytes.Add(encoded.Pop());
            }
        }

        return Asn1Wrap(0x06, [.. bytes]);
    }

    public static byte[] Asn1EncodeInteger(int value)
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

            if ((parts.Peek() & 0x80) != 0)
            {
                parts.Push(0);
            }

            while (parts.Count > 0)
            {
                bytes.Add(parts.Pop());
            }
        }

        return Asn1Wrap(0x02, [.. bytes]);
    }

    public static byte[] Asn1EncodeOctetString(string value)
    {
        return Asn1Wrap(0x04, Encoding.ASCII.GetBytes(value));
    }

    public static (int TotalLength, byte[]? Value) Asn1ReadTlv(byte[] data, int offset)
    {
        if (offset >= data.Length)
        {
            return (0, null);
        }

        var start = offset;
        offset++;

        if (offset >= data.Length)
        {
            return (offset - start, null);
        }

        // The declared length is the sender's. It is accumulated in 64 bits and bounded by what is
        // left in the buffer: as an int it wrapped negative on a four-byte length, and
        // offset + length wrapped negative with it, so the clamp below never ran and a 64 KB datagram
        // could ask for a two-gigabyte array.
        long declared;
        if ((data[offset] & 0x80) == 0)
        {
            declared = data[offset];
            offset++;
        }
        else
        {
            var lengthBytes = data[offset] & 0x7F;
            offset++;
            declared = 0;
            for (var i = 0; i < lengthBytes && offset < data.Length; i++)
            {
                declared = Math.Min((declared << 8) | data[offset], int.MaxValue);
                offset++;
            }
        }

        var length = (int)Math.Min(declared, data.Length - offset);

        var value = new byte[length];
        Array.Copy(data, offset, value, 0, length);
        return (offset - start + length, value);
    }

    public static string? Asn1DecodeOid(byte[] content)
    {
        if (content.Length < 1)
        {
            return null;
        }

        var components = new List<int>
        {
            content[0] / 40,
            content[0] % 40,
        };

        var value = 0;
        for (var i = 1; i < content.Length; i++)
        {
            value = (value << 7) | (content[i] & 0x7F);
            if ((content[i] & 0x80) == 0)
            {
                components.Add(value);
                value = 0;
            }
        }

        return string.Join('.', components);
    }

    public static long Asn1DecodeUnsignedInt(byte[] content)
    {
        long result = 0;
        foreach (var value in content)
        {
            result = (result << 8) | value;
        }

        return result;
    }

    public static long Asn1DecodeSignedInt(byte[] content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        long result = (content[0] & 0x80) != 0 ? -1L : 0L;
        foreach (var value in content)
        {
            result = (result << 8) | value;
        }

        return result;
    }

    public static string BuildCsvExport(IReadOnlyList<SnmpEntry> entries, Func<string, string>? localize = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{L(localize, "ToolSnmpColOid")},{L(localize, "ToolSnmpColName")},{L(localize, "ToolSnmpColType")},{L(localize, "ToolSnmpColValue")}");

        foreach (var entry in entries)
        {
            var oid = InputValidator.SanitizeCsvCell(entry.Oid);
            var name = InputValidator.SanitizeCsvCell(entry.Name);
            var type = InputValidator.SanitizeCsvCell(entry.Type);
            var value = InputValidator.SanitizeCsvCell(entry.Value).Replace("\"", "\"\"", StringComparison.Ordinal);
            builder.AppendLine($"{oid},{name},{type},\"{value}\"");
        }

        return builder.ToString();
    }

    private static string L(Func<string, string>? localize, string key) => localize?.Invoke(key) ?? key;
}

/// <summary>
/// Represents a single OID entry returned by an SNMP walk.
/// </summary>
public sealed record SnmpEntry
{
    public string Oid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Result of testing a community string against an SNMP agent.
/// </summary>
public sealed record CommunityResult
{
    public string Community { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string SysName { get; init; } = string.Empty;
}
