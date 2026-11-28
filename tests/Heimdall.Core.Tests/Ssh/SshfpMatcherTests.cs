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
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests.Ssh;

public sealed class SshfpMatcherTests
{
    [Fact]
    public void Verify_MatchingSha256AndSha1_ReturnsMatchedWithDnssecMetadata()
    {
        byte[] blob = [0, 0, 0, 11, .. System.Text.Encoding.ASCII.GetBytes("ssh-ed25519"), 0, 0, 0, 32, .. new byte[32]];
        string sha256B64 = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        string sha256Hex = Convert.ToHexString(SHA256.HashData(blob));

        var result = SshfpMatcher.Verify(
            "ssh-ed25519",
            sha256B64,
            blob,
            [new SshfpRecord(SshfpMatcher.AlgEd25519, SshfpMatcher.FpTypeSha256, sha256Hex)],
            authenticatedData: true,
            usedLoopbackResolver: true);

        Assert.Equal(SshfpVerificationStatus.Matched, result.Status);
        Assert.True(result.AuthenticatedData);
        Assert.True(result.UsedLoopbackResolver);
    }

    [Fact]
    public void Verify_MismatchedDigest_ReturnsMismatch()
    {
        byte[] blob = new byte[48];
        string sha256B64 = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        var result = SshfpMatcher.Verify(
            "ssh-ed25519",
            sha256B64,
            blob,
            [new SshfpRecord(SshfpMatcher.AlgEd25519, SshfpMatcher.FpTypeSha256, new string('A', 64))],
            authenticatedData: false,
            usedLoopbackResolver: false);

        Assert.Equal(SshfpVerificationStatus.Mismatch, result.Status);
    }

    [Fact]
    public void BuildAndParseSshfpQueryAndResponse_HandlesCompressionAndRejectsLoops()
    {
        ushort txId = 0x1234;
        byte[] query = SshfpMatcher.BuildSshfpQuery("srv.example.com", txId);
        Assert.True(query.Length > 20);
        Assert.Equal(txId, BinaryPrimitives.ReadUInt16BigEndian(query));

        // Craft a valid response with 1 SSHFP answer using a compression pointer to QNAME (0xC00C).
        byte[] digest = SHA256.HashData([1, 2, 3, 4]);
        using var ms = new MemoryStream();
        ms.Write([0x12, 0x34, 0x81, 0xA0, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]); // QR=1, RD=1, RA=1, AD=1
        // Question: srv.example.com QTYPE=44 QCLASS=1
        ms.Write([3, (byte)'s', (byte)'r', (byte)'v', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0]);
        ms.Write([0, 44, 0, 1]);
        // Answer: pointer to offset 12 (0xC0 0x0C), TYPE=44, CLASS=1, TTL=300, RDLEN=34, alg=4, fptype=2, digest
        ms.Write([0xC0, 0x0C, 0, 44, 0, 1, 0, 0, 1, 44, 0, 34, 4, 2]);
        ms.Write(digest);

        bool parsed = SshfpMatcher.TryParseSshfpResponse(ms.ToArray(), txId, out var records, out bool ad, out bool tc);
        Assert.True(parsed);
        Assert.True(ad);
        Assert.False(tc);
        Assert.Single(records);
        Assert.Equal(4, records[0].Algorithm);
        Assert.Equal(2, records[0].FingerprintType);
        Assert.Equal(Convert.ToHexString(digest), records[0].FingerprintHex);

        // Self-referencing compression pointer loop must be rejected cleanly.
        byte[] loopPacket = [0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x0C, 0, 44, 0, 1];
        Assert.False(SshfpMatcher.TryParseSshfpResponse(loopPacket, txId, out _, out _, out _));
    }
}
