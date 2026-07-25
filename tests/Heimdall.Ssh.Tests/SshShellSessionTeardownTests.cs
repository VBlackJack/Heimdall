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
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public sealed class SshShellSessionTeardownTests
{
    /// <summary>
    /// Failure backstop for a background teardown to report completion. The
    /// controllable clock, not this bound, is what releases the session's final
    /// wait, so the value only has to be generous enough that a heavily loaded
    /// runner cannot exhaust it before the thread pool schedules the teardown
    /// at all. Its cost is paid only when the test genuinely fails.
    /// </summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest the caller may block while the read loop is stuck. Well below
    /// the session's own two-second final wait, which is what the test proves
    /// the caller does not pay for.
    /// </summary>
    private static readonly TimeSpan MaxCallerBlockingTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Amount the controllable clock jumps per pump iteration. Comfortably past
    /// the session's final wait so a single applied step releases it.
    /// </summary>
    private static readonly TimeSpan ClockStep = TimeSpan.FromSeconds(5);

    /// <summary>Gap between two clock pump iterations.</summary>
    private static readonly TimeSpan ClockPumpInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void Dispose_OnUnconnectedSession_DoesNotThrow()
    {
        var session = new SshShellSession();

        session.Dispose();
        session.Dispose();
    }

    [Fact]
    public void Disconnect_OnUnconnectedSession_DoesNotThrow()
    {
        using var session = new SshShellSession();

        session.Disconnect();
    }

    [Fact]
    public void Dispose_AfterDisconnect_IsIdempotent()
    {
        var session = new SshShellSession();

        session.Disconnect();
        session.Dispose();
        session.Dispose();
    }

    [Fact]
    public void IsConnected_ReturnsFalse_WhenNotConnected()
    {
        using var session = new SshShellSession();

        Assert.False(session.IsConnected);
    }

    [Fact]
    public void CreateShellEofDisconnectInfo_ConnectedTransport_ReturnsCleanDisconnect()
    {
        var disconnect = SshShellSession.CreateShellEofDisconnectInfo(transportConnected: true);

        Assert.True(disconnect.IsClean);
        Assert.Null(disconnect.Failure);
        Assert.False(SshReconnectPolicy.AllowsAutoReconnect(disconnect));
    }

    [Fact]
    public void CreateShellEofDisconnectInfo_DisconnectedTransport_ReturnsTransientDisconnect()
    {
        var disconnect = SshShellSession.CreateShellEofDisconnectInfo(transportConnected: false);

        Assert.False(disconnect.IsClean);
        Assert.Equal(SshFailureCode.SessionDisconnected, disconnect.Failure?.Code);
        Assert.True(SshReconnectPolicy.AllowsAutoReconnect(disconnect));
    }

    [Fact]
    public void Dispose_DoesNotBlockBeyondTotalWait()
    {
        var session = new SshShellSession();
        var stopwatch = Stopwatch.StartNew();

        session.Dispose();

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Dispose took {stopwatch.ElapsedMilliseconds} ms on an unconnected session.");
    }

    [Fact]
    public async Task Disconnect_StuckReadLoop_DoesNotBlockCallerForFinalWait()
    {
        var timeProvider = new FakeTimeProvider();
        var session = CreateSessionWithReadLoop(Task.Delay(Timeout.InfiniteTimeSpan), timeProvider);
        var disconnectCount = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += _ =>
        {
            Interlocked.Increment(ref disconnectCount);
            notified.TrySetResult();
        };

        var stopwatch = Stopwatch.StartNew();
        session.Disconnect();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < MaxCallerBlockingTime,
            $"Disconnect took {stopwatch.ElapsedMilliseconds} ms with a stuck read loop.");

        await PumpClockUntilCompletedAsync(timeProvider, notified.Task, TeardownTimeout);

        Assert.True(
            notified.Task.IsCompleted,
            "Background teardown did not complete clean disconnect notification.");
        Assert.Equal(1, Volatile.Read(ref disconnectCount));
    }

    [Fact]
    public async Task DisconnectAndDispose_Concurrent_DoNotThrowOrDoubleNotify()
    {
        var session = CreateSessionWithReadLoop(Task.CompletedTask);
        var disconnectCount = 0;
        session.Disconnected += _ => Interlocked.Increment(ref disconnectCount);

        Task disconnect = Task.Run(session.Disconnect);
        Task dispose = Task.Run(session.Dispose);

        await Task.WhenAll(disconnect, dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(Volatile.Read(ref disconnectCount), 0, 1);
    }

    [Fact]
    public void Resize_OnDisposedSession_Throws()
    {
        var session = new SshShellSession();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Resize(80, 24));
    }

    [Fact]
    public void Write_OnDisposedSession_Throws()
    {
        var session = new SshShellSession();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Write("x"));
    }

    /// <summary>
    /// Advances the controllable clock until <paramref name="completion"/> finishes.
    /// The session registers its final-wait timer from a thread-pool thread, so a
    /// single advance could land before that registration and leave the delay
    /// pending for ever. Repeating the advance makes the wait independent of when
    /// the pool happens to schedule the background teardown; the wall-clock bound
    /// is only a failure backstop, never the synchronisation point.
    /// </summary>
    private static async Task PumpClockUntilCompletedAsync(
        FakeTimeProvider timeProvider,
        Task completion,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!completion.IsCompleted && stopwatch.Elapsed < timeout)
        {
            timeProvider.Advance(ClockStep);
            await Task.WhenAny(completion, Task.Delay(ClockPumpInterval)).ConfigureAwait(false);
        }
    }

    private static SshShellSession CreateSessionWithReadLoop(
        Task readLoopTask,
        TimeProvider? timeProvider = null)
    {
        var session = new SshShellSession(timeProvider);
        SetPrivateField(session, "_client", new SshClient("127.0.0.1", "user", "password"));
        SetPrivateField(session, "_readCts", new CancellationTokenSource());
        SetPrivateField(session, "_readLoopTask", readLoopTask);
        return session;
    }

    private static void SetPrivateField<T>(SshShellSession session, string fieldName, T value)
    {
        FieldInfo? field = typeof(SshShellSession).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(session, value);
    }
}
