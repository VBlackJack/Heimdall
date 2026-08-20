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
        // The child is the command processor rather than PowerShell. It carries the same
        // contract - write a large payload, record that it is written, then wait for a line and
        // echo it back - and it starts in a fraction of the time. The PowerShell child this
        // replaces was measured on a saturated runner with its process created and running and
        // not one byte received after a minute.
        //
        // Everything the child touches lives in a directory this test owns and passes as the
        // child's working directory, so the command line names files without a path and without
        // a quote. An absolute path quoted inside a nested command-processor command is broken
        // by a space anywhere along it, and the temporary directory's root is not ours to choose.
        const string PayloadFileName = "payload.txt";
        const string SignalFileName = "ready.txt";
        const string BootstrapPrefix = "PIPE-BOOTSTRAP:";
        const string LivePrefix = "PIPE-LIVE:";
        const int PayloadPadding = 131072;

        string childDirectory = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(childDirectory);
        string signalPath = Path.Combine(childDirectory, SignalFileName);
        File.WriteAllText(
            Path.Combine(childDirectory, PayloadFileName),
            BootstrapPrefix + new string('X', PayloadPadding));

        PipeModeSession session = new();
        StringBuilder output = new();
        object outputLock = new object();
        TaskCompletionSource<bool> liveOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolveExitCodeChildExecutable(),
                TerminalTestHelpers.BuildBootstrapReplayChildArguments(
                    PayloadFileName,
                    SignalFileName,
                    LivePrefix),
                workingDirectory: childDirectory);

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
                    hasLiveOutput = output.ToString().Contains(LivePrefix + "after-replay", StringComparison.Ordinal);
                }

                if (hasLiveOutput)
                {
                    liveOutput.TrySetResult(true);
                }
            };

            // The replay is delivered inside the add-accessor above, under the delivery lock, and
            // PipeModeSession's own comment there guarantees the read loop cannot interleave a
            // direct delivery ahead of it. So whatever is present the instant the subscription
            // returns was replayed, and anything live can only arrive afterwards.
            //
            // Without this the ordering assertion below cannot fail for the right reason. A payload
            // that reached the subscriber live rather than from the buffer still lands before the
            // line written next, so the test would report a passing replay having exercised no
            // buffer at all. Same reasoning, and same wording, as the ConPTY sibling.
            int replayedAtSubscription;
            lock (outputLock)
            {
                replayedAtSubscription = output.Length;
            }

            Assert.True(
                replayedAtSubscription > 0,
                "Nothing was replayed when the subscription attached, so the bootstrap buffer was "
                    + "never exercised and the ordering assertion below proves nothing.");

            session.Write("after-replay\r\n");

            await TerminalTestHelpers.AwaitProcessEventAsync(liveOutput.Task, "LiveOutput");
            string text;
            lock (outputLock)
            {
                text = output.ToString();
            }

            int bootstrapIndex = text.IndexOf(BootstrapPrefix, StringComparison.Ordinal);
            int liveIndex = text.IndexOf(LivePrefix + "after-replay", StringComparison.Ordinal);
            Assert.True(bootstrapIndex >= 0, "The late subscriber did not receive the buffered bootstrap output.");
            Assert.True(liveIndex > bootstrapIndex, "Live output was delivered before the bootstrap replay.");
        }
        finally
        {
            session.Dispose();
            try
            {
                Directory.Delete(childDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The child may still hold the payload open for a moment after Dispose. Leaving a
                // directory behind in the temporary folder must not fail the test that made it.
            }
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
                TerminalTestHelpers.ResolveExitCodeChildExecutable(),
                TerminalTestHelpers.BuildExitCodeChildArguments(37));

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
            // The command processor reads the redirected line just as well as a PowerShell host
            // and starts in a fraction of the time. That difference is the whole point: CI runs
            // recorded this child alive with receivedBytes=0 sixty seconds after being started.
            await session.StartAsync(
                TerminalTestHelpers.ResolveExitCodeChildExecutable(),
                TerminalTestHelpers.BuildStdinEchoChildArguments("pipe-echo:"));

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
