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

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Heimdall.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that the command library's filter selections survive a library reload.
/// </summary>
/// <remarks>
/// <para>
/// A reload happens after every add, edit, delete, import and Git sync. The category
/// combo has always restored its index explicitly; the platform and risk combos did not,
/// and their item collections are rebuilt on the same path. Because they are bound as
/// <c>ItemsSource</c> with a two-way <c>SelectedIndex</c>, clearing the collection makes
/// the <c>Selector</c> drop its selection and push -1 back into the view model, leaving
/// two blank combos and two silently disabled filters.
/// </para>
/// <para>
/// The combos are built here with the same two bindings the XAML declares, rather than
/// asserting on the view model alone: the reset originates in the <c>Selector</c>, so a
/// view-model-only test cannot see it and would pass throughout.
/// </para>
/// </remarks>
public sealed class CommandLibraryFilterPersistenceTests
{
    [Fact]
    public void PlatformFilterSelectionSurvivesAReload()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var viewModel = await CreateLoadedViewModelAsync();
            using var _ = viewModel;

            var combo = BindCombo(
                viewModel,
                nameof(CommandLibraryViewModel.PlatformFilterItems),
                nameof(CommandLibraryViewModel.PlatformFilterIndex));

            // The user picks Linux.
            combo.SelectedIndex = 2;
            Assert.Equal(2, viewModel.PlatformFilterIndex);

            await viewModel.ReloadAsync();

            Assert.Equal(2, viewModel.PlatformFilterIndex);
            Assert.Equal(2, combo.SelectedIndex);
        });
    }

    [Fact]
    public void RiskFilterSelectionSurvivesAReload()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var viewModel = await CreateLoadedViewModelAsync();
            using var _ = viewModel;

            var combo = BindCombo(
                viewModel,
                nameof(CommandLibraryViewModel.RiskFilterItems),
                nameof(CommandLibraryViewModel.RiskFilterIndex));

            combo.SelectedIndex = 3;
            Assert.Equal(3, viewModel.RiskFilterIndex);

            await viewModel.ReloadAsync();

            Assert.Equal(3, viewModel.RiskFilterIndex);
            Assert.Equal(3, combo.SelectedIndex);
        });
    }

    /// <summary>
    /// Positive control for the mechanism the two tests above rely on: the category
    /// combo is bound the same way and has always restored its index, so if this one
    /// fails the harness is wrong rather than the view model.
    /// </summary>
    [Fact]
    public void CategoryFilterSelectionSurvivesAReload()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var viewModel = await CreateLoadedViewModelAsync();
            using var _ = viewModel;

            var combo = BindCombo(
                viewModel,
                nameof(CommandLibraryViewModel.CategoryFilterItems),
                nameof(CommandLibraryViewModel.CategoryFilterIndex));

            combo.SelectedIndex = 1;
            Assert.Equal(1, viewModel.CategoryFilterIndex);

            await viewModel.ReloadAsync();

            Assert.Equal(1, viewModel.CategoryFilterIndex);
        });
    }

    /// <summary>
    /// The platform pre-selected from the originating session must still be the one in
    /// force once the library has finished loading.
    /// </summary>
    /// <remarks>
    /// The view calls <c>AutoSelectPlatform</c> and then <c>InitializeAsync</c>, and the
    /// latter rebuilds the very collection the combo is bound to, so the pre-selection
    /// had nothing protecting it from the same reset.
    /// </remarks>
    [Fact]
    public void AutoSelectedPlatformSurvivesTheInitialLoad()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var viewModel = await CreateViewModelAsync();
            using var _ = viewModel;

            var combo = BindCombo(
                viewModel,
                nameof(CommandLibraryViewModel.PlatformFilterItems),
                nameof(CommandLibraryViewModel.PlatformFilterIndex));

            // Mirrors CommandLibraryView.Initialize: pre-select, then load.
            viewModel.AutoSelectPlatform("SSH");
            await viewModel.InitializeAsync(targetHost: null);

            Assert.Equal(2, viewModel.PlatformFilterIndex);
        });
    }

    private static ComboBox BindCombo(
        CommandLibraryViewModel viewModel, string itemsPath, string indexPath)
    {
        var combo = new ComboBox { DataContext = viewModel };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
        combo.SetBinding(
            Selector.SelectedIndexProperty,
            new Binding(indexPath) { Mode = BindingMode.TwoWay });
        return combo;
    }

    private static async Task<CommandLibraryViewModel> CreateLoadedViewModelAsync()
    {
        var viewModel = await CreateViewModelAsync();
        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync()
    {
        var actions = new[]
        {
            CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "echo a"),
            CommandLibraryTestHelpers.CreateLinuxAction("b", "Bravo", "echo b")
        };
        actions[1].Category = "Other";

        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        return new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);
    }

}
