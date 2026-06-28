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
using Heimdall.App.Services.Macros;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class MacroPlaybackExecutorTests
{
    [Fact]
    public async Task RunAsync_ExpectMatch_SendsEntryInput()
    {
        var writes = new List<string>();

        var result = await MacroPlaybackExecutor.RunAsync(
            [new MacroEntry { ExpectPattern = "ready", Input = "next" }],
            static (_, _) => Task.FromResult(MacroExpectWaitResult.Matched),
            static (_, _) => Task.CompletedTask,
            bytes => writes.Add(Utf8(bytes)),
            expectTimeoutCallback: null,
            CancellationToken.None);

        Assert.Equal(["next"], writes);
        Assert.False(result.WasStoppedByExpectTimeout);
        Assert.Equal(1, result.WritesIssued);
    }

    [Fact]
    public async Task RunAsync_ExpectTimeoutAbort_StopsWithoutSendingCurrentOrLaterEntries()
    {
        var writes = new List<string>();
        var timeoutCount = 0;

        var result = await MacroPlaybackExecutor.RunAsync(
            [
                new MacroEntry
                {
                    ExpectPattern = "ready",
                    ExpectOnTimeout = ExpectTimeoutAction.Abort,
                    Input = "first"
                },
                new MacroEntry { Input = "second" }
            ],
            static (_, _) => Task.FromResult(MacroExpectWaitResult.TimedOut(25)),
            static (_, _) => Task.CompletedTask,
            bytes => writes.Add(Utf8(bytes)),
            (_, _) => timeoutCount++,
            CancellationToken.None);

        Assert.Empty(writes);
        Assert.True(result.WasStoppedByExpectTimeout);
        Assert.Equal(25, result.ExpectTimeoutMs);
        Assert.Equal(1, timeoutCount);
    }

    [Fact]
    public async Task RunAsync_ExpectTimeoutContinue_ProceedsToCurrentAndLaterEntries()
    {
        var writes = new List<string>();

        var result = await MacroPlaybackExecutor.RunAsync(
            [
                new MacroEntry
                {
                    ExpectPattern = "ready",
                    ExpectOnTimeout = ExpectTimeoutAction.Continue,
                    Input = "first"
                },
                new MacroEntry { Input = "second" }
            ],
            static (_, _) => Task.FromResult(MacroExpectWaitResult.TimedOut(25)),
            static (_, _) => Task.CompletedTask,
            bytes => writes.Add(Utf8(bytes)),
            expectTimeoutCallback: null,
            CancellationToken.None);

        Assert.Equal(["first", "second"], writes);
        Assert.False(result.WasStoppedByExpectTimeout);
        Assert.Equal(2, result.WritesIssued);
    }

    [Fact]
    public async Task RunAsync_NoExpectEntry_SendsWithoutCallingWait()
    {
        var writes = new List<string>();
        var waitCalls = 0;

        await MacroPlaybackExecutor.RunAsync(
            [new MacroEntry { Input = "pwd" }],
            (_, _) =>
            {
                waitCalls++;
                return Task.FromResult(MacroExpectWaitResult.Matched);
            },
            static (_, _) => Task.CompletedTask,
            bytes => writes.Add(Utf8(bytes)),
            expectTimeoutCallback: null,
            CancellationToken.None);

        Assert.Equal(0, waitCalls);
        Assert.Equal(["pwd"], writes);
    }

    [Fact]
    public async Task RunAsync_PureExpectEntry_WaitsAndMovesOnWithoutSendingEmptyInput()
    {
        var writes = new List<string>();
        var waitCalls = 0;

        var result = await MacroPlaybackExecutor.RunAsync(
            [new MacroEntry { ExpectPattern = "ready", Input = string.Empty }],
            (_, _) =>
            {
                waitCalls++;
                return Task.FromResult(MacroExpectWaitResult.Matched);
            },
            static (_, _) => Task.CompletedTask,
            bytes => writes.Add(Utf8(bytes)),
            expectTimeoutCallback: null,
            CancellationToken.None);

        Assert.Equal(1, waitCalls);
        Assert.Empty(writes);
        Assert.Equal(0, result.WritesIssued);
    }

    private static string Utf8(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }
}
