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
using Konscious.Security.Cryptography;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Derives the vault key-encryption key (KEK) from a master password using
/// Argon2id (RFC 9106), the memory-hard password-based KDF. Argon2id is the
/// hybrid variant resistant to both side-channel and GPU/ASIC attacks; the
/// data-independent and data-dependent variants (Argon2i / Argon2d) are
/// intentionally not exposed.
/// </summary>
public static class VaultKdf
{
    /// <summary>Minimum salt length accepted, per the Argon2 specification.</summary>
    public const int MinSaltLengthBytes = 8;

    /// <summary>Default salt length produced by <see cref="GenerateSalt"/> (128-bit).</summary>
    public const int DefaultSaltLengthBytes = 16;

    /// <summary>Default derived-key length (256-bit, for AES-256-GCM).</summary>
    public const int DefaultKeyLengthBytes = 32;

    /// <summary>
    /// Derive a fixed-length key from <paramref name="password"/> and
    /// <paramref name="salt"/> using Argon2id with the supplied cost parameters.
    /// </summary>
    /// <param name="password">The master-password bytes (UTF-8). Not retained.</param>
    /// <param name="salt">The per-vault salt (at least <see cref="MinSaltLengthBytes"/> bytes).</param>
    /// <param name="p">Argon2id cost parameters.</param>
    /// <param name="outLen">Derived key length in bytes (default 32).</param>
    /// <returns>
    /// A pinned byte array holding the derived key. The caller owns the buffer
    /// and is responsible for zeroing it with
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> once it is no
    /// longer needed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the salt is too short, the parameters are out of range, or
    /// <paramref name="outLen"/> is not positive.
    /// </exception>
    public static byte[] DeriveKey(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        Argon2idParameters p,
        int outLen = DefaultKeyLengthBytes)
    {
        if (salt.Length < MinSaltLengthBytes)
        {
            throw new ArgumentException(
                $"Salt must be at least {MinSaltLengthBytes} bytes.", nameof(salt));
        }

        if (!p.IsValid)
        {
            throw new ArgumentException("Argon2id parameters are out of range.", nameof(p));
        }

        if (outLen <= 0)
        {
            throw new ArgumentException("Output length must be positive.", nameof(outLen));
        }

        // Konscious exposes a byte[]-only surface (constructor + Salt property),
        // so the password and salt spans must be materialised into managed
        // arrays. These copies are unavoidable; they are pinned so the GC cannot
        // relocate (and thereby duplicate) the bytes, and zeroed in finally.
        byte[]? passwordCopy = null;
        byte[]? saltCopy = null;
        byte[]? rawKey = null;

        try
        {
            passwordCopy = GC.AllocateArray<byte>(password.Length, pinned: true);
            password.CopyTo(passwordCopy);

            saltCopy = GC.AllocateArray<byte>(salt.Length, pinned: true);
            salt.CopyTo(saltCopy);

            using var argon2 = new Argon2id(passwordCopy)
            {
                Salt = saltCopy,
                MemorySize = p.MemoryKib,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };

            // Konscious returns its own managed (non-pinned) array. We copy the
            // result into a pinned buffer and zero the Konscious-owned array.
            // Konscious zeroes its internal working memory on Dispose.
            rawKey = argon2.GetBytes(outLen);

            var derived = GC.AllocateArray<byte>(outLen, pinned: true);
            rawKey.AsSpan().CopyTo(derived);
            return derived;
        }
        finally
        {
            if (rawKey is not null) CryptographicOperations.ZeroMemory(rawKey);
            if (passwordCopy is not null) CryptographicOperations.ZeroMemory(passwordCopy);
            if (saltCopy is not null) CryptographicOperations.ZeroMemory(saltCopy);
        }
    }

    /// <summary>
    /// Generate a cryptographically random salt. The salt is not secret; it is
    /// stored alongside the ciphertext in the vault envelope.
    /// </summary>
    /// <param name="len">Salt length in bytes (default 16, minimum 8).</param>
    /// <returns>A new random salt.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="len"/> is below the minimum.</exception>
    public static byte[] GenerateSalt(int len = DefaultSaltLengthBytes)
    {
        if (len < MinSaltLengthBytes)
        {
            throw new ArgumentException(
                $"Salt length must be at least {MinSaltLengthBytes} bytes.", nameof(len));
        }

        var salt = new byte[len];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}
