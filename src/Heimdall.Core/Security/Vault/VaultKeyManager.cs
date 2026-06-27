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

using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Orchestrates the vault's DEK/KEK key hierarchy with two factors. A random
/// 256-bit data-encryption key (DEK) encrypts the individual secrets. The DEK is
/// wrapped by a key-encryption key (KEK) derived from the master password via
/// Argon2id, and the resulting envelope is additionally DPAPI-protected. Reading
/// a secret therefore needs both the master password (to derive the KEK and
/// unwrap the DEK) and the Windows user session (to peel the DPAPI layer).
/// </summary>
[SupportedOSPlatform("windows")]
public static class VaultKeyManager
{
    /// <summary>
    /// Fixed domain-separation tag bound into the AES-GCM AAD when wrapping the
    /// DEK. Distinct from the secret-blob tag so a wrapped-DEK envelope can never
    /// be cross-used as a secret.
    /// </summary>
    private static readonly byte[] DekWrapAad = "heimdall.dek-wrap.v1"u8.ToArray();

    /// <summary>
    /// Generate a fresh random 256-bit DEK in a pinned, zeroable holder.
    /// </summary>
    /// <returns>A new <see cref="VaultDekHolder"/>; the caller owns and disposes it.</returns>
    public static VaultDekHolder GenerateDek()
    {
        Span<byte> dek = stackalloc byte[VaultCipher.KeySizeBytes];
        try
        {
            RandomNumberGenerator.Fill(dek);
            return new VaultDekHolder(dek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Wrap a DEK under a master password and persist-ready DPAPI layer. Derives
    /// a KEK from <paramref name="masterPassword"/> with a fresh random salt,
    /// AES-256-GCM-encrypts the DEK under the KEK, packs the result into a
    /// <see cref="VaultEnvelope"/> (carrying the Argon2id parameters + salt), and
    /// DPAPI-protects the whole envelope string.
    /// </summary>
    /// <param name="masterPassword">The master-password bytes (UTF-8).</param>
    /// <param name="dek">The 256-bit DEK to wrap.</param>
    /// <param name="p">The Argon2id cost parameters to record and use.</param>
    /// <returns>The DPAPI-wrapped envelope string to persist.</returns>
    /// <exception cref="ArgumentException">Thrown when the DEK length is wrong.</exception>
    public static string WrapDek(ReadOnlySpan<byte> masterPassword, ReadOnlySpan<byte> dek, Argon2idParameters p)
    {
        if (dek.Length != VaultCipher.KeySizeBytes)
        {
            throw new ArgumentException(
                $"DEK must be {VaultCipher.KeySizeBytes} bytes.", nameof(dek));
        }

        byte[]? kek = null;

        try
        {
            var salt = VaultKdf.GenerateSalt();
            kek = VaultKdf.DeriveKey(masterPassword, salt, p);

            var wrapped = VaultCipher.Encrypt(kek, dek, DekWrapAad);
            var envelope = VaultEnvelope.Encode(p, salt, wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag);

            return DpapiProvider.Protect(envelope);
        }
        finally
        {
            if (kek is not null)
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
    }

    /// <summary>
    /// Unwrap a DEK from its stored blob: peel the DPAPI layer, decode the
    /// envelope, re-derive the KEK from <paramref name="masterPassword"/> using
    /// the envelope's stored parameters and salt, and AES-GCM-decrypt the DEK.
    /// </summary>
    /// <param name="masterPassword">The candidate master-password bytes (UTF-8).</param>
    /// <param name="storedWrappedDek">The DPAPI-wrapped envelope string from storage.</param>
    /// <returns>The recovered DEK in a pinned holder; the caller owns and disposes it.</returns>
    /// <exception cref="VaultUnlockException">
    /// Thrown on ANY failure — DPAPI failure, envelope decode failure, or AEAD
    /// tag mismatch — with a single generic message, so a wrong password and a
    /// corrupted vault are indistinguishable.
    /// </exception>
    public static VaultDekHolder UnwrapDek(ReadOnlySpan<byte> masterPassword, string storedWrappedDek)
    {
        ArgumentNullException.ThrowIfNull(storedWrappedDek);

        byte[]? kek = null;
        byte[]? dek = null;

        try
        {
            string envelopeString;
            try
            {
                envelopeString = DpapiProvider.Unprotect(storedWrappedDek);
            }
            catch
            {
                throw new VaultUnlockException();
            }

            if (!VaultEnvelope.TryDecode(envelopeString, out var envelope) || envelope is null)
            {
                throw new VaultUnlockException();
            }

            kek = VaultKdf.DeriveKey(masterPassword, envelope.Salt, envelope.KdfParameters);

            try
            {
                dek = VaultCipher.Decrypt(
                    kek, envelope.Nonce, envelope.Ciphertext, envelope.Tag, DekWrapAad);
            }
            catch
            {
                throw new VaultUnlockException();
            }

            return new VaultDekHolder(dek);
        }
        finally
        {
            if (kek is not null)
            {
                CryptographicOperations.ZeroMemory(kek);
            }

            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    /// <summary>
    /// Re-wrap the SAME DEK under a new master password. Unwraps with the old
    /// password (failing closed if it is wrong) and re-wraps with the new one
    /// using a fresh salt. Persistence of the returned blob is a later lot.
    /// </summary>
    /// <param name="oldPassword">The current master-password bytes.</param>
    /// <param name="newPassword">The replacement master-password bytes.</param>
    /// <param name="storedWrappedDek">The current DPAPI-wrapped envelope string.</param>
    /// <param name="p">The Argon2id cost parameters for the new wrap.</param>
    /// <returns>The new DPAPI-wrapped envelope string.</returns>
    /// <exception cref="VaultUnlockException">Thrown when the old password is wrong or the vault is corrupted.</exception>
    public static string ChangeMasterPassword(
        ReadOnlySpan<byte> oldPassword,
        ReadOnlySpan<byte> newPassword,
        string storedWrappedDek,
        Argon2idParameters p)
    {
        using var holder = UnwrapDek(oldPassword, storedWrappedDek);
        return WrapDek(newPassword, holder.Key, p);
    }
}
