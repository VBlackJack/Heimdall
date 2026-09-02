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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Identifier for the key-derivation function recorded in a vault envelope.
/// </summary>
public enum VaultKdfId : byte
{
    /// <summary>Argon2id (RFC 9106).</summary>
    Argon2id = 1,
}

/// <summary>
/// Identifier for the authenticated cipher recorded in a vault envelope.
/// </summary>
public enum VaultCipherId : byte
{
    /// <summary>AES-256-GCM.</summary>
    Aes256Gcm = 1,
}

/// <summary>
/// Self-describing, versioned binary envelope for a master-password-protected
/// secret. The on-disk shape carries everything needed to re-derive the key and
/// decrypt: the KDF id and cost parameters, the salt, the cipher id, the nonce,
/// the tag, and the ciphertext. It is encoded as Base64 for storage.
/// </summary>
/// <remarks>
/// This format is distinct from the legacy <c>"|HMAC|"</c> DPAPI format handled
/// by <c>HmacIntegrity</c>; the two never share parsing code. No field here is a
/// plaintext secret - the salt and nonce are non-secret framing values and the
/// ciphertext/tag are already encrypted, so the envelope bytes are not zeroed.
/// </remarks>
public sealed class VaultEnvelope
{
    /// <summary>Magic prefix identifying the Heimdall master-vault format ("HMV1").</summary>
    public static readonly byte[] Magic = "HMV1"u8.ToArray();

    /// <summary>The only format version currently understood.</summary>
    public const byte CurrentFormatVersion = 1;

    private const int Uint32Width = 4;

    /// <summary>The envelope format version.</summary>
    public byte FormatVersion { get; }

    /// <summary>The key-derivation function used.</summary>
    public VaultKdfId KdfId { get; }

    /// <summary>The Argon2id cost parameters used to derive the key.</summary>
    public Argon2idParameters KdfParameters { get; }

    /// <summary>The salt fed to the KDF.</summary>
    public byte[] Salt { get; }

    /// <summary>The authenticated cipher used.</summary>
    public VaultCipherId CipherId { get; }

    /// <summary>The nonce used by the cipher.</summary>
    public byte[] Nonce { get; }

    /// <summary>The authentication tag produced by the cipher.</summary>
    public byte[] Tag { get; }

    /// <summary>The ciphertext.</summary>
    public byte[] Ciphertext { get; }

    private VaultEnvelope(
        byte formatVersion,
        VaultKdfId kdfId,
        Argon2idParameters kdfParameters,
        byte[] salt,
        VaultCipherId cipherId,
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext)
    {
        FormatVersion = formatVersion;
        KdfId = kdfId;
        KdfParameters = kdfParameters;
        Salt = salt;
        CipherId = cipherId;
        Nonce = nonce;
        Tag = tag;
        Ciphertext = ciphertext;
    }

    /// <summary>
    /// Encode a vault envelope to a Base64 string for storage. Uses Argon2id and
    /// AES-256-GCM, the only algorithms this format version supports.
    /// </summary>
    /// <param name="kdfParameters">The Argon2id parameters used for derivation.</param>
    /// <param name="salt">The KDF salt (1..255 bytes).</param>
    /// <param name="nonce">The cipher nonce (1..255 bytes).</param>
    /// <param name="ciphertext">The ciphertext.</param>
    /// <param name="tag">The authentication tag (1..255 bytes).</param>
    /// <returns>The Base64-encoded envelope.</returns>
    /// <exception cref="ArgumentException">Thrown when a length field exceeds its on-wire width.</exception>
    public static string Encode(
        Argon2idParameters kdfParameters,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        RequireByteLength(salt.Length, nameof(salt));
        RequireByteLength(nonce.Length, nameof(nonce));
        RequireByteLength(tag.Length, nameof(tag));

        int total =
            Magic.Length +
            1 +                       // format version
            1 +                       // kdf id
            (Uint32Width * 3) +       // memKib, iters, parallelism
            1 + salt.Length +         // salt-len + salt
            1 +                       // cipher id
            1 + nonce.Length +        // nonce-len + nonce
            1 + tag.Length +          // tag-len + tag
            Uint32Width + ciphertext.Length; // ct-len + ct

        var buffer = new byte[total];
        var span = buffer.AsSpan();
        int offset = 0;

        Magic.CopyTo(span[offset..]);
        offset += Magic.Length;

        span[offset++] = CurrentFormatVersion;
        span[offset++] = (byte)VaultKdfId.Argon2id;

        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)kdfParameters.MemoryKib);
        offset += Uint32Width;
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)kdfParameters.Iterations);
        offset += Uint32Width;
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)kdfParameters.Parallelism);
        offset += Uint32Width;

        span[offset++] = (byte)salt.Length;
        salt.CopyTo(span[offset..]);
        offset += salt.Length;

        span[offset++] = (byte)VaultCipherId.Aes256Gcm;

        span[offset++] = (byte)nonce.Length;
        nonce.CopyTo(span[offset..]);
        offset += nonce.Length;

        span[offset++] = (byte)tag.Length;
        tag.CopyTo(span[offset..]);
        offset += tag.Length;

        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)ciphertext.Length);
        offset += Uint32Width;
        ciphertext.CopyTo(span[offset..]);

        return Convert.ToBase64String(buffer);
    }

    /// <summary>
    /// Attempt to decode a Base64 vault envelope. Rejects an unknown magic,
    /// format version, KDF id, or cipher id, and any truncation or length
    /// mismatch (including trailing bytes), returning <c>false</c> without
    /// throwing. No diagnostic detail is emitted that could leak envelope bytes.
    /// </summary>
    /// <param name="encoded">The Base64-encoded envelope.</param>
    /// <param name="envelope">The decoded envelope, or <c>null</c> on failure.</param>
    /// <returns><c>true</c> if the input is a well-formed, supported envelope.</returns>
    public static bool TryDecode(string? encoded, out VaultEnvelope? envelope)
    {
        envelope = null;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        var reader = new EnvelopeReader(buffer);

        if (!reader.TryReadMagic() ||
            !reader.TryReadByte(out byte formatVersion) || formatVersion != CurrentFormatVersion ||
            !reader.TryReadByte(out byte kdfId) || kdfId != (byte)VaultKdfId.Argon2id ||
            !reader.TryReadUInt32(out uint memKib) ||
            !reader.TryReadUInt32(out uint iters) ||
            !reader.TryReadUInt32(out uint parallelism) ||
            !reader.TryReadLengthPrefixed(out byte[]? salt) ||
            !reader.TryReadByte(out byte cipherId) || cipherId != (byte)VaultCipherId.Aes256Gcm ||
            !reader.TryReadLengthPrefixed(out byte[]? nonce) ||
            !reader.TryReadLengthPrefixed(out byte[]? tag) ||
            !reader.TryReadUInt32(out uint ctLen) ||
            !reader.TryReadExact((int)ctLen, out byte[]? ciphertext) ||
            !reader.AtEnd)
        {
            return false;
        }

        if (memKib > int.MaxValue || iters > int.MaxValue || parallelism > int.MaxValue)
        {
            return false;
        }

        envelope = new VaultEnvelope(
            formatVersion,
            VaultKdfId.Argon2id,
            new Argon2idParameters((int)memKib, (int)iters, (int)parallelism),
            salt!,
            VaultCipherId.Aes256Gcm,
            nonce!,
            tag!,
            ciphertext!);
        return true;
    }

    private static void RequireByteLength(int length, string paramName)
    {
        if (length is < 1 or > byte.MaxValue)
        {
            throw new ArgumentException(
                $"Length must be between 1 and {byte.MaxValue} bytes.", paramName);
        }
    }

    /// <summary>
    /// Bounds-checked forward reader over the decoded envelope bytes. Every read
    /// validates remaining length, so a truncated or corrupted-length field
    /// fails closed.
    /// </summary>
    private ref struct EnvelopeReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _offset;

        public readonly bool AtEnd => _offset == _buffer.Length;

        public bool TryReadMagic()
        {
            if (_buffer.Length - _offset < Magic.Length)
            {
                return false;
            }

            if (!_buffer.Slice(_offset, Magic.Length).SequenceEqual(Magic))
            {
                return false;
            }

            _offset += Magic.Length;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            if (_buffer.Length - _offset < 1)
            {
                value = 0;
                return false;
            }

            value = _buffer[_offset];
            _offset += 1;
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (_buffer.Length - _offset < Uint32Width)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_offset, Uint32Width));
            _offset += Uint32Width;
            return true;
        }

        public bool TryReadExact(int length, out byte[]? value)
        {
            value = null;
            if (length < 0 || _buffer.Length - _offset < length)
            {
                return false;
            }

            value = _buffer.Slice(_offset, length).ToArray();
            _offset += length;
            return true;
        }

        public bool TryReadLengthPrefixed(out byte[]? value)
        {
            value = null;
            return TryReadByte(out byte length) && TryReadExact(length, out value);
        }
    }
}
