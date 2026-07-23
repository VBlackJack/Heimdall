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

using System.Runtime.Versioning;
using System.Security.Principal;
using Heimdall.Ssh.Pageant;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Tests for <see cref="PageantClient"/> protocol parsing logic.
/// These tests exercise the wire format parsing methods directly,
/// without requiring Pageant to be running.
/// </summary>
public class PageantClientTests
{
    // ── BigEndian helpers ─────────────────────────────────────────────

    [Fact]
    public void WriteBigEndianUInt32_WritesCorrectBytes()
    {
        var buffer = new byte[4];
        PageantClient.WriteBigEndianUInt32(buffer, 0, 0x01020304);

        Assert.Equal(0x01, buffer[0]);
        Assert.Equal(0x02, buffer[1]);
        Assert.Equal(0x03, buffer[2]);
        Assert.Equal(0x04, buffer[3]);
    }

    [Fact]
    public void ReadBigEndianUInt32_ReadsCorrectValue()
    {
        var buffer = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        uint result = PageantClient.ReadBigEndianUInt32(buffer, 0);

        Assert.Equal(0x01020304u, result);
    }

    [Fact]
    public void BigEndianRoundTrip_PreservesValue()
    {
        var buffer = new byte[8];
        PageantClient.WriteBigEndianUInt32(buffer, 2, 12345678u);
        uint result = PageantClient.ReadBigEndianUInt32(buffer, 2);

        Assert.Equal(12345678u, result);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    [InlineData(0x80000000u)]
    public void BigEndianRoundTrip_EdgeCases(uint value)
    {
        var buffer = new byte[4];
        PageantClient.WriteBigEndianUInt32(buffer, 0, value);
        uint result = PageantClient.ReadBigEndianUInt32(buffer, 0);

        Assert.Equal(value, result);
    }

    [Fact]
    public void ResponseLengthValidation_RejectsMappedSize_AndAcceptsValidMaximumMinusOne()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PageantClient.GetValidatedTotalResponseLength(PageantClient.AgentMaxMessageLength));

        uint validPayloadLength = (uint)(
            PageantClient.AgentMaxMessageLength - sizeof(uint) - 1);
        int totalResponseLength =
            PageantClient.GetValidatedTotalResponseLength(validPayloadLength);

        Assert.Equal(PageantClient.AgentMaxMessageLength - 1, totalResponseLength);
    }

    // ── ExtractKeyTypeFromBlob ────────────────────────────────────────

    [Fact]
    public void ExtractKeyTypeFromBlob_SshRsa_ReturnsCorrectType()
    {
        var blob = BuildBlobWithType("ssh-rsa");
        string keyType = PageantClient.ExtractKeyTypeFromBlob(blob);

        Assert.Equal("ssh-rsa", keyType);
    }

    [Fact]
    public void ExtractKeyTypeFromBlob_SshEd25519_ReturnsCorrectType()
    {
        var blob = BuildBlobWithType("ssh-ed25519");
        string keyType = PageantClient.ExtractKeyTypeFromBlob(blob);

        Assert.Equal("ssh-ed25519", keyType);
    }

    [Fact]
    public void ExtractKeyTypeFromBlob_EcdsaSha2Nistp256_ReturnsCorrectType()
    {
        var blob = BuildBlobWithType("ecdsa-sha2-nistp256");
        string keyType = PageantClient.ExtractKeyTypeFromBlob(blob);

        Assert.Equal("ecdsa-sha2-nistp256", keyType);
    }

    [Fact]
    public void ExtractKeyTypeFromBlob_TooShort_ReturnsUnknown()
    {
        Assert.Equal("unknown", PageantClient.ExtractKeyTypeFromBlob(new byte[] { 0x00 }));
        Assert.Equal("unknown", PageantClient.ExtractKeyTypeFromBlob(Array.Empty<byte>()));
    }

    [Fact]
    public void ExtractKeyTypeFromBlob_InvalidLength_ReturnsUnknown()
    {
        // Type length says 100 but blob is only 8 bytes
        var blob = new byte[8];
        PageantClient.WriteBigEndianUInt32(blob, 0, 100);

        Assert.Equal("unknown", PageantClient.ExtractKeyTypeFromBlob(blob));
    }

    // ── ParseIdentitiesResponse ──────────────────────────────────────

    [Fact]
    public void ParseIdentitiesResponse_EmptyKeyList_ReturnsEmptyList()
    {
        var response = BuildIdentitiesResponse(Array.Empty<(byte[] blob, string comment)>());
        var keys = PageantClient.ParseIdentitiesResponse(response);

        Assert.Empty(keys);
    }

    [Fact]
    public void ParseIdentitiesResponse_SingleKey_ParsesCorrectly()
    {
        var keyBlob = BuildBlobWithType("ssh-ed25519");
        var response = BuildIdentitiesResponse(new[] { (keyBlob, "test@host") });

        var keys = PageantClient.ParseIdentitiesResponse(response);

        Assert.Single(keys);
        Assert.Equal("ssh-ed25519", keys[0].KeyType);
        Assert.Equal("test@host", keys[0].Comment);
        Assert.Equal(keyBlob, keys[0].Blob);
    }

    [Fact]
    public void ParseIdentitiesResponse_MultipleKeys_ParsesAll()
    {
        var rsaBlob = BuildBlobWithType("ssh-rsa");
        var edBlob = BuildBlobWithType("ssh-ed25519");
        var response = BuildIdentitiesResponse(new[]
        {
            (rsaBlob, "rsa-key-20260101"),
            (edBlob, "ed25519-key")
        });

        var keys = PageantClient.ParseIdentitiesResponse(response);

        Assert.Equal(2, keys.Count);
        Assert.Equal("ssh-rsa", keys[0].KeyType);
        Assert.Equal("rsa-key-20260101", keys[0].Comment);
        Assert.Equal("ssh-ed25519", keys[1].KeyType);
        Assert.Equal("ed25519-key", keys[1].Comment);
    }

    [Fact]
    public void ParseIdentitiesResponse_WrongMessageType_Throws()
    {
        // Build a response with wrong type byte
        var response = new byte[] { 0, 0, 0, 1, 99 }; // type = 99

        var ex = Assert.Throws<InvalidOperationException>(
            () => PageantClient.ParseIdentitiesResponse(response));
        Assert.Contains("Unexpected agent response type", ex.Message);
    }

    [Fact]
    public void ParseIdentitiesResponse_TooShort_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => PageantClient.ParseIdentitiesResponse(new byte[] { 0, 0, 0, 1 }));
    }

    [Fact]
    public void ParseIdentitiesResponse_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PageantClient.ParseIdentitiesResponse(null!));
    }

    [Fact]
    public void ParseIdentitiesResponse_TruncatedBlob_Throws()
    {
        // Valid header but blob length exceeds remaining data
        var ms = new MemoryStream();
        WriteBE(ms, 10u); // length prefix (doesn't matter for parsing, just needs to be > 5)
        ms.WriteByte(12); // SSH2_AGENT_IDENTITIES_ANSWER
        WriteBE(ms, 1u);  // 1 key
        WriteBE(ms, 999u); // blob length = 999 (but no data follows)

        var ex = Assert.Throws<InvalidOperationException>(
            () => PageantClient.ParseIdentitiesResponse(ms.ToArray()));
        Assert.Contains("Invalid key blob length", ex.Message);
    }

    // ── ParseSignResponse ────────────────────────────────────────────

    [Fact]
    public void ParseSignResponse_ValidResponse_ReturnsSignature()
    {
        var signature = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var response = BuildSignResponse(signature);

        var result = PageantClient.ParseSignResponse(response);

        Assert.Equal(signature, result);
    }

    [Fact]
    public void ParseSignResponse_WrongMessageType_Throws()
    {
        var response = new byte[] { 0, 0, 0, 1, 99 }; // type = 99

        var ex = Assert.Throws<InvalidOperationException>(
            () => PageantClient.ParseSignResponse(response));
        Assert.Contains("Unexpected agent response type", ex.Message);
    }

    [Fact]
    public void ParseSignResponse_TooShort_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => PageantClient.ParseSignResponse(new byte[] { 0, 0 }));
    }

    [Fact]
    public void ParseSignResponse_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PageantClient.ParseSignResponse(null!));
    }

    [Fact]
    public void ParseSignResponse_InvalidSignatureLength_Throws()
    {
        var ms = new MemoryStream();
        WriteBE(ms, 5u); // length prefix
        ms.WriteByte(14); // SSH2_AGENT_SIGN_RESPONSE
        WriteBE(ms, 999u); // signature length = 999 (exceeds response)

        var ex = Assert.Throws<InvalidOperationException>(
            () => PageantClient.ParseSignResponse(ms.ToArray()));
        Assert.Contains("Invalid signature length", ex.Message);
    }

    // ── Test helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal SSH public key blob with the given key type string
    /// as the first field: [type_len:4][type_string].
    /// </summary>
    private static byte[] BuildBlobWithType(string keyType)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(keyType);
        var blob = new byte[4 + typeBytes.Length];
        PageantClient.WriteBigEndianUInt32(blob, 0, (uint)typeBytes.Length);
        Array.Copy(typeBytes, 0, blob, 4, typeBytes.Length);
        return blob;
    }

    /// <summary>
    /// Builds a full SSH2_AGENT_IDENTITIES_ANSWER response from key data.
    /// Wire format: [length:4][type:1][count:4][{blob_len:4,blob,comment_len:4,comment}...]
    /// </summary>
    private static byte[] BuildIdentitiesResponse(
        (byte[] blob, string comment)[] keys)
    {
        var ms = new MemoryStream();

        // Placeholder for length prefix
        ms.Write(new byte[4], 0, 4);

        // Message type
        ms.WriteByte(12); // SSH2_AGENT_IDENTITIES_ANSWER

        // Key count
        WriteBE(ms, (uint)keys.Length);

        foreach (var (blob, comment) in keys)
        {
            WriteBE(ms, (uint)blob.Length);
            ms.Write(blob, 0, blob.Length);

            var commentBytes = System.Text.Encoding.UTF8.GetBytes(comment);
            WriteBE(ms, (uint)commentBytes.Length);
            ms.Write(commentBytes, 0, commentBytes.Length);
        }

        var result = ms.ToArray();

        // Write actual payload length (total - 4 byte length prefix)
        var payloadLength = (uint)(result.Length - 4);
        PageantClient.WriteBigEndianUInt32(result, 0, payloadLength);

        return result;
    }

    /// <summary>
    /// Builds a full SSH2_AGENT_SIGN_RESPONSE from a raw signature.
    /// Wire format: [length:4][type:1][sig_len:4][signature]
    /// </summary>
    private static byte[] BuildSignResponse(byte[] signature)
    {
        var ms = new MemoryStream();

        // Placeholder for length prefix
        ms.Write(new byte[4], 0, 4);

        // Message type
        ms.WriteByte(14); // SSH2_AGENT_SIGN_RESPONSE

        // Signature
        WriteBE(ms, (uint)signature.Length);
        ms.Write(signature, 0, signature.Length);

        var result = ms.ToArray();
        var payloadLength = (uint)(result.Length - 4);
        PageantClient.WriteBigEndianUInt32(result, 0, payloadLength);

        return result;
    }

    /// <summary>Helper: writes a uint32 in big-endian to a stream.</summary>
    private static void WriteBE(MemoryStream ms, uint value)
    {
        ms.WriteByte((byte)(value >> 24));
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }

    // ── SecurityAttributesScope DACL (self-only) ─────────────────────

    [Fact]
    [SupportedOSPlatform("windows")]
    public void BuildSelfOnlySddl_ProducesProtectedDaclWithSingleAce()
    {
        var sid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var sddl = SecurityAttributesScope.BuildSelfOnlySddl(sid);

        Assert.Equal($"D:P(A;;FA;;;{sid.Value})", sddl);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void BuildSelfOnlySddl_NullSid_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SecurityAttributesScope.BuildSelfOnlySddl(null!));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateSelfOnly_ReturnsNonZeroPointerAndIsDisposable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var scope = SecurityAttributesScope.CreateSelfOnly();
        Assert.NotEqual(IntPtr.Zero, scope.Pointer);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateSelfOnly_ManyAllocations_DoNotLeakOrThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Smoke test for the alloc/free symmetry post-P1-MEM-01: exercising
        // the path many times must not leak handles or throw on the second
        // allocation. We can't directly assert "no native leak" from xUnit
        // without ETW instrumentation, but a tight loop catches the obvious
        // regression where saPtr is never released.
        for (var i = 0; i < 64; i++)
        {
            using var scope = SecurityAttributesScope.CreateSelfOnly();
            Assert.NotEqual(IntPtr.Zero, scope.Pointer);
        }
    }
}
