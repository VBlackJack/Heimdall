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

using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultSecretBlobTests
{
    private static byte[] NewDek() => RandomNumberGenerator.GetBytes(VaultCipher.KeySizeBytes);

    private static string FlipDecodedByte(string base64, int offset)
    {
        var raw = Convert.FromBase64String(base64);
        raw[offset] ^= 0xFF;
        return Convert.ToBase64String(raw);
    }

    [Fact]
    public void Seal_ThenOpen_RoundTrips()
    {
        var dek = NewDek();
        var plaintext = Encoding.UTF8.GetBytes("vault-secret-payload-éŋ");

        var blob = VaultSecretBlob.Seal(dek, plaintext);
        var recovered = VaultSecretBlob.Open(dek, blob);

        Assert.Equal(plaintext, recovered);
        Assert.True(VaultSecretBlob.IsSecretBlob(blob));
    }

    [Fact]
    public void Seal_SamePlaintext_ProducesDistinctBlobs()
    {
        var dek = NewDek();
        var plaintext = Encoding.UTF8.GetBytes("payload");

        var first = VaultSecretBlob.Seal(dek, plaintext);
        var second = VaultSecretBlob.Seal(dek, plaintext);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Open_WrongDek_Throws()
    {
        var blob = VaultSecretBlob.Seal(NewDek(), Encoding.UTF8.GetBytes("secret"));

        Assert.ThrowsAny<CryptographicException>(() => VaultSecretBlob.Open(NewDek(), blob));
    }

    [Fact]
    public void Open_TamperedCiphertextByte_Throws()
    {
        var dek = NewDek();
        var blob = VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("secret"));

        // Layout: magic(4)+ver(1)+cipherId(1)+nonceLen(1)+nonce(12)+tagLen(1)+tag(16)+ctLen(4) = 40.
        var tampered = FlipDecodedByte(blob, 40);

        Assert.ThrowsAny<CryptographicException>(() => VaultSecretBlob.Open(dek, tampered));
    }

    [Fact]
    public void Open_TamperedTagByte_Throws()
    {
        var dek = NewDek();
        var blob = VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("secret"));

        // Tag starts at magic(4)+ver(1)+cipherId(1)+nonceLen(1)+nonce(12)+tagLen(1) = 20.
        var tampered = FlipDecodedByte(blob, 20);

        Assert.ThrowsAny<CryptographicException>(() => VaultSecretBlob.Open(dek, tampered));
    }

    [Fact]
    public void Open_MalformedFrame_Throws()
    {
        Assert.ThrowsAny<CryptographicException>(() =>
            VaultSecretBlob.Open(NewDek(), Convert.ToBase64String([1, 2, 3, 4])));
    }

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        byte[] nonce = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        byte[] tag = [21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36];
        byte[] ciphertext = [100, 101, 102, 103];

        var encoded = VaultSecretBlob.Encode(nonce, ciphertext, tag);
        var ok = VaultSecretBlob.TryDecode(encoded, out var blob);

        Assert.True(ok);
        Assert.NotNull(blob);
        Assert.Equal(VaultSecretBlob.CurrentFormatVersion, blob!.FormatVersion);
        Assert.Equal(VaultCipherId.Aes256Gcm, blob.CipherId);
        Assert.Equal(nonce, blob.Nonce);
        Assert.Equal(tag, blob.Tag);
        Assert.Equal(ciphertext, blob.Ciphertext);
    }

    [Fact]
    public void TryDecode_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(VaultSecretBlob.TryDecode(null, out _));
        Assert.False(VaultSecretBlob.TryDecode(string.Empty, out _));
    }

    [Fact]
    public void TryDecode_NonBase64_ReturnsFalse()
    {
        Assert.False(VaultSecretBlob.TryDecode("not base64 !!!", out var blob));
        Assert.Null(blob);
    }

    [Fact]
    public void TryDecode_UnknownMagic_ReturnsFalse()
    {
        var dek = NewDek();
        var raw = Convert.FromBase64String(VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("x")));
        raw[0] = (byte)'X';

        Assert.False(VaultSecretBlob.TryDecode(Convert.ToBase64String(raw), out var blob));
        Assert.Null(blob);
    }

    [Fact]
    public void TryDecode_UnknownVersion_ReturnsFalse()
    {
        var dek = NewDek();
        var raw = Convert.FromBase64String(VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("x")));
        raw[VaultSecretBlob.Magic.Length] = 0x02; // version byte after magic

        Assert.False(VaultSecretBlob.TryDecode(Convert.ToBase64String(raw), out var blob));
        Assert.Null(blob);
    }

    [Fact]
    public void TryDecode_FlippedCipherId_ReturnsFalse()
    {
        var dek = NewDek();
        var raw = Convert.FromBase64String(VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("x")));
        raw[VaultSecretBlob.Magic.Length + 1] = 0x09; // cipher id after magic + version

        Assert.False(VaultSecretBlob.TryDecode(Convert.ToBase64String(raw), out var blob));
        Assert.Null(blob);
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse()
    {
        var dek = NewDek();
        var raw = Convert.FromBase64String(VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("secret")));
        var truncated = raw[..(raw.Length - 2)];

        Assert.False(VaultSecretBlob.TryDecode(Convert.ToBase64String(truncated), out var blob));
        Assert.Null(blob);
    }

    [Fact]
    public void TryDecode_TrailingBytes_ReturnsFalse()
    {
        var dek = NewDek();
        var raw = Convert.FromBase64String(VaultSecretBlob.Seal(dek, Encoding.UTF8.GetBytes("secret")));
        var extended = new byte[raw.Length + 1];
        raw.CopyTo(extended, 0);

        Assert.False(VaultSecretBlob.TryDecode(Convert.ToBase64String(extended), out var blob));
        Assert.Null(blob);
    }

    [Fact]
    public void IsSecretBlob_SecretBlob_ReturnsTrue()
    {
        var blob = VaultSecretBlob.Seal(NewDek(), Encoding.UTF8.GetBytes("x"));

        Assert.True(VaultSecretBlob.IsSecretBlob(blob));
    }

    [Fact]
    public void IsSecretBlob_VaultEnvelope_ReturnsFalse()
    {
        // An HMV1 DEK-wrapping envelope must not be misread as a secret blob.
        var envelope = VaultEnvelope.Encode(
            Argon2idParameters.Recommended,
            new byte[16],
            new byte[12],
            new byte[8],
            new byte[16]);

        Assert.False(VaultSecretBlob.IsSecretBlob(envelope));
    }

    [Fact]
    public void IsSecretBlob_LegacyHmacString_ReturnsFalse()
    {
        Assert.False(VaultSecretBlob.IsSecretBlob("c29tZS1kYXRh|HMAC|c29tZS1obWFj"));
    }

    [Fact]
    public void IsSecretBlob_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(VaultSecretBlob.IsSecretBlob(null));
        Assert.False(VaultSecretBlob.IsSecretBlob(string.Empty));
    }
}
