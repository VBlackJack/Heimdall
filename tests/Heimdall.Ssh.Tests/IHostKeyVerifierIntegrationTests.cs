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

using System.Diagnostics;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Mirrors the host-key decision flow used by SshConnectionFactory and the plink fallback:
/// known+matching hosts proceed silently, otherwise the verifier decides whether the
/// presented fingerprint is persisted through HostKeyStore.Trust.
/// </summary>
public sealed class IHostKeyVerifierIntegrationTests
{
    private const string Host = "host.example.com";
    private const int Port = 22;
    private const string Algorithm = "ssh-ed25519";
    private static readonly byte[] FirstKey = [0x01, 0x02, 0x03, 0x04, 0x05];
    private static readonly byte[] DifferentKey = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];

    [Fact]
    public async Task MatchingKnownHost_SkipsVerifier_AndReturnsPinnedVerifier()
    {
        var store = new HostKeyStore();
        var trustedFingerprint = HostKeyStore.ComputeFingerprint(FirstKey);
        store.Trust(Host, Port, trustedFingerprint);

        var verifier = new FixedDecisionVerifier(HostKeyDecision.Reject);
        var trustedEvents = new List<(string Host, string Fingerprint, bool Trusted)>();
        store.HostKeyEvent += (host, fingerprint, trusted) =>
            trustedEvents.Add((host, fingerprint, trusted));

        var pinned = await ResolvePresentationAsync(store, verifier, FirstKey);

        Assert.True(pinned.Matches(Host, Port, trustedFingerprint));
        Assert.Equal(trustedFingerprint, store.GetFingerprint(Host, Port));
        Assert.Equal(0, verifier.CallCount);
        var trustedEvent = Assert.Single(trustedEvents);
        Assert.Equal($"{Host}:{Port}", trustedEvent.Host);
        Assert.Equal(trustedFingerprint, trustedEvent.Fingerprint);
        Assert.True(trustedEvent.Trusted);
    }

    [Fact]
    public async Task FirstUseAccept_TrustsFingerprint_AndReturnsPinnedVerifier()
    {
        var store = new HostKeyStore();
        var verifier = new FixedDecisionVerifier(HostKeyDecision.Accept);
        var trustedEvents = new List<(string Host, string Fingerprint, bool Trusted)>();
        store.HostKeyEvent += (host, fingerprint, trusted) =>
            trustedEvents.Add((host, fingerprint, trusted));

        var pinned = await ResolvePresentationAsync(store, verifier, FirstKey);
        var storedFingerprint = store.GetFingerprint(Host, Port);

        Assert.NotNull(storedFingerprint);
        Assert.True(pinned.Matches(Host, Port, storedFingerprint!));
        Assert.Equal(1, verifier.CallCount);
        Assert.Null(verifier.LastStoredFingerprint);
        Assert.DoesNotContain(nameof(SshConnectionFactory.AttachHostKeyVerification), verifier.LastStackTrace);
        Assert.DoesNotContain("HostKeyReceived", verifier.LastStackTrace);
        var trustedEvent = Assert.Single(trustedEvents);
        Assert.Equal($"{Host}:{Port}", trustedEvent.Host);
        Assert.Equal(storedFingerprint, trustedEvent.Fingerprint);
        Assert.True(trustedEvent.Trusted);
    }

    [Fact]
    public async Task FirstUseReject_ThrowsHostKeyRejected_AndDoesNotEmitEvent()
    {
        var store = new HostKeyStore();
        var verifier = new FixedDecisionVerifier(HostKeyDecision.Reject);
        var eventRaised = false;
        store.HostKeyEvent += (_, _, _) => eventRaised = true;

        await Assert.ThrowsAsync<HostKeyRejectedException>(
            () => ResolvePresentationAsync(store, verifier, FirstKey));

        Assert.Null(store.GetFingerprint(Host, Port));
        Assert.Equal(1, verifier.CallCount);
        Assert.False(eventRaised);
    }

    [Fact]
    public async Task FirstUseTrustOnce_ReturnsPinnedVerifier_WithoutPersisting()
    {
        var store = new HostKeyStore();
        var verifier = new FixedDecisionVerifier(HostKeyDecision.TrustOnce);
        var eventRaised = false;
        store.HostKeyEvent += (_, _, _) => eventRaised = true;

        var pinned = await ResolvePresentationAsync(store, verifier, FirstKey);
        var fingerprint = HostKeyStore.ComputeFingerprint(FirstKey);

        Assert.True(pinned.Matches(Host, Port, fingerprint));
        Assert.Null(store.GetFingerprint(Host, Port));
        Assert.False(eventRaised);
        Assert.Equal(1, verifier.CallCount);

        var rejectingVerifier = new FixedDecisionVerifier(HostKeyDecision.Reject);
        var secondPinned = await ResolvePresentationAsync(store, rejectingVerifier, FirstKey);

        Assert.True(secondPinned.Matches(Host, Port, fingerprint));
        Assert.Equal(0, rejectingVerifier.CallCount);
    }

    [Fact]
    public async Task MismatchAccept_OverwritesStoredFingerprint_AndReturnsNewPin()
    {
        var store = new HostKeyStore();
        store.Trust(Host, Port, HostKeyStore.ComputeFingerprint(FirstKey));
        var verifier = new FixedDecisionVerifier(HostKeyDecision.Accept);

        var pinned = await ResolvePresentationAsync(store, verifier, DifferentKey);
        var storedFingerprint = store.GetFingerprint(Host, Port);

        Assert.Equal(1, verifier.CallCount);
        Assert.NotNull(verifier.LastStoredFingerprint);
        Assert.Equal(HostKeyStore.ComputeFingerprint(DifferentKey), storedFingerprint);
        Assert.True(pinned.Matches(Host, Port, storedFingerprint!));
    }

    [Fact]
    public async Task MismatchTrustOnce_PreservesStoredFingerprint_AndReturnsNewPin()
    {
        var store = new HostKeyStore();
        var originalFingerprint = HostKeyStore.ComputeFingerprint(FirstKey);
        var replacementFingerprint = HostKeyStore.ComputeFingerprint(DifferentKey);
        store.Trust(Host, Port, originalFingerprint);
        var verifier = new FixedDecisionVerifier(HostKeyDecision.TrustOnce);

        var pinned = await ResolvePresentationAsync(store, verifier, DifferentKey);

        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(originalFingerprint, verifier.LastStoredFingerprint);
        Assert.Equal(originalFingerprint, store.GetFingerprint(Host, Port));
        Assert.True(pinned.Matches(Host, Port, replacementFingerprint));

        var rejectingVerifier = new FixedDecisionVerifier(HostKeyDecision.Reject);
        var secondPinned = await ResolvePresentationAsync(store, rejectingVerifier, DifferentKey);

        Assert.True(secondPinned.Matches(Host, Port, replacementFingerprint));
        Assert.Equal(0, rejectingVerifier.CallCount);
        Assert.Equal(originalFingerprint, store.GetFingerprint(Host, Port));
    }

    [Fact]
    public async Task MismatchReject_ThrowsHostKeyRejected_AndPreservesStoredFingerprint()
    {
        var store = new HostKeyStore();
        var originalFingerprint = HostKeyStore.ComputeFingerprint(FirstKey);
        store.Trust(Host, Port, originalFingerprint);
        var verifier = new FixedDecisionVerifier(HostKeyDecision.Reject);
        var eventRaised = false;
        store.HostKeyEvent += (_, _, _) => eventRaised = true;

        var ex = await Assert.ThrowsAsync<HostKeyRejectedException>(
            () => ResolvePresentationAsync(store, verifier, DifferentKey));

        Assert.True(ex.IsMismatch);
        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(originalFingerprint, verifier.LastStoredFingerprint);
        Assert.Equal(originalFingerprint, store.GetFingerprint(Host, Port));
        Assert.False(eventRaised);
    }

    [Fact]
    public void AttachHostKeyVerification_RejectsInteractiveVerifierSynchronously()
    {
        using var client = new SshClient(new ConnectionInfo(
            Host,
            Port,
            "user",
            new NoneAuthenticationMethod("user")));
        var verifier = new SlowVerifier();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SingleThreadSynchronizationContext());
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() =>
                SshConnectionFactory.AttachHostKeyVerification(
                    client,
                    Host,
                    Port,
                    new HostKeyStore(),
                    verifier));
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 50);
            Assert.Equal(0, verifier.CallCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void EvaluatePinnedHostKey_CompletesSynchronously_WithoutVerifierCallback()
    {
        var fingerprint = HostKeyStore.ComputeFingerprint(FirstKey);
        var pinned = new PinnedFingerprintVerifier(Host, Port, fingerprint);
        var stopwatch = Stopwatch.StartNew();

        var outcome = SshConnectionFactory.EvaluatePinnedHostKey(
            Host,
            Port,
            Algorithm,
            FirstKey,
            pinned);

        stopwatch.Stop();
        Assert.True(outcome.Trusted);
        Assert.Equal(fingerprint, outcome.Fingerprint);
        Assert.True(stopwatch.ElapsedMilliseconds < 50);
    }

    [Fact]
    public async Task ExplicitAutoAcceptInjection_RemainsAvailableForTests()
    {
        var store = new HostKeyStore();

        var pinned = await ResolvePresentationAsync(
            store,
            AutoAcceptHostKeyVerifier.Instance,
            FirstKey);

        var fingerprint = store.GetFingerprint(Host, Port);
        Assert.NotNull(fingerprint);
        Assert.True(pinned.Matches(Host, Port, fingerprint!));
    }

    [Fact]
    public async Task SshShellSession_WithNullVerifier_ThrowsBeforeNetwork()
    {
        using var session = new SshShellSession();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.ConnectAsync(
                CreateConnectionParams(),
                hostKeyStore: new HostKeyStore(),
                hostKeyVerifier: null!));

        Assert.Equal("hostKeyVerifier", ex.ParamName);
    }

    [Fact]
    public async Task SftpBrowser_WithNullVerifier_ThrowsBeforeNetwork()
    {
        using var browser = new SftpBrowser();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => browser.ConnectAsync(
                CreateConnectionParams(),
                hostKeyStore: new HostKeyStore(),
                hostKeyVerifier: null!));

        Assert.Equal("hostKeyVerifier", ex.ParamName);
    }

    private static Task<PinnedFingerprintVerifier> ResolvePresentationAsync(
        HostKeyStore store,
        IHostKeyVerifier verifier,
        byte[] hostKey,
        CancellationToken ct = default)
    {
        var presentation = new SshConnectionFactory.HostKeyPresentation(
            Host,
            Port,
            Algorithm,
            HostKeyStore.ComputeFingerprint(hostKey),
            hostKey);
        return SshConnectionFactory.ResolvePresentedHostKeyAsync(
            Host,
            Port,
            presentation,
            store,
            verifier,
            ct);
    }

    private static SshConnectionParams CreateConnectionParams()
    {
        return new SshConnectionParams
        {
            Host = "127.0.0.1",
            Port = 65000,
            Username = "user",
            Password = "secret",
            ConnectTimeout = TimeSpan.FromMilliseconds(50)
        };
    }

    private sealed class FixedDecisionVerifier(HostKeyDecision decision) : IHostKeyVerifier
    {
        public int CallCount { get; private set; }

        public string? LastStoredFingerprint { get; private set; }

        public string? LastStackTrace { get; private set; }

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default)
        {
            CallCount++;
            LastStoredFingerprint = storedFingerprint;
            LastStackTrace = Environment.StackTrace;
            return Task.FromResult(decision);
        }
    }

    private sealed class SlowVerifier : IHostKeyVerifier
    {
        public int CallCount { get; private set; }

        public async Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default)
        {
            CallCount++;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return HostKeyDecision.Accept;
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }
}
