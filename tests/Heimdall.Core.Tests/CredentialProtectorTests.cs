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
using Heimdall.Core.Security;
using Heimdall.Core.Tests.Vault;

namespace Heimdall.Core.Tests;

[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public class CredentialProtectorTests
{
    /// <summary>
    /// Helper to generate a valid 256-bit HMAC key for testing.
    /// </summary>
    private static string GenerateTestKey() => HmacIntegrity.GenerateRawKey();

    // ── Protect + Unprotect round-trip ──────────────────────────────────

    [Theory]
    [InlineData("my-secret-password-123!")]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("special chars: é à ü ñ 中文 🔑")]
    [InlineData("line1\nline2\ttab")]
    public void Protect_ThenUnprotect_WithHmacKey_ReturnsOriginalPlaintext(string plaintext)
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var protectedValue = CredentialProtector.Protect(plaintext);
        var decrypted = CredentialProtector.Unprotect(protectedValue);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [InlineData("my-secret-password-123!")]
    [InlineData("")]
    public void Protect_ThenUnprotect_WithoutHmacKey_ReturnsOriginalPlaintext(string plaintext)
    {
        CredentialProtector.Initialize(null);

        var protectedValue = CredentialProtector.Protect(plaintext);
        var decrypted = CredentialProtector.Unprotect(protectedValue);

        Assert.Equal(plaintext, decrypted);
    }

    // ── HMAC key produces different output format ───────────────────────

    [Fact]
    public void Protect_WithHmacKey_OutputContainsHmacSeparator()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var protectedValue = CredentialProtector.Protect("test-credential");

        Assert.Contains("|HMAC|", protectedValue);
    }

    [Fact]
    public void Protect_WithoutHmacKey_OutputDoesNotContainHmacSeparator()
    {
        CredentialProtector.Initialize(null);

        var protectedValue = CredentialProtector.Protect("test-credential");

        Assert.DoesNotContain("|HMAC|", protectedValue);
    }

    // ── Wrong HMAC key ──────────────────────────────────────────────────

    [Fact]
    public void Unprotect_WithWrongHmacKey_ReturnsNull()
    {
        var key1 = GenerateTestKey();
        var key2 = GenerateTestKey();

        CredentialProtector.Initialize(key1);
        var protectedValue = CredentialProtector.Protect("secret");

        // Switch to a different HMAC key before decrypting
        CredentialProtector.Initialize(key2);
        var result = CredentialProtector.Unprotect(protectedValue);

        Assert.Null(result);
    }

    // ── Legacy blob backward compatibility ──────────────────────────────

    [Fact]
    public void Unprotect_LegacyDpapiBlob_WithHmacKeyInitialized_ReturnsPlaintext()
    {
        // Create a plain DPAPI blob (legacy format, no HMAC)
        var plaintext = "legacy-password-value";
        CredentialProtector.Initialize(null);
        var legacyBlob = CredentialProtector.Protect(plaintext);

        // Now initialize with an HMAC key and try to decrypt the legacy blob
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var decrypted = CredentialProtector.Unprotect(legacyBlob);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void IsLegacyFormat_DpapiOnlyBlob_ReturnsTrue()
    {
        CredentialProtector.Initialize(null);
        var legacyBlob = CredentialProtector.Protect("test");

        Assert.True(CredentialProtector.IsLegacyFormat(legacyBlob));
    }

    [Fact]
    public void IsLegacyFormat_HmacProtectedBlob_ReturnsFalse()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);
        var hmacBlob = CredentialProtector.Protect("test");

        Assert.False(CredentialProtector.IsLegacyFormat(hmacBlob));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLegacyFormat_NullOrWhitespace_ReturnsFalse(string? input)
    {
        Assert.False(CredentialProtector.IsLegacyFormat(input));
    }

    // ── Null and empty input handling ───────────────────────────────────

    [Fact]
    public void Protect_NullInput_ThrowsArgumentNullException()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var act = () => CredentialProtector.Protect(null!);

        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void Protect_EmptyString_DoesNotThrow()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var act = () => CredentialProtector.Protect(string.Empty);

        var ex = Record.Exception(act);
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unprotect_NullOrWhitespace_ReturnsNull(string? input)
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var result = CredentialProtector.Unprotect(input);

        Assert.Null(result);
    }

    [Fact]
    public void UnprotectToBytes_NullInput_ReturnsNull()
    {
        byte[]? result = CredentialProtector.UnprotectToBytes(null);

        Assert.Null(result);
    }

    [Fact]
    public void UnprotectToBytes_WhitespaceInput_ReturnsNull()
    {
        byte[]? result = CredentialProtector.UnprotectToBytes("   ");

        Assert.Null(result);
    }

    [Fact]
    public void UnprotectToBytes_HmacRoundTrip_PreservesPlaintext()
    {
        string key = GenerateTestKey();
        string plaintext = "hmac-mot-de-passe-é-✓-Ω";
        byte[]? result = null;

        CredentialProtector.Initialize(key);

        try
        {
            string protectedValue = CredentialProtector.Protect(plaintext);
            result = CredentialProtector.UnprotectToBytes(protectedValue);

            Assert.NotNull(result);
            Assert.Equal(plaintext, Encoding.UTF8.GetString(result));
        }
        finally
        {
            if (result is not null)
            {
                CryptographicOperations.ZeroMemory(result);
            }
        }
    }

    [Fact]
    public void UnprotectToBytes_LegacyDpapiRoundTrip_PreservesPlaintext()
    {
        string plaintext = "legacy-mot-de-passe-é-✓-Ω";
        byte[]? result = null;

        CredentialProtector.Initialize(null);

        try
        {
            string protectedValue = CredentialProtector.Protect(plaintext);
            result = CredentialProtector.UnprotectToBytes(protectedValue);

            Assert.NotNull(result);
            Assert.Equal(plaintext, Encoding.UTF8.GetString(result));
        }
        finally
        {
            if (result is not null)
            {
                CryptographicOperations.ZeroMemory(result);
            }
        }
    }

    // ── Initialize modes ────────────────────────────────────────────────

    [Fact]
    public void Initialize_WithNullKey_FallsBackToDpapiOnly()
    {
        CredentialProtector.Initialize(null);

        var protectedValue = CredentialProtector.Protect("dpapi-only-test");

        Assert.DoesNotContain("|HMAC|", protectedValue);
        Assert.Equal("dpapi-only-test", CredentialProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Initialize_WithValidKey_UsesHmacProtection()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var protectedValue = CredentialProtector.Protect("hmac-test");

        Assert.Contains("|HMAC|", protectedValue);
        Assert.Equal("hmac-test", CredentialProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Initialize_CalledMultipleTimes_UsesLatestKey()
    {
        var key1 = GenerateTestKey();
        var key2 = GenerateTestKey();

        CredentialProtector.Initialize(key1);
        var protectedWithKey1 = CredentialProtector.Protect("test");

        CredentialProtector.Initialize(key2);
        var protectedWithKey2 = CredentialProtector.Protect("test");

        // Key2 should decrypt its own output
        Assert.Equal("test", CredentialProtector.Unprotect(protectedWithKey2));

        // Key2 should NOT decrypt key1's output (wrong HMAC)
        Assert.Null(CredentialProtector.Unprotect(protectedWithKey1));
    }

    // ── DPAPI nonce: multiple protect calls produce different ciphertext ─

    [Fact]
    public void Protect_SameInput_ProducesDifferentCiphertext_WithHmac()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var a = CredentialProtector.Protect("identical-input");
        var b = CredentialProtector.Protect("identical-input");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Protect_SameInput_ProducesDifferentCiphertext_WithoutHmac()
    {
        CredentialProtector.Initialize(null);

        var a = CredentialProtector.Protect("identical-input");
        var b = CredentialProtector.Protect("identical-input");

        Assert.NotEqual(a, b);
    }

    // ── Tampered ciphertext ─────────────────────────────────────────────

    [Fact]
    public void Unprotect_TamperedCiphertext_WithHmac_ReturnsNull()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var protectedValue = CredentialProtector.Protect("sensitive-data");
        var parts = protectedValue.Split("|HMAC|");
        var tamperedEncrypted = "AAAA" + parts[0][4..];
        var tampered = tamperedEncrypted + "|HMAC|" + parts[1];

        var result = CredentialProtector.Unprotect(tampered);

        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_TamperedHmac_ReturnsNull()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var protectedValue = CredentialProtector.Protect("sensitive-data");
        var parts = protectedValue.Split("|HMAC|");
        var tampered = parts[0] + "|HMAC|" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        var result = CredentialProtector.Unprotect(tampered);

        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_CompletelyInvalidBase64_ReturnsNull()
    {
        var key = GenerateTestKey();
        CredentialProtector.Initialize(key);

        var result = CredentialProtector.Unprotect("not-valid-base64-at-all!!!");

        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_ValidBase64ButNotDpapi_ReturnsNull()
    {
        CredentialProtector.Initialize(null);

        // Valid Base64 but not a real DPAPI blob
        var fakeBlob = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var result = CredentialProtector.Unprotect(fakeBlob);

        Assert.Null(result);
    }
}
