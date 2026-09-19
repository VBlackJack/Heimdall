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
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers the smaller Command Library findings: search ranking, the import door's
/// unreadable-file path, the dialog's length limits, and the two surfaces that report
/// whether work is in flight.
/// </summary>
public sealed class CommandLibraryP3Tests
{
    // ── Search ranking ────────────────────────────────────────────

    /// <summary>
    /// Rank must follow the order the search service returned, for every entry and not
    /// just the first.
    /// </summary>
    /// <remarks>
    /// The ranks moved from a list scanned linearly to a dictionary. A test that only
    /// checked the top hit would pass on an implementation that returned 0 for
    /// everything, which is what a broken lookup looks like.
    /// </remarks>
    [Fact]
    public async Task SearchRankFollowsTheServiceOrderForEveryEntry()
    {
        var third = CommandLibraryTestHelpers.CreateLinuxAction("c", "Charlie", "echo c");
        var first = CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "echo a");
        var second = CommandLibraryTestHelpers.CreateLinuxAction("b", "Bravo", "echo b");

        // The service answers in a deliberate order that is neither the load order nor
        // alphabetical, so a rank that ignored it could not accidentally match.
        var search = new FixedOrderSearchService([second, third, first]);
        var viewModel = await CreateViewModelAsync(search, first, second, third);
        using var _ = viewModel;

        await viewModel.ApplySearchAsync("e");

        Assert.Equal(0, Entry(viewModel, "b").SearchRank);
        Assert.Equal(1, Entry(viewModel, "c").SearchRank);
        Assert.Equal(2, Entry(viewModel, "a").SearchRank);
    }

    [Fact]
    public async Task AnUnrankedEntrySortsLast()
    {
        var ranked = CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "echo a");
        var ignored = CommandLibraryTestHelpers.CreateLinuxAction("b", "Bravo", "echo b");

        var viewModel = await CreateViewModelAsync(
            new FixedOrderSearchService([ranked]), ranked, ignored);
        using var _ = viewModel;

        await viewModel.ApplySearchAsync("alpha");

        Assert.Equal(0, Entry(viewModel, "a").SearchRank);
        Assert.Equal(int.MaxValue, Entry(viewModel, "b").SearchRank);
    }

    [Fact]
    public async Task ClearingTheSearchClearsEveryRank()
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "echo a");
        var viewModel = await CreateViewModelAsync(new FixedOrderSearchService([action]), action);
        using var _ = viewModel;

        await viewModel.ApplySearchAsync("alpha");
        Assert.True(viewModel.HasActiveSearch);

        await viewModel.ApplySearchAsync(string.Empty);

        Assert.False(viewModel.HasActiveSearch);
        Assert.Equal(int.MaxValue, Entry(viewModel, "a").SearchRank);
    }

    // ── Import: the file cannot be read ───────────────────────────

    [Fact]
    public async Task ImportingAMissingFileReportsItRatherThanThrowing()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService(Array.Empty<ActionModel>());
        var missing = Path.Combine(Path.GetTempPath(), $"cmdlib-absent-{Guid.NewGuid():N}.json");

        var result = await service.ImportAsync(actionService, missing);

        Assert.Equal(CommandLibraryImportOutcome.FileUnreadable, result.Outcome);
    }

    /// <summary>
    /// A file held exclusively by another handle is the case that actually happens: the
    /// user exports, leaves the file open, and imports it back.
    /// </summary>
    [Fact]
    public async Task ImportingALockedFileReportsItRatherThanThrowing()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService(Array.Empty<ActionModel>());
        var path = Path.GetTempFileName();

        try
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await service.ImportAsync(actionService, path);
                Assert.Equal(CommandLibraryImportOutcome.FileUnreadable, result.Outcome);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Positive control: a readable file must not take the unreadable path, or the two
    /// tests above would pass on an import that always reports failure.
    /// </summary>
    [Fact]
    public async Task AReadableFileIsNotReportedAsUnreadable()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService(Array.Empty<ActionModel>());
        var path = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(path, "{ \"actions\": [] }");

            var result = await service.ImportAsync(actionService, path);

            Assert.NotEqual(CommandLibraryImportOutcome.FileUnreadable, result.Outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Dialog length limits ──────────────────────────────────────

    /// <summary>
    /// Description and Notes carry length limits but no inline error label, so their
    /// message has to reach the shared validation line or the limits do nothing.
    /// </summary>
    [Theory]
    [InlineData(2001, 0)]
    [InlineData(0, 5001)]
    public async Task AnOverLongDescriptionOrNoteBlocksValidation(int descriptionLength, int notesLength)
    {
        var vm = new CommandActionDialogViewModel
        {
            Localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            Title = "Tail the log",
            Category = "Linux",
            LinuxPattern = "tail -f /tmp/app.log",
            Description = new string('d', descriptionLength),
            Notes = new string('n', notesLength)
        };

        vm.ValidateCommand.Execute(null);

        Assert.False(string.IsNullOrEmpty(vm.ValidationError));
    }

    [Fact]
    public async Task AnActionWithinTheLimitsValidates()
    {
        var vm = new CommandActionDialogViewModel
        {
            Localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            Title = "Tail the log",
            Category = "Linux",
            LinuxPattern = "tail -f /tmp/app.log",
            Description = new string('d', 2000),
            Notes = new string('n', 5000)
        };

        vm.ValidateCommand.Execute(null);

        Assert.True(string.IsNullOrEmpty(vm.ValidationError));
    }

    // ── Work-in-flight surfaces ───────────────────────────────────

    /// <summary>
    /// The progress bar binds to <see cref="CommandLibraryViewModel.IsOperationRunning"/>
    /// and the commands to <see cref="CommandLibraryViewModel.IsLibraryIdle"/>. They are
    /// one decision, so they must never both be true or both be false.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheIdleAndRunningFlagsAreAlwaysOpposites(bool busy, bool syncing)
    {
        var viewModel = await CreateViewModelAsync(new FixedOrderSearchService([]));
        using var _ = viewModel;

        viewModel.IsBusy = busy;
        viewModel.IsSyncing = syncing;

        Assert.Equal(viewModel.IsLibraryIdle, !viewModel.IsOperationRunning);
        Assert.Equal(busy || syncing, viewModel.IsOperationRunning);
    }

    /// <summary>
    /// Copy stays visible while disabled, so its tooltip has to change with it.
    /// </summary>
    [Fact]
    public async Task TheCopyTooltipExplainsADisabledCopy()
    {
        var viewModel = await CreateViewModelAsync(new FixedOrderSearchService([]));
        using var _ = viewModel;

        viewModel.IsCommandValid = false;
        var whenDisabled = viewModel.CopyTooltip;

        viewModel.IsCommandValid = true;
        var whenEnabled = viewModel.CopyTooltip;

        Assert.NotEqual(whenEnabled, whenDisabled);
        Assert.Equal(viewModel.LocalizeKey("ToolCmdLibSendTooltipInvalid"), whenDisabled);
        Assert.False(string.IsNullOrWhiteSpace(whenEnabled));
    }

    private static CommandLibraryActionEntry Entry(CommandLibraryViewModel viewModel, string id)
        => viewModel.AllEntries.Single(e => string.Equals(e.Source.Id, id, StringComparison.Ordinal));

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(
        ISearchService search, params ActionModel[] actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped(_ => search);
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }

    /// <summary>
    /// Search service that always answers with the same actions in the same order,
    /// whatever the term, so a test can state the ranking it expects.
    /// </summary>
    private sealed class FixedOrderSearchService(IReadOnlyList<ActionModel> ordered) : ISearchService
    {
        public Task<IEnumerable<ActionModel>> SearchAsync(IEnumerable<ActionModel> actions, string searchTerm)
            => Task.FromResult<IEnumerable<ActionModel>>(ordered);
    }
}
