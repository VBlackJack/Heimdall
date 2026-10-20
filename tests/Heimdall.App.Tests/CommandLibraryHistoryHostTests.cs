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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what the library records about where a command went, and what the history panel
/// shows of it.
/// </summary>
/// <remarks>
/// The recording runs on a worker thread, fire and forget, so each test waits for the row
/// the gesture is expected to write, with a bound, rather than reading a list that may not
/// have been written yet.
/// </remarks>
public sealed class CommandLibraryHistoryHostTests
{
    private const string Host = "srv-01";
    private static readonly TimeSpan RecordWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Copy_RecordsTheHostAndNotExecuted()
    {
        var history = new RecordingHistoryService();
        using var viewModel = await CreateSelectedViewModelAsync(history, Host);
        viewModel.SetClipboardText = _ => true;

        viewModel.Copy();

        var recorded = await history.NextAsync().WaitAsync(RecordWait);
        Assert.Equal(Host, recorded.TargetHost);
        Assert.False(recorded.IsExecuted);
    }

    [Fact]
    public async Task Send_RecordsTheHostAndExecuted()
    {
        var history = new RecordingHistoryService();
        using var viewModel = await CreateSelectedViewModelAsync(history, Host);
        viewModel.SendCommandHandler = _ => { };

        await viewModel.SendAsync();

        var recorded = await history.NextAsync().WaitAsync(RecordWait);
        Assert.Equal(Host, recorded.TargetHost);
        Assert.True(recorded.IsExecuted);
    }

    [Fact]
    public async Task ThisHostOnly_HidesAnotherHostAndShowsTheSameHost()
    {
        var history = new RecordingHistoryService(
            Row("Same host", "SRV-01"),
            Row("Other host", "srv-02"),
            Row("No host", null));
        using var viewModel = await CreateSelectedViewModelAsync(history, Host);

        await viewModel.LoadHistoryAsync();

        Assert.True(viewModel.HasTargetHost);
        Assert.True(viewModel.HistoryThisHostOnly);
        Assert.Equal(["Same host"], VisibleTitles(viewModel));
        Assert.False(viewModel.IsHistoryEmpty);
    }

    // Positive control for the filter above: unticked, every row is back, so the test above
    // cannot pass on a panel that simply lost two rows.
    [Fact]
    public async Task ThisHostOnly_Unticked_ShowsEveryRow_AndStaysUntickedAcrossAReload()
    {
        var history = new RecordingHistoryService(
            Row("Same host", Host),
            Row("Other host", "srv-02"));
        using var viewModel = await CreateSelectedViewModelAsync(history, Host);
        await viewModel.LoadHistoryAsync();

        viewModel.HistoryThisHostOnly = false;
        await viewModel.LoadHistoryAsync();

        Assert.False(viewModel.HistoryThisHostOnly);
        Assert.Equal(["Same host", "Other host"], VisibleTitles(viewModel));
    }

    [Fact]
    public async Task WithoutAHost_TheFilterIsNotOfferedAndHidesNothing()
    {
        var history = new RecordingHistoryService(
            Row("Same host", Host),
            Row("Other host", "srv-02"));
        using var viewModel = await CreateSelectedViewModelAsync(history, targetHost: null);

        await viewModel.LoadHistoryAsync();

        Assert.False(viewModel.HasTargetHost);
        Assert.Equal(["Same host", "Other host"], VisibleTitles(viewModel));
    }

    [Fact]
    public async Task ARowCarriesItsHostAndItsSentFlagIntoThePanel()
    {
        var history = new RecordingHistoryService(Row("Same host", Host, executed: true));
        using var viewModel = await CreateSelectedViewModelAsync(history, Host);

        await viewModel.LoadHistoryAsync();

        var entry = Assert.Single(viewModel.HistoryEntries);
        Assert.Equal(Host, entry.TargetHost);
        Assert.True(entry.IsExecuted);
        Assert.False(entry.IsCopied);
    }

    private static List<string> VisibleTitles(CommandLibraryViewModel viewModel)
        => viewModel.HistoryView.Cast<CommandLibraryHistoryEntry>().Select(entry => entry.ActionTitle).ToList();

    private static CommandHistory Row(string title, string? host, bool executed = false) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ActionId = "action-1",
        ActionTitle = title,
        Category = "Ops",
        GeneratedCommand = "uptime",
        Platform = Platform.Linux,
        TargetHost = host,
        IsExecuted = executed,
        CreatedAt = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)
    };

    internal static async Task<CommandLibraryViewModel> CreateSelectedViewModelAsync(
        ICommandHistoryService history,
        string? targetHost)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(
            [CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Uptime", "uptime")]));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        services.AddScoped(_ => history);

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost);
        viewModel.SelectAction(viewModel.AllEntries[0]);
        return viewModel;
    }

    /// <summary>
    /// History service that records what the library wrote and answers with the rows a test
    /// gave it.
    /// </summary>
    internal sealed class RecordingHistoryService(params CommandHistory[] rows) : ICommandHistoryService
    {
        private readonly TaskCompletionSource<(string? TargetHost, bool IsExecuted)> _next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(string? TargetHost, bool IsExecuted)> NextAsync() => _next.Task;

        public Task<string> AddCommandAsync(
            string actionId, string generatedCommand, Dictionary<string, string> parameters,
            Platform platform, string actionTitle, string category,
            string? targetHost, bool isExecuted)
        {
            _next.TrySetResult((targetHost, isExecuted));
            return Task.FromResult(Guid.NewGuid().ToString());
        }

        public Task UpdateWithExecutionResultsAsync(string historyId, int exitCode, TimeSpan duration, bool success)
            => Task.CompletedTask;

        public Task<IEnumerable<CommandHistory>> GetRecentAsync(int count = 50)
            => Task.FromResult<IEnumerable<CommandHistory>>(rows);

        public Task<IEnumerable<CommandHistory>> SearchAsync(
            string? searchText = null,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            Platform? platform = null,
            string? category = null)
            => Task.FromResult<IEnumerable<CommandHistory>>(rows);

        public Task<CommandHistory?> GetByIdAsync(string id)
            => Task.FromResult(rows.FirstOrDefault(row => row.Id == id));

        public Task DeleteAsync(string id) => Task.CompletedTask;

        public Task DeleteRangeAsync(IEnumerable<string> ids) => Task.CompletedTask;

        public Task CleanupOldEntriesAsync(int daysToKeep = 90) => Task.CompletedTask;

        public Task<int> GetCountAsync() => Task.FromResult(rows.Length);

        public Task ClearAllAsync() => Task.CompletedTask;
    }
}
