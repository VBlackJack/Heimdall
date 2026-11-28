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
using System.Text;
using Heimdall.App.Services;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The native OpenSSH host-key decision, with no ssh.exe, no ConPTY and no network: a fake probe
/// stands for the server and a recording verifier for the user.
/// </summary>
public sealed class OpenSshHostKeyResolverTests
{
    private const string Host = "app.corp.example";
    private const int Port = 22;
    private const string LoopbackTransport = "127.0.0.1";
    private const int LoopbackPort = 49200;
    private const int ProbeTimeoutMs = 4321;

    private sealed class FakeProbe(PlinkHostKeyPresentation? presentation) : IOpenSshHostKeyProbe
    {
        public int Calls { get; private set; }

        public string? LastHost { get; private set; }

        public int LastPort { get; private set; }

        public SshCryptoPolicy LastPolicy { get; private set; }

        public int LastTimeoutMs { get; private set; }

        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string host,
            int port,
            SshCryptoPolicy cryptoPolicy,
            int timeoutMs,
            CancellationToken ct)
        {
            Calls++;
            LastHost = host;
            LastPort = port;
            LastPolicy = cryptoPolicy;
            LastTimeoutMs = timeoutMs;
            return Task.FromResult(presentation);
        }
    }

    private sealed record VerifierCall(
        string Host,
        int Port,
        string Algorithm,
        string PresentedFingerprint,
        string? StoredFingerprint,
        byte[] Blob,
        bool IsViaGateway);

    private sealed class RecordingVerifier(HostKeyDecision decision) : IHostKeyVerifier
    {
        public List<VerifierCall> Calls { get; } = [];

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default)
            => VerifyAsync(host, port, algorithm, presentedFingerprint, storedFingerprint, ReadOnlyMemory<byte>.Empty, false, ct);

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            ReadOnlyMemory<byte> hostKeyBlob,
            bool isViaGateway = false,
            CancellationToken ct = default)
        {
            Calls.Add(new VerifierCall(
                host,
                port,
                algorithm,
                presentedFingerprint,
                storedFingerprint,
                hostKeyBlob.ToArray(),
                isViaGateway));
            return Task.FromResult(decision);
        }
    }

    private sealed class Rig
    {
        public Rig(PlinkHostKeyPresentation? presentation, HostKeyDecision verifierDecision = HostKeyDecision.Accept)
        {
            Store = new HostKeyStore();
            Trust = new HostKeyTrustService(Store);
            Probe = new FakeProbe(presentation);
            Verifier = new RecordingVerifier(verifierDecision);
            Resolver = new OpenSshHostKeyResolver(Store, Trust, Verifier, Probe);
        }

        public HostKeyStore Store { get; }

        public HostKeyTrustService Trust { get; }

        public FakeProbe Probe { get; }

        public RecordingVerifier Verifier { get; }

        public OpenSshHostKeyResolver Resolver { get; }

        public Task<OpenSshHostKeyResolution> ResolveDirectAsync(SshCryptoPolicy policy = SshCryptoPolicy.Standard)
            => Resolver.ResolveAsync(Host, Port, Host, Port, policy, ProbeTimeoutMs, CancellationToken.None);
    }

    // ----- key material -------------------------------------------------------------------

    private static byte[] PlainBlob(string keyType, params byte[][] parts)
    {
        using var ms = new MemoryStream();
        WriteString(ms, keyType);
        foreach (byte[] part in parts)
        {
            WriteBytes(ms, part);
        }

        return ms.ToArray();
    }

    private static byte[] Ed25519Raw() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Ed25519Blob(byte[]? raw = null) => PlainBlob("ssh-ed25519", raw ?? Ed25519Raw());

    private static byte[] RsaBlob() => PlainBlob("ssh-rsa", [1, 0, 1], RandomNumberGenerator.GetBytes(256));

    private static byte[] DsaBlob() => PlainBlob("ssh-dss", RandomNumberGenerator.GetBytes(64));

    private static string Fp(byte[] blob) => HostKeyFormats.ComputeSha256Fingerprint(blob);

    private static string B64(byte[] blob) => Convert.ToBase64String(blob);

    private static PlinkHostKeyPresentation Present(byte[] blob, string algorithm = "ssh-ed25519")
        => new(algorithm, Fp(blob), B64(blob));

    private static string Alias => OpenSshEphemeralKnownHosts.HostKeyAlias;

    // ----- first use and stored pins -------------------------------------------------------

    [Fact]
    public async Task FirstUse_Accept_PromptsWithTheRealBlob_WritesOneLine_AndStoresTheTrust()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(Present(blob), HostKeyDecision.Accept);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        OpenSshKnownHostsKey key = Assert.Single(result.Keys);
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.Equal(B64(blob), key.PublicKeyBase64);
        Assert.False(result.CertificatePresented);

        VerifierCall call = Assert.Single(rig.Verifier.Calls);
        Assert.Equal(blob, call.Blob);
        Assert.Equal(Fp(blob), call.PresentedFingerprint);
        Assert.Null(call.StoredFingerprint);

        HostKeyEntry? stored = rig.Trust.GetEntry(Host, Port);
        Assert.NotNull(stored);
        Assert.Equal(Fp(blob), stored.Fingerprint);
        Assert.Equal(B64(blob), stored.PublicKeyBase64);
    }

    [Fact]
    public async Task FirstUse_TrustOnce_WritesOneLine_AndStoresOnlyForTheSession()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(Present(blob), HostKeyDecision.TrustOnce);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Single(result.Keys);
        Assert.Null(rig.Trust.GetEntry(Host, Port));
        Assert.Equal(Fp(blob), rig.Store.GetSessionEntry(Host, Port)?.Fingerprint);
    }

    [Fact]
    public async Task FirstUse_Reject_IsCancelled_AndWritesNothing()
    {
        Rig rig = new(Present(Ed25519Blob()), HostKeyDecision.Reject);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(OpenSshHostKeyFailure.Cancelled, result.Failure);
        Assert.Empty(result.Keys);
        Assert.Null(rig.Trust.GetEntry(Host, Port));
    }

    [Fact]
    public async Task FirstUse_ProbeFailure_FailsClosedAsUnavailable()
    {
        Rig rig = new(presentation: null);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Unavailable, result.Failure);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task RevokedPresentedKey_IsRejected_WithoutPrompting()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(Present(blob));
        rig.Store.AddRevokedKey("*", Fp(blob));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Revoked, result.Failure);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task StoredPinMatch_ProceedsWithoutPrompting_AndWritesOneLine()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(Present(blob));
        rig.Trust.Trust(Host, Port, Fp(blob), "ssh-ed25519", HostKeySource.UserConfirmed, B64(blob));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(B64(blob), Assert.Single(result.Keys).PublicKeyBase64);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task StoredPinMismatch_AsksWithTheStoredFingerprint_AndRejectionIsAMismatch()
    {
        byte[] stored = Ed25519Blob();
        byte[] presented = Ed25519Blob();
        Rig rig = new(Present(presented), HostKeyDecision.Reject);
        rig.Trust.Trust(Host, Port, Fp(stored), "ssh-ed25519", HostKeySource.UserConfirmed, B64(stored));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Mismatch, result.Failure);
        Assert.Equal(Fp(stored), result.StoredFingerprint);
        Assert.Equal(Fp(presented), result.PresentedFingerprint);
        VerifierCall call = Assert.Single(rig.Verifier.Calls);
        Assert.Equal(Fp(stored), call.StoredFingerprint);
        Assert.Equal(presented, call.Blob);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public async Task StoredPinMismatch_AcceptedByTheUser_WritesThePresentedKeyOnly()
    {
        byte[] stored = Ed25519Blob();
        byte[] presented = Ed25519Blob();
        Rig rig = new(Present(presented), HostKeyDecision.Accept);
        rig.Trust.Trust(Host, Port, Fp(stored), "ssh-ed25519", HostKeySource.UserConfirmed, B64(stored));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(B64(presented), Assert.Single(result.Keys).PublicKeyBase64);
    }

    [Fact]
    public async Task StoredPinWithoutABlob_IsBackFilledWhenTheServerPresentsTheSameKey()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(Present(blob));
        rig.Trust.Trust(Host, Port, Fp(blob), "ssh-ed25519", HostKeySource.ImportedKnownHosts);
        Assert.Null(rig.Trust.GetEntry(Host, Port)!.PublicKeyBase64);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        HostKeyEntry entry = rig.Trust.GetEntry(Host, Port)!;
        Assert.Equal(B64(blob), entry.PublicKeyBase64);
        Assert.Equal(Fp(blob), entry.Fingerprint);
        Assert.Equal(HostKeySource.ImportedKnownHosts, entry.Source);
        Assert.Empty(rig.Verifier.Calls);
    }

    // ----- probe failure on a stored pin ---------------------------------------------------

    [Fact]
    public async Task LegacyPinWithoutABlob_AndProbeFailure_IsUnavailable_NeverLaunches()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(blob), "ssh-ed25519", HostKeySource.UserConfirmed);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Unavailable, result.Failure);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public async Task ProbeFailure_WithPinnedBlobs_WritesEveryPinnedKeyOfTheHost()
    {
        byte[] ed = Ed25519Blob();
        byte[] rsa = RsaBlob();
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(ed), "ssh-ed25519", HostKeySource.UserConfirmed, B64(ed));
        rig.Trust.TrustAdditionalAlgorithm(Host, Port, Fp(rsa), "ssh-rsa", HostKeySource.UserConfirmed, B64(rsa));
        byte[] other = Ed25519Blob();
        rig.Trust.Trust("other.example", Port, Fp(other), "ssh-ed25519", HostKeySource.UserConfirmed, B64(other));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Keys.Count);
        Assert.Contains(result.Keys, k => k.KeyType == "ssh-ed25519" && k.PublicKeyBase64 == B64(ed));
        Assert.Contains(result.Keys, k => k.KeyType == "ssh-rsa" && k.PublicKeyBase64 == B64(rsa));
        Assert.DoesNotContain(result.Keys, k => k.PublicKeyBase64 == B64(other));
    }

    [Fact]
    public async Task ProbeFailure_RevokedPinnedKey_IsRejected()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(blob), "ssh-ed25519", HostKeySource.UserConfirmed, B64(blob));
        rig.Store.AddRevokedKey(Host, Fp(blob));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Revoked, result.Failure);
    }

    [Fact]
    public async Task ProbeFailure_AnAdditionalPinWithABlobDoesNotStandInForABlobLessDecidedPin()
    {
        byte[] ed = Ed25519Blob();
        byte[] rsa = RsaBlob();
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(ed), "ssh-ed25519", HostKeySource.UserConfirmed);
        rig.Trust.TrustAdditionalAlgorithm(Host, Port, Fp(rsa), "ssh-rsa", HostKeySource.UserConfirmed, B64(rsa));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task ProbeFailure_HashedKnownHostsPin_IsFoundAndWritten()
    {
        byte[] blob = Ed25519Blob();
        byte[] salt = RandomNumberGenerator.GetBytes(20);
        using var hmac = new HMACSHA1(salt);
        string token = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(Host)))}";
        Rig rig = new(presentation: null);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        rig.Store.TrustEntry(
            token,
            Port,
            new HostKeyEntry(Fp(blob), now, now, "ssh-ed25519", HostKeySource.ImportedKnownHosts) { PublicKeyBase64 = B64(blob) });

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(B64(blob), Assert.Single(result.Keys).PublicKeyBase64);
    }

    // ----- integrity and key types ---------------------------------------------------------

    [Fact]
    public async Task ProbeBlobThatDoesNotMatchItsFingerprint_IsRefused()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(new PlinkHostKeyPresentation("ssh-ed25519", Fp(Ed25519Blob()), B64(blob)));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Unavailable, result.Failure);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task StoredBlobThatDoesNotMatchItsFingerprint_IsRefused()
    {
        byte[] real = Ed25519Blob();
        byte[] swapped = Ed25519Blob();
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(real), "ssh-ed25519", HostKeySource.UserConfirmed, B64(swapped));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Unavailable, result.Failure);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public async Task DsaAndUnknownAndSecurityKeyTypes_AreRefusedBeforeAnyPrompt()
    {
        byte[][] blobs =
        [
            DsaBlob(),
            PlainBlob("ssh-unknown-type", RandomNumberGenerator.GetBytes(32)),
            PlainBlob("sk-ssh-ed25519@openssh.com", RandomNumberGenerator.GetBytes(32), Encoding.ASCII.GetBytes("ssh:")),
        ];

        foreach (byte[] blob in blobs)
        {
            Rig rig = new(Present(blob, "ssh-dss"));

            OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

            Assert.Equal(OpenSshHostKeyFailure.UnsupportedKeyType, result.Failure);
            Assert.Empty(rig.Verifier.Calls);
            Assert.Null(rig.Trust.GetEntry(Host, Port));
        }
    }

    [Fact]
    public async Task ProbeFailure_AStoredDsaPin_IsRefusedAsUnsupported()
    {
        byte[] blob = DsaBlob();
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(blob), "ssh-dss", HostKeySource.ImportedKnownHosts, B64(blob));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.UnsupportedKeyType, result.Failure);
    }

    // ----- certificates --------------------------------------------------------------------

    private static (Ed25519PrivateKeyParameters Priv, byte[] PubBlob) CaKey()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        return (priv, PlainBlob("ssh-ed25519", pub.GetEncoded()));
    }

    private static byte[] HostCertificate(
        byte[] subjectRaw,
        string principal,
        Ed25519PrivateKeyParameters caPrivate,
        byte[] caPublicBlob,
        DateTimeOffset? validBefore = null)
    {
        using var body = new MemoryStream();
        WriteString(body, OpenSshCertificateParser.CertEd25519);
        WriteBytes(body, RandomNumberGenerator.GetBytes(32));
        WriteBytes(body, subjectRaw);
        WriteUInt64(body, 1UL);
        WriteUInt32(body, (uint)OpenSshCertificateType.Host);
        WriteString(body, "host-key-id");
        using (var principals = new MemoryStream())
        {
            WriteString(principals, principal);
            WriteBytes(body, principals.ToArray());
        }

        WriteUInt64(body, (ulong)DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
        WriteUInt64(body, (ulong)(validBefore ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds());
        WriteBytes(body, []);
        WriteBytes(body, []);
        WriteBytes(body, []);
        WriteBytes(body, caPublicBlob);

        byte[] signedData = body.ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, caPrivate);
        signer.BlockUpdate(signedData, 0, signedData.Length);
        byte[] rawSignature = signer.GenerateSignature();

        using var signature = new MemoryStream();
        WriteString(signature, "ssh-ed25519");
        WriteBytes(signature, rawSignature);
        WriteBytes(body, signature.ToArray());
        return body.ToArray();
    }

    private static void TrustCa(Rig rig, byte[] caPublicBlob)
        => rig.Store.LoadPolicyFromKnownHosts($"@cert-authority *.corp.example ssh-ed25519 {B64(caPublicBlob)}\n");

    [Fact]
    public async Task CertificateTrustedByACa_ProceedsWithoutPrompting_AndWritesTheSubjectKey()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] subjectRaw = Ed25519Raw();
        byte[] certificate = HostCertificate(subjectRaw, Host, caPriv, caPub);
        Rig rig = new(Present(certificate, "ssh-ed25519-cert-v01@openssh.com"));
        TrustCa(rig, caPub);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        OpenSshKnownHostsKey key = Assert.Single(result.Keys);
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.Equal(B64(PlainBlob("ssh-ed25519", subjectRaw)), key.PublicKeyBase64);
        Assert.True(result.CertificatePresented);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task CertificateFromATrustedCa_ButInvalidForTheHost_IsRejected()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] certificate = HostCertificate(Ed25519Raw(), "someone-else.corp.example", caPriv, caPub);
        Rig rig = new(Present(certificate, "ssh-ed25519-cert-v01@openssh.com"));
        TrustCa(rig, caPub);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.CertificateInvalid, result.Failure);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task ExpiredCertificateFromATrustedCa_IsRejected()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] certificate = HostCertificate(Ed25519Raw(), Host, caPriv, caPub, DateTimeOffset.UtcNow.AddMinutes(-1));
        Rig rig = new(Present(certificate, "ssh-ed25519-cert-v01@openssh.com"));
        TrustCa(rig, caPub);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.CertificateInvalid, result.Failure);
    }

    [Fact]
    public async Task RevokedCertificate_IsRejected_WithoutPrompting()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] certificate = HostCertificate(Ed25519Raw(), Host, caPriv, caPub);
        Rig rig = new(Present(certificate, "ssh-ed25519-cert-v01@openssh.com"));
        TrustCa(rig, caPub);
        rig.Store.AddRevokedKey("*", Fp(certificate));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Revoked, result.Failure);
        Assert.Empty(rig.Verifier.Calls);
    }

    [Fact]
    public async Task CertificateWhoseSubjectKeyIsRevoked_IsRejected()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] subjectRaw = Ed25519Raw();
        byte[] certificate = HostCertificate(subjectRaw, Host, caPriv, caPub);
        Rig rig = new(Present(certificate, "ssh-ed25519-cert-v01@openssh.com"));
        TrustCa(rig, caPub);
        rig.Store.AddRevokedKey("*", Fp(PlainBlob("ssh-ed25519", subjectRaw)));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.Equal(OpenSshHostKeyFailure.Revoked, result.Failure);
    }

    [Fact]
    public async Task CertificateWithNoMatchingCa_GoesThroughTheDialog_AndWritesTheSubjectKey()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] subjectRaw = Ed25519Raw();
        byte[] certificate = HostCertificate(subjectRaw, Host, caPriv, caPub);
        Rig rig = new(Present(certificate, "ssh-ed25519-cert-v01@openssh.com"), HostKeyDecision.Accept);

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(B64(PlainBlob("ssh-ed25519", subjectRaw)), Assert.Single(result.Keys).PublicKeyBase64);
        Assert.True(result.CertificatePresented);
        VerifierCall call = Assert.Single(rig.Verifier.Calls);
        Assert.Equal(certificate, call.Blob);
        Assert.Equal(Fp(certificate), rig.Trust.GetEntry(Host, Port)!.Fingerprint);
    }

    [Fact]
    public async Task StoredCertificatePin_ProceedsWithoutPrompting_AndWritesTheSubjectKey()
    {
        (Ed25519PrivateKeyParameters caPriv, byte[] caPub) = CaKey();
        byte[] subjectRaw = Ed25519Raw();
        byte[] certificate = HostCertificate(subjectRaw, Host, caPriv, caPub);
        Rig rig = new(presentation: null);
        rig.Trust.Trust(Host, Port, Fp(certificate), "ssh-ed25519-cert-v01@openssh.com", HostKeySource.UserConfirmed, B64(certificate));

        OpenSshHostKeyResolution result = await rig.ResolveDirectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(B64(PlainBlob("ssh-ed25519", subjectRaw)), Assert.Single(result.Keys).PublicKeyBase64);
        Assert.True(result.CertificatePresented);
    }

    // ----- gateway, probe wiring -----------------------------------------------------------

    [Fact]
    public async Task ThroughAGateway_TrustIsAskedAndStoredUnderTheLogicalHost_WhileTheProbeDialsTheTransport()
    {
        byte[] blob = Ed25519Blob();
        Rig rig = new(Present(blob), HostKeyDecision.Accept);

        OpenSshHostKeyResolution result = await rig.Resolver.ResolveAsync(
            LoopbackTransport,
            LoopbackPort,
            "inner.internal",
            2222,
            SshCryptoPolicy.StrictModern,
            ProbeTimeoutMs,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(LoopbackTransport, rig.Probe.LastHost);
        Assert.Equal(LoopbackPort, rig.Probe.LastPort);
        Assert.Equal(SshCryptoPolicy.StrictModern, rig.Probe.LastPolicy);
        Assert.Equal(ProbeTimeoutMs, rig.Probe.LastTimeoutMs);
        VerifierCall call = Assert.Single(rig.Verifier.Calls);
        Assert.Equal("inner.internal", call.Host);
        Assert.Equal(2222, call.Port);
        Assert.True(call.IsViaGateway);
        Assert.NotNull(rig.Trust.GetEntry("inner.internal", 2222));
        Assert.Null(rig.Trust.GetEntry(LoopbackTransport, LoopbackPort));
    }

    [Fact]
    public async Task DirectConnection_IsNotFlaggedAsViaGateway()
    {
        Rig rig = new(Present(Ed25519Blob()));

        await rig.ResolveDirectAsync();

        Assert.False(Assert.Single(rig.Verifier.Calls).IsViaGateway);
    }

    [Fact]
    public async Task ACancelledToken_StopsTheResolutionBeforeProbing()
    {
        Rig rig = new(Present(Ed25519Blob()));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Resolver.ResolveAsync(Host, Port, Host, Port, SshCryptoPolicy.Standard, ProbeTimeoutMs, cts.Token));
        Assert.Equal(0, rig.Probe.Calls);
    }

    [Fact]
    public void TheResolver_NeverReachesThePlinkProbe_AndTheSshNetProbeIsAnOpenSshProbe()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Heimdall.App", "Services", "OpenSshHostKeyResolver.cs"));

        Assert.DoesNotContain("IPlinkHostKeyProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultPlinkHostKeyProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PlinkHostKeyProbe.ProbeAsync", source, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOpenSshHostKeyProbe>(new SshNetOpenSshHostKeyProbe());
    }

    [Fact]
    public void TheLaunchSpecBuilder_NeverEmitsAnSshSideTrustPrompt()
    {
        // The resolver hands ssh a key to accept or nothing: no spec can make ssh ask or learn.
        OpenSshKnownHostsKey key = new("ssh-ed25519", B64(Ed25519Blob()));
        using OpenSshEphemeralKnownHosts file = OpenSshEphemeralKnownHosts.Create(
            [key],
            Path.Combine(Path.GetTempPath(), "heimdall-resolver-tests-" + Guid.NewGuid().ToString("N")));
        try
        {
            IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(new OpenSshLaunchSpec
            {
                Host = "app.corp.example",
                EphemeralKnownHostsPath = file.FilePath,
                PinnedKeyTypes = [key.KeyType],
            });

            Assert.Contains("StrictHostKeyChecking=yes", args);
            Assert.DoesNotContain(args, a => a.Contains("accept-new", StringComparison.Ordinal) || a.Contains("=ask", StringComparison.Ordinal));
            Assert.Contains($"HostKeyAlias={Alias}", args);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(file.FilePath);
            file.Dispose();
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ----- helpers -------------------------------------------------------------------------

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
        => WriteBytes(s, Encoding.ASCII.GetBytes(value));

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
