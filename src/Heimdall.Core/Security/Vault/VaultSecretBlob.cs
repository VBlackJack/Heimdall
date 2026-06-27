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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Self-describing, versioned codec for an individual secret encrypted directly
/// under the vault DEK with AES-256-GCM. Unlike <see cref="VaultEnvelope"/>
/// (which wraps the DEK and therefore carries the Argon2id KDF parameters), a
/// secret blob needs no KDF section: it carries only the cipher id, nonce, tag,
/// and ciphertext. A fixed domain-separation tag is bound into the AEAD AAD so a
/// secret blob can never be cross-used as a wrapped-DEK envelope.
/// </summary>
/// <remarks>
/// Distinct on the wire from <see cref="VaultEnvelope"/> ("HMV1") and from the
/// legacy <c>"|HMAC|"</c> / plain-DPAPI formats. No field is plaintext, so the
/// encoded bytes are not zeroed.
/// </remarks>
public sealed class VaultSecretBlob
{
    /// <summary>Magic prefix identifying a Heimdall vault secret blob ("HMS1").</summary>
    public static readonly byte[] Magic = "HMS1"u8.ToArray();

    /// <summary>The only format version currently understood.</summary>
    public const byte CurrentFormatVersion = 1;

    /// <summary>
    /// Fixed domain-separation tag bound into the AES-GCM AAD for every secret.
    /// Separates secret encryption from DEK wrapping and from any other AEAD use.
    /// </summary>
    private static readonly byte[] DomainSeparationAad = "heimdall.secret.v1"u8.ToArray();

    private const int Uint32Width = 4;

    /// <summary>The format version of the decoded blob.</summary>
    public byte FormatVersion { get; }

    /// <summary>The authenticated cipher used.</summary>
    public VaultCipherId CipherId { get; }

    /// <summary>The cipher nonce.</summary>
    public byte[] Nonce { get; }

    /// <summary>The authentication tag.</summary>
    public byte[] Tag { get; }

    /// <summary>The ciphertext.</summary>
    public byte[] Ciphertext { get; }

    private VaultSecretBlob(byte formatVersion, VaultCipherId cipherId, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        FormatVersion = formatVersion;
        CipherId = cipherId;
        Nonce = nonce;
        Tag = tag;
        Ciphertext = ciphertext;
    }

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under the vault <paramref name="dek"/>
    /// (AES-256-GCM with the fixed domain-separation AAD) and frame it as a
    /// Base64 secret blob.
    /// </summary>
    /// <param name="dek">The 256-bit data-encryption key.</param>
    /// <param name="plaintext">The secret to seal.</param>
    /// <returns>The Base64-encoded secret blob.</returns>
    public static string Seal(ReadOnlySpan<byte> dek, ReadOnlySpan<byte> plaintext)
    {
        var sealed_ = VaultCipher.Encrypt(dek, plaintext, DomainSeparationAad);
        return Encode(sealed_.Nonce, sealed_.Ciphertext, sealed_.Tag);
    }

    /// <summary>
    /// Decode and decrypt a Base64 secret blob under the vault <paramref name="dek"/>.
    /// Fails closed: a malformed frame or any authentication failure throws a
    /// <see cref="CryptographicException"/> without distinguishing detail.
    /// </summary>
    /// <param name="dek">The 256-bit data-encryption key.</param>
    /// <param name="encoded">The Base64-encoded secret blob.</param>
    /// <returns>
    /// A pinned plaintext buffer owned by the caller, who must zero it with
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> after use.
    /// </returns>
    /// <exception cref="CryptographicException">Thrown on a malformed frame or authentication failure.</exception>
    public static byte[] Open(ReadOnlySpan<byte> dek, string encoded)
    {
        if (!TryDecode(encoded, out var blob) || blob is null)
        {
            throw new CryptographicException("Vault secret blob could not be decrypted.");
        }

        return VaultCipher.Decrypt(dek, blob.Nonce, blob.Ciphertext, blob.Tag, DomainSeparationAad);
    }

    /// <summary>
    /// Frame an already-encrypted secret as a Base64 blob.
    /// </summary>
    /// <param name="nonce">The cipher nonce (1..255 bytes).</param>
    /// <param name="ciphertext">The ciphertext.</param>
    /// <param name="tag">The authentication tag (1..255 bytes).</param>
    /// <returns>The Base64-encoded blob.</returns>
    /// <exception cref="ArgumentException">Thrown when a length field exceeds its on-wire width.</exception>
    public static string Encode(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        RequireByteLength(nonce.Length, nameof(nonce));
        RequireByteLength(tag.Length, nameof(tag));

        int total =
            Magic.Length +
            1 +                       // format version
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
    /// Attempt to decode a Base64 secret blob. Rejects an unknown magic, format
    /// version, or cipher id, and any truncation, length mismatch, or trailing
    /// bytes, returning <c>false</c> without throwing and without emitting any
    /// secret-leaking diagnostic.
    /// </summary>
    /// <param name="encoded">The Base64-encoded blob.</param>
    /// <param name="blob">The decoded blob, or <c>null</c> on failure.</param>
    /// <returns><c>true</c> if the input is a well-formed, supported secret blob.</returns>
    public static bool TryDecode(string? encoded, out VaultSecretBlob? blob)
    {
        blob = null;

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

        var reader = new SecretBlobReader(buffer);

        if (!reader.TryReadMagic() ||
            !reader.TryReadByte(out byte formatVersion) || formatVersion != CurrentFormatVersion ||
            !reader.TryReadByte(out byte cipherId) || cipherId != (byte)VaultCipherId.Aes256Gcm ||
            !reader.TryReadLengthPrefixed(out byte[]? nonce) ||
            !reader.TryReadLengthPrefixed(out byte[]? tag) ||
            !reader.TryReadUInt32(out uint ctLen) ||
            !reader.TryReadExact((int)ctLen, out byte[]? ciphertext) ||
            !reader.AtEnd)
        {
            return false;
        }

        blob = new VaultSecretBlob(
            formatVersion,
            VaultCipherId.Aes256Gcm,
            nonce!,
            tag!,
            ciphertext!);
        return true;
    }

    /// <summary>
    /// Cheaply test whether <paramref name="encoded"/> is a vault secret blob by
    /// its magic prefix, without full validation. Used to route reads between
    /// the v2 and legacy paths. A legacy <c>"|HMAC|"</c> string is not valid
    /// Base64 and a plain-DPAPI blob does not carry the magic, so neither is
    /// misclassified.
    /// </summary>
    /// <param name="encoded">The candidate string.</param>
    /// <returns><c>true</c> if the decoded prefix matches the secret-blob magic.</returns>
    public static bool IsSecretBlob(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        try
        {
            var raw = Convert.FromBase64String(encoded);
            return raw.Length >= Magic.Length && raw.AsSpan(0, Magic.Length).SequenceEqual(Magic);
        }
        catch (FormatException)
        {
            return false;
        }
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
    /// Bounds-checked forward reader over the decoded blob bytes; every read
    /// validates remaining length, so a truncated or corrupted-length field
    /// fails closed.
    /// </summary>
    private ref struct SecretBlobReader(ReadOnlySpan<byte> buffer)
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
