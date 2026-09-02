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

namespace Heimdall.Core.Security;

/// <summary>
/// Provides HMAC-SHA256 integrity protection for DPAPI-encrypted data.
/// Protects against tampering of encrypted credential blobs (CWE-565 mitigation).
/// </summary>
[SupportedOSPlatform("windows")]
public static class HmacIntegrity
{
    /// <summary>
    /// HMAC key length in bytes (256-bit key).
    /// </summary>
    private const int HmacKeyLengthBytes = 32;

    /// <summary>
    /// Separator between the DPAPI-encrypted Base64 data and the HMAC signature.
    /// </summary>
    private const string HmacSeparator = "|HMAC|";

    /// <summary>
    /// Default key rotation period in days.
    /// </summary>
    public const int DefaultRotationDays = 90;

    /// <summary>
    /// Generate a cryptographically random 256-bit HMAC key, DPAPI-protect it,
    /// and return the DPAPI-encrypted key as a Base64 string.
    /// </summary>
    /// <returns>Base64-encoded DPAPI-protected HMAC key.</returns>
    public static string GenerateKey()
    {
        var keyBytes = new byte[HmacKeyLengthBytes];

        try
        {
            RandomNumberGenerator.Fill(keyBytes);
            var rawBase64 = Convert.ToBase64String(keyBytes);
            return DpapiProvider.Protect(rawBase64);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Generate a raw (unprotected) 256-bit HMAC key as a Base64 string.
    /// Used internally and for scenarios where DPAPI wrapping is handled externally.
    /// </summary>
    /// <returns>Base64-encoded raw HMAC key.</returns>
    public static string GenerateRawKey()
    {
        var keyBytes = new byte[HmacKeyLengthBytes];

        try
        {
            RandomNumberGenerator.Fill(keyBytes);
            return Convert.ToBase64String(keyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Encrypt a plaintext string with DPAPI and attach an HMAC-SHA256 signature.
    /// </summary>
    /// <param name="plainText">The plaintext to protect.</param>
    /// <param name="hmacKeyBase64">Base64-encoded raw HMAC key (256-bit).</param>
    /// <returns>
    /// A string in the format <c>base64_encrypted|HMAC|base64_hmac</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the HMAC key length is not 32 bytes.</exception>
    public static string ProtectWithHmac(string plainText, string hmacKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentNullException.ThrowIfNull(hmacKeyBase64);

        byte[]? plainBytes = null;
        byte[]? hmacKeyBytes = null;
        byte[]? encrypted = null;
        byte[]? hmacHash = null;

        try
        {
            hmacKeyBytes = Convert.FromBase64String(hmacKeyBase64);
            ValidateKeyLength(hmacKeyBytes.Length);

            // DPAPI encrypt
            plainBytes = Encoding.UTF8.GetBytes(plainText);
            encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            string encryptedBase64 = Convert.ToBase64String(encrypted);

            // HMAC-SHA256 over the raw encrypted bytes
            using (HMACSHA256 hmac = new HMACSHA256(hmacKeyBytes))
            {
                hmacHash = hmac.ComputeHash(encrypted);
            }
            string hmacBase64 = Convert.ToBase64String(hmacHash);

            return $"{encryptedBase64}{HmacSeparator}{hmacBase64}";
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            if (hmacHash is not null) CryptographicOperations.ZeroMemory(hmacHash);
            if (hmacKeyBytes is not null) CryptographicOperations.ZeroMemory(hmacKeyBytes);
        }
    }

    /// <summary>
    /// Verify the HMAC signature and decrypt the protected data.
    /// If integrity verification fails, an exception is thrown.
    /// </summary>
    /// <param name="protectedJson">
    /// The protected string in format <c>base64_encrypted|HMAC|base64_hmac</c>.
    /// </param>
    /// <param name="hmacKeyBase64">Base64-encoded raw HMAC key (256-bit).</param>
    /// <returns>Decrypted plaintext string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <exception cref="CryptographicException">Thrown when HMAC verification fails (data tampered).</exception>
    /// <exception cref="FormatException">Thrown when the input format is invalid.</exception>
    public static string UnprotectWithHmac(string protectedJson, string hmacKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(protectedJson);
        ArgumentNullException.ThrowIfNull(hmacKeyBase64);

        byte[]? hmacKeyBytes = null;
        byte[]? encrypted = null;
        byte[]? storedHmac = null;
        byte[]? computedHmac = null;
        byte[]? decrypted = null;

        try
        {
            string[] parts = protectedJson.Split(HmacSeparator);
            if (parts.Length != 2)
            {
                throw new FormatException(
                    "Invalid HMAC-protected format: expected 'encrypted|HMAC|hmac_b64' separator.");
            }

            encrypted = Convert.FromBase64String(parts[0]);
            storedHmac = Convert.FromBase64String(parts[1]);

            hmacKeyBytes = Convert.FromBase64String(hmacKeyBase64);
            ValidateKeyLength(hmacKeyBytes.Length);

            // Compute HMAC and compare with constant-time comparison
            using (HMACSHA256 hmac = new HMACSHA256(hmacKeyBytes))
            {
                computedHmac = hmac.ComputeHash(encrypted);
            }

            if (!CryptographicOperations.FixedTimeEquals(computedHmac, storedHmac))
            {
                throw new CryptographicException(
                    "HMAC verification failed: data integrity compromised.");
            }

            // HMAC verified - decrypt with DPAPI
            decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (storedHmac is not null) CryptographicOperations.ZeroMemory(storedHmac);
            if (computedHmac is not null) CryptographicOperations.ZeroMemory(computedHmac);
            if (hmacKeyBytes is not null) CryptographicOperations.ZeroMemory(hmacKeyBytes);
            if (decrypted is not null) CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    /// <summary>
    /// Verify the HMAC signature and decrypt the protected data directly to
    /// UTF-8 bytes, without ever materialising the plaintext as a managed string.
    /// </summary>
    /// <param name="protectedJson">
    /// The protected string in format <c>base64_encrypted|HMAC|base64_hmac</c>.
    /// </param>
    /// <param name="hmacKeyBase64">Base64-encoded raw HMAC key (256-bit).</param>
    /// <returns>
    /// A new byte array containing UTF-8 plaintext. The caller is responsible
    /// for zeroing the buffer with <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/>
    /// once it is no longer needed.
    /// </returns>
    /// <exception cref="CryptographicException">Thrown when HMAC verification fails.</exception>
    /// <exception cref="FormatException">Thrown when the input format is invalid.</exception>
    public static byte[] UnprotectToBytesWithHmac(string protectedJson, string hmacKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(protectedJson);
        ArgumentNullException.ThrowIfNull(hmacKeyBase64);

        byte[]? hmacKeyBytes = null;
        byte[]? encrypted = null;
        byte[]? storedHmac = null;
        byte[]? computedHmac = null;
        byte[]? decrypted = null;

        try
        {
            string[] parts = protectedJson.Split(HmacSeparator);
            if (parts.Length != 2)
            {
                throw new FormatException(
                    "Invalid HMAC-protected format: expected 'encrypted|HMAC|hmac_b64' separator.");
            }

            encrypted = Convert.FromBase64String(parts[0]);
            storedHmac = Convert.FromBase64String(parts[1]);

            hmacKeyBytes = Convert.FromBase64String(hmacKeyBase64);
            ValidateKeyLength(hmacKeyBytes.Length);

            using (HMACSHA256 hmac = new HMACSHA256(hmacKeyBytes))
            {
                computedHmac = hmac.ComputeHash(encrypted);
            }

            if (!CryptographicOperations.FixedTimeEquals(computedHmac, storedHmac))
            {
                throw new CryptographicException(
                    "HMAC verification failed: data integrity compromised.");
            }

            decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            byte[] result = new byte[decrypted.Length];
            Buffer.BlockCopy(decrypted, 0, result, 0, decrypted.Length);
            return result;
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (storedHmac is not null) CryptographicOperations.ZeroMemory(storedHmac);
            if (computedHmac is not null) CryptographicOperations.ZeroMemory(computedHmac);
            if (hmacKeyBytes is not null) CryptographicOperations.ZeroMemory(hmacKeyBytes);
            if (decrypted is not null) CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    /// <summary>
    /// Verify the HMAC signature without decrypting the data.
    /// </summary>
    /// <param name="protectedJson">
    /// The protected string in format <c>base64_encrypted|HMAC|base64_hmac</c>.
    /// </param>
    /// <param name="hmacKeyBase64">Base64-encoded raw HMAC key (256-bit).</param>
    /// <returns>True if the HMAC signature is valid; false otherwise.</returns>
    public static bool HasIntegrity(string protectedJson, string hmacKeyBase64)
    {
        if (string.IsNullOrEmpty(protectedJson) || string.IsNullOrEmpty(hmacKeyBase64))
            return false;

        byte[]? hmacKeyBytes = null;
        byte[]? encrypted = null;
        byte[]? storedHmac = null;
        byte[]? computedHmac = null;

        try
        {
            string[] parts = protectedJson.Split(HmacSeparator);
            if (parts.Length != 2)
                return false;

            encrypted = Convert.FromBase64String(parts[0]);
            storedHmac = Convert.FromBase64String(parts[1]);

            hmacKeyBytes = Convert.FromBase64String(hmacKeyBase64);
            if (hmacKeyBytes.Length != HmacKeyLengthBytes)
                return false;

            using (HMACSHA256 hmac = new HMACSHA256(hmacKeyBytes))
            {
                computedHmac = hmac.ComputeHash(encrypted);
            }

            return CryptographicOperations.FixedTimeEquals(computedHmac, storedHmac);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (storedHmac is not null) CryptographicOperations.ZeroMemory(storedHmac);
            if (computedHmac is not null) CryptographicOperations.ZeroMemory(computedHmac);
            if (hmacKeyBytes is not null) CryptographicOperations.ZeroMemory(hmacKeyBytes);
        }
    }

    /// <summary>
    /// Check whether a string contains an HMAC separator (i.e., is in the HMAC-protected format).
    /// </summary>
    /// <param name="encryptedString">The string to check.</param>
    /// <returns>True if the string contains the HMAC separator.</returns>
    public static bool IsHmacProtected(string? encryptedString)
    {
        return encryptedString?.Contains(HmacSeparator, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Check whether an HMAC key needs rotation based on its creation date.
    /// </summary>
    /// <param name="keyCreatedAt">The UTC date/time when the key was created.</param>
    /// <param name="maxDays">Maximum allowed key age in days (default: 90).</param>
    /// <returns>True if the key should be rotated.</returns>
    public static bool NeedsRotation(DateTime keyCreatedAt, int maxDays = DefaultRotationDays)
    {
        var keyAge = (DateTime.UtcNow - keyCreatedAt.ToUniversalTime()).TotalDays;
        return keyAge >= maxDays;
    }

    /// <summary>
    /// Get detailed rotation status for an HMAC key.
    /// </summary>
    /// <param name="keyCreatedAt">The UTC date/time when the key was created.</param>
    /// <param name="maxDays">Maximum allowed key age in days (default: 90).</param>
    /// <returns>A record containing rotation status details.</returns>
    public static HmacKeyRotationStatus GetRotationStatus(
        DateTime keyCreatedAt,
        int maxDays = DefaultRotationDays)
    {
        var keyAge = (int)Math.Floor((DateTime.UtcNow - keyCreatedAt.ToUniversalTime()).TotalDays);
        var daysUntilRotation = maxDays - keyAge;
        var needsRotation = daysUntilRotation <= 0;

        return new HmacKeyRotationStatus(needsRotation, daysUntilRotation, keyAge);
    }

    /// <summary>
    /// Validate that the HMAC key length matches the expected size.
    /// </summary>
    private static void ValidateKeyLength(int actualLength)
    {
        if (actualLength != HmacKeyLengthBytes)
        {
            throw new ArgumentException(
                $"Invalid HMAC key length: expected {HmacKeyLengthBytes} bytes, got {actualLength}.");
        }
    }
}

/// <summary>
/// Describes the rotation status of an HMAC key.
/// </summary>
/// <param name="NeedsRotation">True if the key has exceeded the maximum age.</param>
/// <param name="DaysUntilRotation">Negative if overdue, positive if days remaining.</param>
/// <param name="KeyAgeDays">Current age of the key in days.</param>
public readonly record struct HmacKeyRotationStatus(
    bool NeedsRotation,
    int DaysUntilRotation,
    int KeyAgeDays);
