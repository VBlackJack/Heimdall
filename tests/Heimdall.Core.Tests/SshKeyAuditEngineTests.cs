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
using System.IO;
using System.Security.Cryptography;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

public class SshKeyAuditEngineTests
{
    private const string TestEd25519PublicKey =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl test@host";

    [Theory]
    [InlineData("Ed25519", 256, SecurityRating.Strong)]
    [InlineData("RSA", 4096, SecurityRating.Strong)]
    [InlineData("RSA", 3072, SecurityRating.Strong)]
    [InlineData("RSA", 2048, SecurityRating.Acceptable)]
    [InlineData("RSA", 1024, SecurityRating.Weak)]
    [InlineData("ECDSA", 521, SecurityRating.Strong)]
    [InlineData("ECDSA", 384, SecurityRating.Strong)]
    [InlineData("ECDSA", 256, SecurityRating.Acceptable)]
    [InlineData("DSA", 1024, SecurityRating.Deprecated)]
    [InlineData("DSA", 2048, SecurityRating.Deprecated)]
    public void DetermineRating_ReturnsExpected(string algorithm, int keySize, SecurityRating expected)
    {
        var result = SshKeyAuditEngine.DetermineRating(algorithm, keySize);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Audit_OpenSshEd25519PublicKey_ReturnsStrongRating()
    {
        var result = SshKeyAuditEngine.Audit(TestEd25519PublicKey);

        Assert.NotNull(result);
        Assert.Equal("Ed25519", result.Algorithm);
        Assert.Equal(256, result.KeySize);
        Assert.Equal(SecurityRating.Strong, result.Rating);
        Assert.False(result.IsPrivateKey);
        Assert.False(result.IsEncrypted);
        Assert.Equal("OpenSSH", result.Format);
        Assert.StartsWith("SHA256:", result.Fingerprint);
    }

    [Fact]
    public void Audit_Fido2SkEd25519PublicKey_ReturnsStrongFido2Result()
    {
        using var ms = new MemoryStream();
        WriteTestString(ms, "sk-ssh-ed25519@openssh.com");
        byte[] pub = new byte[32];
        pub[0] = 0x42;
        WriteTestBytes(ms, pub);
        WriteTestString(ms, "ssh:");
        string line = $"sk-ssh-ed25519@openssh.com {Convert.ToBase64String(ms.ToArray())} yubikey@corp";

        var result = SshKeyAuditEngine.Audit(line);

        Assert.NotNull(result);
        Assert.Equal("Ed25519 (FIDO2)", result.Algorithm);
        Assert.Equal(256, result.KeySize);
        Assert.Equal(SecurityRating.Strong, result.Rating);
        Assert.StartsWith("SHA256:", result.Fingerprint);
        Assert.Contains(result.Findings, f => f.Severity == FindingSeverity.Pass);
    }

    [Fact]
    public void Audit_PuttyPpkV3AndV2PrivateKeys_ParsesAlgorithmFingerprintAndEncryption()
    {
        using var ms = new MemoryStream();
        WriteTestString(ms, "ssh-ed25519");
        byte[] pub = new byte[32];
        pub[0] = 0x77;
        WriteTestBytes(ms, pub);
        string pubB64 = Convert.ToBase64String(ms.ToArray());

        string ppkV3 = $"""
            PuTTY-User-Key-File-3: ssh-ed25519
            Encryption: aes256-cbc
            Comment: ops@bastion
            Public-Lines: 1
            {pubB64}
            Key-Derivation: Argon2id
            Private-Lines: 1
            AAAA
            Private-MAC: 0000
            """;

        var v3 = SshKeyAuditEngine.Audit(ppkV3);
        Assert.NotNull(v3);
        Assert.Equal("Ed25519", v3.Algorithm);
        Assert.Equal(256, v3.KeySize);
        Assert.Equal("PuTTY PPK v3", v3.Format);
        Assert.True(v3.IsPrivateKey);
        Assert.True(v3.IsEncrypted);
        Assert.Equal(SecurityRating.Strong, v3.Rating);
        Assert.StartsWith("SHA256:", v3.Fingerprint);

        string ppkV2 = $"""
            PuTTY-User-Key-File-2: ssh-ed25519
            Encryption: none
            Comment: legacy@bastion
            Public-Lines: 1
            {pubB64}
            Private-Lines: 1
            AAAA
            Private-MAC: 0000
            """;

        var v2 = SshKeyAuditEngine.Audit(ppkV2);
        Assert.NotNull(v2);
        Assert.Equal("PuTTY PPK v2", v2.Format);
        Assert.False(v2.IsEncrypted);
        Assert.Contains(v2.Findings, f => f.Text == "ToolSshAuditFindingOldFormat");
    }

    private static void WriteTestBytes(Stream stream, byte[] bytes)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, bytes.Length);
        stream.Write(len);
        stream.Write(bytes);
    }

    [Fact]
    public void Audit_OpenSshRsaPublicKey_ReturnsCorrectKeySize()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);

        using var ms = new MemoryStream();
        WriteTestString(ms, "ssh-rsa");
        WriteTestMpint(ms, parameters.Exponent!);
        WriteTestMpint(ms, parameters.Modulus!);
        var key = $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} test@host";

        var result = SshKeyAuditEngine.Audit(key);

        Assert.NotNull(result);
        Assert.Equal("RSA", result.Algorithm);
        Assert.Equal(2048, result.KeySize);
        Assert.Equal(SecurityRating.Acceptable, result.Rating);
        Assert.Equal("OpenSSH", result.Format);
    }

    [Fact]
    public void BuildFindings_Ed25519_ContainsPassFinding()
    {
        var findings = SshKeyAuditEngine.BuildFindings("Ed25519", 256, false, false, false, null);

        Assert.Single(findings);
        Assert.Equal(FindingSeverity.Pass, findings[0].Severity);
    }

    [Fact]
    public void BuildFindings_UnencryptedPrivateKey_ContainsWarning()
    {
        var findings = SshKeyAuditEngine.BuildFindings("RSA", 4096, isPrivate: true, isEncrypted: false, isOldFormat: false, null);

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void BuildFindings_DeprecatedDsa_ContainsFailFinding()
    {
        var findings = SshKeyAuditEngine.BuildFindings("DSA", 1024, false, false, false, null);

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Fail);
    }

    [Fact]
    public void BuildFindings_OldFormatPrivateKey_ContainsFormatWarning()
    {
        var findings = SshKeyAuditEngine.BuildFindings("RSA", 2048, isPrivate: true, isEncrypted: false, isOldFormat: true, null);

        Assert.True(findings.Count >= 2);
        Assert.Contains(findings, f => f.Severity == FindingSeverity.Warning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a key")]
    [InlineData("ssh-rsa INVALIDBASE64 test@host")]
    [InlineData("-----BEGIN SOMETHING-----\ngarbage\n-----END SOMETHING-----")]
    public void Audit_InvalidInput_ReturnsNull(string? input)
    {
        var result = SshKeyAuditEngine.Audit(input!);

        Assert.Null(result);
    }

    [Fact]
    public void Audit_InputExceedingMaxKeyFileSize_ReturnsNull()
    {
        string oversized = new string('A', SshKeyAuditEngine.MaxKeyFileSize + 1);

        SshKeyAuditResult? result = SshKeyAuditEngine.Audit(oversized);

        Assert.Null(result);
    }

    [Fact]
    public void Audit_Pkcs1RsaPrivateKeyPem_IdentifiesFormatAndType()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();

        var result = SshKeyAuditEngine.Audit(pem);

        Assert.NotNull(result);
        Assert.Equal("RSA", result.Algorithm);
        Assert.Equal(2048, result.KeySize);
        Assert.True(result.IsPrivateKey);
        Assert.False(result.IsEncrypted);
        Assert.Equal("PEM (PKCS#1)", result.Format);
    }

    [Fact]
    public void Audit_WithLocalize_FindingsUseLocalizedText()
    {
        var result = SshKeyAuditEngine.Audit(TestEd25519PublicKey, key => $"[{key}]");

        Assert.NotNull(result);
        Assert.All(result.Findings, finding => Assert.StartsWith("[", finding.Text));
    }

    [Fact]
    public void Audit_Pkcs8EncryptedPrivateKey_ReportsEncryptedUnknownKey()
    {
        using var rsa = RSA.Create(2048);
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12, HashAlgorithmName.SHA1, 1000);
        var pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("secret", parameters);

        var result = SshKeyAuditEngine.Audit(pem);

        Assert.NotNull(result);
        Assert.Equal("Unknown", result.Algorithm);
        Assert.True(result.IsPrivateKey);
        Assert.True(result.IsEncrypted);
        Assert.Equal("PEM (PKCS#8, encrypted)", result.Format);
    }

    private static void WriteTestString(Stream stream, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteTestMpint(Stream stream, byte[] value)
    {
        var needsPadding = value.Length > 0 && (value[0] & 0x80) != 0;
        var length = value.Length + (needsPadding ? 1 : 0);
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, length);
        stream.Write(lengthBytes);
        if (needsPadding)
        {
            stream.WriteByte(0);
        }

        stream.Write(value);
    }
}
