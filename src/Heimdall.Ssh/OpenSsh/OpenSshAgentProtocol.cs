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
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.OpenSsh;

/// <summary>
/// SSH agent protocol framing and parsing helpers.
/// Implements the OpenSSH PROTOCOL.agent request/response subset used for authentication.
/// </summary>
internal static class OpenSshAgentProtocol
{
    internal const byte SshAgentFailure = 5;
    internal const byte SshAgentSuccess = 6;
    internal const byte SshAgentcRequestIdentities = 11;
    internal const byte SshAgentIdentitiesAnswer = 12;
    internal const byte SshAgentcSignRequest = 13;
    internal const byte SshAgentSignResponse = 14;
    internal const byte SshAgentcAddIdentity = 17;
    internal const byte SshAgentcRemoveIdentity = 18;
    internal const byte SshAgentcRemoveAllIdentities = 19;
    internal const byte SshAgentcAddIdConstrained = 25;
    internal const byte SshAgentConstrainLifetime = 1;
    internal const int MaxMessageLength = 262144;

    internal static byte[] BuildRequestIdentitiesMessage()
    {
        return BuildMessage(SshAgentcRequestIdentities, static _ => { });
    }

    internal static byte[] BuildRemoveIdentityMessage(byte[] publicKeyBlob)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBlob);
        return BuildMessage(SshAgentcRemoveIdentity, writer => writer.WriteString(publicKeyBlob));
    }

    internal static byte[] BuildRemoveAllIdentitiesMessage()
    {
        return BuildMessage(SshAgentcRemoveAllIdentities, static _ => { });
    }

    internal static bool ParseSuccessResponse(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var reader = new AgentMessageReader(response);
        var type = reader.ReadByte();
        reader.EnsureFullyRead();
        return type == SshAgentSuccess;
    }

    internal static byte[] BuildSignRequestMessage(
        byte[] publicKeyBlob,
        byte[] data,
        SshAgentSignFlags flags)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBlob);
        ArgumentNullException.ThrowIfNull(data);

        return BuildMessage(SshAgentcSignRequest, writer =>
        {
            writer.WriteString(publicKeyBlob);
            writer.WriteString(data);
            writer.WriteUInt32((uint)flags);
        });
    }

    internal static IReadOnlyList<OpenSshAgentKey> ParseIdentitiesResponse(
        byte[] response,
        OpenSshPipeAgent agent)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(agent);

        var reader = new AgentMessageReader(response);
        var type = reader.ReadByte();
        if (type == SshAgentFailure)
        {
            return [];
        }

        if (type != SshAgentIdentitiesAnswer)
        {
            throw new InvalidOperationException(
                $"Unexpected SSH agent response type: {type} (expected {SshAgentIdentitiesAnswer}).");
        }

        var count = reader.ReadUInt32();
        if (count > 1024)
        {
            throw new InvalidOperationException($"SSH agent returned too many identities: {count}.");
        }

        var keys = new List<OpenSshAgentKey>((int)count);
        for (var i = 0; i < count; i++)
        {
            var blob = reader.ReadString();
            var comment = reader.ReadUtf8String();
            var keyType = ExtractKeyTypeFromBlob(blob);
            keys.Add(new OpenSshAgentKey(agent, blob, comment, keyType));
        }

        reader.EnsureFullyRead();
        return keys;
    }

    internal static byte[] ParseSignResponse(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var reader = new AgentMessageReader(response);
        var type = reader.ReadByte();
        if (type != SshAgentSignResponse)
        {
            throw new InvalidOperationException(
                $"Unexpected SSH agent sign response type: {type} (expected {SshAgentSignResponse}).");
        }

        var signature = reader.ReadString();
        reader.EnsureFullyRead();
        return signature;
    }

    internal static string ExtractKeyTypeFromBlob(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        if (blob.Length < sizeof(uint))
        {
            return "unknown";
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, sizeof(uint)));
        if (length == 0 || length > blob.Length - sizeof(uint))
        {
            return "unknown";
        }

        try
        {
            return Encoding.ASCII.GetString(blob, sizeof(uint), (int)length);
        }
        catch (DecoderFallbackException)
        {
            return "unknown";
        }
    }

    internal static byte[] BuildMessage(byte messageType, Action<AgentMessageWriter> writePayload)
    {
        ArgumentNullException.ThrowIfNull(writePayload);

        using var writer = new AgentMessageWriter();
        writer.WriteByte(messageType);
        writePayload(writer);

        var payloadLength = writer.Length;
        if (payloadLength > MaxMessageLength)
        {
            throw new ArgumentException(
                $"SSH agent message exceeds maximum length of {MaxMessageLength} bytes.");
        }

        var message = new byte[sizeof(uint) + payloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(0, sizeof(uint)), (uint)payloadLength);
        writer.CopyTo(message.AsSpan(sizeof(uint)));
        return message;
    }

    internal sealed class AgentMessageWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public void WriteByte(byte value) => _stream.WriteByte(value);

        public void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void WriteString(byte[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            WriteUInt32((uint)value.Length);
            _stream.Write(value);
        }

        public void WriteUtf8String(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            WriteString(Encoding.UTF8.GetBytes(value));
        }

        public void WriteMpint(byte[] unsignedBigEndian)
        {
            ArgumentNullException.ThrowIfNull(unsignedBigEndian);
            var start = 0;
            while (start < unsignedBigEndian.Length && unsignedBigEndian[start] == 0)
            {
                start++;
            }

            if (start == unsignedBigEndian.Length)
            {
                WriteUInt32(0);
                return;
            }

            var len = unsignedBigEndian.Length - start;
            var needsLeadingZero = (unsignedBigEndian[start] & 0x80) != 0;
            WriteUInt32((uint)(len + (needsLeadingZero ? 1 : 0)));
            if (needsLeadingZero)
            {
                WriteByte(0);
            }

            _stream.Write(unsignedBigEndian, start, len);
        }

        public byte[] ToArray() => _stream.ToArray();

        public void CopyTo(Span<byte> destination)
        {
            if (_stream.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                segment.AsSpan().CopyTo(destination);
                return;
            }

            var fallback = _stream.ToArray();
            try
            {
                fallback.CopyTo(destination);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fallback);
            }
        }

        public void Dispose()
        {
            if (_stream.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array);
            }

            _stream.Dispose();
        }
    }

    internal sealed class AgentMessageReader
    {
        private readonly byte[] _payload;
        private int _offset;

        public AgentMessageReader(byte[] message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.Length < sizeof(uint) + 1)
            {
                throw new InvalidOperationException("SSH agent message is too short.");
            }

            var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(0, sizeof(uint)));
            if (payloadLength == 0 || payloadLength > MaxMessageLength)
            {
                throw new InvalidOperationException($"Invalid SSH agent message length: {payloadLength}.");
            }

            if (payloadLength != message.Length - sizeof(uint))
            {
                throw new InvalidOperationException(
                    $"SSH agent message length mismatch: header={payloadLength}, actual={message.Length - sizeof(uint)}.");
            }

            _payload = message[sizeof(uint)..];
        }

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _payload[_offset++];
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(sizeof(uint));
            var value = BinaryPrimitives.ReadUInt32BigEndian(_payload.AsSpan(_offset, sizeof(uint)));
            _offset += sizeof(uint);
            return value;
        }

        public byte[] ReadString()
        {
            var length = ReadUInt32();
            if (length > MaxMessageLength)
            {
                throw new InvalidOperationException($"SSH agent string is too large: {length}.");
            }

            EnsureAvailable((int)length);
            var value = _payload.AsSpan(_offset, (int)length).ToArray();
            _offset += (int)length;
            return value;
        }

        public string ReadUtf8String()
        {
            return Encoding.UTF8.GetString(ReadString());
        }

        public void EnsureFullyRead()
        {
            if (_offset != _payload.Length)
            {
                throw new InvalidOperationException(
                    $"SSH agent message has trailing data: {_payload.Length - _offset} byte(s).");
            }
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _offset + count > _payload.Length)
            {
                throw new InvalidOperationException("SSH agent message is truncated.");
            }
        }
    }
}
