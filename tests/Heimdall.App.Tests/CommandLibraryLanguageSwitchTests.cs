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
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The command library follows an interface language switch, the way the other tools do.
/// </summary>
/// <remarks>
/// It was the one tool tab with no language path at all: its counter, tooltips, broadcast button,
/// filter combos, help text and row badges kept the old language until something unrelated moved.
/// The switch completes off the UI thread, so the test raises it from where production raises it
/// and lets the library send its refresh to the STA dispatcher that owns its bound collections.
/// </remarks>
public sealed class CommandLibraryLanguageSwitchTests
{
    [Fact]
    public void ALanguageSwitch_RewordsTheLibrary_AndKeepsTheFilterSelection()
    {
        StaDispatcherRunner.Run(async () =>
        {
            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
            using CommandLibraryViewModel viewModel = CreateViewModel(localizer);
            await viewModel.InitializeAsync(targetHost: null);

            var combo = new ComboBox { DataContext = viewModel };
            combo.SetBinding(
                ItemsControl.ItemsSourceProperty,
                new Binding(nameof(CommandLibraryViewModel.PlatformFilterItems)));
            combo.SetBinding(
                Selector.SelectedIndexProperty,
                new Binding(nameof(CommandLibraryViewModel.PlatformFilterIndex)) { Mode = BindingMode.TwoWay });
            combo.SelectedIndex = 2;
            string allPlatformsBefore = viewModel.PlatformFilterItems[0];

            var raised = new List<string?>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            await localizer.SwitchLocaleAsync("fr");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Contains(nameof(CommandLibraryViewModel.ResultCountText), raised);
            Assert.Contains(nameof(CommandLibraryViewModel.SendTooltip), raised);
            Assert.Contains(nameof(CommandLibraryViewModel.CopyTooltip), raised);
            Assert.Contains(nameof(CommandLibraryViewModel.BroadcastButtonText), raised);
            Assert.NotEqual(allPlatformsBefore, viewModel.PlatformFilterItems[0]);

            // Rebuilding the combo's items must not cost the user their filter.
            Assert.Equal(2, viewModel.PlatformFilterIndex);
            Assert.Equal(2, combo.SelectedIndex);
        });
    }

    private static CommandLibraryViewModel CreateViewModel(LocalizationManager localizer)
    {
        var actions = new[]
        {
            CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "echo a"),
        };

        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        return new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            localizer,
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!,
            new CurrentThreadDispatcher(Dispatcher.CurrentDispatcher));
    }

    /// <summary>The dispatcher of the thread that built the view model, as WpfUiDispatcher is.</summary>
    private sealed class CurrentThreadDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Invoke(Action action) => dispatcher.Invoke(action);

        public T Invoke<T>(Func<T> func) => dispatcher.Invoke(func);

        public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

        public async Task InvokeAsync(Func<Task> action) => await await dispatcher.InvokeAsync(action);

        public bool CheckAccess() => dispatcher.CheckAccess();
    }
}
