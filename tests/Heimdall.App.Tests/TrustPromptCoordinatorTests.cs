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

using System.Collections.Concurrent;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class TrustPromptCoordinatorTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestAsync_ConcurrentRequests_SerializesCoalescesAndCompletesWithinBound()
    {
        var coordinator = new TrustPromptCoordinator();
        var releaseDisplays = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronousCallerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var displayCounts = new ConcurrentDictionary<TrustPromptKey, int>();
        int activeDisplays = 0;
        int maximumActiveDisplays = 0;

        TrustPromptKey sshKey = CreateKey(
            TrustPromptKind.SshHostKey,
            "shared.example.com",
            "SHA256:shared");
        TrustPromptKey secondSshKey = CreateKey(
            TrustPromptKind.SshHostKey,
            "second.example.com",
            "SHA256:second");
        TrustPromptKey ftpsKey = CreateKey(
            TrustPromptKind.FtpsCertificate,
            "shared.example.com",
            "SHA256:shared");

        async Task<TestDecision> DisplayAsync(
            TrustPromptKey key,
            TestDecision decision,
            CancellationToken ct)
        {
            _ = displayCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
            int active = Interlocked.Increment(ref activeDisplays);
            UpdateMaximum(ref maximumActiveDisplays, active);
            try
            {
                await releaseDisplays.Task.WaitAsync(ct);
                await Task.Yield();
                return decision;
            }
            finally
            {
                _ = Interlocked.Decrement(ref activeDisplays);
            }
        }

        Task<TestDecision> sshFirst = coordinator.RequestAsync(
            sshKey,
            ct => DisplayAsync(sshKey, TestDecision.TrustOnce, ct),
            TestDecision.Reject);
        Task<TestDecision> sshDuplicateOne = coordinator.RequestAsync(
            sshKey,
            ct => DisplayAsync(sshKey, TestDecision.Accept, ct),
            TestDecision.Reject);
        Task<TestDecision> sshDuplicateTwo = coordinator.RequestAsync(
            sshKey,
            ct => DisplayAsync(sshKey, TestDecision.Reject, ct),
            TestDecision.Reject);
        Task<TestDecision> ftpsFirst = coordinator.RequestAsync(
            ftpsKey,
            ct => DisplayAsync(ftpsKey, TestDecision.Accept, ct),
            TestDecision.Reject);
        Task<TestDecision> ftpsDuplicate = coordinator.RequestAsync(
            ftpsKey,
            ct => DisplayAsync(ftpsKey, TestDecision.Reject, ct),
            TestDecision.Reject);
        Task<TestDecision> synchronousCaller = Task.Run(() =>
        {
            synchronousCallerStarted.TrySetResult();
            return coordinator.RequestAsync(
                    secondSshKey,
                    ct => DisplayAsync(secondSshKey, TestDecision.Accept, ct),
                    TestDecision.Reject)
                .GetAwaiter()
                .GetResult();
        });

        await synchronousCallerStarted.Task.WaitAsync(CompletionTimeout);
        releaseDisplays.TrySetResult();

        TestDecision[] results = await Task.WhenAll(
        [
            sshFirst,
            sshDuplicateOne,
            sshDuplicateTwo,
            ftpsFirst,
            ftpsDuplicate,
            synchronousCaller
        ]).WaitAsync(CompletionTimeout);

        Assert.Equal(1, maximumActiveDisplays);
        Assert.Equal(3, displayCounts.Count);
        Assert.All(displayCounts.Values, count => Assert.Equal(1, count));
        Assert.Equal(TestDecision.TrustOnce, results[0]);
        Assert.Equal(TestDecision.TrustOnce, results[1]);
        Assert.Equal(TestDecision.TrustOnce, results[2]);
        Assert.Equal(TestDecision.Accept, results[3]);
        Assert.Equal(TestDecision.Accept, results[4]);
        Assert.Equal(TestDecision.Accept, results[5]);
    }

    [Fact]
    public async Task RequestAsync_CancelledQueuedRequest_DoesNotBlockFollowingRequest()
    {
        var coordinator = new TrustPromptCoordinator();
        var firstDisplayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDisplay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelledDisplayCount = 0;
        int followingDisplayCount = 0;

        Task<TestDecision> first = coordinator.RequestAsync(
            CreateKey(TrustPromptKind.SshHostKey, "first.example.com", "SHA256:first"),
            async ct =>
            {
                firstDisplayEntered.TrySetResult();
                await releaseFirstDisplay.Task.WaitAsync(ct);
                return TestDecision.Accept;
            },
            TestDecision.Reject);
        await firstDisplayEntered.Task.WaitAsync(CompletionTimeout);

        using var cts = new CancellationTokenSource();
        Task<TestDecision> cancelled = coordinator.RequestAsync(
            CreateKey(TrustPromptKind.SshHostKey, "cancelled.example.com", "SHA256:cancelled"),
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref cancelledDisplayCount);
                return Task.FromResult(TestDecision.Accept);
            },
            TestDecision.Reject,
            cts.Token);
        Task<TestDecision> following = coordinator.RequestAsync(
            CreateKey(TrustPromptKind.FtpsCertificate, "following.example.com", "SHA256:following"),
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref followingDisplayCount);
                return Task.FromResult(TestDecision.TrustOnce);
            },
            TestDecision.Reject);

        cts.Cancel();
        TestDecision cancelledDecision = await cancelled.WaitAsync(CompletionTimeout);
        releaseFirstDisplay.TrySetResult();
        TestDecision[] remaining = await Task.WhenAll(first, following)
            .WaitAsync(CompletionTimeout);

        Assert.Equal(TestDecision.Reject, cancelledDecision);
        Assert.Equal(0, cancelledDisplayCount);
        Assert.Equal(1, followingDisplayCount);
        Assert.Equal(TestDecision.Accept, remaining[0]);
        Assert.Equal(TestDecision.TrustOnce, remaining[1]);
    }

    [Fact]
    public async Task RequestAsync_CancelledCoalescedWaiter_DoesNotCancelSharedDecision()
    {
        var coordinator = new TrustPromptCoordinator();
        var displayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisplay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int displayCount = 0;
        TrustPromptKey key = CreateKey(
            TrustPromptKind.FtpsCertificate,
            "coalesced.example.com",
            "SHA256:coalesced");

        Task<TestDecision> first = coordinator.RequestAsync(
            key,
            async ct =>
            {
                _ = Interlocked.Increment(ref displayCount);
                displayEntered.TrySetResult();
                await releaseDisplay.Task.WaitAsync(ct);
                return TestDecision.TrustOnce;
            },
            TestDecision.Reject);
        await displayEntered.Task.WaitAsync(CompletionTimeout);

        using var cts = new CancellationTokenSource();
        Task<TestDecision> cancelled = coordinator.RequestAsync(
            key,
            _ => Task.FromResult(TestDecision.Accept),
            TestDecision.Reject,
            cts.Token);
        cts.Cancel();

        Assert.Equal(
            TestDecision.Reject,
            await cancelled.WaitAsync(CompletionTimeout));
        releaseDisplay.TrySetResult();

        Assert.Equal(
            TestDecision.TrustOnce,
            await first.WaitAsync(CompletionTimeout));
        Assert.Equal(1, displayCount);
    }

    // The RDP certificate question no longer arrives here. It shares an answer without sharing
    // a display, which this type cannot express: it runs the FIRST caller's display and hands
    // that one return value to everyone who joined, so a teardown in the displaying pane became
    // the other pane's answer. See RdpTrustQuestionCoalescer, and the tests that pin it in
    // PaneRdpCertificateTrustPromptTests. What remains here is the two callers this shape is
    // right for: SSH host keys and FTPS certificates, whose questions are top-level modal
    // windows owned by the application rather than by any one caller.

    private static TrustPromptKey CreateKey(
        TrustPromptKind kind,
        string host,
        string fingerprint)
        => TrustPromptKey.Create(kind, host, 22, fingerprint);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            int previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private enum TestDecision
    {
        Accept,
        TrustOnce,
        Reject
    }
}
