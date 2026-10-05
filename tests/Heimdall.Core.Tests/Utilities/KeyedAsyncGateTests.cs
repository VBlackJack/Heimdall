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

using Heimdall.Core.Utilities;

namespace Heimdall.Core.Tests;

public sealed class KeyedAsyncGateTests
{
    [Fact]
    public async Task EnterAsync_SameKey_WaitsForTheHolderToLeave()
    {
        KeyedAsyncGate gate = new();
        IDisposable first = await gate.EnterAsync("tunnel");

        Task<IDisposable> second = gate.EnterAsync("tunnel");
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using IDisposable secondHolder = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnterAsync_DifferentKeys_DoNotWaitForEachOther()
    {
        KeyedAsyncGate gate = new();
        using IDisposable first = await gate.EnterAsync("a");

        Task<IDisposable> second = gate.EnterAsync("b");

        Assert.True(second.IsCompletedSuccessfully);
        (await second).Dispose();
    }

    [Fact]
    public async Task EnterAsync_UsesTheComparerItWasGiven()
    {
        KeyedAsyncGate gate = new(StringComparer.OrdinalIgnoreCase);
        using IDisposable first = await gate.EnterAsync("Tunnel");

        Assert.False(gate.EnterAsync("TUNNEL").IsCompleted);
    }

    [Fact]
    public async Task EnterAsync_CancelledWhileWaiting_DoesNotHoldTheKey()
    {
        KeyedAsyncGate gate = new();
        IDisposable first = await gate.EnterAsync("tunnel");
        using CancellationTokenSource cts = new();

        Task<IDisposable> waiting = gate.EnterAsync("tunnel", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
        using IDisposable next = await gate.EnterAsync("tunnel").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_ForgetsTheKeyOnceNobodyHoldsOrAwaitsIt()
    {
        KeyedAsyncGate gate = new();
        IDisposable first = await gate.EnterAsync("tunnel");
        Task<IDisposable> second = gate.EnterAsync("tunnel");
        Assert.Equal(1, gate.ActiveKeyCount);

        first.Dispose();
        first.Dispose();
        (await second).Dispose();

        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task EnterAsync_ManyCallersOnOneKey_NeverOverlap()
    {
        KeyedAsyncGate gate = new();
        int inside = 0;
        int maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            using IDisposable holder = await gate.EnterAsync("tunnel");
            int now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxInside, now);
            await Task.Yield();
            Interlocked.Decrement(ref inside);
        })));

        Assert.Equal(1, maxInside);
        Assert.Equal(0, gate.ActiveKeyCount);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
