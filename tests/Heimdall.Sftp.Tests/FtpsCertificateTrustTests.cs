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

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Heimdall.Core.Certificates;

namespace Heimdall.Sftp.Tests;

public sealed class FtpsCertificateTrustTests
{
    [Fact]
    public void ValidateServerCertificate_ValidUnknownCertificate_PinsSilently()
    {
        using var certificate = CreateCertificate("CN=ftps.example.com");
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.Reject);
        var browser = new FtpBrowser(store, verifier);

        var accepted = browser.ValidateServerCertificate(
            "ftps.example.com",
            21,
            certificate,
            chain: null,
            SslPolicyErrors.None,
            policyErrorMessage: null);

        var entry = store.GetEntry("ftps.example.com", 21);
        Assert.True(accepted);
        Assert.NotNull(entry);
        Assert.Equal(FtpsCertificateSource.SystemValidated, entry.Source);
        Assert.Equal(CertificateFingerprint.ComputeSha256(certificate), entry.Fingerprint);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public void ValidateServerCertificate_InvalidUnknownCertificate_AcceptTrustsPersistently()
    {
        using var certificate = CreateCertificate("CN=self-signed.example.com");
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.Accept);
        var browser = new FtpBrowser(store, verifier);

        var accepted = browser.ValidateServerCertificate(
            "self-signed.example.com",
            990,
            certificate,
            chain: null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            "self-signed certificate");

        var entry = store.GetEntry("self-signed.example.com", 990);
        Assert.True(accepted);
        Assert.NotNull(entry);
        Assert.Equal(FtpsCertificateSource.UserConfirmed, entry.Source);
        Assert.Equal("self-signed certificate", entry.ValidationErrors);
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public void ValidateServerCertificate_InvalidUnknownCertificate_TrustOnceDoesNotPersist()
    {
        using var certificate = CreateCertificate("CN=session.example.com");
        using var replacement = CreateCertificate("CN=session.example.com");
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.TrustOnce);
        var browser = new FtpBrowser(store, verifier);

        var accepted = browser.ValidateServerCertificate(
            "session.example.com",
            21,
            certificate,
            chain: null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            "self-signed certificate");
        var acceptedAgain = browser.ValidateServerCertificate(
            "session.example.com",
            21,
            certificate,
            chain: null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            "self-signed certificate");

        Assert.True(accepted);
        Assert.True(acceptedAgain);
        Assert.Null(store.GetEntry("session.example.com", 21));
        Assert.NotNull(store.GetSessionEntry("session.example.com", 21));
        Assert.Equal(1, verifier.CallCount);

        var ex = Assert.Throws<FtpsCertificateRejectedException>(() =>
            browser.ValidateServerCertificate(
                "session.example.com",
                21,
                replacement,
                chain: null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                "self-signed certificate"));
        Assert.True(ex.IsMismatch);
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public void ValidateServerCertificate_InvalidUnknownCertificate_RejectThrows()
    {
        using var certificate = CreateCertificate("CN=rejected.example.com");
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.Reject);
        var browser = new FtpBrowser(store, verifier);

        var ex = Assert.Throws<FtpsCertificateRejectedException>(() =>
            browser.ValidateServerCertificate(
                "rejected.example.com",
                21,
                certificate,
                chain: null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                "self-signed certificate"));

        Assert.False(ex.IsMismatch);
        Assert.Equal(FtpsCertificateRejectionReason.RejectedByUser, ex.Reason);
        Assert.Null(store.GetEntry("rejected.example.com", 21));
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public void ValidateServerCertificate_PinnedMismatchRejectsWithoutPrompt()
    {
        using var original = CreateCertificate("CN=rotate.example.com");
        using var replacement = CreateCertificate("CN=rotate.example.com");
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.Accept);
        var browser = new FtpBrowser(store, verifier);

        Assert.True(browser.ValidateServerCertificate(
            "rotate.example.com",
            21,
            original,
            chain: null,
            SslPolicyErrors.None,
            policyErrorMessage: null));

        var ex = Assert.Throws<FtpsCertificateRejectedException>(() =>
            browser.ValidateServerCertificate(
                "rotate.example.com",
                21,
                replacement,
                chain: null,
                SslPolicyErrors.None,
                policyErrorMessage: null));

        Assert.True(ex.IsMismatch);
        Assert.Equal(CertificateFingerprint.ComputeSha256(original), ex.StoredFingerprint);
        Assert.Equal(CertificateFingerprint.ComputeSha256(replacement), ex.PresentedFingerprint);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public void ValidateServerCertificate_ExpiredSessionPin_IsRejected()
    {
        using var certificate = CreateExpiredCertificate("CN=session-expired.example.com");
        using var chain = BuildChain(certificate);
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.Accept);
        var browser = new FtpBrowser(store, verifier);
        store.TrustForSession(
            "session-expired.example.com",
            21,
            CreateEntry(certificate, FtpsCertificateSource.UserConfirmed));

        var ex = Assert.Throws<FtpsCertificateRejectedException>(() =>
            browser.ValidateServerCertificate(
                "session-expired.example.com",
                21,
                certificate,
                chain,
                SslPolicyErrors.RemoteCertificateChainErrors,
                "certificate expired"));

        Assert.False(ex.IsMismatch);
        Assert.Equal(CertificateFingerprint.ComputeSha256(certificate), ex.PresentedFingerprint);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public void ValidateServerCertificate_ExpiredPersistentPin_IsRejectedWithoutRefreshingLastSeen()
    {
        using var certificate = CreateExpiredCertificate("CN=persistent-expired.example.com");
        using var chain = BuildChain(certificate);
        var store = new FtpsCertificateStore();
        var verifier = new RecordingVerifier(FtpsCertificateDecision.Accept);
        var browser = new FtpBrowser(store, verifier);
        FtpsCertificateEntry original = CreateEntry(
            certificate,
            FtpsCertificateSource.UserConfirmed) with
        {
            LastSeen = DateTimeOffset.UtcNow.AddDays(-7)
        };
        store.Trust("persistent-expired.example.com", 990, original);

        var ex = Assert.Throws<FtpsCertificateRejectedException>(() =>
            browser.ValidateServerCertificate(
                "persistent-expired.example.com",
                990,
                certificate,
                chain,
                SslPolicyErrors.RemoteCertificateChainErrors,
                "certificate expired"));

        Assert.False(ex.IsMismatch);

        // Its own reason: an expired pin used to be reported in the words of a Reject click.
        Assert.Equal(FtpsCertificateRejectionReason.PinnedCertificateInvalid, ex.Reason);
        Assert.Equal(original.LastSeen, store.GetEntry("persistent-expired.example.com", 990)!.LastSeen);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public void RefreshLastSeen_EmitsUpdatedEntryForPersistence()
    {
        using var certificate = CreateCertificate("CN=refresh.example.com");
        var store = new FtpsCertificateStore();
        FtpsCertificateEntry original = CreateEntry(
            certificate,
            FtpsCertificateSource.UserConfirmed) with
        {
            LastSeen = DateTimeOffset.UtcNow.AddDays(-7)
        };
        store.LoadEntriesFromConfig(
        [
            new KeyValuePair<string, FtpsCertificateEntry>(
                FtpsCertificateStore.MakeKey("refresh.example.com", 21),
                original)
        ]);
        (string Key, FtpsCertificateEntry Entry)? updated = null;
        store.CertificateTrusted += (key, entry) => updated = (key, entry);

        store.RefreshLastSeen("refresh.example.com", 21);

        Assert.NotNull(updated);
        Assert.Equal("refresh.example.com:21", updated.Value.Key);
        Assert.True(updated.Value.Entry.LastSeen > original.LastSeen);
    }

    [Theory]
    [InlineData("ftp.example.com", 21, "ftp.example.com:21")]
    [InlineData("2001:db8::1", 990, "[2001:db8::1]:990")]
    // DNS names are case-insensitive; two spellings used to keep two pins, and the second
    // spelling met a first-use prompt instead of a change detection.
    [InlineData("FTP.Example.COM", 21, "ftp.example.com:21")]
    [InlineData("2001:DB8::1", 990, "[2001:db8::1]:990")]
    public void MakeKey_FormatsHostPortForPersistence(string host, int port, string expected)
    {
        Assert.Equal(expected, FtpsCertificateStore.MakeKey(host, port));
    }

    [Fact]
    public void LoadEntriesFromConfig_FoldsTheCaseOfKeysWrittenBeforeTheRule()
    {
        using var certificate = CreateCertificate("CN=case.example.com");
        var store = new FtpsCertificateStore();
        FtpsCertificateEntry entry = CreateEntry(certificate, FtpsCertificateSource.SystemValidated);

        store.LoadEntriesFromConfig([new KeyValuePair<string, FtpsCertificateEntry>("Case.Example.com:21", entry)]);

        Assert.NotNull(store.GetEntry("case.example.com", 21));
        Assert.NotNull(store.GetEntry("CASE.EXAMPLE.COM", 21));
    }

    /// <remarks>
    /// The pin exists against one adversary: the holder of a stolen, since-revoked key who
    /// blocks OCSP and CRL so the status comes back unknown. A system-validated pin refuses that
    /// status; a self-signed pin the user confirmed never had a checkable status.
    /// </remarks>
    [Theory]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown, FtpsCertificateSource.SystemValidated, true)]
    [InlineData(X509ChainStatusFlags.OfflineRevocation, FtpsCertificateSource.SystemValidated, true)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown, FtpsCertificateSource.UserConfirmed, false)]
    [InlineData(X509ChainStatusFlags.OfflineRevocation, FtpsCertificateSource.UserConfirmed, false)]
    [InlineData(X509ChainStatusFlags.Revoked, FtpsCertificateSource.UserConfirmed, true)]
    [InlineData(X509ChainStatusFlags.UntrustedRoot, FtpsCertificateSource.UserConfirmed, false)]
    [InlineData(X509ChainStatusFlags.NoError, FtpsCertificateSource.SystemValidated, false)]
    public void IsNonOverridableChainError_RefusesAnUnknownRevocationStatusOnASystemValidatedPin(
        X509ChainStatusFlags status,
        FtpsCertificateSource source,
        bool expected)
    {
        Assert.Equal(expected, FtpBrowser.IsNonOverridableChainError(status, source));
    }

    [Fact]
    public void CreateConfig_PinsTheTlsFloorToOneTwoAndOneThree()
    {
        FluentFTP.FtpConfig config = FtpBrowser.CreateConfig(passiveMode: true, useSsl: true);

        Assert.Equal(
            System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            config.SslProtocols);
    }

    private static X509Certificate2 CreateCertificate(string subjectName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subjectName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
    }

    private static X509Certificate2 CreateExpiredCertificate(string subjectName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subjectName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(-1));
        return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
    }

    private static X509Chain BuildChain(X509Certificate2 certificate)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        _ = chain.Build(certificate);
        return chain;
    }

    private static FtpsCertificateEntry CreateEntry(
        X509Certificate2 certificate,
        FtpsCertificateSource source)
    {
        var now = DateTimeOffset.UtcNow;
        return new FtpsCertificateEntry(
            CertificateFingerprint.ComputeSha256(certificate),
            now,
            now,
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore,
            certificate.NotAfter,
            source);
    }

    private sealed class RecordingVerifier(FtpsCertificateDecision decision) : IFtpsCertificateVerifier
    {
        public int CallCount { get; private set; }

        public Task<FtpsCertificateDecision> VerifyAsync(
            FtpsCertificatePrompt prompt,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(decision);
        }
    }
}
