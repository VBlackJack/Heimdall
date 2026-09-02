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
using System.Text;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Security;

/// <summary>
/// Unified credential protection: DPAPI encryption with HMAC-SHA256 integrity.
/// Provides backward-compatible decryption for legacy DPAPI-only blobs (auto-upgrade).
/// </summary>
/// <remarks>
/// The HMAC key must be initialized via <see cref="Initialize"/> at startup before
/// any Protect/Unprotect calls. Legacy blobs (without HMAC) are accepted on
/// Unprotect and should be re-encrypted with HMAC on next save.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class CredentialProtector
{
    private static string? _hmacKeyRaw;

    /// <summary>
    /// The current vault DEK, borrowed from the unlock flow. When set (and not
    /// disposed), Protect emits version-2 vault secret blobs and Unprotect reads
    /// them; the holder is owned and disposed by the caller, not by this class.
    /// </summary>
    private static VaultDekHolder? _vaultDek;

    /// <summary>
    /// Whether a master-password vault is configured. Set at startup from
    /// <c>AppSettings.VaultEnabled</c> and by the lifecycle service. When the
    /// vault is enabled but locked, Protect fails closed instead of writing a
    /// weaker legacy blob (write-downgrade resistance).
    /// </summary>
    private static bool _vaultEnabled;

    /// <summary>
    /// Whether a usable vault DEK is currently set (the vault is unlocked).
    /// </summary>
    public static bool IsVaultUnlocked => _vaultDek is { IsDisposed: false };

    /// <summary>
    /// Whether a master-password vault is configured (independent of unlock state).
    /// </summary>
    public static bool IsVaultEnabled => _vaultEnabled;

    /// <summary>
    /// Set the "vault configured" flag. Set at startup from settings and toggled
    /// by the lifecycle service on enable/disable.
    /// </summary>
    /// <param name="enabled">True when a master-password vault is configured.</param>
    public static void SetVaultEnabled(bool enabled)
    {
        _vaultEnabled = enabled;
    }

    /// <summary>
    /// Install the vault DEK at unlock. Mirrors the <see cref="Initialize"/>
    /// static-slot pattern. The holder is borrowed, not owned: the caller keeps
    /// ownership and is responsible for disposing it on lock.
    /// </summary>
    /// <param name="dekHolder">The unlocked vault DEK holder.</param>
    public static void SetVaultKey(VaultDekHolder dekHolder)
    {
        ArgumentNullException.ThrowIfNull(dekHolder);
        _vaultDek = dekHolder;
    }

    /// <summary>
    /// Clear the borrowed vault DEK reference at lock. Does not dispose the
    /// holder (the caller owns its lifetime); after this call Protect reverts to
    /// the legacy path and Unprotect of a v2 blob fails closed.
    /// </summary>
    public static void ClearVaultKey()
    {
        _vaultDek = null;
    }

    /// <summary>
    /// Initialize the protector with the raw HMAC key (Base64).
    /// Must be called once at startup after loading settings.
    /// </summary>
    /// <param name="hmacKeyBase64">
    /// The raw (unprotected) HMAC key in Base64. If null, HMAC is disabled
    /// and the protector falls back to plain DPAPI.
    /// </param>
    public static void Initialize(string? hmacKeyBase64)
    {
        _hmacKeyRaw = hmacKeyBase64;
    }

    /// <summary>
    /// Encrypt a plaintext credential with DPAPI + HMAC integrity.
    /// Falls back to plain DPAPI if no HMAC key is configured.
    /// </summary>
    /// <param name="plainText">The plaintext credential to protect.</param>
    /// <returns>Protected string (HMAC-wrapped if key available, plain DPAPI otherwise).</returns>
    public static string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        // Vault mode: when a usable DEK is set, emit a version-2 secret blob.
        var dek = _vaultDek;
        if (dek is { IsDisposed: false })
        {
            byte[]? plainBytes = null;
            try
            {
                plainBytes = Encoding.UTF8.GetBytes(plainText);
                return VaultSecretBlob.Seal(dek.Key, plainBytes);
            }
            finally
            {
                if (plainBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
            }
        }

        // Write-downgrade resistance: if a vault IS configured but no usable DEK
        // is set (locked), refuse to write - NEVER fall back to the weaker legacy
        // format while the vault is enabled.
        if (_vaultEnabled)
        {
            throw new VaultLockedException();
        }

        // Legacy mode (no vault configured): unchanged DPAPI + HMAC output, so
        // installs without a master password stay byte-for-byte compatible.
        return ProtectLegacy(plainText);
    }

    /// <summary>
    /// Encrypt with the legacy DPAPI(+HMAC) format, ignoring the vault DEK and
    /// the enabled flag. Used only by the vault reverse-migration (disable) to
    /// return secrets to their pre-vault at-rest form; it is an explicit,
    /// non-silent operation, not a fallback path.
    /// </summary>
    /// <param name="plainText">The plaintext to protect.</param>
    /// <returns>A legacy HMAC-protected blob, or plain DPAPI when no HMAC key is set.</returns>
    internal static string ProtectLegacy(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        if (_hmacKeyRaw is not null)
        {
            return HmacIntegrity.ProtectWithHmac(plainText, _hmacKeyRaw);
        }

        return DpapiProvider.Protect(plainText);
    }

    /// <summary>
    /// Decrypt a protected credential. Supports both HMAC-protected and legacy
    /// DPAPI-only formats for backward compatibility.
    /// </summary>
    /// <param name="protectedValue">The protected string to decrypt.</param>
    /// <returns>Decrypted plaintext, or null on failure.</returns>
    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return null;

        // Version-2 vault secret blob: fail-closed and downgrade-resistant. A v2
        // blob is NEVER returned as null when locked and NEVER reinterpreted as a
        // legacy blob; a missing DEK is an explicit locked signal and a decrypt
        // failure propagates rather than degrading to null.
        if (VaultSecretBlob.IsSecretBlob(protectedValue))
        {
            var dek = _vaultDek;
            if (dek is not { IsDisposed: false })
            {
                throw new VaultLockedException();
            }

            byte[]? plainBytes = null;
            try
            {
                plainBytes = VaultSecretBlob.Open(dek.Key, protectedValue);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                if (plainBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
            }
        }

        // Legacy path (HMAC or plain DPAPI) - unchanged, preserves migration reads.
        try
        {
            if (HmacIntegrity.IsHmacProtected(protectedValue) && _hmacKeyRaw is not null)
            {
                return HmacIntegrity.UnprotectWithHmac(protectedValue, _hmacKeyRaw);
            }

            // Legacy DPAPI-only blob - decrypt without HMAC verification
            return DpapiProvider.Unprotect(protectedValue);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypt a protected credential directly to UTF-8 bytes, without ever
    /// materialising the plaintext as a managed string. Mirrors
    /// <see cref="Unprotect(string?)"/> but returns bytes; supports both HMAC-protected
    /// and legacy DPAPI-only formats.
    /// </summary>
    /// <param name="protectedValue">The protected string to decrypt.</param>
    /// <returns>
    /// UTF-8 plaintext bytes, or <c>null</c> on failure or empty input. The caller
    /// is responsible for zeroing the buffer with
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> once it is no
    /// longer needed.
    /// </returns>
    public static byte[]? UnprotectToBytes(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        // Version-2 vault secret blob: same fail-closed, downgrade-resistant
        // contract as Unprotect. Returns pinned plaintext bytes the caller zeroes.
        if (VaultSecretBlob.IsSecretBlob(protectedValue))
        {
            var dek = _vaultDek;
            if (dek is not { IsDisposed: false })
            {
                throw new VaultLockedException();
            }

            return VaultSecretBlob.Open(dek.Key, protectedValue);
        }

        try
        {
            if (HmacIntegrity.IsHmacProtected(protectedValue) && _hmacKeyRaw is not null)
            {
                return HmacIntegrity.UnprotectToBytesWithHmac(protectedValue, _hmacKeyRaw);
            }

            return DpapiProvider.UnprotectToBytes(protectedValue);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check whether a protected value uses the legacy DPAPI-only format
    /// (no HMAC integrity). Legacy blobs should be re-encrypted on next save.
    /// </summary>
    public static bool IsLegacyFormat(string? protectedValue)
    {
        return !string.IsNullOrWhiteSpace(protectedValue)
            && !HmacIntegrity.IsHmacProtected(protectedValue);
    }
}
