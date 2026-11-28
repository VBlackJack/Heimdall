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
using System.IO;
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Security;

namespace Heimdall.Ssh;

/// <summary>
/// Unauthenticated RFC 4253 TCP probe that captures a server's SSH identification banner
/// and parses its <c>SSH_MSG_KEXINIT</c> (message 20) algorithm name-lists without
/// attempting key exchange or authentication.
/// </summary>
public static class SshKexInitProbe
{
    private const byte SshMsgKexInit = 20;
    private const int KexInitCookieLength = 16;
    private const int MaxPreBannerLines = 64;
    private const int MaxBannerBytesTotal = 16 * 1024;
    private const int MaxBannerLineLength = 255;
    internal const int MaxKexInitPacketLength = 65536;
    private const string ClientIdentification = "SSH-2.0-Heimdall_Audit\r\n";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Connects to <paramref name="connectHost"/>:<paramref name="connectPort"/> and captures
    /// the server's banner and <c>SSH_MSG_KEXINIT</c> packet.
    /// <paramref name="displayHost"/> and <paramref name="displayPort"/> label the report
    /// (allowing tunnel-forwarded loopback connections to report the real target).
    /// </summary>
    public static async Task<SshKexInitSnapshot> ProbeAsync(
        string connectHost,
        int connectPort,
        string? displayHost = null,
        int? displayPort = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectHost);
        if (connectPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(connectPort));
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);
        var ct = timeoutCts.Token;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(connectHost, connectPort, ct).ConfigureAwait(false);
        using var stream = tcp.GetStream();

        string banner = await ReadServerBannerAsync(stream, ct).ConfigureAwait(false);

        byte[] clientBannerBytes = Encoding.ASCII.GetBytes(ClientIdentification);
        await stream.WriteAsync(clientBannerBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        byte[] kexPayload = await ReadKexInitPayloadAsync(stream, ct).ConfigureAwait(false);
        return ParseKexInitPayload(
            string.IsNullOrWhiteSpace(displayHost) ? connectHost : displayHost,
            displayPort is >= 1 and <= 65535 ? displayPort.Value : connectPort,
            banner,
            kexPayload);
    }

    /// <summary>
    /// Parses a raw <c>SSH_MSG_KEXINIT</c> payload (starting at message type byte 20) into
    /// an <see cref="SshKexInitSnapshot"/>.
    /// </summary>
    internal static SshKexInitSnapshot ParseKexInitPayload(
        string host,
        int port,
        string serverBanner,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 + KexInitCookieLength)
        {
            throw new InvalidDataException("SSH_MSG_KEXINIT payload is truncated.");
        }

        if (payload[0] != SshMsgKexInit)
        {
            throw new InvalidDataException($"Expected SSH_MSG_KEXINIT (20), received message type {payload[0]}.");
        }

        int offset = 1 + KexInitCookieLength;
        var kex = ReadNameList(payload, ref offset);
        var hostKeys = ReadNameList(payload, ref offset);
        var encC2S = ReadNameList(payload, ref offset);
        var encS2C = ReadNameList(payload, ref offset);
        var macC2S = ReadNameList(payload, ref offset);
        var macS2C = ReadNameList(payload, ref offset);
        var compC2S = ReadNameList(payload, ref offset);
        var compS2C = ReadNameList(payload, ref offset);

        return new SshKexInitSnapshot(
            host,
            port,
            serverBanner,
            kex,
            hostKeys,
            encC2S,
            encS2C,
            macC2S,
            macS2C,
            compC2S,
            compS2C);
    }

    private static async Task<string> ReadServerBannerAsync(NetworkStream stream, CancellationToken ct)
    {
        var single = new byte[1];
        var lineBuffer = new List<byte>(128);
        int totalBytes = 0;
        int linesRead = 0;

        while (linesRead < MaxPreBannerLines && totalBytes < MaxBannerBytesTotal)
        {
            lineBuffer.Clear();
            while (true)
            {
                int read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("Connection closed before SSH banner was received.");
                }

                totalBytes++;
                if (totalBytes > MaxBannerBytesTotal)
                {
                    throw new InvalidDataException("Server banner exceeded safety limit.");
                }

                byte b = single[0];
                if (b == (byte)'\n')
                {
                    break;
                }

                if (b != (byte)'\r' && lineBuffer.Count < MaxBannerLineLength)
                {
                    lineBuffer.Add(b);
                }
            }

            linesRead++;
            string line = Encoding.ASCII.GetString(lineBuffer.ToArray());
            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidDataException("Target did not send an SSH identification banner.");
    }

    private static async Task<byte[]> ReadKexInitPayloadAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (packetLength < 2 || packetLength > MaxKexInitPacketLength)
        {
            throw new InvalidDataException($"Invalid SSH packet length: {packetLength}.");
        }

        byte[] body = new byte[packetLength];
        await ReadExactAsync(stream, body, ct).ConfigureAwait(false);

        byte paddingLength = body[0];
        int payloadLength = (int)packetLength - 1 - paddingLength;
        if (payloadLength <= 0)
        {
            throw new InvalidDataException("Invalid SSH packet padding length.");
        }

        byte[] payload = new byte[payloadLength];
        Buffer.BlockCopy(body, 1, payload, 0, payloadLength);
        return payload;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Connection closed while reading SSH_MSG_KEXINIT.");
            }

            offset += read;
        }
    }

    private static IReadOnlyList<string> ReadNameList(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + 4 > payload.Length)
        {
            throw new InvalidDataException("Truncated name-list length in SSH_MSG_KEXINIT.");
        }

        uint len = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
        offset += 4;

        if (len > MaxKexInitPacketLength || offset + (int)len > payload.Length)
        {
            throw new InvalidDataException("Truncated name-list payload in SSH_MSG_KEXINIT.");
        }

        if (len == 0)
        {
            return [];
        }

        string raw = Encoding.ASCII.GetString(payload.Slice(offset, (int)len));
        offset += (int)len;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
