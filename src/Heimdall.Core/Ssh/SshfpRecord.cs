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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Heimdall.Core.Ssh;

/// <summary>Parsed DNS SSHFP resource record (RFC 4255 / 6594 / 8709).</summary>
public sealed record SshfpRecord(byte Algorithm, byte FingerprintType, string FingerprintHex);

/// <summary>Result status of verifying an SSH host key against DNS SSHFP records.</summary>
public enum SshfpVerificationStatus
{
    /// <summary>Lookup skipped (IP literal, single-label host, localhost, or routed through SSH gateway).</summary>
    Skipped,

    /// <summary>No SSHFP records published for the host.</summary>
    NotFound,

    /// <summary>At least one published SSHFP record matches the presented host key.</summary>
    Matched,

    /// <summary>Published SSHFP records exist for this host/algorithm but none match the presented host key.</summary>
    Mismatch,

    /// <summary>DNS query timed out or failed.</summary>
    Error
}

/// <summary>Outcome of verifying an SSH host key against DNS SSHFP records.</summary>
public sealed record SshfpVerificationResult(
    SshfpVerificationStatus Status,
    bool AuthenticatedData = false,
    bool UsedLoopbackResolver = false,
    int RecordCount = 0,
    string? Detail = null);

/// <summary>
/// Pure RFC 4255 / 6594 / 8709 matcher and RFC 1035 / 6891 DNS wire codec for SSHFP (TYPE 44) queries.
/// </summary>
public static class SshfpMatcher
{
    public const ushort DnsTypeSshfp = 44;
    public const byte AlgRsa = 1;
    public const byte AlgDsa = 2;
    public const byte AlgEcdsa = 3;
    public const byte AlgEd25519 = 4;
    public const byte FpTypeSha1 = 1;
    public const byte FpTypeSha256 = 2;

    private const int MaxDnsCompressionHops = 16;

    /// <summary>Maps an OpenSSH key/certificate algorithm name to its RFC 4255 SSHFP algorithm number.</summary>
    public static byte? MapSshAlgorithm(string? sshAlgorithm)
    {
        if (string.IsNullOrWhiteSpace(sshAlgorithm))
        {
            return null;
        }

        var alg = sshAlgorithm.Trim().ToLowerInvariant();
        if (alg.StartsWith("ssh-rsa", StringComparison.Ordinal)
            || alg.StartsWith("rsa-sha2-", StringComparison.Ordinal))
        {
            return AlgRsa;
        }

        if (alg.StartsWith("ssh-dss", StringComparison.Ordinal))
        {
            return AlgDsa;
        }

        if (alg.StartsWith("ecdsa-sha2-", StringComparison.Ordinal)
            || alg.StartsWith("sk-ecdsa-sha2-", StringComparison.Ordinal))
        {
            return AlgEcdsa;
        }

        if (alg.StartsWith("ssh-ed25519", StringComparison.Ordinal)
            || alg.StartsWith("sk-ssh-ed25519", StringComparison.Ordinal))
        {
            return AlgEd25519;
        }

        return null;
    }

    /// <summary>
    /// Verifies a presented host key against DNS SSHFP records.
    /// Supports SHA-256 (type 2) from either <paramref name="rawHostKeyBlob"/> or <paramref name="presentedSha256Fingerprint"/>,
    /// and SHA-1 (type 1) when <paramref name="rawHostKeyBlob"/> is provided.
    /// </summary>
    public static SshfpVerificationResult Verify(
        string sshAlgorithm,
        string presentedSha256Fingerprint,
        ReadOnlySpan<byte> rawHostKeyBlob,
        IReadOnlyList<SshfpRecord> records,
        bool authenticatedData,
        bool usedLoopbackResolver)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return new SshfpVerificationResult(SshfpVerificationStatus.NotFound, authenticatedData, usedLoopbackResolver, 0);
        }

        byte? expectedAlg = MapSshAlgorithm(sshAlgorithm);
        if (expectedAlg is null)
        {
            return new SshfpVerificationResult(SshfpVerificationStatus.NotFound, authenticatedData, usedLoopbackResolver, records.Count);
        }

        var matchingAlgRecords = records.Where(r => r.Algorithm == expectedAlg.Value).ToList();
        if (matchingAlgRecords.Count == 0)
        {
            return new SshfpVerificationResult(SshfpVerificationStatus.NotFound, authenticatedData, usedLoopbackResolver, records.Count);
        }

        string? expectedSha256Hex = ComputeExpectedSha256Hex(rawHostKeyBlob, presentedSha256Fingerprint);
        string? expectedSha1Hex = rawHostKeyBlob.Length > 0
            ? Convert.ToHexString(SHA1.HashData(rawHostKeyBlob))
            : null;

        bool comparedAny = false;
        foreach (var rec in matchingAlgRecords)
        {
            if (rec.FingerprintType == FpTypeSha256 && expectedSha256Hex is not null)
            {
                comparedAny = true;
                if (string.Equals(rec.FingerprintHex, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    return new SshfpVerificationResult(
                        SshfpVerificationStatus.Matched,
                        authenticatedData,
                        usedLoopbackResolver,
                        matchingAlgRecords.Count);
                }
            }
            else if (rec.FingerprintType == FpTypeSha1 && expectedSha1Hex is not null)
            {
                comparedAny = true;
                if (string.Equals(rec.FingerprintHex, expectedSha1Hex, StringComparison.OrdinalIgnoreCase))
                {
                    return new SshfpVerificationResult(
                        SshfpVerificationStatus.Matched,
                        authenticatedData,
                        usedLoopbackResolver,
                        matchingAlgRecords.Count);
                }
            }
        }

        return new SshfpVerificationResult(
            comparedAny ? SshfpVerificationStatus.Mismatch : SshfpVerificationStatus.NotFound,
            authenticatedData,
            usedLoopbackResolver,
            matchingAlgRecords.Count);
    }

    private static string? ComputeExpectedSha256Hex(ReadOnlySpan<byte> rawHostKeyBlob, string presentedSha256Fingerprint)
    {
        if (rawHostKeyBlob.Length > 0)
        {
            return Convert.ToHexString(SHA256.HashData(rawHostKeyBlob));
        }

        if (string.IsNullOrWhiteSpace(presentedSha256Fingerprint))
        {
            return null;
        }

        const string prefix = "SHA256:";
        string trimmed = presentedSha256Fingerprint.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string b64 = trimmed[prefix.Length..];
        int mod = b64.Length % 4;
        if (mod == 2) b64 += "==";
        else if (mod == 3) b64 += "=";

        try
        {
            byte[] digest = Convert.FromBase64String(b64);
            return digest.Length == 32 ? Convert.ToHexString(digest) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Builds a minimal RFC 1035 + EDNS0 (DO=1) DNS query packet for TYPE 44 (SSHFP).</summary>
    public static byte[] BuildSshfpQuery(string hostname, ushort transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        var labels = hostname.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0)
        {
            throw new ArgumentException("Hostname must contain at least one DNS label.", nameof(hostname));
        }

        using var ms = new MemoryStream(128);
        Span<byte> u16 = stackalloc byte[2];

        // Header: ID, Flags (RD=1, AD=1 -> 0x0120), QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=1 (EDNS0 OPT)
        BinaryPrimitives.WriteUInt16BigEndian(u16, transactionId);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0x0120);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 1);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 1);
        ms.Write(u16);

        // Question: QNAME, QTYPE=44 (SSHFP), QCLASS=1 (IN)
        foreach (string label in labels)
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length == 0 || labelBytes.Length > 63)
            {
                throw new ArgumentException($"Invalid DNS label length in '{hostname}'.", nameof(hostname));
            }

            ms.WriteByte((byte)labelBytes.Length);
            ms.Write(labelBytes);
        }

        ms.WriteByte(0); // root label
        BinaryPrimitives.WriteUInt16BigEndian(u16, DnsTypeSshfp);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 1);
        ms.Write(u16);

        // EDNS0 OPT pseudo-record (RFC 6891): NAME=0, TYPE=41, UDP payload=1232, extRCODE=0, ver=0, DO=1 (0x8000), RDLEN=0
        ms.WriteByte(0);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 41);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 1232);
        ms.Write(u16);
        ms.WriteByte(0);
        ms.WriteByte(0);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0x8000);
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        ms.Write(u16);

        return ms.ToArray();
    }

    /// <summary>
    /// Parses a raw DNS wire response for SSHFP records, guarding against malformed packets and
    /// compression pointer loops.
    /// </summary>
    public static bool TryParseSshfpResponse(
        ReadOnlySpan<byte> packet,
        ushort expectedTransactionId,
        out IReadOnlyList<SshfpRecord> records,
        out bool authenticatedData,
        out bool truncated)
    {
        records = [];
        authenticatedData = false;
        truncated = false;

        if (packet.Length < 12)
        {
            return false;
        }

        ushort txId = BinaryPrimitives.ReadUInt16BigEndian(packet[..2]);
        if (txId != expectedTransactionId)
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        bool isResponse = (flags & 0x8000) != 0;
        if (!isResponse)
        {
            return false;
        }

        truncated = (flags & 0x0200) != 0;
        authenticatedData = (flags & 0x0020) != 0;
        int rcode = flags & 0x000F;
        if (rcode != 0 && rcode != 3) // NOERROR or NXDOMAIN
        {
            return false;
        }

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        int offset = 12;

        for (int q = 0; q < qdCount; q++)
        {
            if (!TrySkipDnsName(packet, ref offset) || offset + 4 > packet.Length)
            {
                return false;
            }

            offset += 4;
        }

        var parsed = new List<SshfpRecord>();
        for (int a = 0; a < anCount; a++)
        {
            if (!TrySkipDnsName(packet, ref offset) || offset + 10 > packet.Length)
            {
                return false;
            }

            ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > packet.Length)
            {
                return false;
            }

            if (rrType == DnsTypeSshfp && rrClass == 1 && rdLength > 2)
            {
                byte alg = packet[offset];
                byte fpType = packet[offset + 1];
                string fpHex = Convert.ToHexString(packet.Slice(offset + 2, rdLength - 2));
                parsed.Add(new SshfpRecord(alg, fpType, fpHex));
            }

            offset += rdLength;
        }

        records = parsed;
        return true;
    }

    private static bool TrySkipDnsName(ReadOnlySpan<byte> packet, ref int offset)
    {
        int cursor = offset;
        int hops = 0;
        bool jumped = false;

        while (true)
        {
            if (cursor >= packet.Length)
            {
                return false;
            }

            byte len = packet[cursor];
            if (len == 0)
            {
                if (!jumped)
                {
                    offset = cursor + 1;
                }

                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length)
                {
                    return false;
                }

                int target = ((len & 0x3F) << 8) | packet[cursor + 1];
                if (target >= cursor || target >= packet.Length)
                {
                    return false;
                }

                if (!jumped)
                {
                    offset = cursor + 2;
                    jumped = true;
                }

                cursor = target;
                if (++hops > MaxDnsCompressionHops)
                {
                    return false;
                }

                continue;
            }

            if ((len & 0xC0) != 0)
            {
                return false;
            }

            cursor += 1 + len;
            if (cursor > packet.Length)
            {
                return false;
            }
        }
    }
}
