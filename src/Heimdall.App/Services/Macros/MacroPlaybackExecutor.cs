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
using Heimdall.Core.Models;

namespace Heimdall.App.Services.Macros;

internal enum MacroExpectWaitStatus
{
    NotRequired,
    Matched,
    TimedOut
}

internal readonly record struct MacroExpectWaitResult(
    MacroExpectWaitStatus Status,
    int TimeoutMs = 0)
{
    public static MacroExpectWaitResult NotRequired { get; } =
        new(MacroExpectWaitStatus.NotRequired);

    public static MacroExpectWaitResult Matched { get; } =
        new(MacroExpectWaitStatus.Matched);

    public static MacroExpectWaitResult TimedOut(int timeoutMs) =>
        new(MacroExpectWaitStatus.TimedOut, timeoutMs);
}

internal sealed record MacroPlaybackResult(
    int EntriesProcessed,
    int WritesIssued,
    bool WasStoppedByExpectTimeout,
    int? ExpectTimeoutMs);

internal static class MacroPlaybackExecutor
{
    public static async Task<MacroPlaybackResult> RunAsync(
        IReadOnlyList<MacroEntry> entries,
        Func<MacroEntry, CancellationToken, Task<MacroExpectWaitResult>> waitForExpectAsync,
        Func<int, CancellationToken, Task> delayAsync,
        Action<byte[]> writeBytes,
        Action<MacroEntry, MacroExpectWaitResult>? expectTimeoutCallback,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(waitForExpectAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(writeBytes);

        var entriesProcessed = 0;
        var writesIssued = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            entriesProcessed++;

            if (entry.ExpectPattern is not null)
            {
                var expectResult = await waitForExpectAsync(entry, ct);
                if (expectResult.Status == MacroExpectWaitStatus.TimedOut)
                {
                    expectTimeoutCallback?.Invoke(entry, expectResult);
                    if (entry.ExpectOnTimeout == ExpectTimeoutAction.Abort)
                    {
                        return new MacroPlaybackResult(
                            entriesProcessed,
                            writesIssued,
                            WasStoppedByExpectTimeout: true,
                            expectResult.TimeoutMs);
                    }
                }
            }

            if (entry.DelayMs > 0)
            {
                await delayAsync(entry.DelayMs, ct);
            }

            ct.ThrowIfCancellationRequested();
            if (entry.Input.Length == 0)
            {
                continue;
            }

            writeBytes(Encoding.UTF8.GetBytes(entry.Input));
            writesIssued++;
        }

        return new MacroPlaybackResult(
            entriesProcessed,
            writesIssued,
            WasStoppedByExpectTimeout: false,
            ExpectTimeoutMs: null);
    }
}
