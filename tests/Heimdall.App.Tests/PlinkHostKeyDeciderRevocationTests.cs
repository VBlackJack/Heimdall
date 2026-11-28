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

using Heimdall.App.Services;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class PlinkHostKeyDeciderRevocationTests
{
    private sealed class StubProbe(PlinkHostKeyPresentation? presentation) : IPlinkHostKeyProbe
    {
        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct)
            => Task.FromResult(presentation);
    }

    private sealed class CountingAcceptVerifier : IHostKeyVerifier
    {
        public int Calls { get; private set; }

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(HostKeyDecision.Accept);
        }
    }

    [Fact]
    public async Task DecideAsync_RevokedStoredFingerprint_BlocksWithoutProceedingOrPrompting()
    {
        var store = new HostKeyStore();
        var trustService = new HostKeyTrustService(store);
        const string revokedFp = "SHA256:RevokedStoredKey123456789012345678901234567";
        store.AddRevokedKey("prod.example.com", revokedFp);

        var verifier = new CountingAcceptVerifier();
        var decision = await PlinkHostKeyDecider.DecideAsync(
            "prod.example.com",
            22,
            "ops",
            plinkPath: null,
            probeTimeoutMs: 5000,
            storedFingerprint: revokedFp,
            new StubProbe(null),
            verifier,
            trustService,
            CancellationToken.None);

        Assert.False(decision.ShouldProceed);
        Assert.Equal(SshFailureCode.HostKeyRevoked, decision.FailureCode);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task DecideAsync_RevokedPresentedFingerprint_BlocksFirstUseAndMismatchWithoutPrompting()
    {
        var store = new HostKeyStore();
        var trustService = new HostKeyTrustService(store);
        const string revokedFp = "SHA256:RevokedPresentedKey123456789012345678901234";
        store.AddRevokedKey("*.example.com", revokedFp);

        var presentation = new PlinkHostKeyPresentation("ssh-ed25519", revokedFp, null);
        var verifier = new CountingAcceptVerifier();

        var firstUseDecision = await PlinkHostKeyDecider.DecideAsync(
            "app.example.com",
            22,
            "ops",
            plinkPath: "plink.exe",
            probeTimeoutMs: 5000,
            storedFingerprint: null,
            new StubProbe(presentation),
            verifier,
            trustService,
            CancellationToken.None);

        Assert.False(firstUseDecision.ShouldProceed);
        Assert.Equal(SshFailureCode.HostKeyRevoked, firstUseDecision.FailureCode);
        Assert.Equal(0, verifier.Calls);

        var mismatchDecision = await PlinkHostKeyDecider.DecideAsync(
            "app.example.com",
            22,
            "ops",
            plinkPath: "plink.exe",
            probeTimeoutMs: 5000,
            storedFingerprint: "SHA256:OldTrustedKey123456789012345678901234567890",
            new StubProbe(presentation),
            verifier,
            trustService,
            CancellationToken.None);

        Assert.False(mismatchDecision.ShouldProceed);
        Assert.Equal(SshFailureCode.HostKeyRevoked, mismatchDecision.FailureCode);
        Assert.Equal(0, verifier.Calls);
    }
}
