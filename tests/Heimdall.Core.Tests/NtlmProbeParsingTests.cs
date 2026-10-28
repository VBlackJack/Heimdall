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
using Heimdall.Core.Discovery;

namespace Heimdall.Core.Tests;

/// <summary>
/// The replies of a host being probed are hostile input. These tests feed the NTLM probe's parsers
/// truncated buffers, negative and huge offsets, overflowing offset-plus-length pairs and random
/// corruption, and require that they never throw: an exception reaching the probe's catch used to
/// discard the SMB information already parsed from the Negotiate reply.
/// </summary>
public class NtlmProbeParsingTests
{
    private const int Type2TargetInfoLengthField = 40;
    private const int Type2TargetInfoOffsetField = 44;
    private const int Type2PayloadOffset = 56;
    private const int NegotiateBodyOffset = 64;
    private const int NegotiateLength = 128;
    private const int SessionSetupSecurityBufferOffset = 72;
    private const int FuzzIterations = 4000;
    private const int FuzzSeed = 20261028;
    private const int SilentServerTimeoutMs = 400;

    private static readonly byte[] Signature = "NTLMSSP\0"u8.ToArray();

    // ── ParseNtlmType2 ──────────────────────────────────────────────

    [Fact]
    public void ParseNtlmType2_ValidChallenge_ReadsNamesAndBuild()
    {
        var info = NtlmProbe.ParseNtlmType2(ValidType2());

        Assert.NotNull(info);
        Assert.Equal("DC01", info.NetBiosComputerName);
        Assert.Equal("CORP", info.NetBiosDomainName);
        Assert.Equal("dc01.corp.example.com", info.DnsComputerName);
        Assert.Equal("corp.example.com", info.DnsDomainName);
        Assert.Equal("example.com", info.DnsForestName);
        Assert.Equal("10.0.19041", info.OsBuild);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(31)]
    public void ParseNtlmType2_BufferShorterThanTheFixedHeader_IsNotAChallenge(int length)
    {
        Assert.Null(NtlmProbe.ParseNtlmType2(ValidType2().AsSpan(0, length).ToArray()));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void ParseNtlmType2_AnotherMessageType_IsNotAChallenge(uint messageType)
    {
        var data = ValidType2();
        BitConverter.GetBytes(messageType).CopyTo(data, 8);

        Assert.Null(NtlmProbe.ParseNtlmType2(data));
    }

    [Fact]
    public void ParseNtlmType2_WrongSignature_IsNotAChallenge()
    {
        var data = ValidType2();
        data[0] = (byte)'X';

        Assert.Null(NtlmProbe.ParseNtlmType2(data));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(47)]
    public void ParseNtlmType2_BufferWithoutTargetInfoFields_YieldsAnEmptyInfo(int length)
    {
        var info = NtlmProbe.ParseNtlmType2(ValidType2().AsSpan(0, length).ToArray());

        AssertNoNames(info);
        Assert.Null(info!.OsBuild);
    }

    /// <summary>
    /// The defect: the offset was a signed Int32, so 0xFFFFFFF0 read as -16 passed the bounds check
    /// and BitConverter threw. Every offset that points outside the buffer keeps the build from the
    /// version field and reads no names.
    /// </summary>
    [Theory]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(0x80000000u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0x7FFFFFF0u)]
    [InlineData(0x00010000u)]
    [InlineData(1u)]
    [InlineData(40u)]
    [InlineData(47u)]
    public void ParseNtlmType2_TargetInfoOffsetOutsideTheBuffer_KeepsTheBuildAndReadsNoNames(uint offset)
    {
        var data = ValidType2();
        BitConverter.GetBytes(offset).CopyTo(data, Type2TargetInfoOffsetField);

        var info = NtlmProbe.ParseNtlmType2(data);

        AssertNoNames(info);
        Assert.Equal("10.0.19041", info!.OsBuild);
    }

    /// <summary>offset + length overflowing 32 bits wrapped negative and passed the check.</summary>
    [Theory]
    [InlineData(0x7FFFFFF8u, (ushort)0x0010)]
    [InlineData(0xFFFFFFFFu, (ushort)0xFFFF)]
    [InlineData(0xFFFFFFF8u, (ushort)0x0010)]
    [InlineData(0x7FFFFFFFu, (ushort)0x0001)]
    public void ParseNtlmType2_TargetInfoOffsetPlusLengthOverflows_KeepsTheBuildAndReadsNoNames(uint offset, ushort length)
    {
        var data = ValidType2();
        BitConverter.GetBytes(length).CopyTo(data, Type2TargetInfoLengthField);
        BitConverter.GetBytes(offset).CopyTo(data, Type2TargetInfoOffsetField);

        var info = NtlmProbe.ParseNtlmType2(data);

        AssertNoNames(info);
        Assert.Equal("10.0.19041", info!.OsBuild);
    }

    [Fact]
    public void ParseNtlmType2_TargetInfoLongerThanTheBuffer_ReadsNoNames()
    {
        var data = ValidType2();
        BitConverter.GetBytes((ushort)(data.Length - Type2PayloadOffset + 1)).CopyTo(data, Type2TargetInfoLengthField);

        var info = NtlmProbe.ParseNtlmType2(data);

        AssertNoNames(info);
        Assert.Equal("10.0.19041", info!.OsBuild);
    }

    [Fact]
    public void ParseNtlmType2_ZeroLengthTargetInfo_ReadsNoNames()
    {
        var data = ValidType2();
        BitConverter.GetBytes((ushort)0).CopyTo(data, Type2TargetInfoLengthField);

        var info = NtlmProbe.ParseNtlmType2(data);

        AssertNoNames(info);
        Assert.Equal("10.0.19041", info!.OsBuild);
    }

    [Fact]
    public void ParseNtlmType2_AnAttributeLongerThanTheBlock_KeepsTheOnesBeforeIt()
    {
        var pairs = Concat(AvPair(2, "CORP"), AvPair(1, "DC01"), Truncated(AvPair(3, "dc01.corp.example.com"), 10, claimedLength: 0xFFFF));
        var info = NtlmProbe.ParseNtlmType2(Type2With(pairs));

        Assert.NotNull(info);
        Assert.Equal("CORP", info.NetBiosDomainName);
        Assert.Equal("DC01", info.NetBiosComputerName);
        Assert.Null(info.DnsComputerName);
    }

    [Fact]
    public void ParseNtlmType2_EndOfListMarker_StopsTheRead()
    {
        var pairs = Concat(AvPair(2, "CORP"), new byte[4], AvPair(1, "AFTER-EOL"));
        var info = NtlmProbe.ParseNtlmType2(Type2With(pairs));

        Assert.NotNull(info);
        Assert.Equal("CORP", info.NetBiosDomainName);
        Assert.Null(info.NetBiosComputerName);
    }

    [Fact]
    public void ParseNtlmType2_OddAttributeLength_DoesNotThrow()
    {
        var odd = new byte[] { 0x01, 0x00, 0x03, 0x00, 0x41, 0x00, 0x42 };

        var info = NtlmProbe.ParseNtlmType2(Type2With(odd));

        Assert.NotNull(info);
    }

    /// <summary>The names are chosen by the host: no control character may travel with them.</summary>
    [Fact]
    public void ParseNtlmType2_ControlCharactersInANameAreDropped()
    {
        var pairs = Concat(AvPair(1, "DC\u001b[31m01\r\n"), AvPair(2, "\u0007"), new byte[4]);
        var info = NtlmProbe.ParseNtlmType2(Type2With(pairs));

        Assert.NotNull(info);
        Assert.Equal("DC[31m01", info.NetBiosComputerName);
        Assert.Null(info.NetBiosDomainName);
    }

    [Fact]
    public void ParseNtlmType2_AnOversizedNameIsCut()
    {
        var pairs = Concat(AvPair(3, new string('a', 30000)), new byte[4]);
        var info = NtlmProbe.ParseNtlmType2(Type2With(pairs));

        Assert.NotNull(info);
        Assert.NotNull(info.DnsComputerName);
        Assert.True(info.DnsComputerName.Length <= 256, $"The name kept {info.DnsComputerName.Length} characters.");
    }

    [Fact]
    public void ParseNtlmType2_EveryTruncationOfAValidChallenge_NeverThrows()
    {
        var valid = ValidType2();
        for (var length = 0; length <= valid.Length; length++)
        {
            var cut = valid.AsSpan(0, length).ToArray();
            var exception = Record.Exception(() => NtlmProbe.ParseNtlmType2(cut));
            Assert.True(exception is null, $"Truncation at {length} threw {exception}");
        }
    }

    [Fact]
    public void ParseNtlmType2_RandomCorruptionOfAValidChallenge_NeverThrows()
    {
        var random = new Random(FuzzSeed);
        var valid = ValidType2();
        for (var iteration = 0; iteration < FuzzIterations; iteration++)
        {
            var data = (byte[])valid.Clone();
            for (var edit = random.Next(1, 8); edit > 0; edit--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => NtlmProbe.ParseNtlmType2(data));
            Assert.True(exception is null, $"Iteration {iteration} threw {exception}: {Convert.ToHexString(data)}");
        }
    }

    // ── ParseNegotiateResponse ──────────────────────────────────────

    [Fact]
    public void ParseNegotiateResponse_ValidResponse_ReadsDialectSigningGuidAndTimes()
    {
        var info = NtlmProbe.ParseNegotiateResponse(ValidNegotiate());

        Assert.NotNull(info);
        Assert.Equal((ushort)0x0311, info.DialectRevision);
        Assert.True(info.SigningRequired);
        Assert.True(info.SigningEnabled);
        Assert.Equal(new Guid("00112233-4455-6677-8899-aabbccddeeff").ToString("D"), info.ServerGuid);
        Assert.Equal(0x00000001u, info.Capabilities);
        Assert.Equal(new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), info.SystemTime);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), info.ServerStartTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(127)]
    public void ParseNegotiateResponse_TruncatedResponse_IsRefused(int length)
    {
        Assert.Null(NtlmProbe.ParseNegotiateResponse(ValidNegotiate().AsSpan(0, length).ToArray()));
    }

    [Fact]
    public void ParseNegotiateResponse_ErrorStatus_IsRefused()
    {
        var data = ValidNegotiate();
        BitConverter.GetBytes(0xC00000BBu).CopyTo(data, 8); // STATUS_NOT_SUPPORTED

        Assert.Null(NtlmProbe.ParseNegotiateResponse(data));
    }

    [Fact]
    public void ParseNegotiateResponse_NotAnSmb2Header_IsRefused()
    {
        var data = ValidNegotiate();
        data[3] = (byte)'X';

        Assert.Null(NtlmProbe.ParseNegotiateResponse(data));
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)9)]
    [InlineData((ushort)64)]
    [InlineData((ushort)0xFFFF)]
    public void ParseNegotiateResponse_WrongBodyStructureSize_IsRefused(ushort structureSize)
    {
        var data = ValidNegotiate();
        BitConverter.GetBytes(structureSize).CopyTo(data, NegotiateBodyOffset);

        Assert.Null(NtlmProbe.ParseNegotiateResponse(data));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void ParseNegotiateResponse_UnrepresentableFileTimes_AreDroppedNotThrown(long ticks)
    {
        var data = ValidNegotiate();
        BitConverter.GetBytes(ticks).CopyTo(data, NegotiateBodyOffset + 0x28);
        BitConverter.GetBytes(ticks).CopyTo(data, NegotiateBodyOffset + 0x30);

        var info = NtlmProbe.ParseNegotiateResponse(data);

        Assert.NotNull(info);
        Assert.Null(info.SystemTime);
        Assert.Null(info.ServerStartTime);
    }

    [Fact]
    public void ParseNegotiateResponse_OversizedResponse_ReadsOnlyTheBody()
    {
        var data = new byte[65536];
        ValidNegotiate().CopyTo(data, 0);

        var info = NtlmProbe.ParseNegotiateResponse(data);

        Assert.NotNull(info);
        Assert.Equal((ushort)0x0311, info.DialectRevision);
    }

    [Fact]
    public void ParseNegotiateResponse_EveryTruncationAndRandomCorruption_NeverThrows()
    {
        var valid = ValidNegotiate();
        for (var length = 0; length <= valid.Length; length++)
        {
            var cut = valid.AsSpan(0, length).ToArray();
            var exception = Record.Exception(() => NtlmProbe.ParseNegotiateResponse(cut));
            Assert.True(exception is null, $"Truncation at {length} threw {exception}");
        }

        var random = new Random(FuzzSeed);
        for (var iteration = 0; iteration < FuzzIterations; iteration++)
        {
            var data = (byte[])valid.Clone();
            for (var edit = random.Next(1, 8); edit > 0; edit--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => NtlmProbe.ParseNegotiateResponse(data));
            Assert.True(exception is null, $"Iteration {iteration} threw {exception}: {Convert.ToHexString(data)}");
        }
    }

    // ── ParseSessionSetupResponse ───────────────────────────────────

    [Fact]
    public void ParseSessionSetupResponse_ValidResponse_ReadsTheChallenge()
    {
        var info = NtlmProbe.ParseSessionSetupResponse(SessionSetupResponse(ValidType2()));

        Assert.NotNull(info);
        Assert.Equal("dc01.corp.example.com", info.DnsComputerName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(72)]
    public void ParseSessionSetupResponse_TruncatedHeader_YieldsNothing(int length)
    {
        var response = SessionSetupResponse(ValidType2());

        Assert.Null(NtlmProbe.ParseSessionSetupResponse(response.AsSpan(0, length).ToArray()));
    }

    [Theory]
    [InlineData((ushort)0xFFFF, (ushort)0xFFFF)]
    [InlineData((ushort)0xFFFF, (ushort)1)]
    [InlineData((ushort)200, (ushort)0xFFFF)]
    public void ParseSessionSetupResponse_SecurityBufferOutsideTheResponse_YieldsNothing(ushort offset, ushort length)
    {
        var response = SessionSetupResponse(ValidType2());
        BitConverter.GetBytes(offset).CopyTo(response, 68);
        BitConverter.GetBytes(length).CopyTo(response, 70);

        Assert.Null(NtlmProbe.ParseSessionSetupResponse(response));
    }

    [Fact]
    public void ParseSessionSetupResponse_NoChallengeInTheSecurityBuffer_YieldsNothing()
    {
        Assert.Null(NtlmProbe.ParseSessionSetupResponse(SessionSetupResponse(Encoding.ASCII.GetBytes("not a challenge at all"))));
    }

    [Fact]
    public void ParseSessionSetupResponse_ChallengeWithAHostileTargetInfoOffset_StillYieldsTheInfo()
    {
        var type2 = ValidType2();
        BitConverter.GetBytes(0xFFFFFFF0u).CopyTo(type2, Type2TargetInfoOffsetField);

        var info = NtlmProbe.ParseSessionSetupResponse(SessionSetupResponse(type2));

        Assert.NotNull(info);
        Assert.Equal("10.0.19041", info.OsBuild);
    }

    [Fact]
    public void ParseSessionSetupResponse_EveryTruncationAndRandomCorruption_NeverThrows()
    {
        var valid = SessionSetupResponse(ValidType2());
        for (var length = 0; length <= valid.Length; length++)
        {
            var cut = valid.AsSpan(0, length).ToArray();
            var exception = Record.Exception(() => NtlmProbe.ParseSessionSetupResponse(cut));
            Assert.True(exception is null, $"Truncation at {length} threw {exception}");
        }

        var random = new Random(FuzzSeed);
        for (var iteration = 0; iteration < FuzzIterations; iteration++)
        {
            var data = (byte[])valid.Clone();
            for (var edit = random.Next(1, 8); edit > 0; edit--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => NtlmProbe.ParseSessionSetupResponse(data));
            Assert.True(exception is null, $"Iteration {iteration} threw {exception}: {Convert.ToHexString(data)}");
        }
    }

    // ── The probe over a socket ─────────────────────────────────────

    /// <summary>
    /// What the Negotiate reply already told us must survive a failure of the Session Setup
    /// exchange: the catch used to return (null, null) and discard it. The server answers the
    /// Negotiate and then stays silent, so the probe's own timeout cancels the second read.
    /// </summary>
    [Fact]
    public async Task Probe_SessionSetupTimesOut_KeepsTheNegotiateInformation()
    {
        using var listener = StartLoopbackListener(out var port);
        var release = new TaskCompletionSource();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadFramedAsync(stream);
            await WriteFramedAsync(stream, ValidNegotiate());
            await ReadFramedAsync(stream);
            await release.Task;
        });

        var (ntlm, smb) = await NtlmProbe.ProbeWithSmbInfoOnPortAsync("127.0.0.1", port, SilentServerTimeoutMs, CancellationToken.None);
        release.SetResult();
        await server;

        Assert.Null(ntlm);
        Assert.NotNull(smb);
        Assert.Equal((ushort)0x0311, smb.DialectRevision);
    }

    [Fact]
    public async Task Probe_ChallengeWithAHostileTargetInfoOffset_KeepsTheNegotiateInformationAndTheBuild()
    {
        var type2 = ValidType2();
        BitConverter.GetBytes(0xFFFFFFF0u).CopyTo(type2, Type2TargetInfoOffsetField);

        using var listener = StartLoopbackListener(out var port);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadFramedAsync(stream);
            await WriteFramedAsync(stream, ValidNegotiate());
            await ReadFramedAsync(stream);
            await WriteFramedAsync(stream, SessionSetupResponse(type2));
        });

        var (ntlm, smb) = await NtlmProbe.ProbeWithSmbInfoOnPortAsync("127.0.0.1", port, 5000, CancellationToken.None);
        await server;

        Assert.NotNull(smb);
        Assert.NotNull(ntlm);
        Assert.Equal("10.0.19041", ntlm.OsBuild);
        Assert.Null(ntlm.DnsComputerName);
    }

    // ── Builders ────────────────────────────────────────────────────

    private static void AssertNoNames(NtlmInfo? info)
    {
        Assert.NotNull(info);
        Assert.Null(info.NetBiosComputerName);
        Assert.Null(info.NetBiosDomainName);
        Assert.Null(info.DnsComputerName);
        Assert.Null(info.DnsDomainName);
        Assert.Null(info.DnsForestName);
    }

    private static byte[] ValidType2() => Type2With(Concat(
        AvPair(2, "CORP"),
        AvPair(1, "DC01"),
        AvPair(4, "corp.example.com"),
        AvPair(3, "dc01.corp.example.com"),
        AvPair(5, "example.com"),
        new byte[4]));

    private static byte[] Type2With(byte[] targetInfo)
    {
        var message = new byte[Type2PayloadOffset + targetInfo.Length];
        Signature.CopyTo(message, 0);
        BitConverter.GetBytes(2u).CopyTo(message, 8);
        BitConverter.GetBytes(0xA2898215u).CopyTo(message, 20);
        BitConverter.GetBytes((ushort)targetInfo.Length).CopyTo(message, Type2TargetInfoLengthField);
        BitConverter.GetBytes((ushort)targetInfo.Length).CopyTo(message, Type2TargetInfoLengthField + 2);
        BitConverter.GetBytes((uint)Type2PayloadOffset).CopyTo(message, Type2TargetInfoOffsetField);
        message[48] = 10;
        message[49] = 0;
        BitConverter.GetBytes((ushort)19041).CopyTo(message, 50);
        message[55] = 15;
        targetInfo.CopyTo(message, Type2PayloadOffset);
        return message;
    }

    private static byte[] AvPair(ushort id, string value)
    {
        var text = Encoding.Unicode.GetBytes(value);
        var pair = new byte[4 + text.Length];
        BitConverter.GetBytes(id).CopyTo(pair, 0);
        BitConverter.GetBytes((ushort)text.Length).CopyTo(pair, 2);
        text.CopyTo(pair, 4);
        return pair;
    }

    /// <summary>The first <paramref name="keep"/> bytes of a pair, with its length field rewritten.</summary>
    private static byte[] Truncated(byte[] pair, int keep, ushort claimedLength)
    {
        var cut = pair.AsSpan(0, keep).ToArray();
        BitConverter.GetBytes(claimedLength).CopyTo(cut, 2);
        return cut;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] ValidNegotiate()
    {
        var data = new byte[NegotiateLength];
        new byte[] { 0xFE, (byte)'S', (byte)'M', (byte)'B' }.CopyTo(data, 0);
        BitConverter.GetBytes((ushort)64).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)65).CopyTo(data, NegotiateBodyOffset);
        BitConverter.GetBytes((ushort)0x0003).CopyTo(data, NegotiateBodyOffset + 0x02);
        BitConverter.GetBytes((ushort)0x0311).CopyTo(data, NegotiateBodyOffset + 0x04);
        new Guid("00112233-4455-6677-8899-aabbccddeeff").ToByteArray().CopyTo(data, NegotiateBodyOffset + 0x08);
        BitConverter.GetBytes(0x00000001u).CopyTo(data, NegotiateBodyOffset + 0x18);
        BitConverter.GetBytes(new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(data, NegotiateBodyOffset + 0x28);
        BitConverter.GetBytes(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(data, NegotiateBodyOffset + 0x30);
        return data;
    }

    /// <summary>An SMB2 Session Setup response carrying <paramref name="securityBuffer"/> behind a short SPNEGO-like prefix.</summary>
    private static byte[] SessionSetupResponse(byte[] securityBuffer)
    {
        byte[] prefix = [0xA1, 0x81, 0x00, 0x30, 0x81, 0x00];
        var buffer = Concat(prefix, securityBuffer);
        var response = new byte[SessionSetupSecurityBufferOffset + buffer.Length];
        new byte[] { 0xFE, (byte)'S', (byte)'M', (byte)'B' }.CopyTo(response, 0);
        BitConverter.GetBytes((ushort)64).CopyTo(response, 4);
        BitConverter.GetBytes(0xC0000016u).CopyTo(response, 8); // STATUS_MORE_PROCESSING_REQUIRED
        BitConverter.GetBytes((ushort)9).CopyTo(response, 64);
        BitConverter.GetBytes((ushort)SessionSetupSecurityBufferOffset).CopyTo(response, 68);
        BitConverter.GetBytes((ushort)buffer.Length).CopyTo(response, 70);
        buffer.CopyTo(response, SessionSetupSecurityBufferOffset);
        return response;
    }

    private static TcpListener StartLoopbackListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task<byte[]> ReadFramedAsync(NetworkStream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var length = (header[1] << 16) | (header[2] << 8) | header[3];
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        return payload;
    }

    private static async Task WriteFramedAsync(NetworkStream stream, byte[] payload)
    {
        var header = new byte[] { 0x00, (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length };
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }
}
