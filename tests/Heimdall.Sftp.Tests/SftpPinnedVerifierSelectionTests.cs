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
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Heimdall.Sftp.Tests;

public sealed class SftpPinnedVerifierSelectionTests
{
    [Fact]
    public void PreResolvedHostKeyVerifier_IsReusedEvenOnDualAlgorithmHost_UnlessRevoked()
    {
        var store = new HostKeyStore();
        var trustService = new HostKeyTrustService(store);
        trustService.Trust("dual.internal", 22, "SHA256:ed25519fp", "ssh-ed25519", HostKeySource.UserConfirmed);
        store.TrustAdditionalAlgorithm("dual.internal", 22, "SHA256:rsafp", "ssh-rsa", HostKeySource.UserConfirmed);

        var preResolved = new PinnedFingerprintVerifier("dual.internal", 22, "SHA256:rsafp");
        var parameters = new SshConnectionParams
        {
            Host = "dual.internal",
            Port = 22,
            Username = "ops",
            SkipHostKeyProbeIfTrusted = true,
            PreResolvedHostKeyVerifier = preResolved
        };

        var selected = SftpBrowser.TrySelectPinnedHostKeyVerifier(parameters, store);
        Assert.Same(preResolved, selected);

        // Once revoked, the pre-resolved verifier must NOT be reused.
        store.AddRevokedKey("dual.internal", "SHA256:rsafp");
        Assert.Null(SftpBrowser.TrySelectPinnedHostKeyVerifier(parameters, store));
    }

    [Fact]
    public void PreResolvedHostKeyVerifier_WithHostOrPortMismatch_IsIgnored()
    {
        var store = new HostKeyStore();
        var preResolved = new PinnedFingerprintVerifier("other.internal", 22, "SHA256:fp");
        var parameters = new SshConnectionParams
        {
            Host = "target.internal",
            Port = 22,
            Username = "ops",
            SkipHostKeyProbeIfTrusted = false,
            PreResolvedHostKeyVerifier = preResolved
        };

        Assert.Null(SftpBrowser.TrySelectPinnedHostKeyVerifier(parameters, store));
    }

    [Fact]
    public void PreResolvedHostKeyVerifier_WithExpiredOrRevokedCertificateBlob_IsNotReused()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var caPair = gen.GenerateKeyPair();
        var caPriv = (Ed25519PrivateKeyParameters)caPair.Private;
        var caPubRaw = ((Ed25519PublicKeyParameters)caPair.Public).GetEncoded();

        var hostPair = gen.GenerateKeyPair();
        var hostPubRaw = ((Ed25519PublicKeyParameters)hostPair.Public).GetEncoded();

        using var caPubMs = new MemoryStream();
        WriteString(caPubMs, "ssh-ed25519");
        WriteBytes(caPubMs, caPubRaw);
        var caPubBlob = caPubMs.ToArray();

        var validCert = BuildEd25519HostCert(
            hostPubRaw,
            "sftp.corp.example",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            caPriv,
            caPubBlob);
        var expiredCert = BuildEd25519HostCert(
            hostPubRaw,
            "sftp.corp.example",
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1),
            caPriv,
            caPubBlob);

        var store = new HostKeyStore();
        store.LoadPolicyFromKnownHosts($"@cert-authority *.corp.example ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n");

        var validPin = new PinnedFingerprintVerifier(
            "sftp.corp.example",
            22,
            HostKeyFormats.ComputeSha256Fingerprint(validCert),
            validCert);
        var validParams = new SshConnectionParams
        {
            Host = "sftp.corp.example",
            Port = 22,
            Username = "ops",
            PreResolvedHostKeyVerifier = validPin
        };
        Assert.Same(validPin, SftpBrowser.TrySelectPinnedHostKeyVerifier(validParams, store));

        // Revoking the CA invalidates the pinned certificate immediately.
        store.AddRevokedKey("*.corp.example", HostKeyFormats.ComputeSha256Fingerprint(caPubBlob));
        Assert.Null(SftpBrowser.TrySelectPinnedHostKeyVerifier(validParams, store));

        // Expired certificate must never be reused even with an active CA.
        var freshStore = new HostKeyStore();
        freshStore.LoadPolicyFromKnownHosts($"@cert-authority *.corp.example ssh-ed25519 {Convert.ToBase64String(caPubBlob)}\n");
        var expiredPin = new PinnedFingerprintVerifier(
            "sftp.corp.example",
            22,
            HostKeyFormats.ComputeSha256Fingerprint(expiredCert),
            expiredCert);
        var expiredParams = new SshConnectionParams
        {
            Host = "sftp.corp.example",
            Port = 22,
            Username = "ops",
            PreResolvedHostKeyVerifier = expiredPin
        };
        Assert.Null(SftpBrowser.TrySelectPinnedHostKeyVerifier(expiredParams, freshStore));
    }

    private static byte[] BuildEd25519HostCert(
        byte[] subjectPubKey32,
        string principal,
        DateTimeOffset validAfter,
        DateTimeOffset validBefore,
        Ed25519PrivateKeyParameters caPrivateKey,
        byte[] caPublicKeyBlob)
    {
        using var body = new MemoryStream();
        WriteString(body, OpenSshCertificateParser.CertEd25519);
        WriteBytes(body, RandomNumberGenerator.GetBytes(32));
        WriteBytes(body, subjectPubKey32);
        WriteUInt64(body, 1UL);
        WriteUInt32(body, (uint)OpenSshCertificateType.Host);
        WriteString(body, "sftp-host");
        using (var principals = new MemoryStream())
        {
            WriteString(principals, principal);
            WriteBytes(body, principals.ToArray());
        }

        WriteUInt64(body, (ulong)validAfter.ToUnixTimeSeconds());
        WriteUInt64(body, (ulong)validBefore.ToUnixTimeSeconds());
        WriteBytes(body, []);
        WriteBytes(body, []);
        WriteBytes(body, []);
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
