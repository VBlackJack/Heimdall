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
using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// A handful of runs with real console tools of the Windows system folder, to prove the real path
/// works: they exit on their own or are killed by the test, never a window, never elevated, no
/// network. Each one asserts that it leaves no process behind.
/// </summary>
public sealed class RecipeRunnerRealProcessTests : IDisposable
{
    private const string WaitForName = "waitfor";

    private static readonly TimeSpan LeftoverWait = TimeSpan.FromSeconds(10);

    private readonly ScratchFolder _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static string System32(string tool) => Path.Combine(Environment.SystemDirectory, tool);

    private static Recipe MakeRecipe(RecipeStderrMode stderr = RecipeStderrMode.Separate, int timeoutSeconds = 30, int[]? successCodes = null)
        => RecipeTestData.Minimal() with { Stderr = stderr, TimeoutSeconds = timeoutSeconds, SuccessExitCodes = successCodes ?? [0] };

    private string EmptyFolder() => _scratch.Dir("work-" + Guid.NewGuid().ToString("N"));

    private static async Task AssertNoProcessLeftAsync(string name, int before)
    {
        Stopwatch clock = Stopwatch.StartNew();
        int count = CountProcesses(name);
        while (count > before && clock.Elapsed < LeftoverWait)
        {
            await Task.Delay(100);
            count = CountProcesses(name);
        }

        Assert.True(count <= before, $"{count - before} process(es) named {name} were left behind.");
    }

    private static int CountProcesses(string name)
    {
        Process[] found = Process.GetProcessesByName(name);
        int count = found.Length;
        foreach (Process process in found)
            process.Dispose();

        return count;
    }

    [Fact]
    public async Task ARealTool_RunsToItsEnd_AndItsOutputIsCaptured()
    {
        int before = CountProcesses("hostname");

        RecipeRunResult result = await new RecipeRunner().RunAsync(MakeRecipe(), System32("hostname.exe"), [], EmptyFolder());

        Assert.Equal(RecipeRunStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
        Assert.False(result.Truncated);
        await AssertNoProcessLeftAsync("hostname", before);
    }

    [Fact]
    public async Task AnArgumentWithASpace_ReachesTheToolAsOneArgument_AndTheInputIsAlreadyClosed()
    {
        // findstr reads the standard input when it is given no file: it only returns because the
        // runner closed the input, and it only accepts this switch if the space did not split it.
        int before = CountProcesses("findstr");

        RecipeRunResult result = await new RecipeRunner().RunAsync(
            MakeRecipe(timeoutSeconds: 20),
            System32("findstr.exe"),
            ["/C:two words"],
            EmptyFolder());

        Assert.Equal(RecipeRunStatus.FailedExitCode, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        await AssertNoProcessLeftAsync("findstr", before);
    }

    [Fact]
    public async Task StandardError_IsKeptApartInSeparateMode_AndMergedInMergeMode()
    {
        // where.exe prints an INFO line on the error stream when it finds nothing, and exits with 1.
        string[] arguments = ["heimdall-no-such-file-zz9"];
        int before = CountProcesses("where");

        RecipeRunResult separate = await new RecipeRunner().RunAsync(MakeRecipe(RecipeStderrMode.Separate), System32("where.exe"), arguments, EmptyFolder());
        RecipeRunResult merged = await new RecipeRunner().RunAsync(MakeRecipe(RecipeStderrMode.Merge), System32("where.exe"), arguments, EmptyFolder());

        Assert.Equal(RecipeRunStatus.FailedExitCode, separate.Status);
        Assert.Equal(string.Empty, separate.Output);
        Assert.False(string.IsNullOrWhiteSpace(separate.ErrorOutput));
        Assert.Equal(string.Empty, merged.ErrorOutput);
        Assert.False(string.IsNullOrWhiteSpace(merged.Output));
        await AssertNoProcessLeftAsync("where", before);
    }

    [Fact]
    public async Task ATimeout_KillsARealProcess_AndLeavesNothingBehind()
    {
        int before = CountProcesses(WaitForName);
        Stopwatch clock = Stopwatch.StartNew();

        RecipeRunResult result = await new RecipeRunner().RunAsync(
            MakeRecipe(timeoutSeconds: RecipeLimits.MinTimeoutSeconds),
            System32("waitfor.exe"),
            ["/T", "300", "HeimdallRecipeTestSignalTimeout"],
            EmptyFolder());

        Assert.Equal(RecipeRunStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60));
        await AssertNoProcessLeftAsync(WaitForName, before);
    }

    [Fact]
    public async Task ACancellation_KillsARealProcess_AndLeavesNothingBehind()
    {
        int before = CountProcesses(WaitForName);
        using CancellationTokenSource cancel = new();

        Task<RecipeRunResult> run = new RecipeRunner().RunAsync(
            MakeRecipe(timeoutSeconds: RecipeLimits.MaxTimeoutSeconds),
            System32("waitfor.exe"),
            ["/T", "300", "HeimdallRecipeTestSignalCancel"],
            EmptyFolder(),
            cancel.Token);
        await Task.Delay(500);
        await cancel.CancelAsync();
        RecipeRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(RecipeRunStatus.Cancelled, result.Status);
        await AssertNoProcessLeftAsync(WaitForName, before);
    }
}
