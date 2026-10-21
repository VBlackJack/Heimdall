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
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that a reload resolves the selection again among the fresh entries.
/// </summary>
/// <remarks>
/// The reload rebuilds every row. <see cref="CommandLibraryViewModel.SelectedEntry"/> used to
/// keep the object from before it, a row no longer in <see cref="CommandLibraryViewModel.AllEntries"/>,
/// whose action could even have been deleted. Run on one dispatcher thread, as the view runs
/// it: the reload rebuilds a collection view, which refuses changes from another thread.
/// </remarks>
public sealed class CommandLibraryReloadSelectionTests
{
    [Fact]
    public void AfterAReload_TheSelectedEntryIsOneOfTheFreshEntries()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var actions = new FakeActionService(
            [
                CommandLibraryTestHelpers.CreateLinuxAction("tail", "Tail a log", "tail -f /var/log/syslog"),
                CommandLibraryTestHelpers.CreateLinuxAction("disk", "Disk usage", "df -h"),
            ]);
            using var viewModel = await CreateViewModelAsync(actions);
            viewModel.SelectedEntry = viewModel.AllEntries.Single(entry => entry.Source.Id == "disk");
            var before = viewModel.SelectedEntry;

            await viewModel.ReloadAsync();

            Assert.NotNull(viewModel.SelectedEntry);
            Assert.NotSame(before, viewModel.SelectedEntry);
            Assert.Contains(viewModel.AllEntries, entry => ReferenceEquals(entry, viewModel.SelectedEntry));
            Assert.Equal("disk", viewModel.SelectedEntry!.Source.Id);
        });
    }

    [Fact]
    public void AfterTheSelectedActionIsDeleted_NothingIsSelected()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var actions = new FakeActionService(
            [
                CommandLibraryTestHelpers.CreateLinuxAction("tail", "Tail a log", "tail -f /var/log/syslog"),
                CommandLibraryTestHelpers.CreateLinuxAction("disk", "Disk usage", "df -h"),
            ]);
            using var viewModel = await CreateViewModelAsync(actions);
            viewModel.SelectedEntry = viewModel.AllEntries.Single(entry => entry.Source.Id == "disk");

            await actions.DeleteActionAsync("disk");
            await viewModel.ReloadAsync();

            Assert.Null(viewModel.SelectedEntry);
        });
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(FakeActionService actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => actions);
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
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
}
