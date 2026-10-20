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

using System.Reflection;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// The library, the palette and the post-connect picker show a shipped action in the
/// operator's language, and the library follows a language switch without losing the
/// selection; an edit still starts from the stored pivot.
/// </summary>
public sealed class CommandLibraryLocalizedCatalogueTests
{
    private const string PivotTitle = "Restart a service";
    private const string FrenchTitle = "Redémarrer un service";
    private static readonly TimeSpan ReloadWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void TheLibrary_ShowsTheOperatorsLanguage_AndFollowsASwitch_KeepingTheSelection()
    {
        StaDispatcherRunner.Run(async () =>
        {
            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
            using CommandLibraryViewModel viewModel = CreateLibrary(localizer);
            await viewModel.InitializeAsync(targetHost: null);

            Assert.Equal(PivotTitle, viewModel.AllEntries.Single(e => e.Source.Id == "svc").Title);
            viewModel.SelectActionById("svc");

            await localizer.SwitchLocaleAsync("fr");

            // The switch reloads the library, and the reload re-applies the search through its
            // debounce before restoring the selection: wait for the selection, bounded.
            var deadline = DateTime.UtcNow + ReloadWait;
            while (viewModel.SelectedEntry?.Title != FrenchTitle && DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
            }

            Assert.Equal(FrenchTitle, viewModel.AllEntries.Single(e => e.Source.Id == "svc").Title);
            Assert.NotNull(viewModel.SelectedEntry);
            Assert.Equal("svc", viewModel.SelectedEntry!.Source.Id);
            Assert.Equal(FrenchTitle, viewModel.SelectedEntry.Title);
        });
    }

    [Fact]
    public void AnEdit_StartsFromThePivot_AndKeepsTheTranslations()
    {
        StaDispatcherRunner.Run(async () =>
        {
            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
            using CommandLibraryViewModel viewModel = CreateLibrary(localizer);
            await viewModel.InitializeAsync(targetHost: null);

            var entry = viewModel.AllEntries.Single(e => e.Source.Id == "svc");
            Assert.Equal(FrenchTitle, entry.Title);

            CommandActionDialogViewModel? shown = null;
            viewModel.ShowActionDialogAsync = dialog =>
            {
                shown = dialog;
                return Task.FromResult(false);
            };

            await viewModel.EditActionAsync(entry);

            Assert.NotNull(shown);
            Assert.Equal(PivotTitle, shown!.Title);
            var saved = shown.ToAction();
            Assert.Equal(FrenchTitle, Assert.Single(saved.Translations).Title);
        });
    }

    [Fact]
    public async Task ThePaletteSnippets_CarryTheOperatorsLanguage()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        IServiceProvider provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(CreateTranslatedAction());
        var palette = new CommandPaletteViewModel(
            null!, localizer, null!, null!, null!, null!, null!, null!,
            provider.GetRequiredService<IServiceScopeFactory>());

        MethodInfo refresh = typeof(CommandPaletteViewModel).GetMethod(
            "RefreshSnippetCacheAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)refresh.Invoke(palette, null)!;

        Assert.Equal(FrenchTitle, Assert.Single(palette.Snippets).Title);
    }

    [Fact]
    public async Task ThePicker_ShowsTheOperatorsLanguage()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        IServiceProvider provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(CreateTranslatedAction());
        var picker = new CommandLibraryPickerDialogViewModel(localizer, provider.GetRequiredService<IServiceScopeFactory>());

        await picker.InitializeAsync(null);

        var item = Assert.Single(picker.Actions);
        Assert.Equal(FrenchTitle, item.Title);
        Assert.Equal("Nom du service", Assert.Single(item.LinuxParameters).Label);
    }

    private static ActionModel CreateTranslatedAction()
    {
        ActionModel action = CommandLibraryTestHelpers.CreateLinuxAction(
            "svc",
            PivotTitle,
            "sudo systemctl restart {serviceName}",
            CommandLibraryTestHelpers.RequiredParameter("serviceName", "Service name"));
        action.Translations =
        [
            new ActionTranslation
            {
                CultureCode = "fr",
                Title = FrenchTitle,
                LinuxParameters = new() { ["serviceName"] = new ParameterTranslation("Nom du service", null) }
            }
        ];
        return action;
    }

    private static CommandLibraryViewModel CreateLibrary(LocalizationManager localizer)
    {
        var actions = new[]
        {
            CreateTranslatedAction(),
            CommandLibraryTestHelpers.CreateLinuxAction("other", "List files", "ls -la"),
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
        public void Invoke(System.Action action) => dispatcher.Invoke(action);

        public T Invoke<T>(Func<T> func) => dispatcher.Invoke(func);

        public Task InvokeAsync(System.Action action) => dispatcher.InvokeAsync(action).Task;

        public async Task InvokeAsync(Func<Task> action) => await await dispatcher.InvokeAsync(action);

        public bool CheckAccess() => dispatcher.CheckAccess();
    }
}
