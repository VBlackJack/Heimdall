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
/// The comparison of the program with the one stored at import leaves the UI thread. While it runs, the run is
/// under way (it can be stopped, a second one is refused), and what it answers is applied only if the tab is
/// still the tool it was.
/// </summary>
public sealed class RecipeToolProgramCheckTests
{
    private const string RecipeId = "acme.probe";

    private static readonly string OtherSha256 = new('b', 64);

    // A check that is never released must fail the test, not hang it.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>A resolver that waits until the test lets it answer, and records on which thread it was asked.</summary>
    private sealed class BlockingResolver : IRecipeProgramResolver
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ManualResetEventSlim _release = new(initialState: false);

        public int Calls { get; private set; }

        public int ThreadId { get; private set; }

        public bool Blocking { get; set; } = true;

        public RecipeProgramComparison Answer { get; set; } = RecipeProgramComparison.Same;

        public Task Started => _started.Task;

        public void Release() => _release.Set();

        public RecipeProgramComparison CompareWithStored(string? program, IReadOnlyList<string> allowedFolders, string storedPath)
        {
            Calls++;
            ThreadId = Environment.CurrentManagedThreadId;
            _started.TrySetResult();
            if (Blocking && !_release.Wait(Bound))
            {
                throw new TimeoutException("The test never released the program check.");
            }

            return Answer;
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(RecipeViewModelHarness harness, Recipe recipe, ToolContext? context = null)
        {
            Harness = harness;
            Resolver = new BlockingResolver();
            RecipeToolServices services = new(
                harness.Runner, Resolver, harness.WorkFolders, harness.Availability, () => [], harness.Versions);
            ViewModel = new RecipeToolViewModel(recipe, StoredPath, StoredSha256, services);
            ViewModel.Initialize(context, harness.Localizer);
        }

        public RecipeViewModelHarness Harness { get; }

        public BlockingResolver Resolver { get; }

        public RecipeToolViewModel ViewModel { get; }

        public void Dispose() => ViewModel.Dispose();
    }

    private static async Task<Rig> RigAsync(RecipeViewModelHarness harness, Recipe? recipe = null, ToolContext? context = null)
        => await Task.FromResult(new Rig(harness, recipe ?? Recipe(), context));

    [Fact]
    public async Task WhileTheProgramIsBeingCompared_TheRunIsUnderWay_AndASecondOneIsRefused()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);

        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        Assert.False(run.IsCompleted);
        Assert.True(rig.ViewModel.IsRunning);
        Assert.True(rig.ViewModel.StopCommand.CanExecute(null));
        await rig.ViewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Resolver.Calls);

        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Single(harness.Runner.Calls);
        Assert.False(rig.ViewModel.IsRunning);
    }

    [Fact]
    public async Task TheComparisonRunsOffTheCallingThread()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        rig.Resolver.Blocking = false;

        // The run is started from a dedicated thread that is not a pool thread and stays occupied until the run
        // is over, so the pool can never hand the comparison back to the thread that called RunAsync.
        int callerThread = 0;
        Task started = Task.Factory.StartNew(
            () =>
            {
                callerThread = Environment.CurrentManagedThreadId;
                rig.ViewModel.RunCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await started.WaitAsync(Bound);

        Assert.NotEqual(0, callerThread);
        Assert.NotEqual(0, rig.Resolver.ThreadId);
        Assert.NotEqual(callerThread, rig.Resolver.ThreadId);
        Assert.Single(harness.Runner.Calls);
    }

    [Fact]
    public async Task StopDuringTheComparison_StartsNothing_AndShowsTheCancelledStatus()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        rig.ViewModel.StopCommand.Execute(null);
        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], rig.ViewModel.StatusText);
        Assert.False(rig.ViewModel.IsRunning);
    }

    [Fact]
    public async Task ThePackTurnedOffDuringTheComparison_StartsNothing()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        harness.Availability.State = ToolAvailabilityState.DisabledByUser;
        harness.Availability.RaiseChanged();
        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], rig.ViewModel.StatusText);
    }

    [Fact]
    public async Task AToolThatStoppedBeingAvailableWithoutAnEvent_StartsNothing_AndSaysWhy()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        harness.Availability.State = ToolAvailabilityState.ForbiddenByPolicy;
        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Equal(
            harness.Localizer.Format(ToolUnavailableException.ForbiddenByPolicyMessageKey, rig.ViewModel.Title),
            rig.ViewModel.GeneralError);
    }

    [Fact]
    public async Task AReplacementDuringTheComparison_LeavesTheTabStale_AndStartsNothing()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        harness.Versions.Replace(RecipeId, OtherSha256);
        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.True(rig.ViewModel.IsStale);
        Assert.Equal(harness.Localizer["RecipeStaleMessage"], rig.ViewModel.GeneralError);
    }

    [Fact]
    public async Task DisposeDuringTheComparison_StartsNothing()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        rig.Dispose();
        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
    }

    [Fact]
    public async Task ADifferentProgram_IsRefusedWithItsMessage_AfterTheComparison()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        rig.Resolver.Blocking = false;
        rig.Resolver.Answer = RecipeProgramComparison.Different;

        await rig.ViewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.Runner.Calls);
        Assert.Equal(RecipeRunText.Describe(harness.Localizer, RecipeProgramComparison.Different, StoredPath), rig.ViewModel.GeneralError);
    }

    // The messages come in the order they always did, and none of them reaches the comparison.
    [Fact]
    public async Task TheRefusalsBeforeTheComparison_KeepTheirOrder_AndNeverReachIt()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(
            inputs: [Input("t", RecipeInputType.String, required: true)],
            arguments: [RecipeArgument.Bare("{Host}"), RecipeArgument.Bare("{in:t}")]);
        using Rig rig = await RigAsync(harness, recipe);
        rig.Resolver.Blocking = false;

        // Stale first, even when the tool is also unavailable.
        harness.Versions.Replace(RecipeId, OtherSha256, announce: false);
        harness.Availability.State = ToolAvailabilityState.DisabledByUser;
        await rig.ViewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(harness.Localizer["RecipeStaleMessage"], rig.ViewModel.GeneralError);

        using Rig fresh = await RigAsync(harness, recipe);
        fresh.Resolver.Blocking = false;
        harness.Versions.ImportAgain(RecipeId, StoredSha256);

        // Unavailable next.
        await fresh.ViewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(
            harness.Localizer.Format(ToolUnavailableException.DisabledByUserMessageKey, fresh.ViewModel.Title),
            fresh.ViewModel.GeneralError);

        // Then the session, then the values.
        harness.Availability.State = ToolAvailabilityState.Available;
        await fresh.ViewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(harness.Localizer["RecipeRunNoSession"], fresh.ViewModel.GeneralError);

        fresh.ViewModel.Initialize(new ToolContext(TargetHost: "host.example"), harness.Localizer);
        await fresh.ViewModel.RunCommand.ExecuteAsync(null);
        Assert.True(fresh.ViewModel.Fields[0].HasError);

        Assert.Equal(0, rig.Resolver.Calls + fresh.Resolver.Calls);
    }

    // ---- the unavailable message names the title the tab shows

    [Fact]
    public async Task TheUnavailableMessage_NamesTheTranslatedTitleOfTheTab()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync("fr");
        Recipe recipe = Recipe(
            name: "Probe",
            labels: new Dictionary<string, RecipeTranslation> { ["fr"] = new("Sonde", null, new Dictionary<string, string>()) });
        RecipeToolViewModel viewModel = harness.Create(recipe);
        harness.Availability.State = ToolAvailabilityState.DisabledByUser;

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal("Sonde", viewModel.Title);
        Assert.Equal(harness.Localizer.Format(ToolUnavailableException.DisabledByUserMessageKey, "Sonde"), viewModel.GeneralError);
        Assert.DoesNotContain("Probe", viewModel.GeneralError, StringComparison.Ordinal);
    }

    // ---- immediate feedback when Stop is pressed before the program starts -----------------------

    [Fact]
    public async Task StopDuringTheComparison_ShowsStoppingStatusAtOnce_BeforeTheComparisonReturns()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);
        Assert.Equal(string.Empty, rig.ViewModel.StatusText);

        rig.ViewModel.StopCommand.Execute(null);

        Assert.Equal(harness.Localizer["RecipeStatusStopping"], rig.ViewModel.StatusText);
        Assert.True(rig.ViewModel.HasStatus);
        Assert.False(rig.ViewModel.IsStatusFailure);
        Assert.True(rig.ViewModel.IsRunning);

        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], rig.ViewModel.StatusText);
        Assert.True(rig.ViewModel.IsStatusFailure);
        Assert.False(rig.ViewModel.IsRunning);
    }

    [Fact]
    public async Task SwitchingLanguageWhileStoppingDuringTheComparison_RewordsTheStoppingStatus()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        rig.ViewModel.StopCommand.Execute(null);
        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(harness.Localizer["RecipeStatusStopping"], rig.ViewModel.StatusText);

        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], rig.ViewModel.StatusText);
    }

    [Fact]
    public async Task StopAfterTheProgramStarted_DoesNotShowThePreStartStoppingStatus()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        rig.Resolver.Blocking = false;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<RecipeRunResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.Script = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };

        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Bound);

        rig.ViewModel.StopCommand.Execute(null);

        Assert.NotEqual(harness.Localizer["RecipeStatusStopping"], rig.ViewModel.StatusText);
        Assert.Equal(string.Empty, rig.ViewModel.StatusText);

        release.SetResult(FakeRecipeRunner.Result(RecipeRunStatus.Cancelled));
        await run.WaitAsync(Bound);

        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], rig.ViewModel.StatusText);
    }

    [Fact]
    public async Task AvailabilityChangedOffUiThreadDuringComparison_DoesNotChangeStatusTextBeforeComparisonReturns()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using Rig rig = await RigAsync(harness);
        Task run = rig.ViewModel.RunCommand.ExecuteAsync(null);
        await rig.Resolver.Started.WaitAsync(Bound);

        await Task.Run(() =>
        {
            harness.Availability.State = ToolAvailabilityState.DisabledByUser;
            harness.Availability.RaiseChanged();
        });

        Assert.Equal(string.Empty, rig.ViewModel.StatusText);

        rig.Resolver.Release();
        await run.WaitAsync(Bound);

        Assert.Empty(harness.Runner.Calls);
        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], rig.ViewModel.StatusText);
    }
}
