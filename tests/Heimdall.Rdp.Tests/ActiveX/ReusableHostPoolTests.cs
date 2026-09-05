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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// The pooling decision. Reuse is what avoids a measured 66 kernel handles per session,
/// but a host handed on in the wrong state would carry one session's settings, or its
/// credential, into the next: every path that could do so must end in a disposal.
/// </summary>
public sealed class ReusableHostPoolTests
{
    [Fact]
    public void Acquire_BuildsAHostWhenNoneIsIdle()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());

        FakeHost host = pool.Acquire();

        Assert.NotNull(host);
        Assert.Equal(1, pool.CreationCount);
        Assert.Equal(0, pool.ReuseCount);
    }

    [Fact]
    public void Release_ThenAcquire_HandsBackTheSameHost()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost first = pool.Acquire();

        pool.Release(first);
        FakeHost second = pool.Acquire();

        Assert.Same(first, second);
        Assert.Equal(1, first.ResetCount);
        Assert.False(first.IsDisposed);
        Assert.Equal(1, pool.CreationCount);
        Assert.Equal(1, pool.ReuseCount);
    }

    /// <summary>
    /// The point of the whole contract: a host is reset before it can serve anyone else.
    /// </summary>
    [Fact]
    public void Release_ResetsTheHostBeforePoolingIt()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost host = pool.Acquire();

        pool.Release(host);

        Assert.Equal(1, host.ResetCount);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void Release_DisposesAHostThatReportsItselfUnusable()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost host = pool.Acquire();
        host.IsReusable = false;

        pool.Release(host);

        Assert.True(host.IsDisposed);
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(0, host.ResetCount);
    }

    [Fact]
    public void Release_DisposesAHostWhoseResetFails()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost host = pool.Acquire();
        host.ResetResult = false;

        pool.Release(host);

        Assert.True(host.IsDisposed);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public void Release_DisposesAHostWhoseResetThrows()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost host = pool.Acquire();
        host.ResetThrows = true;

        pool.Release(host);

        Assert.True(host.IsDisposed);
        Assert.Equal(0, pool.IdleCount);
    }

    /// <summary>
    /// A pool that grew without bound would trade a leak for a standing cost, since each
    /// idle host holds the resources of a control that has connected.
    /// </summary>
    [Fact]
    public void Release_DisposesBeyondCapacity()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost(), capacity: 1);
        FakeHost kept = pool.Acquire();
        FakeHost extra = pool.Acquire();

        pool.Release(kept);
        pool.Release(extra);

        Assert.False(kept.IsDisposed);
        Assert.True(extra.IsDisposed);
        Assert.Equal(1, pool.IdleCount);
    }

    /// <summary>
    /// A host can go bad while it sits idle, so the state is checked on the way out too.
    /// </summary>
    [Fact]
    public void Acquire_SkipsAnIdleHostThatWentBad()
    {
        using var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost first = pool.Acquire();
        pool.Release(first);
        first.IsReusable = false;

        FakeHost second = pool.Acquire();

        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.Equal(2, pool.CreationCount);
    }

    [Fact]
    public void Dispose_DisposesEveryIdleHost()
    {
        var pool = new ReusableHostPool<FakeHost>(() => new FakeHost(), capacity: 2);
        FakeHost first = pool.Acquire();
        FakeHost second = pool.Acquire();
        pool.Release(first);
        pool.Release(second);

        pool.Dispose();

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public void Release_AfterDispose_DisposesTheHostInsteadOfKeepingIt()
    {
        var pool = new ReusableHostPool<FakeHost>(() => new FakeHost());
        FakeHost host = pool.Acquire();
        pool.Dispose();

        pool.Release(host);

        Assert.True(host.IsDisposed);
        Assert.Equal(0, pool.IdleCount);
    }

    // What gives the memory back. An idle host holds about 300 MB, and two of them kept for
    // the life of the process left a used Heimdall 600 MB above a fresh one.
    [Fact]
    public void Trim_DisposesTheHostsThatOutlivedTheExpiry_OldestFirst()
    {
        ManualClock clock = new();
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => 2, () => TimeSpan.FromMinutes(5), clock);
        FakeHost older = pool.Acquire();
        FakeHost newer = pool.Acquire();
        pool.Release(older);
        clock.Advance(TimeSpan.FromMinutes(3));
        pool.Release(newer);
        clock.Advance(TimeSpan.FromMinutes(2));

        int disposed = pool.Trim();

        Assert.Equal(1, disposed);
        Assert.True(older.IsDisposed);
        Assert.False(newer.IsDisposed);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void Trim_BeforeTheExpiry_DisposesNothing()
    {
        ManualClock clock = new();
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => 2, () => TimeSpan.FromMinutes(5), clock);
        FakeHost host = pool.Acquire();
        pool.Release(host);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));

        Assert.Equal(0, pool.Trim());
        Assert.False(host.IsDisposed);
        Assert.Equal(1, pool.IdleCount);
    }

    // Zero is "keep until the pool is disposed", which is what the fixed-capacity shape does.
    [Fact]
    public void Trim_WithNoExpiry_KeepsIdleHostsForEver()
    {
        ManualClock clock = new();
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => 2, () => TimeSpan.Zero, clock);
        FakeHost host = pool.Acquire();
        pool.Release(host);
        clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(0, pool.Trim());
        Assert.False(host.IsDisposed);
    }

    // Acquire takes the most recently released host, so the oldest keeps ageing towards its
    // expiry instead of being revived by every new session.
    [Fact]
    public void Acquire_HandsOutTheMostRecentlyReleasedHost()
    {
        ManualClock clock = new();
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => 2, () => TimeSpan.Zero, clock);
        FakeHost older = pool.Acquire();
        FakeHost newer = pool.Acquire();
        pool.Release(older);
        clock.Advance(TimeSpan.FromMinutes(1));
        pool.Release(newer);

        Assert.Same(newer, pool.Acquire());
    }

    // The capacity is read live: lowering it on the settings screen applies at the next trim
    // rather than at the next restart, and it is the oldest idle hosts that go.
    [Fact]
    public void Trim_DisposesTheOldestHostsBeyondALoweredCapacity()
    {
        int capacity = 2;
        ManualClock clock = new();
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => capacity, () => TimeSpan.Zero, clock);
        FakeHost older = pool.Acquire();
        FakeHost newer = pool.Acquire();
        pool.Release(older);
        clock.Advance(TimeSpan.FromSeconds(1));
        pool.Release(newer);

        capacity = 1;
        int disposed = pool.Trim();

        Assert.Equal(1, disposed);
        Assert.True(older.IsDisposed);
        Assert.False(newer.IsDisposed);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void Release_ReadsTheCapacityLive()
    {
        int capacity = 0;
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => capacity, () => TimeSpan.Zero);
        FakeHost first = pool.Acquire();
        pool.Release(first);
        Assert.True(first.IsDisposed);

        capacity = 1;
        FakeHost second = pool.Acquire();
        pool.Release(second);

        Assert.False(second.IsDisposed);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void ANegativeCapacityReadsAsZero()
    {
        using var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => -3, () => TimeSpan.Zero);

        Assert.Equal(0, pool.Capacity);
    }

    [Fact]
    public void Trim_AfterDispose_DoesNothing()
    {
        var pool = new ReusableHostPool<FakeHost>(
            () => new FakeHost(), () => 2, () => TimeSpan.FromMinutes(1));
        pool.Dispose();

        Assert.Equal(0, pool.Trim());
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class FakeHost : IReusableHost
    {
        public bool IsReusable { get; set; } = true;

        public bool ResetResult { get; set; } = true;

        public bool ResetThrows { get; set; }

        public int ResetCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool ResetForReuse()
        {
            ResetCount++;
            if (ResetThrows)
            {
                throw new InvalidOperationException("reset failed");
            }

            return ResetResult;
        }

        public void Dispose() => IsDisposed = true;
    }
}
