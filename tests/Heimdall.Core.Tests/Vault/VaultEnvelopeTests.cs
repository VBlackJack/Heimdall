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

using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultEnvelopeTests
{
    private static readonly Argon2idParameters Params = new(MemoryKib: 65536, Iterations: 3, Parallelism: 1);
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly byte[] Nonce = [21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32];
    private static readonly byte[] Tag = [41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56];
    private static readonly byte[] Ciphertext = [100, 101, 102, 103, 104, 105, 106, 107];

    private static string EncodeSample() =>
        VaultEnvelope.Encode(Params, Salt, Nonce, Ciphertext, Tag);

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var encoded = EncodeSample();

        var ok = VaultEnvelope.TryDecode(encoded, out var envelope);

        Assert.True(ok);
        Assert.NotNull(envelope);
        Assert.Equal(VaultEnvelope.CurrentFormatVersion, envelope!.FormatVersion);
        Assert.Equal(VaultKdfId.Argon2id, envelope.KdfId);
        Assert.Equal(VaultCipherId.Aes256Gcm, envelope.CipherId);
        Assert.Equal(Params, envelope.KdfParameters);
        Assert.Equal(Salt, envelope.Salt);
        Assert.Equal(Nonce, envelope.Nonce);
        Assert.Equal(Tag, envelope.Tag);
        Assert.Equal(Ciphertext, envelope.Ciphertext);
    }

    [Fact]
    public void TryDecode_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(VaultEnvelope.TryDecode(null, out _));
        Assert.False(VaultEnvelope.TryDecode(string.Empty, out _));
    }

    [Fact]
    public void TryDecode_NonBase64_ReturnsFalse()
    {
        Assert.False(VaultEnvelope.TryDecode("not valid base64 !!!", out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_UnknownMagic_ReturnsFalse()
    {
        var raw = Convert.FromBase64String(EncodeSample());
        raw[0] = (byte)'X'; // corrupt the magic prefix

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(raw), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_UnknownFormatVersion_ReturnsFalse()
    {
        var raw = Convert.FromBase64String(EncodeSample());
        raw[VaultEnvelope.Magic.Length] = 0x02; // version byte follows the magic

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(raw), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_UnknownKdfId_ReturnsFalse()
    {
        var raw = Convert.FromBase64String(EncodeSample());
        raw[VaultEnvelope.Magic.Length + 1] = 0x09; // kdf id follows magic + version

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(raw), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_FlippedCipherId_ReturnsFalse()
    {
        // Locate the cipher id: magic + version + kdfId + 3*uint32 params
        // + salt-len byte + salt.
        int cipherIdOffset = VaultEnvelope.Magic.Length + 1 + 1 + (4 * 3) + 1 + Salt.Length;
        var raw = Convert.FromBase64String(EncodeSample());
        raw[cipherIdOffset] = 0x07;

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(raw), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_TruncatedBuffer_ReturnsFalse()
    {
        var raw = Convert.FromBase64String(EncodeSample());
        var truncated = raw[..(raw.Length - 3)]; // drop trailing ciphertext bytes

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(truncated), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_TrailingBytes_ReturnsFalse()
    {
        var raw = Convert.FromBase64String(EncodeSample());
        var extended = new byte[raw.Length + 1];
        raw.CopyTo(extended, 0); // an extra trailing byte must be rejected

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(extended), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryDecode_CorruptedCiphertextLength_ReturnsFalse()
    {
        // Inflate the ct-len field so it overruns the actual buffer.
        int ctLenOffset = VaultEnvelope.Magic.Length + 1 + 1 + (4 * 3)
            + 1 + Salt.Length // salt
            + 1               // cipher id
            + 1 + Nonce.Length // nonce
            + 1 + Tag.Length;  // tag
        var raw = Convert.FromBase64String(EncodeSample());
        raw[ctLenOffset] = 0x7F; // high byte of a now-huge big-endian length

        Assert.False(VaultEnvelope.TryDecode(Convert.ToBase64String(raw), out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void Encode_EmptySalt_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VaultEnvelope.Encode(Params, ReadOnlySpan<byte>.Empty, Nonce, Ciphertext, Tag));
    }

    [Fact]
    public void Encode_OversizedNonce_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VaultEnvelope.Encode(Params, Salt, new byte[256], Ciphertext, Tag));
    }
}
