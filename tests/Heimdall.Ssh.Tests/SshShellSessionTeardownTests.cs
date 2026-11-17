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
using System.Threading.Channels;
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

    /// <summary>Pinned value of the caller's graceful wait for the read loop.</summary>
    private static readonly TimeSpan ExpectedGracefulWait = TimeSpan.FromMilliseconds(500);

    /// <summary>Pinned value of the background final wait.</summary>
    private static readonly TimeSpan ExpectedFinalWait = TimeSpan.FromSeconds(2);

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

    // A-06: the clean EOF detail was raw English printed into the terminal marker in every
    // locale. The SSH layer names the message by locale key and leaves the text to the view.
    [Fact]
    public void CreateShellEofDisconnectInfo_ConnectedTransport_CarriesALocaleKeyInsteadOfEnglishText()
    {
        SshSessionDisconnectInfo disconnect = SshShellSession.CreateShellEofDisconnectInfo(transportConnected: true);

        Assert.Equal(SshDisconnectMessageKeys.MessageKeyRemoteShellExited, disconnect.MessageKey);
        Assert.Empty(disconnect.MessageArguments);
        Assert.Null(disconnect.Message);
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
        var timeProvider = new TimerRecordingTimeProvider();
        var session = CreateSessionWithReadLoop(Task.Delay(Timeout.InfiniteTimeSpan), timeProvider);
        var disconnectCount = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += _ =>
        {
            Interlocked.Increment(ref disconnectCount);
            notified.TrySetResult();
        };

        TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread caller = new(() =>
        {
            try
            {
                session.Disconnect();
                returned.TrySetResult();
            }
            catch (Exception ex)
            {
                returned.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        caller.Start();

        // The caller's graceful wait is the first timer made through the clock.
        TimeSpan gracefulDue = await timeProvider.NextTimerDueTimeAsync(CancellationToken.None)
            .AsTask().WaitAsync(TeardownTimeout);
        Assert.Equal(SshShellSession.StopReadLoopGraceful, gracefulDue);
        Assert.False(returned.Task.IsCompleted);

        timeProvider.Advance(SshShellSession.StopReadLoopGraceful);
        await returned.Task.WaitAsync(TeardownTimeout);
        Assert.False(
            notified.Task.IsCompleted,
            "Disconnect returned only after the clean disconnect was announced: the caller paid the final wait.");

        TimeSpan finalDue = await timeProvider.NextTimerDueTimeAsync(CancellationToken.None)
            .AsTask().WaitAsync(TeardownTimeout);
        Assert.Equal(SshShellSession.StopReadLoopFinal, finalDue);
        timeProvider.Advance(SshShellSession.StopReadLoopFinal);
        await notified.Task.WaitAsync(TeardownTimeout);

        Assert.True(
            notified.Task.IsCompleted,
            "Background teardown did not complete clean disconnect notification.");
        Assert.Equal(1, Volatile.Read(ref disconnectCount));
    }

    // Lengthening either wait is a deliberate decision: update these expectations with it.
    [Fact]
    public void TeardownWaits_KeepTheirDocumentedValues()
    {
        Assert.Equal(ExpectedGracefulWait, SshShellSession.StopReadLoopGraceful);
        Assert.Equal(ExpectedFinalWait, SshShellSession.StopReadLoopFinal);
    }

    [Fact]
    public async Task Disconnect_StuckReadLoop_WithRealClock_ReturnsAndNotifiesOnce()
    {
        var session = CreateSessionWithReadLoop(Task.Delay(Timeout.InfiniteTimeSpan));
        var disconnectCount = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += _ =>
        {
            Interlocked.Increment(ref disconnectCount);
            notified.TrySetResult();
        };

        await Task.Run(session.Disconnect).WaitAsync(TeardownTimeout);
        await notified.Task.WaitAsync(TeardownTimeout);

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

    [Fact]
    public void WriteAndCleanup_HoldTheSameStreamLifecycleGate()
    {
        using SshShellSession session = new();
        object streamGate = GetPrivateField<object>(session, "_streamGate");
        FakeShellStream stream = new(() => Monitor.IsEntered(streamGate));
        SetPrivateField<ISshShellStream>(session, "_stream", stream);

        session.Write([0x41]);
        InvokePrivateMethod(session, "CleanupStream");

        Assert.True(stream.WriteObservedGateHeld);
        Assert.True(stream.CloseObservedGateHeld);
        Assert.Equal(1, stream.WriteCount);
        Assert.Equal(1, stream.FlushCount);
        Assert.Equal(1, stream.CloseCount);
        Assert.Equal(1, stream.DisposeCount);
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

    private static T GetPrivateField<T>(SshShellSession session, string fieldName)
    {
        FieldInfo? field = typeof(SshShellSession).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(session));
    }

    private static void InvokePrivateMethod(SshShellSession session, string methodName)
    {
        MethodInfo? method = typeof(SshShellSession).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(session, null);
    }

    /// <summary>
    /// Records the due time of every timer the session creates, in creation order,
    /// so a test can wait for a specific wait to be registered and then move the
    /// clock exactly that far. A timer made idle and armed later through
    /// <see cref="ITimer.Change"/> is recorded when it is armed.
    /// </summary>
    private sealed class TimerRecordingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _dueTimes = Channel.CreateUnbounded<TimeSpan>();

        public ValueTask<TimeSpan> NextTimerDueTimeAsync(CancellationToken cancellationToken)
        {
            return _dueTimes.Reader.ReadAsync(cancellationToken);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            Record(dueTime);
            return new RecordingTimer(timer, Record);
        }

        private void Record(TimeSpan dueTime)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _dueTimes.Writer.TryWrite(dueTime);
            }
        }

        private sealed class RecordingTimer(ITimer inner, Action<TimeSpan> record) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                bool changed = inner.Change(dueTime, period);
                record(dueTime);
                return changed;
            }

            public void Dispose()
            {
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                return inner.DisposeAsync();
            }
        }
    }

    private sealed class FakeShellStream(Func<bool> isGateHeld) : ISshShellStream
    {
        public bool WriteObservedGateHeld { get; private set; }

        public bool CloseObservedGateHeld { get; private set; }

        public int WriteCount { get; private set; }

        public int FlushCount { get; private set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(0);
        }

        public void Write(byte[] data, int offset, int count)
        {
            WriteObservedGateHeld = isGateHeld();
            WriteCount++;
        }

        public void Flush()
        {
            FlushCount++;
        }

        public void ChangeWindowSize(uint columns, uint rows, uint width, uint height)
        {
        }

        public void Close()
        {
            CloseObservedGateHeld = isGateHeld();
            CloseCount++;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
