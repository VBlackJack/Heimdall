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
using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Heimdall.Ssh.Tests;

public sealed class OpenSshCertificateTests
{
    private sealed class AlwaysAcceptVerifier : IHostKeyVerifier
    {
        public int Calls { get; private set; }

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string fingerprint,
            string? previousFingerprint,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(HostKeyDecision.Accept);
        }
    }

    [Fact]
    public void HostCertificate_SignedByTrustedCertAuthority_IsVerifiedAndAutoTrusted()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();

        var certBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            keyId: "host-web01",
            principals: ["web01.corp.example.com"],
            validAfter: DateTimeOffset.UtcNow.AddMinutes(-5),
            validBefore: DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob);

        Assert.True(OpenSshCertificateParser.TryParseBlob(certBlob, out var cert));
        Assert.NotNull(cert);
        Assert.Equal(OpenSshCertificateType.Host, cert.Type);
        Assert.Equal("host-web01", cert.KeyId);
        Assert.True(OpenSshCertificateVerifier.VerifyCaSignature(cert));

        var store = new HostKeyStore();
        var knownHosts = $"@cert-authority *.corp.example.com ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n";
        var policy = store.LoadPolicyFromKnownHosts(knownHosts);
        Assert.Single(policy.CertAuthorities);

        var status = OpenSshCertificateVerifier.VerifyHostCertificate(
            certBlob, "web01.corp.example.com", 22, store);
        Assert.Equal(HostCertificateVerificationStatus.TrustedByCertAuthority, status);

        // Wrong principal is rejected
        Assert.Equal(
            HostCertificateVerificationStatus.InvalidOrUntrusted,
            OpenSshCertificateVerifier.VerifyHostCertificate(certBlob, "other.corp.example.com", 22, store));

        // Tampered certificate fails CA signature verification
        var tampered = (byte[])certBlob.Clone();
        tampered[^1] ^= 0xFF;
        Assert.Equal(
            HostCertificateVerificationStatus.InvalidOrUntrusted,
            OpenSshCertificateVerifier.VerifyHostCertificate(tampered, "web01.corp.example.com", 22, store));
    }

    [Fact]
    public async Task ResolvePresentedHostKeyAsync_AutoTrustsValidCertAuthority_WithoutPromptingVerifier()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();
        var certBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            "prod-db",
            ["db.internal.example"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob);

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts(
            $"@cert-authority *.internal.example ssh-ed25519 {Convert.ToBase64String(caPubBlob)}");

        var presentation = new SshConnectionFactory.HostKeyPresentation(
            "db.internal.example",
            22,
            OpenSshCertificateParser.CertEd25519,
            HostKeyFormats.ComputeSha256Fingerprint(certBlob),
            certBlob);
        var verifier = new AlwaysAcceptVerifier();

        var pinned = await SshConnectionFactory.ResolvePresentedHostKeyAsync(
            "db.internal.example", 22, presentation, store, verifier);

        Assert.Equal(presentation.Fingerprint, pinned.Fingerprint);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task ResolvePresentedHostKeyAsync_RejectsRevokedKeyEvenWhenVerifierWouldAccept()
    {
        var (_, hostPubBlob) = GenerateEd25519KeyPair();
        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts(
            $"@revoked compromised.example.com ssh-ed25519 {Convert.ToBase64String(hostPubBlob)}");

        var presentation = new SshConnectionFactory.HostKeyPresentation(
            "compromised.example.com",
            22,
            "ssh-ed25519",
            HostKeyFormats.ComputeSha256Fingerprint(hostPubBlob),
            hostPubBlob);
        var verifier = new AlwaysAcceptVerifier();

        await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SshConnectionFactory.ResolvePresentedHostKeyAsync(
                "compromised.example.com", 22, presentation, store, verifier));
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public void SshKeyAuditEngine_AuditsValidAndExpiredOpenSshCertificates()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, userPubRaw) = GenerateEd25519RawKeyPair();

        var validBlob = BuildEd25519Certificate(
            OpenSshCertificateType.User,
            userPubRaw,
            "alice@corp",
            ["alice"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(8),
            caPriv,
            caPubBlob);
        var validLine = $"{OpenSshCertificateParser.CertEd25519} {Convert.ToBase64String(validBlob)} alice@corp";

        var validAudit = SshKeyAuditEngine.Audit(validLine);
        Assert.NotNull(validAudit);
        Assert.Equal("Ed25519", validAudit.Algorithm);
        Assert.Equal(256, validAudit.KeySize);
        Assert.Equal("OpenSSH Certificate (User)", validAudit.Format);
        Assert.Equal(SecurityRating.Strong, validAudit.Rating);

        var expiredBlob = BuildEd25519Certificate(
            OpenSshCertificateType.User,
            userPubRaw,
            "alice-old",
            ["alice"],
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1),
            caPriv,
            caPubBlob);
        var expiredLine = $"{OpenSshCertificateParser.CertEd25519} {Convert.ToBase64String(expiredBlob)}";

        var expiredAudit = SshKeyAuditEngine.Audit(expiredLine);
        Assert.NotNull(expiredAudit);
        Assert.Equal(SecurityRating.Weak, expiredAudit.Rating);
        Assert.Contains(expiredAudit.Findings, f => f.Severity == FindingSeverity.Fail);
    }

    private static (Ed25519PrivateKeyParameters Priv, byte[] PubBlob) GenerateEd25519KeyPair()
    {
        var (priv, pubRaw) = GenerateEd25519RawKeyPair();
        using var ms = new MemoryStream();
        WriteString(ms, "ssh-ed25519");
        WriteBytes(ms, pubRaw);
        return (priv, ms.ToArray());
    }

    private static (Ed25519PrivateKeyParameters Priv, byte[] PubRaw) GenerateEd25519RawKeyPair()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        return (priv, pub.GetEncoded());
    }

    private static byte[] BuildEd25519Certificate(
        OpenSshCertificateType certType,
        byte[] subjectPubKey32,
        string keyId,
        IReadOnlyList<string> principals,
        DateTimeOffset validAfter,
        DateTimeOffset validBefore,
        Ed25519PrivateKeyParameters caPrivateKey,
        byte[] caPublicKeyBlob)
    {
        using var body = new MemoryStream();
        WriteString(body, OpenSshCertificateParser.CertEd25519);
        WriteBytes(body, RandomNumberGenerator.GetBytes(32)); // nonce
        WriteBytes(body, subjectPubKey32);
        WriteUInt64(body, 42UL);
        WriteUInt32(body, (uint)certType);
        WriteString(body, keyId);

        using (var principalsMs = new MemoryStream())
        {
            foreach (var p in principals)
            {
                WriteString(principalsMs, p);
            }

            WriteBytes(body, principalsMs.ToArray());
        }

        WriteUInt64(body, (ulong)validAfter.ToUnixTimeSeconds());
        WriteUInt64(body, (ulong)validBefore.ToUnixTimeSeconds());
        WriteBytes(body, []); // critical options
        WriteBytes(body, []); // extensions
        WriteBytes(body, []); // reserved
        WriteBytes(body, caPublicKeyBlob);

        var signedData = body.ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, caPrivateKey);
        signer.BlockUpdate(signedData, 0, signedData.Length);
        var rawSig = signer.GenerateSignature();

        using var sigBlob = new MemoryStream();
        WriteString(sigBlob, "ssh-ed25519");
        WriteBytes(sigBlob, rawSig);

        WriteBytes(body, sigBlob.ToArray());
        return body.ToArray();
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteUInt64(Stream s, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBytes(Stream s, byte[] bytes)
    {
        WriteUInt32(s, (uint)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteString(Stream s, string value)
        => WriteBytes(s, Encoding.UTF8.GetBytes(value));
}
