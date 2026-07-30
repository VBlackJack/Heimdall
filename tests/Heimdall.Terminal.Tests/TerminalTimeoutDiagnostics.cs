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
using System.Globalization;

namespace Heimdall.Terminal.Tests;

internal sealed record TerminalTimeoutContext(
    string AwaitedEvent,
    Func<int?> ReadProcessId,
    Func<bool> ReadIsRunning,
    Func<int> ReadDataNotificationCount,
    Func<long> ReadReceivedByteCount,
    Func<int> ReadProcessExitNotificationCount,
    Func<int?> ReadObservedExitCode);

internal readonly record struct TerminalProcessSnapshot(
    DateTimeOffset StartedUtc,
    bool HasExited,
    int? ExitCode);

internal static class TerminalTimeoutDiagnostics
{
    internal static async Task<T> WaitAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        TerminalTimeoutContext context)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(CreateMessage(context, elapsed.Elapsed), exception);
        }
    }

    internal static string CreateMessage(
        TerminalTimeoutContext context,
        TimeSpan elapsed,
        Func<DateTimeOffset>? readUtcNow = null,
        Func<int, TerminalProcessSnapshot>? readProcess = null)
    {
        try
        {
            readUtcNow ??= static () => DateTimeOffset.UtcNow;
            readProcess ??= ReadProcess;

            string utc = ReadSafely(
                () => readUtcNow().ToString("O", CultureInfo.InvariantCulture));
            int? processId = null;
            string processIdText = ReadSafely(() =>
            {
                processId = context.ReadProcessId();
                return processId?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
            });
            string processCreated = processIdText.StartsWith("unreadable(", StringComparison.Ordinal)
                ? processIdText
                : processId.HasValue ? "true" : "false";
            string sessionIsRunning = ReadSafely(
                () => context.ReadIsRunning().ToString().ToLowerInvariant());

            string processStartedUtc;
            string processState;
            string processExitCode;
            if (!processId.HasValue)
            {
                processStartedUtc = "unavailable(process-not-created)";
                processState = "not-created";
                processExitCode = "unavailable(process-not-created)";
            }
            else
            {
                try
                {
                    TerminalProcessSnapshot snapshot = readProcess(processId.Value);
                    processStartedUtc = snapshot.StartedUtc.ToString("O", CultureInfo.InvariantCulture);
                    processState = snapshot.HasExited ? "exited" : "running";
                    processExitCode = snapshot.HasExited
                        ? snapshot.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unreadable(exit-code-null)"
                        : "unreadable(process-still-running)";
                }
                catch (Exception exception)
                {
                    string reason = FormatUnreadable(exception);
                    processStartedUtc = reason;
                    processState = reason;
                    processExitCode = reason;
                }
            }

            string dataNotifications = ReadSafely(
                () => context.ReadDataNotificationCount().ToString(CultureInfo.InvariantCulture));
            string receivedBytes = ReadSafely(
                () => context.ReadReceivedByteCount().ToString(CultureInfo.InvariantCulture));
            string processExitNotifications = ReadSafely(
                () => context.ReadProcessExitNotificationCount().ToString(CultureInfo.InvariantCulture));
            string observedExitCode = ReadSafely(
                () => context.ReadObservedExitCode()?.ToString(CultureInfo.InvariantCulture)
                    ?? "unavailable(event-not-received)");

            return string.Create(
                CultureInfo.InvariantCulture,
                $"Terminal wait timed out: utc={utc}, awaited={context.AwaitedEvent}, elapsedMs={elapsed.TotalMilliseconds:F3}, processCreated={processCreated}, processId={processIdText}, processStartedUtc={processStartedUtc}, processState={processState}, exitCode={processExitCode}, dataNotifications={dataNotifications}, receivedBytes={receivedBytes}, processExitNotifications={processExitNotifications}, observedExitCode={observedExitCode}, sessionIsRunning={sessionIsRunning}.");
        }
        catch (Exception exception)
        {
            return $"Terminal wait timed out: diagnostic={FormatUnreadable(exception)}.";
        }
    }

    private static TerminalProcessSnapshot ReadProcess(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        DateTimeOffset startedUtc = process.StartTime.ToUniversalTime();
        bool hasExited = process.HasExited;
        int? exitCode = hasExited ? process.ExitCode : null;
        return new TerminalProcessSnapshot(startedUtc, hasExited, exitCode);
    }

    private static string ReadSafely(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            return FormatUnreadable(exception);
        }
    }

    private static string FormatUnreadable(Exception exception)
    {
        string message = exception.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return $"unreadable({exception.GetType().Name}: {message})";
    }
}
