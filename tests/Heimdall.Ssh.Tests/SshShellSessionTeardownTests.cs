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
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public sealed class SshShellSessionTeardownTests
{
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
    public void Disconnect_StuckReadLoop_DoesNotBlockCallerForFinalWait()
    {
        var session = CreateSessionWithReadLoop(Task.Delay(Timeout.InfiniteTimeSpan));
        var disconnectCount = 0;
        session.Disconnected += _ => Interlocked.Increment(ref disconnectCount);

        var stopwatch = Stopwatch.StartNew();
        session.Disconnect();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1.5),
            $"Disconnect took {stopwatch.ElapsedMilliseconds} ms with a stuck read loop.");
        Assert.True(
            SpinWait.SpinUntil(() => Volatile.Read(ref disconnectCount) == 1, TimeSpan.FromSeconds(5)),
            "Background teardown did not complete clean disconnect notification.");
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

    private static SshShellSession CreateSessionWithReadLoop(Task readLoopTask)
    {
        var session = new SshShellSession();
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
