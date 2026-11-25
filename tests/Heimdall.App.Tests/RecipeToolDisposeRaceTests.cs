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

using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Recipes;
using static Heimdall.App.Tests.RecipeBuilders;

namespace Heimdall.App.Tests;

/// <summary>
/// Closing a tab from one thread while a run is being started on another: Dispose must wait for a program that
/// is already starting, and must start none once it has won.
/// </summary>
public sealed class RecipeToolDisposeRaceTests
{
    // A step that is never released must fail the test, not hang it.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    // How long Dispose is given to wrongly return while the runner is still starting.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    private static Recipe Probe() => Recipe(arguments: [RecipeArgument.Bare("-n")]);

    [Fact]
    public async Task DisposeFromAnotherThreadWaitsForARunnerThatIsStillStarting()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        bool programGone = false;
        harness.Runner.Script = call =>
        {
            // The synchronous part of the runner: it signals, then does not return its task until released.
            entered.Set();
            if (!release.Wait(Bound))
            {
                throw new TimeoutException("The test never released the runner.");
            }

            return Finish(call.Token, () => programGone = true);
        };
        RecipeToolViewModel viewModel = harness.Create(Probe());

        Task run = Task.Run(() => viewModel.RunCommand.ExecuteAsync(null));
        Assert.True(entered.Wait(Bound), "The runner was never started.");

        Task dispose = Task.Run(viewModel.Dispose);

        Assert.False(await CompletesWithin(dispose, Grace), "Dispose returned while the runner was still starting.");
        release.Set();
        Assert.True(await CompletesWithin(dispose, Bound), "Dispose never returned.");
        Assert.True(programGone, "Dispose returned before the program was gone.");
        Assert.Empty(harness.RunFolders());
        await run.WaitAsync(Bound);
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan limit)
        => await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false) == task;

    private static async Task<RecipeRunResult> Finish(CancellationToken token, Action gone)
    {
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (token.Register(() => cancelled.TrySetResult()))
        {
            await cancelled.Task.ConfigureAwait(false);
        }

        await Task.Delay(100).ConfigureAwait(false);
        gone();
        return FakeRecipeRunner.Result(RecipeRunStatus.Cancelled);
    }

    [Fact]
    public async Task ADisposeThatWinsTheRaceStartsNoRunnerAndLeavesNoFolder()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using ManualResetEventSlim comparing = new(false);
        using ManualResetEventSlim release = new(false);
        BlockingComparison resolver = new(comparing, release);
        RecipeToolServices services = new(
            harness.Runner, resolver, harness.WorkFolders, harness.Availability, () => [], harness.Versions);
        RecipeToolViewModel viewModel = new(Probe(), StoredPath, StoredSha256, services);
        viewModel.Initialize(null, harness.Localizer);

        Task run = Task.Run(() => viewModel.RunCommand.ExecuteAsync(null));
        Assert.True(comparing.Wait(Bound), "The comparison never started.");

        viewModel.Dispose();
        release.Set();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
    }

    private sealed class BlockingComparison(ManualResetEventSlim started, ManualResetEventSlim release) : IRecipeProgramResolver
    {
        public RecipeProgramComparison CompareWithStored(string? program, IReadOnlyList<string> allowedFolders, string storedPath)
        {
            started.Set();
            if (!release.Wait(Bound))
            {
                throw new TimeoutException("The test never released the comparison.");
            }

            return RecipeProgramComparison.Same;
        }
    }
}
