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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Authenticated encryption for vault secrets using AES-256-GCM. Each call uses
/// a fresh 96-bit random nonce and produces a 128-bit authentication tag. The
/// caller-supplied additional authenticated data (AAD) binds the ciphertext to
/// its context (e.g. the envelope type and version).
/// </summary>
public static class VaultCipher
{
    /// <summary>Required key length in bytes (AES-256).</summary>
    public const int KeySizeBytes = 32;

    /// <summary>Nonce length in bytes (96-bit, the GCM-recommended size).</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>Authentication tag length in bytes (128-bit).</summary>
    public const int TagSizeBytes = 16;

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under <paramref name="key"/> with a
    /// fresh random nonce, authenticating <paramref name="aad"/>.
    /// </summary>
    /// <param name="key">A 256-bit key (typically the vault DEK). Owned by the caller.</param>
    /// <param name="plaintext">The secret to encrypt.</param>
    /// <param name="aad">Additional authenticated data (may be empty).</param>
    /// <returns>The generated nonce, the ciphertext, and the authentication tag.</returns>
    /// <exception cref="ArgumentException">Thrown when the key length is not 32 bytes.</exception>
    public static VaultCipherResult Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> aad)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        return EncryptWithNonce(key, nonce, plaintext, aad);
    }

    /// <summary>
    /// Encrypt with a caller-supplied nonce. Internal seam used by
    /// <see cref="Encrypt"/> and by known-answer tests; production callers must
    /// use <see cref="Encrypt"/> so the nonce is always freshly random.
    /// </summary>
    internal static VaultCipherResult EncryptWithNonce(
        ReadOnlySpan<byte> key,
        byte[] nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> aad)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(nonce);
        if (nonce.Length != NonceSizeBytes)
        {
            throw new ArgumentException(
                $"Nonce must be {NonceSizeBytes} bytes.", nameof(nonce));
        }

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        // AesGcm copies the key into its own native key schedule and clears it on
        // Dispose; we pass the caller's span directly and own no key buffer.
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        // Ciphertext and tag are not plaintext secrets, so they are returned
        // without zeroing; the nonce and tag are non-secret framing values.
        return new VaultCipherResult(nonce, ciphertext, tag);
    }

    /// <summary>
    /// Decrypt and authenticate a vault ciphertext. Throws on any tag mismatch
    /// (wrong key, tampered ciphertext/tag/nonce, or wrong AAD) without
    /// returning partial plaintext.
    /// </summary>
    /// <param name="key">The 256-bit key. Owned by the caller.</param>
    /// <param name="nonce">The 96-bit nonce used at encryption time.</param>
    /// <param name="ciphertext">The ciphertext to decrypt.</param>
    /// <param name="tag">The 128-bit authentication tag.</param>
    /// <param name="aad">The additional authenticated data used at encryption time.</param>
    /// <returns>
    /// A pinned byte array holding the recovered plaintext. The caller owns the
    /// buffer and must zero it with
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> after use.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when the key, nonce, or tag length is wrong.</exception>
    /// <exception cref="AuthenticationTagMismatchException">Thrown when authentication fails.</exception>
    public static byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> aad)
    {
        ValidateKey(key);
        if (nonce.Length != NonceSizeBytes)
        {
            throw new ArgumentException(
                $"Nonce must be {NonceSizeBytes} bytes.", nameof(nonce));
        }

        if (tag.Length != TagSizeBytes)
        {
            throw new ArgumentException(
                $"Tag must be {TagSizeBytes} bytes.", nameof(tag));
        }

        var plaintext = GC.AllocateArray<byte>(ciphertext.Length, pinned: true);

        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch
        {
            // GCM may write to the output span before detecting tag failure;
            // zero it so no partial plaintext escapes (CWE-316).
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException(
                $"Key must be {KeySizeBytes} bytes (AES-256).", nameof(key));
        }
    }
}

/// <summary>
/// The output of <see cref="VaultCipher.Encrypt"/>: the random nonce, the
/// ciphertext, and the authentication tag. None of these are plaintext secrets.
/// </summary>
/// <param name="Nonce">The 96-bit nonce.</param>
/// <param name="Ciphertext">The AES-256-GCM ciphertext.</param>
/// <param name="Tag">The 128-bit authentication tag.</param>
public readonly record struct VaultCipherResult(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
