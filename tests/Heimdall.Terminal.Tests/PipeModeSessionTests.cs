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

using System.Text;
using Heimdall.Terminal;

namespace Heimdall.Terminal.Tests;

public sealed class PipeModeSessionTests
{
    [Fact]
    public async Task DataReceived_SubscriberAddedAfterBootstrapOutput_ReplaysBufferedOutput()
    {
        string signalPath = Path.Combine(Path.GetTempPath(), $"heimdall-pipe-ready-{Guid.NewGuid():N}.txt");
        string escapedSignalPath = signalPath.Replace("'", "''", StringComparison.Ordinal);
        PipeModeSession session = new();
        StringBuilder output = new();
        object outputLock = new object();
        TaskCompletionSource<bool> liveOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                $"-NoLogo -NoProfile -Command \"$payload = 'PIPE-BOOTSTRAP:' + ('X' * 131072); " +
                "[Console]::Out.WriteLine($payload); [Console]::Out.Flush(); " +
                $"[IO.File]::WriteAllText('{escapedSignalPath}', 'ready'); " +
                "$line = Read-Host; [Console]::Out.WriteLine('PIPE-LIVE:' + $line)\"");

            Assert.True(
                TerminalTestHelpers.SpinUntilProcessEvent(
                    () => File.Exists(signalPath),
                    "BootstrapSignalFile"),
                "The child process did not confirm that its bootstrap output was written.");

            session.DataReceived += data =>
            {
                string text = Encoding.UTF8.GetString(data.Span);
                bool hasLiveOutput;
                lock (outputLock)
                {
                    output.Append(text);
                    hasLiveOutput = output.ToString().Contains("PIPE-LIVE:after-replay", StringComparison.Ordinal);
                }

                if (hasLiveOutput)
                {
                    liveOutput.TrySetResult(true);
                }
            };

            session.Write("after-replay\r\n");

            await TerminalTestHelpers.AwaitProcessEventAsync(liveOutput.Task, "LiveOutput");
            string text;
            lock (outputLock)
            {
                text = output.ToString();
            }

            int bootstrapIndex = text.IndexOf("PIPE-BOOTSTRAP:", StringComparison.Ordinal);
            int liveIndex = text.IndexOf("PIPE-LIVE:after-replay", StringComparison.Ordinal);
            Assert.True(bootstrapIndex >= 0, "The late subscriber did not receive the buffered bootstrap output.");
            Assert.True(liveIndex > bootstrapIndex, "Live output was delivered before the bootstrap replay.");
        }
        finally
        {
            session.Dispose();
            File.Delete(signalPath);
        }
    }

    [Fact]
    public async Task ProcessExited_SubscriberAddedAfterFastExit_ReplaysExitCode()
    {
        PipeModeSession session = new();
        TaskCompletionSource<int> replayed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                "-NoLogo -NoProfile -Command \"exit 37\"");

            Assert.True(
                TerminalTestHelpers.SpinUntilProcessEvent(
                    () => !session.IsRunning,
                    "SessionStopped"),
                "The child process did not exit.");

            session.ProcessExited += exitCode => replayed.TrySetResult(exitCode);

            int exitCode = await TerminalTestHelpers.AwaitProcessEventAsync(
                replayed.Task,
                "ProcessExitedReplay");
            Assert.Equal(37, exitCode);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Write_InputReachesProcessStdin()
    {
        PipeModeSession session = new();
        StringBuilder output = new();
        object outputLock = new object();
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dataNotificationCount = 0;
        long receivedByteCount = 0;
        int processExitNotificationCount = 0;
        int observedExitCode = int.MinValue;

        session.DataReceived += data =>
        {
            Interlocked.Increment(ref dataNotificationCount);
            Interlocked.Add(ref receivedByteCount, data.Length);
            lock (outputLock)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
            }
        };
        session.ProcessExited += exitCode =>
        {
            Interlocked.Increment(ref processExitNotificationCount);
            Interlocked.Exchange(ref observedExitCode, exitCode);
            exited.TrySetResult(exitCode);
        };

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                "-NoLogo -NoProfile -Command \"$line = Read-Host; Write-Output ('pipe-echo:' + $line)\"");

            session.Write(Encoding.UTF8.GetBytes("hello\r\n"));

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
            Assert.Equal(0, exitCode);

            string text;
            lock (outputLock)
            {
                text = output.ToString();
            }

            Assert.Contains("pipe-echo:hello", text);
        }
        finally
        {
            session.Dispose();
        }
    }
}
