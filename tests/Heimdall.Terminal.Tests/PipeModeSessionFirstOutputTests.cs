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

public sealed class PipeModeSessionFirstOutputTests
{
    private const string StdoutMarker = "FIRST-OUT-STDOUT";
    private const string StderrMarker = "FIRST-OUT-STDERR";
    private const string BootstrapPrefix = "PIPE-BOOTSTRAP:";
    private const string LivePrefix = "PIPE-LIVE:";
    private const string PayloadFileName = "payload.txt";
    private const string SignalFileName = "ready.txt";
    private const int PayloadPadding = 4096;
    private const int SilentExitCode = 3;

    // Writes one marker on stdout and one on stderr, then exits.
    private static readonly string BothStreamsArguments =
        $"/d /c \"echo {StdoutMarker}& echo {StderrMarker} 1>&2\"";

    // Waits for a line on stdin before writing anything: silent until the test writes.
    private const string SilentArguments = "/d /c \"set /p ignored=\"";

    [Fact]
    public async Task FirstOutputReceived_WhenStdoutAndStderrBothProduce_FiresExactlyOnce()
    {
        PipeModeSession session = new();
        int raised = 0;
        StringBuilder output = new();
        object outputLock = new();
        TaskCompletionSource<bool> bothSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FirstOutputReceived += () =>
        {
            Interlocked.Increment(ref raised);
            signalled.TrySetResult(true);
        };
        session.DataReceived += data =>
        {
            lock (outputLock)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                string text = output.ToString();
                if (text.Contains(StdoutMarker, StringComparison.Ordinal)
                    && text.Contains(StderrMarker, StringComparison.Ordinal))
                {
                    bothSeen.TrySetResult(true);
                }
            }
        };

        try
        {
            await session.StartAsync(TerminalTestHelpers.ResolveExitCodeChildExecutable(), BothStreamsArguments);

            await TerminalTestHelpers.AwaitProcessEventAsync(bothSeen.Task, "BothStreamsSeen");
            await TerminalTestHelpers.AwaitProcessEventAsync(signalled.Task, "FirstOutputSignalled");

            Assert.Equal(1, Volatile.Read(ref raised));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task FirstOutputReceived_SubscriberAddedAfterFirstOutput_IsInvokedInsideTheAdd()
    {
        PipeModeSession session = new();
        TaskCompletionSource<bool> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FirstOutputReceived += () => first.TrySetResult(true);

        try
        {
            await session.StartAsync(TerminalTestHelpers.ResolveExitCodeChildExecutable(), BothStreamsArguments);
            await TerminalTestHelpers.AwaitProcessEventAsync(first.Task, "FirstOutput");

            int lateCalls = 0;
            int threadAtAdd = Environment.CurrentManagedThreadId;
            int threadAtCall = -1;
            session.FirstOutputReceived += () =>
            {
                Interlocked.Increment(ref lateCalls);
                threadAtCall = Environment.CurrentManagedThreadId;
            };

            Assert.Equal(1, Volatile.Read(ref lateCalls));
            Assert.Equal(threadAtAdd, threadAtCall);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task FirstOutputReceived_WhenProcessExitsWithoutOutput_IsNotRaised()
    {
        PipeModeSession session = new();
        int raised = 0;
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FirstOutputReceived += () => Interlocked.Increment(ref raised);
        session.ProcessExited += code => exited.TrySetResult(code);

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolveExitCodeChildExecutable(),
                TerminalTestHelpers.BuildExitCodeChildArguments(SilentExitCode));

            int exitCode = await TerminalTestHelpers.AwaitProcessEventAsync(exited.Task, "ProcessExited");
            Assert.Equal(SilentExitCode, exitCode);

            session.FirstOutputReceived += () => Interlocked.Increment(ref raised);

            Assert.Equal(0, Volatile.Read(ref raised));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task FirstOutputReceived_WhenSilentProcessIsDisposed_IsNotRaised()
    {
        PipeModeSession session = new();
        int raised = 0;
        session.FirstOutputReceived += () => Interlocked.Increment(ref raised);

        try
        {
            await session.StartAsync(TerminalTestHelpers.ResolveExitCodeChildExecutable(), SilentArguments);
            Assert.True(session.IsRunning);

            session.Dispose();

            session.FirstOutputReceived += () => Interlocked.Increment(ref raised);
            Assert.Equal(0, Volatile.Read(ref raised));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task FirstOutputReceived_ThrowingSubscriber_DoesNotSkipTheNextNorStopDelivery()
    {
        PipeModeSession session = new();
        TaskCompletionSource<bool> nextRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FirstOutputReceived += () => throw new InvalidOperationException("subscriber failed");
        session.FirstOutputReceived += () => nextRan.TrySetResult(true);
        session.DataReceived += data =>
        {
            if (Encoding.UTF8.GetString(data.Span).Contains(StdoutMarker, StringComparison.Ordinal))
            {
                delivered.TrySetResult(true);
            }
        };

        try
        {
            await session.StartAsync(TerminalTestHelpers.ResolveExitCodeChildExecutable(), BothStreamsArguments);

            Assert.True(await TerminalTestHelpers.AwaitProcessEventAsync(nextRan.Task, "NextSubscriber"));
            Assert.True(await TerminalTestHelpers.AwaitProcessEventAsync(delivered.Task, "DataDelivered"));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task FirstOutputReceived_SubscriberAddedBeforeStart_DoesNotConsumeTheReplay()
    {
        string childDirectory = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(childDirectory);
        string signalPath = Path.Combine(childDirectory, SignalFileName);
        File.WriteAllText(
            Path.Combine(childDirectory, PayloadFileName),
            BootstrapPrefix + new string('X', PayloadPadding));

        PipeModeSession session = new();
        int raised = 0;
        session.FirstOutputReceived += () => Interlocked.Increment(ref raised);

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
            Assert.True(
                TerminalTestHelpers.SpinUntilProcessEvent(
                    () => Volatile.Read(ref raised) > 0,
                    "FirstOutputRaised"),
                "The first output signal was never raised.");

            StringBuilder replayed = new();
            object replayedLock = new();
            session.DataReceived += data =>
            {
                lock (replayedLock)
                {
                    replayed.Append(Encoding.UTF8.GetString(data.Span));
                }
            };

            // The signal is raised after the chunk was buffered, and the replay runs inside the
            // add above, so what is present now is what was replayed.
            string text;
            lock (replayedLock)
            {
                text = replayed.ToString();
            }

            Assert.True(text.Length > 0, "The late DataReceived subscriber received an empty replay.");
            Assert.Contains(BootstrapPrefix, text, StringComparison.Ordinal);
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
                // The child may still hold the payload open for a moment after Dispose.
            }
        }
    }
}
