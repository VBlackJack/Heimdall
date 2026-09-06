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

using System.IO;
using Heimdall.App.Services;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// One bounded, contained step of application exit. The container disposal was the
/// only unbounded await on the exit path, inside an async void override.
/// </summary>
public sealed class ExitStepTests
{
    [Fact]
    public async Task RunBoundedAsync_WorkCompletes_ReturnsTrueAndWarnsNothing()
    {
        List<string> warnings = [];

        bool completed = await ExitStep.RunBoundedAsync(
            "quick step",
            () => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            warnings.Add);

        Assert.True(completed);
        Assert.Empty(warnings);
    }

    [Fact(Timeout = 10000)]
    public async Task RunBoundedAsync_WorkHangs_ReturnsFalseAfterTheBudgetAndWarns()
    {
        List<string> warnings = [];
        TaskCompletionSource never = new();

        bool completed = await ExitStep.RunBoundedAsync(
            "hanging step",
            () => never.Task,
            TimeSpan.FromMilliseconds(100),
            warnings.Add);

        Assert.False(completed);
        string warning = Assert.Single(warnings);
        Assert.Contains("hanging step", warning, StringComparison.Ordinal);
        Assert.Contains("did not complete", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunBoundedAsync_WorkThrows_ReturnsFalseAndWarnsWithTheException()
    {
        List<string> warnings = [];

        bool completed = await ExitStep.RunBoundedAsync(
            "throwing step",
            () => throw new InvalidOperationException("dispatcher gone"),
            TimeSpan.FromSeconds(5),
            warnings.Add);

        Assert.False(completed);
        string warning = Assert.Single(warnings);
        Assert.Contains("throwing step", warning, StringComparison.Ordinal);
        Assert.Contains("dispatcher gone", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunBoundedAsync_WorkFaultsLater_IsStillContained()
    {
        List<string> warnings = [];
        TaskCompletionSource source = new();
        Task run = ExitStep.RunBoundedAsync("faulting step", () => source.Task, TimeSpan.FromSeconds(5), warnings.Add);

        source.SetException(new IOException("late"));
        await run;

        Assert.Single(warnings);
    }
}
