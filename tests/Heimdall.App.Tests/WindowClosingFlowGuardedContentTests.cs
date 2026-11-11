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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class WindowClosingFlowGuardedContentTests
{
    private static Task<bool> PrepareAsync(
        List<string> trace,
        Func<Task<bool>>? confirmGuardedContent,
        int connectedSessionCount = 1,
        Action? warnCloseNotConfirmed = null)
    {
        return WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: false,
            connectedSessionCount,
            () => throw new InvalidOperationException("Settings prompt must not run."),
            () => throw new InvalidOperationException("Settings save must not run."),
            () =>
            {
                trace.Add("sessions");
                return Task.FromResult(true);
            },
            () =>
            {
                trace.Add("flush");
                return Task.CompletedTask;
            },
            () =>
            {
                trace.Add("persist");
                return Task.CompletedTask;
            },
            () => throw new InvalidOperationException("Save warning must not run."),
            discardSettingsAsync: null,
            warnCloseNotConfirmed,
            confirmGuardedContent);
    }

    [Fact]
    public async Task GuardedContent_RunsAfterTheSessionsPrompt_AndBeforePersistence()
    {
        List<string> trace = [];

        bool canClose = await PrepareAsync(trace, () =>
        {
            trace.Add("guarded");
            return Task.FromResult(true);
        });

        Assert.True(canClose);
        Assert.Equal(["sessions", "guarded", "flush", "persist"], trace);
    }

    [Fact]
    public async Task GuardedContent_Refused_SkipsPersistence()
    {
        List<string> trace = [];

        bool canClose = await PrepareAsync(trace, () => Task.FromResult(false));

        Assert.False(canClose);
        Assert.Equal(["sessions"], trace);
    }

    [Fact]
    public async Task GuardedContent_Throws_WarnsAndStaysOpen()
    {
        List<string> trace = [];
        int warnCount = 0;

        bool canClose = await PrepareAsync(
            trace,
            () => throw new InvalidOperationException("boom"),
            warnCloseNotConfirmed: () => warnCount++);

        Assert.False(canClose);
        Assert.Equal(1, warnCount);
        Assert.Equal(["sessions"], trace);
    }

    [Fact]
    public async Task GuardedContent_AskedEvenWithNoConnectedSession()
    {
        List<string> trace = [];
        int askCount = 0;

        bool canClose = await PrepareAsync(trace, () =>
        {
            askCount++;
            return Task.FromResult(true);
        }, connectedSessionCount: 0);

        Assert.True(canClose);
        Assert.Equal(1, askCount);
        Assert.DoesNotContain("sessions", trace);
    }

    [Fact]
    public async Task GuardedContent_Null_BehavesAsBefore()
    {
        List<string> trace = [];

        bool canClose = await PrepareAsync(trace, confirmGuardedContent: null);

        Assert.True(canClose);
        Assert.Equal(["sessions", "flush", "persist"], trace);
    }
}
