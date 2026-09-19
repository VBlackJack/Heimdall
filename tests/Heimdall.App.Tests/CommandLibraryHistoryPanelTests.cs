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
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what the history panel is given for a row that cannot be opened.
/// </summary>
/// <remarks>
/// The mapper decides that a row is unreadable; this covers the rest of the journey. The
/// fact has to survive into the display model, or the panel renders a blank line where a
/// command should be and offers to copy nothing.
/// </remarks>
public sealed class CommandLibraryHistoryPanelTests
{
    [Fact]
    public async Task AReadableRowCarriesItsCommand()
    {
        var viewModel = await CreateViewModelAsync(
            Row("Tail the log", "tail -f /tmp/app.log", readable: true));
        using var _ = viewModel;

        await viewModel.LoadHistoryAsync();

        var entry = Assert.Single(viewModel.HistoryEntries);
        Assert.True(entry.IsReadable);
        Assert.False(entry.IsUnreadable);
        Assert.Equal("tail -f /tmp/app.log", entry.GeneratedCommand);
    }

    [Fact]
    public async Task AnUnreadableRowIsMarkedAndCarriesNoCommand()
    {
        var viewModel = await CreateViewModelAsync(
            Row("Open a MySQL shell", string.Empty, readable: false));
        using var _ = viewModel;

        await viewModel.LoadHistoryAsync();

        var entry = Assert.Single(viewModel.HistoryEntries);
        Assert.False(entry.IsReadable);
        Assert.True(entry.IsUnreadable);
        Assert.Equal(string.Empty, entry.GeneratedCommand);
    }

    /// <summary>
    /// The title and the timestamp are kept in clear precisely so an unreadable row still
    /// says what ran and when. If they stopped arriving, the row would be worthless and
    /// nothing above would notice.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRowStillSaysWhatRanAndWhen()
    {
        var viewModel = await CreateViewModelAsync(
            Row("Open a MySQL shell", string.Empty, readable: false));
        using var _ = viewModel;

        await viewModel.LoadHistoryAsync();

        var entry = Assert.Single(viewModel.HistoryEntries);
        Assert.Equal("Open a MySQL shell", entry.ActionTitle);
        Assert.False(string.IsNullOrWhiteSpace(entry.Timestamp));
    }

    /// <summary>
    /// The row's double-click gesture reaches the copy command with an empty string. It
    /// must do nothing rather than clear the user's clipboard.
    /// </summary>
    [Fact]
    public async Task CopyingAnUnreadableRowTouchesNothing()
    {
        var viewModel = await CreateViewModelAsync(
            Row("Open a MySQL shell", string.Empty, readable: false));
        using var _ = viewModel;

        var clipboardWrites = 0;
        viewModel.SetClipboardText = _ => { clipboardWrites++; return true; };

        await viewModel.LoadHistoryAsync();
        viewModel.CopyHistoryEntryCommand.Execute(viewModel.HistoryEntries[0].GeneratedCommand);

        Assert.Equal(0, clipboardWrites);
    }

    private static CommandHistory Row(string title, string command, bool readable) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ActionId = "action-1",
        ActionTitle = title,
        Category = "Ops",
        GeneratedCommand = command,
        IsReadable = readable,
        Platform = Platform.Linux,
        CreatedAt = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)
    };

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(params CommandHistory[] rows)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(Array.Empty<ActionModel>()));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        services.AddScoped<ICommandHistoryService>(_ => new StubHistoryService(rows));

        return new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);
    }

    /// <summary>
    /// History service that answers with rows the test wrote, so a row can state that it
    /// is unreadable without needing a locked vault to produce one.
    /// </summary>
    private sealed class StubHistoryService(IReadOnlyList<CommandHistory> rows) : ICommandHistoryService
    {
        public Task<string> AddCommandAsync(
            string actionId, string generatedCommand, Dictionary<string, string> parameters,
            Platform platform, string actionTitle, string category)
            => Task.FromResult(Guid.NewGuid().ToString());

        public Task UpdateWithExecutionResultsAsync(
            string historyId, int exitCode, TimeSpan duration, bool success)
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

        public Task<int> GetCountAsync() => Task.FromResult(rows.Count);

        public Task ClearAllAsync() => Task.CompletedTask;

        public Task CleanupOldEntriesAsync(int daysToKeep = 90) => Task.CompletedTask;
    }
}
