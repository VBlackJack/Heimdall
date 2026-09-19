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

using System.Collections.Concurrent;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the ordering guarantee of the command library's relevance search.
/// </summary>
/// <remarks>
/// The debounce in <see cref="CommandLibraryViewModel.ApplySearchAsync"/> cancels the
/// delay, not the query. Once two searches are past the delay they run concurrently and
/// may finish in either order, so the view model has to decide which result it keeps.
/// The gated fake below removes the timing from the test: the test itself chooses the
/// completion order, so the assertion measures the decision rather than a scheduler.
/// </remarks>
public sealed class CommandLibraryViewModelSearchRaceTests
{
    [Fact]
    public async Task ALateResultFromASupersededSearchIsDiscarded()
    {
        var search = new GatedSearchService();
        var (viewModel, alpha, beta) = await CreateViewModelAsync(search);
        using var _ = viewModel;

        // First query reaches the service and is held there.
        var first = viewModel.ApplySearchAsync("alpha");
        await search.WaitEnteredAsync("alpha");

        // Second query supersedes it and is held too, so both are genuinely in flight.
        var second = viewModel.ApplySearchAsync("beta");
        await search.WaitEnteredAsync("beta");

        // The newer query finishes first, then the superseded one finishes last.
        search.Complete("beta", beta);
        await second;
        search.Complete("alpha", alpha);
        await first;

        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "beta")));
        Assert.False(viewModel.ShouldShowAction(Entry(viewModel, "alpha")));
    }

    /// <summary>
    /// The losing query must not claim the search term either: the view model
    /// short-circuits a repeat of the last term, so a stale claim would make the wrong
    /// result survive a re-search instead of being corrected by it.
    /// </summary>
    [Fact]
    public async Task ASupersededSearchDoesNotClaimTheTerm()
    {
        var search = new GatedSearchService();
        var (viewModel, alpha, beta) = await CreateViewModelAsync(search);
        using var _ = viewModel;

        var first = viewModel.ApplySearchAsync("alpha");
        await search.WaitEnteredAsync("alpha");
        var second = viewModel.ApplySearchAsync("beta");
        await search.WaitEnteredAsync("beta");

        search.Complete("beta", beta);
        await second;
        search.Complete("alpha", alpha);
        await first;

        // Re-issuing the superseded term must reach the service again rather than
        // being answered from a claim the losing query left behind.
        var callsBefore = search.CallCount("alpha");
        var again = viewModel.ApplySearchAsync("alpha");
        await search.WaitEnteredAsync("alpha");
        search.Complete("alpha", alpha);
        await again;

        Assert.Equal(callsBefore + 1, search.CallCount("alpha"));
        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "alpha")));
        Assert.False(viewModel.ShouldShowAction(Entry(viewModel, "beta")));
    }

    /// <summary>
    /// A search that throws must not leave the term claimed, otherwise retyping it
    /// silently returns the unfiltered list instead of retrying.
    /// </summary>
    [Fact]
    public async Task AFailedSearchDoesNotClaimTheTerm()
    {
        var search = new GatedSearchService();
        var (viewModel, alpha, _) = await CreateViewModelAsync(search);
        using var __ = viewModel;

        var first = viewModel.ApplySearchAsync("alpha");
        await search.WaitEnteredAsync("alpha");
        search.Fail("alpha", new InvalidOperationException("search backend down"));
        await first;

        var callsBefore = search.CallCount("alpha");
        var retry = viewModel.ApplySearchAsync("alpha");
        await search.WaitEnteredAsync("alpha");
        search.Complete("alpha", alpha);
        await retry;

        Assert.Equal(callsBefore + 1, search.CallCount("alpha"));
        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "alpha")));
    }

    /// <summary>
    /// A failure must also release a term claimed by an <i>earlier successful</i> search.
    /// </summary>
    /// <remarks>
    /// This is the case that distinguishes releasing the term in the failure path from
    /// merely claiming it late on success: with nothing claimed beforehand, the two are
    /// indistinguishable. Here "alpha" succeeds first, so the field is genuinely
    /// occupied when "beta" fails, and the state left behind is a term the view model
    /// believes is filtering while no match set exists.
    /// </remarks>
    [Fact]
    public async Task AFailedSearchReleasesATermClaimedByAnEarlierSuccess()
    {
        var search = new GatedSearchService();
        var (viewModel, alpha, _) = await CreateViewModelAsync(search);
        using var __ = viewModel;

        var succeeded = viewModel.ApplySearchAsync("alpha");
        await search.WaitEnteredAsync("alpha");
        search.Complete("alpha", alpha);
        await succeeded;
        Assert.True(viewModel.HasActiveFilters);

        var failed = viewModel.ApplySearchAsync("beta");
        await search.WaitEnteredAsync("beta");
        search.Fail("beta", new InvalidOperationException("search backend down"));
        await failed;

        // Nothing is filtering any more, so the view model must not still report an
        // active filter - that is what drives the "no results" versus "empty library"
        // placeholder choice.
        Assert.False(viewModel.HasActiveFilters);
        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "alpha")));
        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "beta")));
    }

    private static CommandLibraryActionEntry Entry(
        CommandLibraryViewModel viewModel, string id)
        => viewModel.AllEntries.Single(entry => string.Equals(entry.Source.Id, id, StringComparison.Ordinal));

    private static async Task<(CommandLibraryViewModel ViewModel, ActionModel[] Alpha, ActionModel[] Beta)>
        CreateViewModelAsync(GatedSearchService search)
    {
        var alpha = CommandLibraryTestHelpers.CreateLinuxAction("alpha", "Alpha action", "echo alpha");
        var beta = CommandLibraryTestHelpers.CreateLinuxAction("beta", "Beta action", "echo beta");

        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService([alpha, beta]));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService>(_ => search);
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        var provider = services.BuildServiceProvider();

        // Only the provider and the localizer are on the search path; the config,
        // dialog, sync and transfer dependencies are never touched by loading the
        // library or by running a search, so they are left unsupplied rather than
        // stubbed into something a reader might mistake for part of the scenario.
        var viewModel = new CommandLibraryViewModel(
            provider,
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            dialogService: null!,
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);

        return (viewModel, [alpha], [beta]);
    }

    /// <summary>
    /// Search service whose every call blocks until the test releases it by term,
    /// and which reports when a term has been entered so the test never has to sleep.
    /// </summary>
    private sealed class GatedSearchService : ISearchService
    {
        /// <summary>
        /// Generous next to the view model's 200 ms debounce, so a slow machine never
        /// trips it, yet short enough that a genuine regression reports instead of hangs.
        /// </summary>
        private static readonly TimeSpan EntryWaitTimeout = TimeSpan.FromSeconds(10);

        private readonly ConcurrentDictionary<string, TaskCompletionSource<IEnumerable<ActionModel>>> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public Task<IEnumerable<ActionModel>> SearchAsync(IEnumerable<ActionModel> actions, string searchTerm)
        {
            _calls.AddOrUpdate(searchTerm, 1, static (_, count) => count + 1);

            // Capture the gate before signalling entry: the test may complete and reset
            // the term as soon as it observes the signal, and a second lookup would then
            // hand back a fresh, never-completed gate.
            var gate = Gate(searchTerm);
            Entered(searchTerm).TrySetResult();
            return gate.Task;
        }

        /// <summary>
        /// Waits for <paramref name="term"/> to reach the service, and fails rather than
        /// hanging if it never does.
        /// </summary>
        /// <remarks>
        /// The bound is not decoration. A regression that lets a superseded query claim
        /// the search term makes the view model short-circuit the next identical term
        /// without calling the service at all, so an unbounded wait here would hang the
        /// whole run instead of reporting a failure. Measured against exactly that mutant.
        /// </remarks>
        public async Task WaitEnteredAsync(string term)
        {
            var entered = Entered(term).Task;
            var completed = await Task.WhenAny(entered, Task.Delay(EntryWaitTimeout));
            if (completed != entered)
            {
                Assert.Fail(
                    $"Search term '{term}' never reached the search service within {EntryWaitTimeout.TotalSeconds:0}s. "
                    + "The view model most likely short-circuited it against a stale claimed term.");
            }

            await entered;
        }

        public int CallCount(string term) => _calls.TryGetValue(term, out var count) ? count : 0;

        public void Complete(string term, IEnumerable<ActionModel> results)
        {
            Gate(term).TrySetResult(results);
            Reset(term);
        }

        public void Fail(string term, Exception error)
        {
            Gate(term).TrySetException(error);
            Reset(term);
        }

        /// <summary>
        /// Drops the per-term gate and entry signal so a later call for the same term
        /// blocks again instead of being answered by the previous call's completed task.
        /// </summary>
        private void Reset(string term)
        {
            _gates.TryRemove(term, out _);
            _entered.TryRemove(term, out _);
        }

        private TaskCompletionSource<IEnumerable<ActionModel>> Gate(string term)
            => _gates.GetOrAdd(term, static _ => new TaskCompletionSource<IEnumerable<ActionModel>>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource Entered(string term)
            => _entered.GetOrAdd(term, static _ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
