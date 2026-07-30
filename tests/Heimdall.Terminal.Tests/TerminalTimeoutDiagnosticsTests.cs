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

namespace Heimdall.Terminal.Tests;

public sealed class TerminalTimeoutDiagnosticsTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 30, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void CreateMessage_ProcessNeverStarted_ReportsUnavailableProcessValues()
    {
        TerminalTimeoutContext context = CreateContext(
            processId: null,
            isRunning: false,
            dataNotifications: 0,
            receivedBytes: 0,
            processExitNotifications: 0,
            observedExitCode: null);

        string message = TerminalTimeoutDiagnostics.CreateMessage(
            context,
            TimeSpan.FromSeconds(10),
            () => FixedUtc,
            _ => throw new InvalidOperationException("Process reader should not be called."));

        Assert.Contains("utc=2026-07-30T12:34:56.0000000+00:00", message, StringComparison.Ordinal);
        Assert.Contains("processCreated=false", message, StringComparison.Ordinal);
        Assert.Contains("processId=unavailable", message, StringComparison.Ordinal);
        Assert.Contains("processState=not-created", message, StringComparison.Ordinal);
        Assert.Contains("dataNotifications=0", message, StringComparison.Ordinal);
        Assert.Contains("processExitNotifications=0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMessage_LivingProcessWithoutOutput_DoesNotReadExitCode()
    {
        TerminalTimeoutContext context = CreateContext(
            processId: 123,
            isRunning: true,
            dataNotifications: 0,
            receivedBytes: 0,
            processExitNotifications: 0,
            observedExitCode: null);

        string message = TerminalTimeoutDiagnostics.CreateMessage(
            context,
            TimeSpan.FromSeconds(10),
            () => FixedUtc,
            _ => new TerminalProcessSnapshot(FixedUtc.AddSeconds(-1), HasExited: false, ExitCode: null));

        Assert.Contains("processCreated=true", message, StringComparison.Ordinal);
        Assert.Contains("processId=123", message, StringComparison.Ordinal);
        Assert.Contains("processState=running", message, StringComparison.Ordinal);
        Assert.Contains("exitCode=unreadable(process-still-running)", message, StringComparison.Ordinal);
        Assert.Contains("receivedBytes=0", message, StringComparison.Ordinal);
        Assert.Contains("sessionIsRunning=true", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMessage_ExitedProcess_ReportsExitCodeAndObservedEvents()
    {
        TerminalTimeoutContext context = CreateContext(
            processId: 456,
            isRunning: false,
            dataNotifications: 2,
            receivedBytes: 17,
            processExitNotifications: 1,
            observedExitCode: 23);

        string message = TerminalTimeoutDiagnostics.CreateMessage(
            context,
            TimeSpan.FromSeconds(10),
            () => FixedUtc,
            _ => new TerminalProcessSnapshot(FixedUtc.AddSeconds(-2), HasExited: true, ExitCode: 23));

        Assert.Contains("processState=exited", message, StringComparison.Ordinal);
        Assert.Contains("exitCode=23", message, StringComparison.Ordinal);
        Assert.Contains("dataNotifications=2", message, StringComparison.Ordinal);
        Assert.Contains("receivedBytes=17", message, StringComparison.Ordinal);
        Assert.Contains("processExitNotifications=1", message, StringComparison.Ordinal);
        Assert.Contains("observedExitCode=23", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMessage_UnreadableValues_ReportsReasonsWithoutThrowing()
    {
        TerminalTimeoutContext context = new(
            "ProcessExited",
            () => 789,
            () => throw new InvalidOperationException("running state unavailable"),
            () => throw new InvalidOperationException("notification count unavailable"),
            () => 0,
            () => 0,
            () => null);

        Exception? exception = Record.Exception(() =>
            TerminalTimeoutDiagnostics.CreateMessage(
                context,
                TimeSpan.FromSeconds(10),
                () => FixedUtc,
                _ => throw new InvalidOperationException("process values unavailable")));

        Assert.Null(exception);

        string message = TerminalTimeoutDiagnostics.CreateMessage(
            context,
            TimeSpan.FromSeconds(10),
            () => FixedUtc,
            _ => throw new InvalidOperationException("process values unavailable"));

        Assert.Contains(
            "processState=unreadable(InvalidOperationException: process values unavailable)",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "dataNotifications=unreadable(InvalidOperationException: notification count unavailable)",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "sessionIsRunning=unreadable(InvalidOperationException: running state unavailable)",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitAsync_Timeout_ThrowsTimeoutExceptionWithDiagnostic()
    {
        TaskCompletionSource<int> neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TerminalTimeoutContext context = CreateContext(
            processId: null,
            isRunning: false,
            dataNotifications: 0,
            receivedBytes: 0,
            processExitNotifications: 0,
            observedExitCode: null);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            TerminalTimeoutDiagnostics.WaitAsync(neverCompletes.Task, TimeSpan.Zero, context));

        Assert.Contains("Terminal wait timed out:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("awaited=ProcessExited", exception.Message, StringComparison.Ordinal);
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    private static TerminalTimeoutContext CreateContext(
        int? processId,
        bool isRunning,
        int dataNotifications,
        long receivedBytes,
        int processExitNotifications,
        int? observedExitCode)
    {
        return new TerminalTimeoutContext(
            "ProcessExited",
            () => processId,
            () => isRunning,
            () => dataNotifications,
            () => receivedBytes,
            () => processExitNotifications,
            () => observedExitCode);
    }
}
