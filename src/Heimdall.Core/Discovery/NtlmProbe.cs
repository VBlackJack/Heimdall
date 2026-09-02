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

using System.Net.Sockets;
using System.Text;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Extracts hostname, domain, and OS build from Windows hosts via SMB2 NTLMSSP
/// challenge exchange. No credentials are required - only the NTLM Type 1
/// (Negotiate) is sent, and the Type 2 (Challenge) response is parsed for
/// TargetInfo AV_PAIR structures.
/// </summary>
public static class NtlmProbe
{
    // SMB2 constants
    private static readonly byte[] Smb2Magic = [0xFE, (byte)'S', (byte)'M', (byte)'B'];
    private const ushort CommandNegotiate = 0x0000;
    private const ushort CommandSessionSetup = 0x0001;

    // NTLMSSP constants
    private static readonly byte[] NtlmsspSignature =
        [0x4E, 0x54, 0x4C, 0x4D, 0x53, 0x53, 0x50, 0x00]; // "NTLMSSP\0"

    /// <summary>
    /// Probes a host on port 445 (SMB) to extract NTLM challenge information.
    /// Returns null if the host doesn't respond or doesn't support NTLMSSP.
    /// </summary>
    public static async Task<NtlmInfo?> ProbeAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        var (ntlm, _) = await ProbeWithSmbInfoAsync(host, timeoutMs, ct).ConfigureAwait(false);
        return ntlm;
    }

    /// <summary>
    /// Probes SMB2 and returns both NTLM challenge info and SMB2 Negotiate metadata
    /// (dialect, signing, server GUID, uptime).
    /// </summary>
    public static async Task<(NtlmInfo? Ntlm, SmbNegotiateInfo? Smb)> ProbeWithSmbInfoAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            await client.ConnectAsync(host, 445, linked.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs;

            // Step 1: SMB2 Negotiate
            var negotiate = BuildSmb2Negotiate();
            await SendNetBiosMessage(stream, negotiate, linked.Token).ConfigureAwait(false);
            var negResp = await ReadNetBiosMessage(stream, linked.Token).ConfigureAwait(false);
            if (negResp is null || negResp.Length < 68) return (null, null);

            // Verify SMB2 header
            if (negResp[0] != 0xFE || negResp[1] != (byte)'S') return (null, null);

            // Parse SMB2 Negotiate Response (body starts at offset 64)
            var smbInfo = ParseNegotiateResponse(negResp);

            // Step 2: Session Setup with NTLMSSP Type 1
            var ntlmType1 = BuildNtlmsspType1();
            var spnego = WrapInSpnego(ntlmType1);
            var sessionSetup = BuildSmb2SessionSetup(spnego);
            await SendNetBiosMessage(stream, sessionSetup, linked.Token).ConfigureAwait(false);
            var setupResp = await ReadNetBiosMessage(stream, linked.Token).ConfigureAwait(false);
            if (setupResp is null || setupResp.Length < 73) return (null, smbInfo);

            // Extract security buffer from Session Setup response
            var secBufferOffset = BitConverter.ToUInt16(setupResp, 68);
            var secBufferLen = BitConverter.ToUInt16(setupResp, 70);
            if (secBufferOffset + secBufferLen > setupResp.Length) return (null, smbInfo);

            var secBuffer = setupResp.AsSpan(secBufferOffset, secBufferLen);

            // Find NTLMSSP Type 2 inside SPNEGO wrapper
            var ntlmType2 = FindNtlmsspMessage(secBuffer);
            if (ntlmType2 is null) return (null, smbInfo);

            return (ParseNtlmType2(ntlmType2), smbInfo);
        }
        catch (Exception ex)
        {
            Logging.FileLogger.Log("DEBUG",
                $"NTLM probe failed for {host}: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    private static byte[] BuildSmb2Negotiate()
    {
        var header = new byte[64];
        // SMB2 header
        Smb2Magic.CopyTo(header, 0);
        BitConverter.GetBytes((ushort)64).CopyTo(header, 4); // StructureSize
        BitConverter.GetBytes(CommandNegotiate).CopyTo(header, 12); // Command
        BitConverter.GetBytes((ushort)1).CopyTo(header, 14); // CreditRequest

        // Negotiate body: StructureSize(2) + DialectCount(2) + SecurityMode(2) +
        // Reserved(2) + Capabilities(4) + ClientGuid(16) + ClientStartTime(8) + Dialects(6)
        var body = new byte[40];
        BitConverter.GetBytes((ushort)36).CopyTo(body, 0); // StructureSize
        BitConverter.GetBytes((ushort)3).CopyTo(body, 2);  // DialectCount
        BitConverter.GetBytes((ushort)1).CopyTo(body, 4);  // SecurityMode: SIGNING_ENABLED
        // ClientGuid: leave as zeros (anonymous scanner)
        // Dialects
        BitConverter.GetBytes((ushort)0x0202).CopyTo(body, 32); // SMB 2.0.2
        BitConverter.GetBytes((ushort)0x0210).CopyTo(body, 34); // SMB 2.1
        BitConverter.GetBytes((ushort)0x0300).CopyTo(body, 36); // SMB 3.0

        var packet = new byte[header.Length + body.Length];
        header.CopyTo(packet, 0);
        body.CopyTo(packet, header.Length);
        return packet;
    }

    private static byte[] BuildSmb2SessionSetup(byte[] securityBuffer)
    {
        var header = new byte[64];
        Smb2Magic.CopyTo(header, 0);
        BitConverter.GetBytes((ushort)64).CopyTo(header, 4);
        BitConverter.GetBytes(CommandSessionSetup).CopyTo(header, 12);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 14); // CreditRequest
        BitConverter.GetBytes((long)1).CopyTo(header, 28);   // MessageId = 1

        // Session Setup body (24 bytes before security buffer)
        var body = new byte[24 + securityBuffer.Length];
        BitConverter.GetBytes((ushort)25).CopyTo(body, 0); // StructureSize
        body[2] = 0; // Flags
        body[3] = 1; // SecurityMode: SIGNING_ENABLED
        // SecurityBufferOffset = 64 (header) + 24 (body fixed) = 88
        BitConverter.GetBytes((ushort)88).CopyTo(body, 12);
        BitConverter.GetBytes((ushort)securityBuffer.Length).CopyTo(body, 14);
        securityBuffer.CopyTo(body, 24);

        var packet = new byte[header.Length + body.Length];
        header.CopyTo(packet, 0);
        body.CopyTo(packet, header.Length);
        return packet;
    }

    private static byte[] BuildNtlmsspType1()
    {
        // Minimal NTLMSSP Negotiate message
        var msg = new byte[32];
        NtlmsspSignature.CopyTo(msg, 0);
        BitConverter.GetBytes(1u).CopyTo(msg, 8); // MessageType: Negotiate
        // Flags: UNICODE | REQUEST_TARGET | NTLM | ALWAYS_SIGN
        BitConverter.GetBytes(0x00088207u).CopyTo(msg, 12);
        // DomainName and Workstation fields left empty (offset 16-31 = zeros)
        return msg;
    }

    /// <summary>
    /// Wraps an NTLMSSP message in a minimal SPNEGO/GSS-API initToken.
    /// Uses proper ASN.1 DER length encoding for messages > 127 bytes.
    /// </summary>
    private static byte[] WrapInSpnego(byte[] ntlmssp)
    {
        // OID 1.3.6.1.5.5.2 (SPNEGO)
        byte[] spnegoOid = [0x06, 0x06, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x02];
        // OID 1.3.6.1.4.1.311.2.2.10 (NTLMSSP)
        byte[] ntlmOid = [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A];

        var mechTypes = DerWrap(0x30, ntlmOid);
        var mechTypesCtx = DerWrap(0xA0, mechTypes);
        var mechToken = DerWrap(0x04, ntlmssp);
        var mechTokenCtx = DerWrap(0xA2, mechToken);
        var negTokenInit = Concat(mechTypesCtx, mechTokenCtx);
        var negTokenInitSeq = DerWrap(0x30, negTokenInit);
        var ctx0 = DerWrap(0xA0, negTokenInitSeq);
        var inner = Concat(spnegoOid, ctx0);
        return DerWrap(0x60, inner);
    }

    /// <summary>Wraps content with ASN.1 DER tag + properly encoded length.</summary>
    private static byte[] DerWrap(byte tag, byte[] content)
    {
        var lenBytes = DerEncodeLength(content.Length);
        var result = new byte[1 + lenBytes.Length + content.Length];
        result[0] = tag;
        lenBytes.CopyTo(result, 1);
        content.CopyTo(result, 1 + lenBytes.Length);
        return result;
    }

    private static byte[] DerEncodeLength(int length)
    {
        if (length < 0x80) return [(byte)length];
        if (length < 0x100) return [0x81, (byte)length];
        return [0x82, (byte)(length >> 8), (byte)(length & 0xFF)];
    }

    private static async Task SendNetBiosMessage(
        NetworkStream stream, byte[] data, CancellationToken ct)
    {
        // NetBIOS Session Service: 1 byte type (0x00) + 3 bytes length
        var header = new byte[4];
        header[0] = 0x00;
        header[1] = (byte)((data.Length >> 16) & 0xFF);
        header[2] = (byte)((data.Length >> 8) & 0xFF);
        header[3] = (byte)(data.Length & 0xFF);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadNetBiosMessage(
        NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(header.AsMemory(read, 4 - read), ct)
                .ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }

        var length = (header[1] << 16) | (header[2] << 8) | header[3];
        if (length is 0 or > 65536) return null;

        var data = new byte[length];
        read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(data.AsMemory(read, length - read), ct)
                .ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return data;
    }

    /// <summary>
    /// Searches a buffer for the NTLMSSP signature and returns the message bytes.
    /// Handles SPNEGO wrapping by scanning for the signature.
    /// </summary>
    private static byte[]? FindNtlmsspMessage(ReadOnlySpan<byte> buffer)
    {
        for (var i = 0; i <= buffer.Length - NtlmsspSignature.Length; i++)
        {
            if (buffer[i] == 0x4E && buffer.Slice(i).StartsWith(NtlmsspSignature))
            {
                return buffer[i..].ToArray();
            }
        }
        return null;
    }

    /// <summary>
    /// Parses NTLMSSP Type 2 (Challenge) message to extract TargetInfo AV_PAIRs.
    /// </summary>
    internal static NtlmInfo? ParseNtlmType2(byte[] data)
    {
        // Minimum Type 2: signature(8) + type(4) + targetNameFields(8) + flags(4) + challenge(8) = 32
        if (data.Length < 32) return null;
        if (BitConverter.ToUInt32(data, 8) != 2) return null; // Not Type 2

        // TargetInfo fields at offset 40 (if present)
        if (data.Length < 48) return new NtlmInfo(null, null, null, null, null, null);
        var targetInfoLen = BitConverter.ToUInt16(data, 40);
        var targetInfoOffset = BitConverter.ToInt32(data, 44);
        if (targetInfoOffset + targetInfoLen > data.Length)
            return new NtlmInfo(null, null, null, null, null, null);

        // Parse AV_PAIR structures with strict bounds checking
        string? nbComputer = null, nbDomain = null;
        string? dnsComputer = null, dnsDomain = null, dnsForest = null;

        var offset = targetInfoOffset;
        var endOffset = targetInfoOffset + targetInfoLen;
        while (offset + 4 <= endOffset && offset + 4 <= data.Length)
        {
            var avId = BitConverter.ToUInt16(data, offset);
            var avLen = BitConverter.ToUInt16(data, offset + 2);
            offset += 4;
            if (avId == 0) break; // MsvAvEOL
            if (avLen > endOffset - offset || offset + avLen > data.Length) break;

            var value = Encoding.Unicode.GetString(data, offset, avLen);
            switch (avId)
            {
                case 1: nbComputer = value; break;  // MsvAvNbComputerName
                case 2: nbDomain = value; break;     // MsvAvNbDomainName
                case 3: dnsComputer = value; break;  // MsvAvDnsComputerName
                case 4: dnsDomain = value; break;    // MsvAvDnsDomainName
                case 5: dnsForest = value; break;    // MsvAvDnsTreeName
            }
            offset += avLen;
        }

        // Attempt to extract OS build from target name (in the main TargetName field)
        string? osBuild = null;
        if (data.Length >= 56)
        {
            // Version field at offset 48: Major(1) Minor(1) Build(2) Revision(1) NTLMRevision(1)
            var major = data[48];
            var minor = data[49];
            var build = BitConverter.ToUInt16(data, 50);
            if (major > 0 && build > 0)
                osBuild = $"{major}.{minor}.{build}";
        }

        return new NtlmInfo(nbComputer, nbDomain, dnsComputer, dnsDomain, dnsForest, osBuild);
    }

    /// <summary>
    /// Parses the SMB2 Negotiate Response body to extract dialect, signing,
    /// server GUID, system time, and server start time.
    /// </summary>
    private static SmbNegotiateInfo? ParseNegotiateResponse(byte[] data)
    {
        // Body starts at offset 64 (SMB2 header size)
        const int bodyOff = 64;
        if (data.Length < bodyOff + 0x40) return null;

        var securityMode = BitConverter.ToUInt16(data, bodyOff + 0x02);
        var dialect = BitConverter.ToUInt16(data, bodyOff + 0x04);

        // ServerGuid at body+0x08, 16 bytes
        var guidBytes = new byte[16];
        Array.Copy(data, bodyOff + 0x08, guidBytes, 0, 16);
        var serverGuid = new Guid(guidBytes).ToString("D");

        var capabilities = BitConverter.ToUInt32(data, bodyOff + 0x18);

        // SystemTime at body+0x28 (FILETIME)
        DateTime? systemTime = null;
        var sysTimeTicks = BitConverter.ToInt64(data, bodyOff + 0x28);
        if (sysTimeTicks > 0)
        {
            try { systemTime = DateTime.FromFileTimeUtc(sysTimeTicks); }
            catch { /* invalid FILETIME */ }
        }

        // ServerStartTime at body+0x30 (FILETIME)
        DateTime? startTime = null;
        var startTicks = BitConverter.ToInt64(data, bodyOff + 0x30);
        if (startTicks > 0)
        {
            try { startTime = DateTime.FromFileTimeUtc(startTicks); }
            catch { /* invalid FILETIME */ }
        }

        return new SmbNegotiateInfo(
            serverGuid,
            dialect,
            (securityMode & 0x0002) != 0,
            (securityMode & 0x0001) != 0,
            systemTime,
            startTime,
            capabilities);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}
