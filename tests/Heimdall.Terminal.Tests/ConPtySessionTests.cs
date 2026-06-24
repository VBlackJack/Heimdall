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

            string text = await outputObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

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

            string text = await outputObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

        session.ProcessExited += exitCode => exited.TrySetResult(exitCode);

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                BuildEncodedPowerShellArguments("exit 17"));

            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

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

        try
        {
            await session.StartAsync(
                TerminalTestHelpers.ResolvePowerShellExecutable(),
                BuildEncodedPowerShellArguments("exit 23"));

            await WaitUntilStoppedAsync(session, TimeSpan.FromSeconds(10));

            TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ProcessExited += exitCode => exited.TrySetResult(exitCode);
            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));

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

    private static string BuildEncodedPowerShellArguments(string command)
    {
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedCommand}";
    }

    private static async Task WaitUntilStoppedAsync(ConPtySession session, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!session.IsRunning)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.False(session.IsRunning);
    }
}
