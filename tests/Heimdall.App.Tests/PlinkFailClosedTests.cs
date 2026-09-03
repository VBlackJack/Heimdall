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

using System.IO;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class PlinkFailClosedTests
{
    [Fact]
    public async Task DecideAsync_NoStoredAndProbeReturnsNull_RejectsHostKeyUnavailable()
    {
        var probe = new FakeProbe(null);
        var verifier = new FakeHostKeyVerifier();
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync(null, probe, verifier, trust);

        Assert.False(decision.ShouldProceed);
        Assert.Equal(SshFailureCode.HostKeyUnavailable, decision.FailureCode);
        Assert.Equal(SshLocalizationKeys.ErrorSshHostKeyUnavailable, decision.FailureMessageKey);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task DecideAsync_NoStoredProbeKeyAndVerifierAccepts_TrustsAndProceeds()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.Accept);
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync(null, probe, verifier, trust);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:presented", decision.Fingerprint);
        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(1, trust.TrustCallCount);
        Assert.Equal(0, trust.TrustForSessionCallCount);
        Assert.Equal("SHA256:presented", trust.LastTrustedFingerprint);
    }

    [Fact]
    public async Task DecideAsync_WithSeparateTransportAndVerificationIdentity_ProbesTransportAndTrustsLogicalHost()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.Accept);
        var trust = new FakeHostKeyTrustService();

        PlinkHostKeyDecision decision = await PlinkHostKeyDecider.DecideAsync(
            transportHost: "127.0.0.1",
            transportPort: 49152,
            verificationHost: "server01.contoso.local",
            verificationPort: 22,
            username: "operator",
            plinkPath: "plink.exe",
            probeTimeoutMs: 1000,
            storedFingerprint: null,
            probe: probe,
            verifier: verifier,
            trustService: trust,
            ct: CancellationToken.None);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("127.0.0.1", probe.LastHost);
        Assert.Equal(49152, probe.LastPort);
        Assert.Equal("server01.contoso.local", verifier.LastHost);
        Assert.Equal(22, verifier.LastPort);
        Assert.Equal("server01.contoso.local", trust.LastTrustedHost);
        Assert.Equal(22, trust.LastTrustedPort);
    }

    [Fact]
    public async Task DecideAsync_NoStoredProbeKeyAndVerifierTrustOnce_TrustsForSessionAndProceeds()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.TrustOnce);
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync(null, probe, verifier, trust);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:presented", decision.Fingerprint);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(1, trust.TrustForSessionCallCount);
        Assert.Equal("SHA256:presented", trust.LastSessionFingerprint);
    }

    [Fact]
    public async Task DecideAsync_NoStoredProbeKeyAndVerifierRejects_ReturnsCancelled()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.Reject);
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync(null, probe, verifier, trust);

        Assert.False(decision.ShouldProceed);
        Assert.Equal(SshFailureCode.Cancelled, decision.FailureCode);
        Assert.Equal(SshLocalizationKeys.ErrorSshCancelled, decision.FailureMessageKey);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(0, trust.TrustForSessionCallCount);
    }

    [Fact]
    public async Task DecideAsync_StoredAndProbeMatches_ProceedsWithoutVerifierOrTrustMutation()
    {
        var probe = new FakeProbe(Presentation("SHA256:stored"));
        var verifier = new FakeHostKeyVerifier();
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync("SHA256:stored", probe, verifier, trust);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:stored", decision.Fingerprint);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(0, trust.TrustForSessionCallCount);
    }

    [Fact]
    public async Task DecideAsync_NullPlinkPathWithStoredFingerprint_ProceedsWithoutProbe()
    {
        FakeProbe probe = new FakeProbe(Presentation("SHA256:ignored"));
        FakeHostKeyVerifier verifier = new FakeHostKeyVerifier();
        FakeHostKeyTrustService trust = new FakeHostKeyTrustService();

        PlinkHostKeyDecision decision = await DecideAsync("SHA256:stored", probe, verifier, trust, plinkPath: null);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:stored", decision.Fingerprint);
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(0, trust.TrustForSessionCallCount);
    }

    [Fact]
    public async Task DecideAsync_BlankPlinkPathWithoutStoredFingerprint_RejectsHostKeyUnavailableWithoutProbe()
    {
        FakeProbe probe = new FakeProbe(Presentation("SHA256:ignored"));
        FakeHostKeyVerifier verifier = new FakeHostKeyVerifier();
        FakeHostKeyTrustService trust = new FakeHostKeyTrustService();

        PlinkHostKeyDecision decision = await DecideAsync(null, probe, verifier, trust, plinkPath: " ");

        Assert.False(decision.ShouldProceed);
        Assert.Equal(SshFailureCode.HostKeyUnavailable, decision.FailureCode);
        Assert.Equal(SshLocalizationKeys.ErrorSshHostKeyUnavailable, decision.FailureMessageKey);
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(0, trust.TrustForSessionCallCount);
    }

    [Fact]
    public async Task DecideAsync_StoredAndProbeReturnsNull_ProceedsWithStoredFingerprint()
    {
        var probe = new FakeProbe(null);
        var verifier = new FakeHostKeyVerifier();
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync("SHA256:stored", probe, verifier, trust);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:stored", decision.Fingerprint);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(0, trust.TrustForSessionCallCount);
    }

    [Fact]
    public async Task DecideAsync_StoredMismatchAndVerifierAccepts_ReplacesTrustAndProceeds()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.Accept);
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync("SHA256:stored", probe, verifier, trust);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:presented", decision.Fingerprint);
        Assert.Equal(1, trust.TrustCallCount);
        Assert.Equal("SHA256:presented", trust.LastTrustedFingerprint);
        Assert.Equal("SHA256:stored", verifier.LastStoredFingerprint);
    }

    [Fact]
    public async Task DecideAsync_StoredMismatchAndVerifierTrustOnce_TrustsForSessionAndProceeds()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.TrustOnce);
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync("SHA256:stored", probe, verifier, trust);

        Assert.True(decision.ShouldProceed);
        Assert.Equal("SHA256:presented", decision.Fingerprint);
        Assert.Equal(0, trust.TrustCallCount);
        Assert.Equal(1, trust.TrustForSessionCallCount);
        Assert.Equal("SHA256:presented", trust.LastSessionFingerprint);
    }

    [Fact]
    public async Task DecideAsync_StoredMismatchAndVerifierRejects_ReturnsHostKeyMismatch()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        var verifier = new FakeHostKeyVerifier(HostKeyDecision.Reject);
        var trust = new FakeHostKeyTrustService();

        var decision = await DecideAsync("SHA256:stored", probe, verifier, trust);

        Assert.False(decision.ShouldProceed);
        Assert.Equal(SshFailureCode.HostKeyMismatch, decision.FailureCode);
        Assert.Equal(SshLocalizationKeys.ErrorHostKeyMismatch, decision.FailureMessageKey);
        Assert.Equal("SHA256:stored", decision.StoredFingerprint);
        Assert.Equal("SHA256:presented", decision.PresentedFingerprint);
    }

    [Fact]
    public async Task DecideAsync_InvalidArguments_Throw()
    {
        var probe = new FakeProbe(null);
        var verifier = new FakeHostKeyVerifier();
        var trust = new FakeHostKeyTrustService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => PlinkHostKeyDecider.DecideAsync(
                " ",
                22,
                "user",
                "plink.exe",
                1000,
                null,
                probe,
                verifier,
                trust,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DecideAsync(null, null!, verifier, trust));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DecideAsync(null, probe, null!, trust));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DecideAsync(null, probe, verifier, null!));
    }

    [Fact]
    public async Task DecideAsync_CancelledToken_ThrowsBeforeProbe()
    {
        var probe = new FakeProbe(Presentation("SHA256:presented"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => PlinkHostKeyDecider.DecideAsync(
                "gateway.local",
                22,
                "user",
                "plink.exe",
                1000,
                null,
                probe,
                new FakeHostKeyVerifier(),
                new FakeHostKeyTrustService(),
                cts.Token));

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task TunnelService_PlinkFallback_FailsClosed_WhenHostKeyCannotBeResolved()
    {
        var probe = new FakeProbe(null);
        using var tunnelManager = new TunnelManager();
        var trust = new FakeHostKeyTrustService();
        var localizer = await CreateLocalizerAsync();
        var service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            trust,
            new ConnectionStateMachine(),
            localizer,
            RejectingHostKeyVerifier.Instance,
            probe);
        var plinkPath = Path.GetTempFileName();

        try
        {
            // gatewayRoute is required: an optional trailing argument was how the Plink path
            // came to open tunnels with no route at all.
            var result = await service.EstablishPlinkTunnelAsync(
                "server-1",
                new SshConnectionParams
                {
                    Host = "gateway.local",
                    Port = 22,
                    Username = "user"
                },
                "10.0.0.5",
                3389,
                50123,
                new AppSettings
                {
                    PlinkPath = plinkPath,
                    HostKeyProbeTimeoutMs = 1000
                },
                "gw-key",
                CancellationToken.None,
                gatewayRoute: "Paris datacentre");

            Assert.False(result.Success);
            Assert.Equal(SshFailureCode.HostKeyUnavailable, result.FailureCode);
            Assert.Equal(1, probe.CallCount);
            Assert.Empty(tunnelManager.GetActiveTunnels());
            Assert.Contains("could not verify", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(plinkPath);
        }
    }

    private static Task<PlinkHostKeyDecision> DecideAsync(
        string? storedFingerprint,
        IPlinkHostKeyProbe probe,
        IHostKeyVerifier verifier,
        IHostKeyTrustService trustService,
        string? plinkPath = "plink.exe")
    {
        return PlinkHostKeyDecider.DecideAsync(
            "gateway.local",
            22,
            "user",
            plinkPath,
            1000,
            storedFingerprint,
            probe,
            verifier,
            trustService,
            CancellationToken.None);
    }

    private static PlinkHostKeyPresentation Presentation(string fingerprint)
        => new("ssh-ed25519", fingerprint);

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(FindLocalesPath(), "en").ConfigureAwait(false);
        return localizer;
    }

    private static string FindLocalesPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "locales");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Cannot find locales directory.");
    }

    private sealed class FakeProbe : IPlinkHostKeyProbe
    {
        private readonly PlinkHostKeyPresentation? _presentation;

        public FakeProbe(PlinkHostKeyPresentation? presentation)
        {
            _presentation = presentation;
        }

        public int CallCount { get; private set; }
        public string? LastHost { get; private set; }
        public int? LastPort { get; private set; }

        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct)
        {
            CallCount++;
            LastHost = host;
            LastPort = port;
            return Task.FromResult(_presentation);
        }
    }

    private sealed class FakeHostKeyVerifier : IHostKeyVerifier
    {
        private readonly HostKeyDecision _decision;

        public FakeHostKeyVerifier(HostKeyDecision decision = HostKeyDecision.Accept)
        {
            _decision = decision;
        }

        public int CallCount { get; private set; }
        public string? LastHost { get; private set; }
        public int? LastPort { get; private set; }
        public string? LastStoredFingerprint { get; private set; }

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default)
        {
            CallCount++;
            LastHost = host;
            LastPort = port;
            LastStoredFingerprint = storedFingerprint;
            return Task.FromResult(_decision);
        }
    }

    private sealed class FakeHostKeyTrustService : IHostKeyTrustService
    {
        public int TrustCallCount { get; private set; }
        public int TrustForSessionCallCount { get; private set; }
        public string? LastTrustedHost { get; private set; }
        public int? LastTrustedPort { get; private set; }
        public string? LastTrustedFingerprint { get; private set; }
        public string? LastSessionHost { get; private set; }
        public int? LastSessionPort { get; private set; }
        public string? LastSessionFingerprint { get; private set; }

        public event Action<string, HostKeyEntry>? EntryTrusted { add { } remove { } }
        public event Action<string>? EntryRemoved { add { } remove { } }
        public event Action<string, HostKeyEntry, HostKeyEntry>? EntryReplaced { add { } remove { } }

        public HostKeyEntry? GetEntry(string host, int port) => null;

        public HostKeyEntry? GetEffectiveEntry(string host, int port) => null;

        public IReadOnlyList<(string HostPort, HostKeyEntry Entry)> GetAllEntries() => [];

        public HostKeyVerifyResult Verify(
            string host,
            int port,
            string presentedFingerprint,
            string algorithm)
        {
            throw new NotSupportedException();
        }

        public void Trust(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            HostKeySource source,
            string? publicKeyBase64 = null)
        {
            TrustCallCount++;
            LastTrustedHost = host;
            LastTrustedPort = port;
            LastTrustedFingerprint = fingerprint;
        }

        public void TrustForSession(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            string? publicKeyBase64 = null)
        {
            TrustForSessionCallCount++;
            LastSessionHost = host;
            LastSessionPort = port;
            LastSessionFingerprint = fingerprint;
        }

        public void Import(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            DateTimeOffset importedAt,
            string? publicKeyBase64 = null)
        {
            throw new NotSupportedException();
        }

        public bool Remove(string host, int port) => false;
    }
}
