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

using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the single-operation-at-a-time rule for the command library.
/// </summary>
/// <remarks>
/// Add, edit, delete, import and export all share one <c>IsBusy</c> flag and one
/// envelope, so a second run started while the first is in flight clears that flag on
/// its own exit and leaves the busy indicator lying about work that is still running.
/// </remarks>
public sealed class CommandLibraryOperationGateTests
{
    /// <summary>
    /// How long a refused operation is allowed to take before the test calls it admitted.
    /// A refusal does no I/O at all, so this only has to outrun scheduling noise.
    /// </summary>
    private static readonly TimeSpan RefusalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ASecondImportIsRefusedWhileTheFirstIsRunning()
    {
        var transfer = new GatedTransferService();
        var viewModel = await CreateViewModelAsync(transfer);
        using var _ = viewModel;
        viewModel.ShowOpenFileDialog = _ => "library.json";

        var first = viewModel.ImportAsync();
        await transfer.WaitEnteredAsync();
        Assert.True(viewModel.IsBusy);

        // Second attempt while the first is still held inside the transfer service.
        // It must come straight back rather than queue behind the held first one.
        await AssertRefusedAsync(viewModel.ImportAsync());

        Assert.Equal(1, transfer.ImportCount);

        transfer.CompleteImport();
        await first;

        Assert.False(viewModel.IsBusy);
    }

    /// <summary>
    /// The busy flag must survive the refused run: the refusal returns through the same
    /// envelope, and clearing the flag there would unlock the tool mid-operation.
    /// </summary>
    [Fact]
    public async Task ARefusedOperationLeavesTheBusyFlagSet()
    {
        var transfer = new GatedTransferService();
        var viewModel = await CreateViewModelAsync(transfer);
        using var _ = viewModel;
        viewModel.ShowOpenFileDialog = _ => "library.json";

        var first = viewModel.ImportAsync();
        await transfer.WaitEnteredAsync();

        await AssertRefusedAsync(viewModel.ImportAsync());
        Assert.True(viewModel.IsBusy);

        transfer.CompleteImport();
        await first;
    }

    /// <summary>
    /// Asserts that <paramref name="attempt"/> was turned away instead of joining the
    /// operation already in flight.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. A refused attempt returns without ever reaching the gated
    /// transfer service, so it completes at once; an attempt that slips through blocks on
    /// the same gate as the first one, and awaiting it unbounded would hang the whole
    /// test run rather than report. Measured against exactly that mutant.
    /// </remarks>
    private static async Task AssertRefusedAsync(Task attempt)
    {
        var finished = await Task.WhenAny(attempt, Task.Delay(RefusalTimeout));
        if (finished != attempt)
        {
            Assert.Fail(
                $"The second operation was still running after {RefusalTimeout.TotalSeconds:0}s: "
                + "it was admitted alongside the first instead of being refused.");
        }

        await attempt;
    }

    [Fact]
    public async Task OperationCommandsAreDisabledWhileAnOperationRuns()
    {
        var transfer = new GatedTransferService();
        var viewModel = await CreateViewModelAsync(transfer);
        using var _ = viewModel;
        viewModel.ShowOpenFileDialog = _ => "library.json";

        Assert.True(viewModel.AddActionCommand.CanExecute(null));
        Assert.True(viewModel.ImportCommand.CanExecute(null));
        Assert.True(viewModel.ExportCommand.CanExecute(null));
        Assert.True(viewModel.SyncCommand.CanExecute(null));
        Assert.True(viewModel.EditSelectedCommand.CanExecute(null));
        Assert.True(viewModel.DeleteSelectedCommand.CanExecute(null));

        var first = viewModel.ImportAsync();
        await transfer.WaitEnteredAsync();

        Assert.False(viewModel.AddActionCommand.CanExecute(null));
        Assert.False(viewModel.ImportCommand.CanExecute(null));
        Assert.False(viewModel.ExportCommand.CanExecute(null));
        Assert.False(viewModel.SyncCommand.CanExecute(null));
        Assert.False(viewModel.EditSelectedCommand.CanExecute(null));
        Assert.False(viewModel.DeleteSelectedCommand.CanExecute(null));

        transfer.CompleteImport();
        await first;

        Assert.True(viewModel.ImportCommand.CanExecute(null));
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(ICommandLibraryTransferService transfer)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(Array.Empty<ActionModel>()));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        // Git sync is the one gated operation this file does not drive, so its service
        // is left unsupplied; every other dependency is on the import path.
        return new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transfer);
    }

    /// <summary>
    /// Transfer service whose import blocks until the test releases it, so a second
    /// import attempt genuinely overlaps the first.
    /// </summary>
    private sealed class GatedTransferService : ICommandLibraryTransferService
    {
        private static readonly TimeSpan EntryWaitTimeout = TimeSpan.FromSeconds(10);

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CommandLibraryImportResult> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ImportCount { get; private set; }

        public Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path)
        {
            ImportCount++;
            _entered.TrySetResult();
            return _gate.Task;
        }

        public Task<int> ExportAsync(IActionService actionService, string path) => Task.FromResult(0);

        /// <summary>
        /// Waits for the import to reach the service, bounded so that a regression which
        /// stops it reaching the service reports a failure instead of hanging the run.
        /// </summary>
        public async Task WaitEnteredAsync()
        {
            var completed = await Task.WhenAny(_entered.Task, Task.Delay(EntryWaitTimeout));
            if (completed != _entered.Task)
            {
                Assert.Fail($"The import never reached the transfer service within {EntryWaitTimeout.TotalSeconds:0}s.");
            }

            await _entered.Task;
        }

        public void CompleteImport() => _gate.TrySetResult(CommandLibraryImportResult.Success(0, 0, 0));
    }
}
