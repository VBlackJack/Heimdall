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

        // Wrong principal is rejected as invalid for a trusted CA
        Assert.Equal(
            HostCertificateVerificationStatus.InvalidForTrustedCertAuthority,
            OpenSshCertificateVerifier.VerifyHostCertificate(certBlob, "other.corp.example.com", 22, store));

        // Tampered certificate fails CA signature verification as invalid for a trusted CA
        var tampered = (byte[])certBlob.Clone();
        tampered[^1] ^= 0xFF;
        Assert.Equal(
            HostCertificateVerificationStatus.InvalidForTrustedCertAuthority,
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
        Assert.Equal("ToolSshAuditFormatCertUser", validAudit.Format);
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

    [Fact]
    public void HostCertificate_WithUnknownCriticalOption_IsRejected()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();

        using var critMs = new MemoryStream();
        WriteString(critMs, "force-command");
        WriteString(critMs, "/bin/true");

        var certBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            "host-crit",
            ["web01.corp.example.com"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob,
            criticalOptionsBlob: critMs.ToArray());

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts($"@cert-authority *.corp.example.com ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n");

        Assert.Equal(
            HostCertificateVerificationStatus.InvalidForTrustedCertAuthority,
            OpenSshCertificateVerifier.VerifyHostCertificate(certBlob, "web01.corp.example.com", 22, store));

        // ResolvePresentedHostKeyAsync must hard-reject invalid certs from a trusted CA without TOFU fallback.
        var presentation = new SshConnectionFactory.HostKeyPresentation(
            "web01.corp.example.com",
            22,
            OpenSshCertificateParser.CertEd25519,
            HostKeyFormats.ComputeSha256Fingerprint(certBlob),
            certBlob);
        var verifier = new AlwaysAcceptVerifier();
        Assert.Throws<HostKeyRejectedException>(() =>
            SshConnectionFactory.ResolvePresentedHostKeyAsync(
                "web01.corp.example.com", 22, presentation, store, verifier).GetAwaiter().GetResult());
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public void HostCertificate_WithRevokedSubjectKey_IsRevoked()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();

        using var subjPubMs = new MemoryStream();
        WriteString(subjPubMs, "ssh-ed25519");
        WriteBytes(subjPubMs, hostPubRaw);
        var subjectPubBlob = subjPubMs.ToArray();

        var certBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            "host-revoked-subj",
            ["web01.corp.example.com"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob);

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts(
            $"@cert-authority *.corp.example.com ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n" +
            $"@revoked *.corp.example.com ssh-ed25519 {Convert.ToBase64String(subjectPubBlob)}\n");

        Assert.Equal(
            HostCertificateVerificationStatus.Revoked,
            OpenSshCertificateVerifier.VerifyHostCertificate(certBlob, "web01.corp.example.com", 22, store));
    }

    [Fact]
    public void HostKeyTrustService_RemoveAlgorithm_PreservesPrimaryPersistenceAndRejectsMismatch()
    {
        var store = new HostKeyStore();
        var trustService = new HostKeyTrustService(store);

        var removedEvents = new List<string>();
        var replacedEvents = new List<(string Key, string Alg)>();
        trustService.EntryRemoved += key => removedEvents.Add(key);
        trustService.EntryReplaced += (key, _, next) => replacedEvents.Add((key, next.Algorithm));

        trustService.Trust("dual.example.com", 22, "SHA256:ed25519fp", "ssh-ed25519", HostKeySource.UserConfirmed);
        store.TrustAdditionalAlgorithm("dual.example.com", 22, "SHA256:rsafp", "ssh-rsa", HostKeySource.UserConfirmed);

        // Revoking an algorithm that is NOT stored must return false and leave primary intact.
        Assert.False(trustService.RemoveAlgorithm("dual.example.com", 22, "ecdsa-sha2-nistp256"));
        Assert.NotNull(trustService.GetEntry("dual.example.com", 22));
        Assert.Empty(removedEvents);

        // Revoking the additional algorithm must NOT raise EntryRemoved (which wipes persisted primary key).
        Assert.True(trustService.RemoveAlgorithm("dual.example.com", 22, "ssh-rsa"));
        Assert.Empty(removedEvents);
        Assert.Single(replacedEvents);
        Assert.Equal("ssh-ed25519", trustService.GetEntry("dual.example.com", 22)?.Algorithm);
    }

    [Fact]
    public void SshLocalForwardSpec_Ipv6Loopback_RoundTripsThroughCanonicalAndDirectiveFormats()
    {
        Assert.True(SshLocalForwardSpec.TryParse("[::1]:8443 [::1]:443", out var spec));
        Assert.NotNull(spec);
        Assert.Equal("::1", spec.BindAddress);
        Assert.Equal(8443, spec.LocalPort);
        Assert.Equal("::1", spec.RemoteHost);
        Assert.Equal(443, spec.RemotePort);

        var canonical = spec.ToCanonicalString();
        Assert.Equal("[::1]:8443:[::1]:443", canonical);
        Assert.True(SshLocalForwardSpec.TryParse(canonical, out var roundTripped));
        Assert.Equal(spec, roundTripped);

        var directive = spec.ToOpenSshDirectiveValue();
        Assert.Equal("[::1]:8443 [::1]:443", directive);
        Assert.True(SshLocalForwardSpec.TryParse(directive, out var fromDirective));
        Assert.Equal(spec, fromDirective);
    }

    [Fact]
    public void OpenSshCertificate_WildcardPrincipal_DoesNotMatchArbitraryHostname()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();
        var certBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            "wildcard-attempt",
            ["*.corp.example.com"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob);

        Assert.True(OpenSshCertificateParser.TryParseBlob(certBlob, out var cert));
        Assert.NotNull(cert);
        Assert.False(cert.MatchesPrincipal("web01.corp.example.com"));
        Assert.True(cert.MatchesPrincipal("*.corp.example.com"));

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts($"@cert-authority *.corp.example.com ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n");
        Assert.Equal(
            HostCertificateVerificationStatus.InvalidForTrustedCertAuthority,
            OpenSshCertificateVerifier.VerifyHostCertificate(certBlob, "web01.corp.example.com", 22, store));
    }

    [Fact]
    public void HostKeyStore_HashedRevokedEntry_BlocksPlainHostOnDefaultAndCustomPorts()
    {
        var (_, hostPubBlob) = GenerateEd25519KeyPair();
        var fp = HostKeyFormats.ComputeSha256Fingerprint(hostPubBlob);
        var hashedDefault = CreateHashedHostToken("revoked.example.com", RandomNumberGenerator.GetBytes(20));
        var hashedCustom = CreateHashedHostToken("[revoked.example.com]:2222", RandomNumberGenerator.GetBytes(20));

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts(
            $"@revoked {hashedDefault} ssh-ed25519 {Convert.ToBase64String(hostPubBlob)}\n" +
            $"@revoked {hashedCustom} ssh-ed25519 {Convert.ToBase64String(hostPubBlob)}\n");

        Assert.True(store.IsRevoked("revoked.example.com", 22, fp));
        Assert.True(store.IsRevoked("revoked.example.com", 2222, fp));
        Assert.False(store.IsRevoked("revoked.example.com", 2200, fp));
        Assert.False(store.IsRevoked("other.example.com", 22, fp));
    }

    private static string CreateHashedHostToken(string hostToken, byte[] salt)
    {
        using var hmac = new HMACSHA1(salt);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(hostToken));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
    }

    [Fact]
    public async Task ResolvePresentedHostKeyAsync_RevokedAndInvalidCert_ThrowTypedFatalReasons()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();
        var (_, revokedPubBlob) = GenerateEd25519KeyPair();

        var expiredCertBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            "expired-host",
            ["web01.corp.example.com"],
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1),
            caPriv,
            caPubBlob);

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts(
            $"@cert-authority *.corp.example.com ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n" +
            $"@revoked revoked.corp.example.com ssh-ed25519 {Convert.ToBase64String(revokedPubBlob)}\n");

        var verifier = new AlwaysAcceptVerifier();

        var revokedPresentation = new SshConnectionFactory.HostKeyPresentation(
            "revoked.corp.example.com",
            22,
            "ssh-ed25519",
            HostKeyFormats.ComputeSha256Fingerprint(revokedPubBlob),
            revokedPubBlob);
        var revokedEx = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SshConnectionFactory.ResolvePresentedHostKeyAsync(
                "revoked.corp.example.com", 22, revokedPresentation, store, verifier));
        Assert.Equal(HostKeyRejectionReason.Revoked, revokedEx.Reason);
        var revokedFailure = FailureClassifier.Classify(revokedEx);
        Assert.Equal(SshFailureCode.HostKeyRevoked, revokedFailure.Code);
        Assert.True(revokedFailure.IsFatal);
        Assert.False(SshReconnectPolicy.AllowsAutoReconnect(revokedFailure.Code));

        var certPresentation = new SshConnectionFactory.HostKeyPresentation(
            "web01.corp.example.com",
            22,
            OpenSshCertificateParser.CertEd25519,
            HostKeyFormats.ComputeSha256Fingerprint(expiredCertBlob),
            expiredCertBlob);
        var certEx = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SshConnectionFactory.ResolvePresentedHostKeyAsync(
                "web01.corp.example.com", 22, certPresentation, store, verifier));
        Assert.Equal(HostKeyRejectionReason.CertificateInvalid, certEx.Reason);
        var certFailure = FailureClassifier.Classify(certEx);
        Assert.Equal(SshFailureCode.HostCertificateInvalid, certFailure.Code);
        Assert.True(certFailure.IsFatal);
        Assert.False(SshReconnectPolicy.AllowsAutoReconnect(certFailure.Code));
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task ResolvePresentedHostKeyAsync_TrustedCertAuthority_PopulatesHostKeyBlob()
    {
        var (caPriv, caPubBlob) = GenerateEd25519KeyPair();
        var (_, hostPubRaw) = GenerateEd25519RawKeyPair();
        var certBlob = BuildEd25519Certificate(
            OpenSshCertificateType.Host,
            hostPubRaw,
            "web-ca-blob",
            ["blob.internal.example"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob);

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts(
            $"@cert-authority *.internal.example ssh-ed25519 {Convert.ToBase64String(caPubBlob)}");

        var presentation = new SshConnectionFactory.HostKeyPresentation(
            "blob.internal.example",
            22,
            OpenSshCertificateParser.CertEd25519,
            HostKeyFormats.ComputeSha256Fingerprint(certBlob),
            certBlob);
        var verifier = new AlwaysAcceptVerifier();

        var pinned = await SshConnectionFactory.ResolvePresentedHostKeyAsync(
            "blob.internal.example", 22, presentation, store, verifier);

        Assert.NotNull(pinned.HostKeyBlob);
        Assert.Equal(certBlob, pinned.HostKeyBlob);
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
        byte[] caPublicKeyBlob,
        byte[]? criticalOptionsBlob = null)
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
        WriteBytes(body, criticalOptionsBlob ?? []); // critical options
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
