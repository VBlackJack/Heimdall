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
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;
using static Heimdall.App.Tests.RecipeBuilders;

namespace Heimdall.App.Tests;

/// <summary>
/// A recipe view keeps the recipe and the program path it was built with. When the recipe is
/// replaced, or removed and imported again, the view must not run the old one; and a run under way
/// ends when the tool stops being available.
/// </summary>
public sealed class RecipeToolStaleTabTests
{
    private static readonly string OtherSha256 = new('b', 64);

    // A run that is never cancelled must fail the test, not hang it.
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(10);

    private const string ToolIdOfRecipe = "acme.probe";

    // ---- a replaced or removed recipe -------------------------------------------------------------

    [Fact]
    public async Task AReplacedRecipeIsRefusedAndTheRunnerIsNeverCalled()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        harness.Versions.Replace(ToolIdOfRecipe, OtherSha256);

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
        Assert.Equal(harness.Localizer["RecipeStaleMessage"], viewModel.GeneralError);
        Assert.True(viewModel.IsStale);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ARemovedRecipeIsRefusedAndTheRunnerIsNeverCalled()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        harness.Versions.Remove(ToolIdOfRecipe);

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.Runner.Calls);
        Assert.Equal(harness.Localizer["RecipeStaleMessage"], viewModel.GeneralError);
        Assert.True(viewModel.IsStale);
    }

    [Fact]
    public async Task ARecipeImportedAgainWithTheBytesItFirstHadStaysRefused()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        harness.Versions.Remove(ToolIdOfRecipe);

        harness.Versions.ImportAgain(ToolIdOfRecipe, StoredSha256);
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.Runner.Calls);
        Assert.True(viewModel.IsStale);
    }

    [Fact]
    public async Task TheSameBytesImportedAgainWithTheProgramResolvingElsewhereIsStale()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        harness.Versions.ImportAgainResolvingTo(ToolIdOfRecipe, @"C:\Tools\other\ping.exe");
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsStale);
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public async Task TheRunChecksTheStoreItselfWhenNoChangeWasAnnounced()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        harness.Versions.Replace(ToolIdOfRecipe, OtherSha256, announce: false);
        Assert.False(viewModel.IsStale);

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.Runner.Calls);
        Assert.True(viewModel.IsStale);
    }

    [Fact]
    public async Task OneStaleCheckIsOneReadOfTheCurrentVersion()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        int before = harness.Versions.ReadCount;

        Assert.False(viewModel.CheckStale());
        Assert.Equal(before + 1, harness.Versions.ReadCount);

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(before + 2, harness.Versions.ReadCount);
    }

    [Fact]
    public async Task AnUnchangedRecipeRuns()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Single(harness.Runner.Calls);
        Assert.False(viewModel.IsStale);
        Assert.Equal(string.Empty, viewModel.GeneralError);
        Assert.Equal(string.Empty, viewModel.StaleText);
    }

    [Fact]
    public async Task ARecipeReplacedByTheSameBytesIsNotStale()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        harness.Versions.Replace(ToolIdOfRecipe, StoredSha256);

        Assert.False(viewModel.IsStale);
    }

    [Fact]
    public async Task TheHeaderSaysItAtOnceAndRunIsDisabledUntilTheTabIsClosed()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(viewModel.RunCommand.CanExecute(null));

        harness.Versions.Replace(ToolIdOfRecipe, OtherSha256);

        Assert.True(viewModel.IsStale);
        Assert.Equal(harness.Localizer["RecipeStaleMessage"], viewModel.StaleText);
        Assert.Contains(nameof(RecipeToolViewModel.StaleText), changed);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        Assert.True(viewModel.CheckStale());
    }

    [Fact]
    public async Task ADisposedViewNoLongerFollowsTheStore()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        Assert.Equal(1, harness.Versions.SubscriberCount);
        viewModel.Dispose();

        harness.Versions.Replace(ToolIdOfRecipe, OtherSha256);

        Assert.Equal(0, harness.Versions.SubscriberCount);
        Assert.False(viewModel.IsStale);
    }

    // ---- a run under way when the tool stops being available ---------------------------------------

    private static (TaskCompletionSource Started, TaskCompletionSource<CancellationToken> Token) BlockingRunner(RecipeViewModelHarness harness)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CancellationToken> token = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.Script = async call =>
        {
            token.SetResult(call.Token);
            started.SetResult();
            TaskCompletionSource never = new();
            await using (call.Token.Register(() => never.TrySetResult()))
            {
                await never.Task.ConfigureAwait(false);
            }

            return FakeRecipeRunner.Result(RecipeRunStatus.Cancelled);
        };
        return (started, token);
    }

    [Theory]
    [InlineData(ToolAvailabilityState.DisabledByUser)]
    [InlineData(ToolAvailabilityState.ForbiddenByPolicy)]
    public async Task ARunIsCancelledWhenTheToolStopsBeingAvailable(ToolAvailabilityState state)
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        (TaskCompletionSource started, TaskCompletionSource<CancellationToken> token) = BlockingRunner(harness);
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;
        Assert.False((await token.Task).IsCancellationRequested);
        harness.Availability.State = state;
        harness.Availability.RaiseChanged();
        await run.WaitAsync(RunBound);

        Assert.True((await token.Task).IsCancellationRequested);
        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], viewModel.StatusText);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ARunIsCancelledWhenThePackIsTurnedOffAndTheToolLeavesTheRegistry()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        (TaskCompletionSource started, TaskCompletionSource<CancellationToken> token) = BlockingRunner(harness);
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;
        harness.Availability.State = ToolAvailabilityState.Unknown;
        harness.Availability.PackEnabled = false;
        harness.Availability.RaiseChanged();
        await run.WaitAsync(RunBound);

        Assert.True((await token.Task).IsCancellationRequested);
    }

    [Fact]
    public async Task AnAvailabilityChangeThatLeavesTheToolAvailableDoesNotStopTheRun()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        (TaskCompletionSource started, TaskCompletionSource<CancellationToken> token) = BlockingRunner(harness);
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;
        harness.Availability.RaiseChanged();

        Assert.False((await token.Task).IsCancellationRequested);
        Assert.True(viewModel.IsRunning);
        viewModel.StopCommand.Execute(null);
        await run.WaitAsync(RunBound);
    }

    [Fact]
    public async Task ADisposedViewIgnoresAvailabilityChanges()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        Assert.Equal(1, harness.Availability.SubscriberCount);

        viewModel.Dispose();

        Assert.Equal(0, harness.Availability.SubscriberCount);
    }
}
