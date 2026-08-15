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

using System.Net;
using System.Text;
using Heimdall.Terminal;

namespace Heimdall.Terminal.Tests;

public sealed class TerminalSessionLifecycleTests
{
    private const byte Iac = 255;
    private const byte Do = 253;
    private const byte Sb = 250;
    private const byte Se = 240;
    private const byte Naws = 31;

    [Fact]
    public async Task PipeModeSession_Dispose_TerminatesRunningProcess()
    {
        PipeModeSession session = new();
        await session.StartAsync(
            TerminalTestHelpers.ResolvePowerShellExecutable(),
            "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"");
        int processId = Assert.IsType<int>(session.ProcessId);

        session.Dispose();

        TerminalTestHelpers.AssertProcessHasExited(processId);
    }

    [Fact]
    public async Task PipeModeSession_StartAsync_AfterFailedStart_CanBeRetried()
    {
        PipeModeSession session = new();

        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(
            Path.Combine(Path.GetTempPath(), $"heimdall_missing_{Guid.NewGuid():N}.exe"),
            string.Empty));

        await session.StartAsync(
            TerminalTestHelpers.ResolvePowerShellExecutable(),
            "-NoLogo -NoProfile -NonInteractive -Command \"exit 0\"");
        session.Dispose();
    }

    [Fact]
    public async Task TelnetSession_StartAsync_AfterFailedConnect_CanBeRetried()
    {
        int port = TerminalTestHelpers.ReserveLoopbackPort();
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 250);

        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(string.Empty, string.Empty));

        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            using TcpClient acceptedClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task PipeModeSession_DataReceivedSubscriberException_DoesNotStopReadLoop()
    {
        PipeModeSession session = new();
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        long receivedByteCount = 0;
        int processExitNotificationCount = 0;
        int observedExitCode = int.MinValue;

        session.DataReceived += data =>
        {
            Interlocked.Increment(ref callbackCount);
            Interlocked.Add(ref receivedByteCount, data.Length);
            throw new InvalidOperationException("Test subscriber failure.");
        };
        session.ProcessExited += exitCode =>
        {
            Interlocked.Increment(ref processExitNotificationCount);
            Interlocked.Exchange(ref observedExitCode, exitCode);
            exited.TrySetResult(exitCode);
        };

        await session.StartAsync(
            TerminalTestHelpers.ResolvePowerShellExecutable(),
            "-NoLogo -NoProfile -NonInteractive -Command \"Write-Output 'first'; Start-Sleep -Milliseconds 300; Write-Output 'second'; exit 0\"");

        TerminalTimeoutContext timeoutContext = new(
            "ProcessExited",
            () => session.ProcessId,
            () => session.IsRunning,
            () => Volatile.Read(ref callbackCount),
            () => Interlocked.Read(ref receivedByteCount),
            () => Volatile.Read(ref processExitNotificationCount),
            () => Volatile.Read(ref observedExitCode) == int.MinValue
                ? null
                : Volatile.Read(ref observedExitCode));
        int exitCode = await TerminalTestHelpers.AwaitProcessEventAsync(
            exited.Task,
            timeoutContext);
        session.Dispose();

        Assert.Equal(0, exitCode);
        Assert.True(callbackCount >= 2, $"Expected the read loop to survive the first subscriber exception, got {callbackCount} callback(s).");
    }

    [Fact]
    public async Task PipeModeSession_ProcessExitedSubscriberException_DoesNotSurfaceToCaller()
    {
        PipeModeSession session = new();
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dataNotificationCount = 0;
        long receivedByteCount = 0;
        int processExitNotificationCount = 0;
        int observedExitCode = int.MinValue;

        session.DataReceived += data =>
        {
            Interlocked.Increment(ref dataNotificationCount);
            Interlocked.Add(ref receivedByteCount, data.Length);
        };
        session.ProcessExited += exitCode =>
        {
            Interlocked.Increment(ref processExitNotificationCount);
            Interlocked.Exchange(ref observedExitCode, exitCode);
            exited.TrySetResult(exitCode);
        };
        session.ProcessExited += _ => throw new InvalidOperationException("Test subscriber failure.");

        Exception? observedException = await Record.ExceptionAsync(async () =>
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                "-NoLogo -NoProfile -NonInteractive -Command \"exit 12\"");
            TerminalTimeoutContext timeoutContext = new(
                "ProcessExited",
                () => session.ProcessId,
                () => session.IsRunning,
                () => Volatile.Read(ref dataNotificationCount),
                () => Interlocked.Read(ref receivedByteCount),
                () => Volatile.Read(ref processExitNotificationCount),
                () => Volatile.Read(ref observedExitCode) == int.MinValue
                    ? null
                    : Volatile.Read(ref observedExitCode));
            int exitCode = await TerminalTestHelpers.AwaitProcessEventAsync(
                exited.Task,
                timeoutContext);
            Assert.Equal(12, exitCode);
            session.Dispose();
        });

        Assert.Null(observedException);
    }

    [Fact]
    public async Task TelnetSession_DataReceivedSubscriberException_DoesNotStopReadLoop()
    {
        int port = TerminalTestHelpers.ReserveLoopbackPort();
        TcpListener listener = new(IPAddress.Loopback, port);
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;

        listener.Start();
        Task serverTask = SendTwoTelnetChunksAsync(listener);

        session.DataReceived += _ =>
        {
            Interlocked.Increment(ref callbackCount);
            throw new InvalidOperationException("Test subscriber failure.");
        };
        session.ProcessExited += exitCode => exited.TrySetResult(exitCode);

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotEqual(0, exitCode);
            Assert.True(callbackCount >= 2, $"Expected the read loop to survive the first subscriber exception, got {callbackCount} callback(s).");
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task TelnetSession_Parser_EscapedIacSplitByteByByte_EmitsSingleLiteralIac()
    {
        await AssertTelnetByteByByteAsync(
            new byte[] { Iac, Iac },
            new byte[] { Iac });
    }

    [Fact]
    public async Task TelnetSession_Parser_DoNawsSplitByteByByte_DoesNotLeakNegotiationBytes()
    {
        await AssertTelnetByteByByteAsync(
            new byte[] { Iac, Do, Naws, (byte)'X' },
            new byte[] { (byte)'X' });
    }

    [Fact]
    public async Task TelnetSession_Parser_BareTrailingIacThenData_DropsOnlyIac()
    {
        await AssertTelnetByteByByteAsync(
            new byte[] { Iac, (byte)'X' },
            new byte[] { (byte)'X' });
    }

    [Fact]
    public async Task TelnetSession_Parser_NawsSubnegotiationSplitByteByByte_DoesNotLeakSubnegotiationBytes()
    {
        await AssertTelnetByteByByteAsync(
            new byte[]
            {
                Iac,
                Sb,
                Naws,
                0x00,
                0x50,
                0x00,
                0x18,
                Iac,
                Se,
                (byte)'X'
            },
            new byte[] { (byte)'X' });
    }

    [Fact]
    public async Task TelnetSession_Parser_AsciiSplitByteByByte_EmitsBytesInOrder()
    {
        await AssertTelnetByteByByteAsync(
            Encoding.ASCII.GetBytes("hello"),
            Encoding.ASCII.GetBytes("hello"));
    }

    [Fact]
    public async Task TelnetSession_IsRunning_TracksReadLoopExit()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProcessExited += exitCode => exited.TrySetResult(exitCode);

        // The server keeps the connection open until the test has observed the running
        // state, then closes it on a signal. This makes the read loop exit deterministic
        // rather than racing StartAsync, which would otherwise let the loop end before the
        // running state can be asserted.
        TaskCompletionSource closeServer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            client.NoDelay = true;
            await using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("X"));
            await stream.FlushAsync();
            await closeServer.Task;
        });

        try
        {
            await session.StartAsync(string.Empty, string.Empty);

            // IsRunning flips true on a background read loop; await the real transition.
            await TerminalTestHelpers.WaitForConditionAsync(
                () => session.IsRunning,
                TimeSpan.FromSeconds(10),
                "Session did not report IsRunning after StartAsync.");

            // Release the server so it closes the connection and the read loop exits.
            closeServer.TrySetResult();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Once the read loop has exited, IsRunning must flip back to false.
            await TerminalTestHelpers.WaitForConditionAsync(
                () => !session.IsRunning,
                TimeSpan.FromSeconds(10),
                "Session still reported IsRunning after the read loop exited.");
        }
        finally
        {
            closeServer.TrySetResult();
            session.Dispose();
            listener.Stop();
        }
    }

    private static async Task SendTwoTelnetChunksAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();

        byte[] first = Encoding.ASCII.GetBytes("first");
        byte[] second = Encoding.ASCII.GetBytes("second");

        await stream.WriteAsync(first);
        await stream.FlushAsync();
        await Task.Delay(300);
        await stream.WriteAsync(second);
        await stream.FlushAsync();
    }

    private static async Task AssertTelnetByteByByteAsync(byte[] serverBytes, byte[] expectedBytes)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        List<byte> observed = new List<byte>();
        object observedLock = new object();
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        session.DataReceived += data =>
        {
            lock (observedLock)
            {
                observed.AddRange(data.ToArray());
            }
        };
        session.ProcessExited += exitCode => exited.TrySetResult(exitCode);

        Task serverTask = TerminalTestHelpers.SendBytesOneAtATimeAsync(listener, serverBytes);

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

            byte[] actual;
            lock (observedLock)
            {
                actual = observed.ToArray();
            }

            Assert.Equal(expectedBytes, actual);
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

}
