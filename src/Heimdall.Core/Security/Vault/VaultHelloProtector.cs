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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Pure cryptographic helper for the Windows Hello DEK wrapper.
/// </summary>
public static class VaultHelloProtector
{
    /// <summary>Algorithm identifier bound into AAD and persisted envelopes.</summary>
    public const string AlgorithmId = "heimdall.vault-hello.sign-hkdf-aesgcm.v1";

    /// <summary>Random challenge length fed to KeyCredential.RequestSignAsync.</summary>
    public const int ChallengeSizeBytes = 32;

    /// <summary>HKDF salt length.</summary>
    public const int SaltSizeBytes = 32;

    private static readonly byte[] EnvelopeMagic = "HHV1"u8.ToArray();
    private static readonly byte[] HkdfInfo = "heimdall.vault-hello.kek.v1"u8.ToArray();
    private static readonly byte[] CredentialNamePrefixBytes = "Heimdall.VaultHello."u8.ToArray();
    private const byte CurrentEnvelopeVersion = 1;
    private const int UInt32Width = 4;

    /// <summary>Derive the Hello KEK from a deterministic Hello signature and random salt.</summary>
    public static byte[] DeriveHelloKek(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> salt)
    {
        if (signature.IsEmpty)
        {
            throw new ArgumentException("Signature must not be empty.", nameof(signature));
        }

        if (salt.Length != SaltSizeBytes)
        {
            throw new ArgumentException($"Salt must be {SaltSizeBytes} bytes.", nameof(salt));
        }

        Span<byte> prk = stackalloc byte[32];
        try
        {
            using (var hmac = new HMACSHA256(salt.ToArray()))
            {
                hmac.TryComputeHash(signature, prk, out _);
            }

            var okm = GC.AllocateArray<byte>(VaultCipher.KeySizeBytes, pinned: true);
            var block = new byte[32];
            var prkKey = prk.ToArray();
            try
            {
                using var hmac = new HMACSHA256(prkKey);
                hmac.TransformBlock(HkdfInfo, 0, HkdfInfo.Length, null, 0);
                hmac.TransformFinalBlock([1], 0, 1);
                Buffer.BlockCopy(hmac.Hash!, 0, block, 0, block.Length);
                Buffer.BlockCopy(block, 0, okm, 0, okm.Length);
                return okm;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(block);
                CryptographicOperations.ZeroMemory(prkKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    /// <summary>Build deterministic AAD for the Hello wrapper.</summary>
    public static byte[] BuildAad(VaultHelloBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.VaultId) || string.IsNullOrWhiteSpace(binding.PublicKeyHash))
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }

        if (binding.Challenge.Length != ChallengeSizeBytes || binding.Salt.Length != SaltSizeBytes)
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }

        using var stream = new MemoryStream();
        WriteField(stream, Encoding.UTF8.GetBytes(AlgorithmId));
        WriteField(stream, Encoding.UTF8.GetBytes(binding.VaultId));
        WriteField(stream, Encoding.UTF8.GetBytes(binding.PublicKeyHash));
        WriteField(stream, binding.Challenge);
        WriteField(stream, binding.Salt);
        return stream.ToArray();
    }

    /// <summary>Encrypt a DEK into a Base64 Hello envelope.</summary>
    public static string WrapDek(ReadOnlySpan<byte> dek, ReadOnlySpan<byte> helloKek, VaultHelloBinding binding)
    {
        if (dek.Length != VaultCipher.KeySizeBytes)
        {
            throw new ArgumentException($"DEK must be {VaultCipher.KeySizeBytes} bytes.", nameof(dek));
        }

        var aad = BuildAad(binding);
        var wrapped = VaultCipher.Encrypt(helloKek, dek, aad);
        return EncodeEnvelope(wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag);
    }

    /// <summary>Decrypt a Base64 Hello envelope into a pinned DEK holder.</summary>
    public static VaultDekHolder UnwrapDek(string wrappedDek, ReadOnlySpan<byte> helloKek, VaultHelloBinding binding)
    {
        if (!TryDecodeEnvelope(wrappedDek, out var envelope) || envelope is null)
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }

        byte[]? dek = null;
        try
        {
            var aad = BuildAad(binding);
            dek = VaultCipher.Decrypt(helloKek, envelope.Nonce, envelope.Ciphertext, envelope.Tag, aad);
            return new VaultDekHolder(dek);
        }
        catch (VaultHelloException)
        {
            throw;
        }
        catch
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }
        finally
        {
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    /// <summary>Create the deterministic KeyCredential name for a vault id.</summary>
    public static string CreateCredentialName(string vaultId)
    {
        if (string.IsNullOrWhiteSpace(vaultId))
        {
            throw new ArgumentException("Vault id is required.", nameof(vaultId));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(vaultId));
        return Encoding.UTF8.GetString(CredentialNamePrefixBytes) + Convert.ToHexString(hash);
    }

    private static string EncodeEnvelope(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        RequireByteLength(nonce.Length, nameof(nonce));
        RequireByteLength(tag.Length, nameof(tag));

        var total =
            EnvelopeMagic.Length +
            1 +
            1 + nonce.Length +
            1 + tag.Length +
            UInt32Width + ciphertext.Length;

        var buffer = new byte[total];
        var span = buffer.AsSpan();
        var offset = 0;

        EnvelopeMagic.CopyTo(span[offset..]);
        offset += EnvelopeMagic.Length;
        span[offset++] = CurrentEnvelopeVersion;
        span[offset++] = (byte)nonce.Length;
        nonce.CopyTo(span[offset..]);
        offset += nonce.Length;
        span[offset++] = (byte)tag.Length;
        tag.CopyTo(span[offset..]);
        offset += tag.Length;
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)ciphertext.Length);
        offset += UInt32Width;
        ciphertext.CopyTo(span[offset..]);
        return Convert.ToBase64String(buffer);
    }

    private static bool TryDecodeEnvelope(string? encoded, out VaultHelloEnvelope? envelope)
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
            !reader.TryReadByte(out var version) || version != CurrentEnvelopeVersion ||
            !reader.TryReadLengthPrefixed(out var nonce) ||
            !reader.TryReadLengthPrefixed(out var tag) ||
            !reader.TryReadUInt32(out var ctLen) ||
            !reader.TryReadExact((int)ctLen, out var ciphertext) ||
            !reader.AtEnd)
        {
            return false;
        }

        envelope = new VaultHelloEnvelope(nonce!, tag!, ciphertext!);
        return true;
    }

    private static void WriteField(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[UInt32Width];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static void RequireByteLength(int length, string paramName)
    {
        if (length is < 1 or > byte.MaxValue)
        {
            throw new ArgumentException($"Length must be between 1 and {byte.MaxValue} bytes.", paramName);
        }
    }

    private sealed record VaultHelloEnvelope(byte[] Nonce, byte[] Tag, byte[] Ciphertext);

    private ref struct EnvelopeReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _offset;

        public readonly bool AtEnd => _offset == _buffer.Length;

        public bool TryReadMagic()
        {
            if (_buffer.Length - _offset < EnvelopeMagic.Length)
            {
                return false;
            }

            if (!_buffer.Slice(_offset, EnvelopeMagic.Length).SequenceEqual(EnvelopeMagic))
            {
                return false;
            }

            _offset += EnvelopeMagic.Length;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            if (_buffer.Length - _offset < 1)
            {
                value = 0;
                return false;
            }

            value = _buffer[_offset++];
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (_buffer.Length - _offset < UInt32Width)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_offset, UInt32Width));
            _offset += UInt32Width;
            return true;
        }

        public bool TryReadLengthPrefixed(out byte[]? value)
        {
            value = null;
            return TryReadByte(out var length) && TryReadExact(length, out value);
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
    }
}
