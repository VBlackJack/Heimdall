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
using Heimdall.Terminal.ConPty;

namespace Heimdall.Terminal.Tests;

/// <summary>
/// Seam-free ConPTY coverage. Native setup failure handle-leak paths and
/// deterministic interactive input timing are intentionally not covered here.
/// Non-interactive PowerShell output is also not asserted because, under the
/// xUnit console runner, it is emitted through the inherited stdout stream
/// instead of the ConPTY output pipe.
/// </summary>
public sealed class ConPtySessionTests
{
    /// <summary>
    /// Failure bound, not a synchronisation point. The wait it bounds completes on
    /// an event: the ProcessExited replay to a late subscriber, where the process has
    /// already exited. The value only has to be generous enough that a saturated
    /// thread pool cannot exhaust it before the replay is delivered, and it is paid
    /// only on failure.
    /// </summary>
    private static readonly TimeSpan ReplaySignalBackstop = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The bytes conhost emits on its own account - win32-input-mode and focus-event enables -
    /// the instant a pseudo console exists, whether or not a client ever attaches to it.
    /// </summary>
    private const string ConhostPreamble = "\u001b[?9001h\u001b[?1004h";

    /// <summary>
    /// How long a child that failed to attach to the pseudo console takes to die. Measured
    /// 2026-09-20 over 55 such starts: Windows PowerShell between 457 and 1004 ms, cmd.exe
    /// between 74 and 109 ms. The bound is twice the slowest observed death, which keeps it
    /// discriminating; a healthy shell is alive for as long as the session is.
    /// </summary>
    private static readonly TimeSpan NonAttachedChildDeathWindow = TimeSpan.FromSeconds(2);

    [Fact]
    [Trait("Category", "CIUnstable")]
    public async Task StartAsync_LaunchesShell_DeliversInitialTerminalOutput()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();
        StringBuilder output = new();
        object outputLock = new object();
        TaskCompletionSource<string> outputObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        session.DataReceived += data =>
        {
            lock (outputLock)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                string text = output.ToString();
                if (text.Length > 0)
                {
                    outputObserved.TrySetResult(text);
                }
            }
        };

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                "-NoLogo -NoProfile");

            string text = await TerminalTestHelpers.AwaitProcessEventAsync(
                outputObserved.Task,
                "DataReceived");

            Assert.NotEmpty(text);
            Assert.True(session.IsRunning);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "CIUnstable")]
    public async Task DataReceived_SubscriberAddedAfterBootstrapOutput_ReplaysBufferedOutput()
    {
        // Producer: ConPtySession bootstrap buffering. StartReadLoop routes bytes
        // through DeliverOrBuffer (ConPtySession.cs): with no subscriber yet they are
        // buffered by BufferBootstrapChunk, and the DataReceived add-accessor replays
        // the buffer to the first subscriber. Before this fix the bytes were dropped,
        // so a late subscriber (the real Local Shell case) never saw the prompt.
        //
        // Uses an INTERACTIVE shell so the bootstrap prompt is emitted to the ConPTY
        // output pipe (a non-interactive one-shot command writes to inherited stdout
        // under the test runner -- see the class summary). The shell then idles, so
        // the only way a late subscriber can receive non-empty output is via replay.
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();
        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                "-NoLogo -NoProfile");

            // Let the interactive shell print its prompt into the bootstrap buffer
            // before any subscriber attaches (no subscriber == buffered, not delivered).
            await Task.Delay(1500);

            StringBuilder output = new();
            object outputLock = new object();
            TaskCompletionSource<string> outputObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // Subscribe only now: the already-buffered bootstrap output must be replayed.
            session.DataReceived += data =>
            {
                lock (outputLock)
                {
                    output.Append(Encoding.UTF8.GetString(data.Span));
                    if (output.Length > 0)
                    {
                        outputObserved.TrySetResult(output.ToString());
                    }
                }
            };

            // The replay is delivered inside the add-accessor, under the delivery lock, and
            // ConPtySession's own comment there guarantees the read loop cannot interleave a
            // direct delivery ahead of it. So anything present the instant the subscription
            // returns was replayed, and anything delivered live can only arrive afterwards.
            //
            // Without this check the test does not test replay. If the shell had not printed
            // yet when the wait above expired, its output reached the subscriber LIVE, the
            // non-emptiness assertion below passed, and no buffer was ever replayed. That is
            // precisely the slow-runner case this test exists to cover, and it was the case in
            // which the test could not fail.
            int bufferedAtSubscription;
            lock (outputLock)
            {
                bufferedAtSubscription = output.Length;
            }

            Assert.True(
                bufferedAtSubscription > 0,
                "Nothing was replayed when the subscription attached, so any output observed "
                    + "below was delivered live and the bootstrap buffer was never exercised.");

            string text = await TerminalTestHelpers.AwaitProcessEventAsync(
                outputObserved.Task,
                "DataReceivedReplay");

            Assert.NotEmpty(text);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_TerminatesPseudoConsoleAndProcess()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();

        await session.StartAsync(
            TerminalTestHelpers.ResolvePowerShellExecutable(),
            BuildEncodedPowerShellArguments("Start-Sleep -Seconds 60"));
        int processId = Assert.IsType<int>(session.ProcessId);

        session.Dispose();

        TerminalTestHelpers.AssertProcessHasExited(processId);
    }

    [Fact]
    public async Task ProcessExited_ProcessEndsWithoutConsoleOutput_RaisesExitCode()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();
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

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolveExitCodeChildExecutable(),
                TerminalTestHelpers.BuildExitCodeChildArguments(17));

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

            Assert.Equal(17, exitCode);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task ProcessExited_SubscriberAddedAfterFastExit_ReplaysExitCode()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();
        int dataNotificationCount = 0;
        long receivedByteCount = 0;
        int processExitNotificationCount = 0;
        int observedExitCode = int.MinValue;

        try
        {
            session.DataReceived += data =>
            {
                Interlocked.Increment(ref dataNotificationCount);
                Interlocked.Add(ref receivedByteCount, data.Length);
            };
            await session.StartAsync(
                TerminalTestHelpers.ResolveExitCodeChildExecutable(),
                TerminalTestHelpers.BuildExitCodeChildArguments(23));

            TerminalTimeoutContext stoppedTimeoutContext = new(
                "SessionStopped",
                () => session.ProcessId,
                () => session.IsRunning,
                () => Volatile.Read(ref dataNotificationCount),
                () => Interlocked.Read(ref receivedByteCount),
                () => Volatile.Read(ref processExitNotificationCount),
                () => Volatile.Read(ref observedExitCode) == int.MinValue
                    ? null
                    : Volatile.Read(ref observedExitCode));
            await WaitUntilStoppedAsync(session, stoppedTimeoutContext);

            TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ProcessExited += exitCode =>
            {
                Interlocked.Increment(ref processExitNotificationCount);
                Interlocked.Exchange(ref observedExitCode, exitCode);
                exited.TrySetResult(exitCode);
            };
            TerminalTimeoutContext replayTimeoutContext = stoppedTimeoutContext with
            {
                AwaitedEvent = "ProcessExitedReplay",
            };
            int exitCode = await TerminalTimeoutDiagnostics.WaitAsync(
                exited.Task,
                ReplaySignalBackstop,
                replayTimeoutContext);

            Assert.Equal(23, exitCode);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Resize_AfterStart_DoesNotThrow()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                BuildEncodedPowerShellArguments("Start-Sleep -Seconds 60"));

            Exception? exception = Record.Exception(() =>
            {
                session.Resize(80, 24);
                session.Resize(120, 40);
            });

            Assert.Null(exception);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Resize_OversizeDimensions_DoesNotThrow()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                BuildEncodedPowerShellArguments("Start-Sleep -Seconds 60"));

            Exception? exception = Record.Exception(() =>
            {
                session.Resize(int.MaxValue, int.MaxValue);
            });

            Assert.Null(exception);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// A live shell writes more than the preamble, and is still alive afterwards.
    /// </summary>
    /// <remarks>
    /// <para><see cref="StartAsync_LaunchesShell_DeliversInitialTerminalOutput"/> asserts that
    /// SOME bytes arrived and that the session was running at that instant. Neither holds the
    /// session to anything: measured 2026-09-20, a child that never attaches to the pseudo
    /// console still yields exactly the 16 bytes of <see cref="ConhostPreamble"/> within
    /// milliseconds, and only exits, with code 0, some 500 ms later. Fifteen consecutive dead
    /// sessions were measured in an environment where all seven tests in this class passed.</para>
    /// <para>This test is the discriminating one: it requires output the shell itself produced,
    /// and requires the shell to outlive the window such a child dies in.</para>
    /// </remarks>
    [Fact]
    [Trait("Category", "CIUnstable")]
    public async Task StartAsync_InteractiveShell_OutlivesTheWindowAndWritesMoreThanThePreamble()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ConPtySession session = new();
        StringBuilder output = new();
        object outputLock = new object();
        TaskCompletionSource<string> shellOutputObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        session.DataReceived += data =>
        {
            lock (outputLock)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                string text = output.ToString();
                if (StripPreamble(text).Length > 0)
                {
                    shellOutputObserved.TrySetResult(text);
                }
            }
        };

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                "-NoLogo -NoProfile");

            string text = await TerminalTestHelpers.AwaitProcessEventAsync(
                shellOutputObserved.Task,
                "DataReceived beyond the conhost preamble");

            Assert.NotEmpty(StripPreamble(text));

            // A child that took the parent's console instead of the pseudo console dies
            // inside this window. One that attached outlives it.
            await Task.Delay(NonAttachedChildDeathWindow);
            Assert.True(
                session.IsRunning,
                $"the shell exited within {NonAttachedChildDeathWindow.TotalMilliseconds} ms of start; "
                + $"received {output.Length} chars");
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// An executable carrying a quote is refused before any process is created.
    /// </summary>
    /// <remarks>
    /// The command line is built by wrapping the executable in quotes, so a quote inside it
    /// closes that token early and everything after it becomes arguments. A Windows path
    /// cannot contain one, so it only ever arrives from a crafted profile.
    /// </remarks>
    [Fact]
    public async Task StartAsync_ExecutableContainingAQuote_Throws()
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        using ConPtySession session = new();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => session.StartAsync("C:\\Tools\\sh.exe\" & calc", string.Empty));

        Assert.Equal("executable", error.ParamName);
        Assert.Null(session.ProcessId);
    }

    private static string StripPreamble(string text)
        => text.Replace(ConhostPreamble, string.Empty, StringComparison.Ordinal);

    private static string BuildEncodedPowerShellArguments(string command)
    {
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedCommand}";
    }

    private static async Task WaitUntilStoppedAsync(
        ConPtySession session,
        TerminalTimeoutContext timeoutContext,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool stopped = await TerminalTestHelpers.PollUntilProcessEventAsync(
            () => !session.IsRunning,
            timeoutContext.AwaitedEvent,
            caller);

        string? message = stopped
            ? null
            : TerminalTimeoutDiagnostics.CreateMessage(timeoutContext, elapsed.Elapsed);
        Assert.True(stopped, message);
    }
}
