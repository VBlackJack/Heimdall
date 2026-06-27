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

public sealed class VaultCipherTests
{
    // AES-256-GCM known-answer vector: Test Case 16 from McGrew & Viega,
    // "The Galois/Counter Mode of Operation (GCM)" (the canonical AES-GCM
    // reference, also reproduced in NIST GCM validation material). 96-bit IV,
    // 128-bit tag, with associated data.
    private static readonly byte[] KatKey =
        Convert.FromHexString("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308");

    private static readonly byte[] KatNonce =
        Convert.FromHexString("cafebabefacedbaddecaf888");

    private static readonly byte[] KatPlaintext = Convert.FromHexString(
        "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a72" +
        "1c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39");

    private static readonly byte[] KatAad =
        Convert.FromHexString("feedfacedeadbeeffeedfacedeadbeefabaddad2");

    private static readonly byte[] KatCiphertext = Convert.FromHexString(
        "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa" +
        "8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662");

    private static readonly byte[] KatTag =
        Convert.FromHexString("76fc6ece0f4e1768cddf8853bb2d551b");

    [Fact]
    public void Decrypt_NistGcmVector_RecoversPlaintext()
    {
        var plaintext = VaultCipher.Decrypt(KatKey, KatNonce, KatCiphertext, KatTag, KatAad);

        Assert.Equal(KatPlaintext, plaintext);
    }

    [Fact]
    public void EncryptWithNonce_NistGcmVector_ProducesExpectedCiphertextAndTag()
    {
        var result = VaultCipher.EncryptWithNonce(KatKey, KatNonce, KatPlaintext, KatAad);

        Assert.Equal(KatCiphertext, result.Ciphertext);
        Assert.Equal(KatTag, result.Tag);
        Assert.Equal(KatNonce, result.Nonce);
    }

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(VaultCipher.KeySizeBytes);
        var plaintext = Encoding.UTF8.GetBytes("super-secret-vault-payload");
        var aad = "HMV1"u8.ToArray();

        var result = VaultCipher.Encrypt(key, plaintext, aad);
        var recovered = VaultCipher.Decrypt(key, result.Nonce, result.Ciphertext, result.Tag, aad);

        Assert.Equal(plaintext, recovered);
        Assert.Equal(VaultCipher.NonceSizeBytes, result.Nonce.Length);
        Assert.Equal(VaultCipher.TagSizeBytes, result.Tag.Length);
    }

    [Fact]
    public void Encrypt_ProducesDistinctNoncesPerCall()
    {
        var key = RandomNumberGenerator.GetBytes(VaultCipher.KeySizeBytes);
        var plaintext = Encoding.UTF8.GetBytes("payload");

        var first = VaultCipher.Encrypt(key, plaintext, ReadOnlySpan<byte>.Empty);
        var second = VaultCipher.Encrypt(key, plaintext, ReadOnlySpan<byte>.Empty);

        Assert.NotEqual(first.Nonce, second.Nonce);
    }

    [Fact]
    public void Decrypt_FlippedCiphertextByte_Throws()
    {
        var tampered = (byte[])KatCiphertext.Clone();
        tampered[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            VaultCipher.Decrypt(KatKey, KatNonce, tampered, KatTag, KatAad));
    }

    [Fact]
    public void Decrypt_FlippedTagByte_Throws()
    {
        var tampered = (byte[])KatTag.Clone();
        tampered[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            VaultCipher.Decrypt(KatKey, KatNonce, KatCiphertext, tampered, KatAad));
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var wrongKey = (byte[])KatKey.Clone();
        wrongKey[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            VaultCipher.Decrypt(wrongKey, KatNonce, KatCiphertext, KatTag, KatAad));
    }

    [Fact]
    public void Decrypt_WrongAad_Throws()
    {
        var wrongAad = (byte[])KatAad.Clone();
        wrongAad[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            VaultCipher.Decrypt(KatKey, KatNonce, KatCiphertext, KatTag, wrongAad));
    }

    [Fact]
    public void Encrypt_WrongKeyLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VaultCipher.Encrypt(new byte[16], KatPlaintext, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Decrypt_WrongNonceLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VaultCipher.Decrypt(KatKey, new byte[8], KatCiphertext, KatTag, KatAad));
    }
}
